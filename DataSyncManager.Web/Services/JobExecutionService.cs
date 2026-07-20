using DataSyncManager.Web.Data;
using DataSyncManager.Web.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Data;
using System.Data.Odbc;
using System.Text;

namespace DataSyncManager.Web.Services;

public interface IJobExecutionService
{
    Task<JobRun> ExecuteJobAsync(int jobId, long projectRunId, CancellationToken ct = default);
}

public class JobExecutionService : IJobExecutionService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IEmailService _email;
    private readonly ILogger<JobExecutionService> _log;
    private readonly IHttpClientFactory _httpFactory;

    public JobExecutionService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IEmailService email,
        ILogger<JobExecutionService> log,
        IHttpClientFactory httpFactory)
    {
        _dbFactory = dbFactory;
        _email = email;
        _log = log;
        _httpFactory = httpFactory;
    }

    public async Task<JobRun> ExecuteJobAsync(int jobId, long projectRunId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var job = await db.Jobs
            .Include(j => j.JobFields.Where(f => f.IsIncluded).OrderBy(f => f.SortOrder))
            .Include(j => j.Project).ThenInclude(p => p.SourceServer)
            .Include(j => j.DestinationServer)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new InvalidOperationException($"Job {jobId} not found");

        var run = new JobRun
        {
            JobId = jobId,
            ProjectRunId = projectRunId,
            Status = RunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            await AddLog(db, run.Id, "Info", $"Starting job '{job.Name}'", ct);

            var sourceServer = job.Project.SourceServer;
            var destServer = job.DestinationServer;

            // Ensure destination table exists / has required columns
            await EnsureDestinationTableAsync(job, destServer, db, run.Id, ct);

            if (job.SyncMode == SyncMode.FullReplace)
                await ExecuteFullReplaceAsync(job, sourceServer, destServer, run, db, ct);
            else
                await ExecuteUpsertAsync(job, sourceServer, destServer, run, db, ct);

            run.Status = run.Logs.Any(l => l.Level == "Error") ? RunStatus.PartialSuccess : RunStatus.Succeeded;
        }
        catch (Exception ex)
        {
            run.Status = RunStatus.Failed;
            run.ErrorMessage = ex.Message;
            await AddLog(db, run.Id, "Error", $"Job failed: {ex.Message}", ct);
            _log.LogError(ex, "Job {JobId} failed", jobId);
        }
        finally
        {
            run.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        // Fire alert emails
        await SendJobAlertAsync(job, run, ct);

        return run;
    }

    // ──────────────────────────────────────────────────────────
    // DDL: Ensure destination table & columns exist
    // ──────────────────────────────────────────────────────────

    private async Task EnsureDestinationTableAsync(Job job, DestinationServer dest,
        ApplicationDbContext db, long runId, CancellationToken ct)
    {
        var csb = new SqlConnectionStringBuilder(dest.ConnectionString)
        { InitialCatalog = job.DestinationDatabase };

        await using var conn = new SqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct);

        var parts = job.DestinationTable.Split('.');
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : job.DestinationTable;

        // Check table exists
        var existsCmd = new SqlCommand(
            "SELECT COUNT(1) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA=@s AND TABLE_NAME=@t",
            conn);
        existsCmd.Parameters.AddWithValue("@s", schema);
        existsCmd.Parameters.AddWithValue("@t", table);
        var exists = (int)(await existsCmd.ExecuteScalarAsync(ct))! > 0;

        if (!exists && job.CreateDestinationTableIfMissing)
        {
            // CREATE TABLE from selected fields
            var sb = new StringBuilder();
            sb.Append($"CREATE TABLE [{schema}].[{table}] (");
            sb.Append("[_SyncId] BIGINT IDENTITY(1,1) NOT NULL, ");
            sb.Append("[_SyncedAt] DATETIME2 NOT NULL DEFAULT(GETUTCDATE()), ");

            foreach (var f in job.JobFields)
            {
                var destCol = f.DestinationFieldName ?? f.SourceFieldName;
                var colDef = BuildColumnDdl(destCol, f.DataType ?? "nvarchar", f.MaxLength, f.IsNullable);
                sb.Append(colDef + ", ");
            }

            sb.Append($"CONSTRAINT [PK_{table}_SyncId] PRIMARY KEY ([_SyncId])");
            sb.Append(")");
            await new SqlCommand(sb.ToString(), conn).ExecuteNonQueryAsync(ct);
            await AddLog(db, runId, "Info", $"Created destination table [{schema}].[{table}]", ct);
            await db.SaveChangesAsync(ct);
        }
        else if (exists)
        {
            // Ensure _SyncedAt exists (required by MERGE statement)
            var syncedAtCmd = new SqlCommand(
                "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=@s AND TABLE_NAME=@t AND COLUMN_NAME='_SyncedAt'",
                conn);
            syncedAtCmd.Parameters.AddWithValue("@s", schema);
            syncedAtCmd.Parameters.AddWithValue("@t", table);
            var syncedAtExists = (int)(await syncedAtCmd.ExecuteScalarAsync(ct))! > 0;

            if (!syncedAtExists)
            {
                await new SqlCommand(
                    $"ALTER TABLE [{schema}].[{table}] ADD [_SyncedAt] DATETIME2 NOT NULL DEFAULT (GETUTCDATE())",
                    conn).ExecuteNonQueryAsync(ct);
                await AddLog(db, runId, "Info", $"Added [_SyncedAt] column to [{schema}].[{table}]", ct);
            }
            // Ensure all selected source columns exist in destination
            foreach (var f in job.JobFields)
            {
                var destCol = f.DestinationFieldName ?? f.SourceFieldName;
                var colExistsCmd = new SqlCommand(
                    "SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=@s AND TABLE_NAME=@t AND COLUMN_NAME=@c",
                    conn);
                colExistsCmd.Parameters.AddWithValue("@s", schema);
                colExistsCmd.Parameters.AddWithValue("@t", table);
                colExistsCmd.Parameters.AddWithValue("@c", destCol);
                var colExists = (int)(await colExistsCmd.ExecuteScalarAsync(ct))! > 0;

                if (!colExists)
                {
                    var altSql = $"ALTER TABLE [{schema}].[{table}] ADD {BuildColumnDdl(destCol, f.DataType ?? "nvarchar", f.MaxLength, true)}";
                    await new SqlCommand(altSql, conn).ExecuteNonQueryAsync(ct);
                    await AddLog(db, runId, "Info", $"Added column [{destCol}] to [{schema}].[{table}]", ct);
                }
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private static string BuildColumnDdl(string colName, string dataType, int maxLen, bool nullable)
    {
        var nullDef = nullable ? "NULL" : "NOT NULL";
        var typeLower = dataType.ToLower();
        // SQL Server TIMESTAMP / ROWVERSION is a per-table binary counter.
        // Only one is allowed per table and it cannot be set explicitly — map to BINARY(8).
        if (typeLower is "rowversion")
            return $"[{colName}] BINARY(8) {nullDef}";

        if (typeLower is "timestamp")
            return $"[{colName}] DATETIME2(7) {nullDef}";

        var typeStr = typeLower switch
        {
            "nvarchar" or "varchar" or "char" or "nchar" =>
                $"[{colName}] {dataType.ToUpper()}({(maxLen <= 0 || maxLen > 4000 ? "MAX" : maxLen.ToString())}) {nullDef}",
            "decimal" or "numeric" => $"[{colName}] {dataType.ToUpper()}(18,6) {nullDef}",
            _ => $"[{colName}] {dataType.ToUpper()} {nullDef}"
        };
        return typeStr;
    }

    // ──────────────────────────────────────────────────────────
    // Full Replace Mode
    // ──────────────────────────────────────────────────────────

    private async Task ExecuteFullReplaceAsync(Job job, SourceServer src, DestinationServer dest,
        JobRun run, ApplicationDbContext db, CancellationToken ct)
    {
        await AddLog(db, run.Id, "Info", "Mode: Full Replace", ct);
        await db.SaveChangesAsync(ct);

        var data = await FetchSourceDataAsync(job, src, null, null, ct);
        run.RowsRead = data.Rows.Count;
        await AddLog(db, run.Id, "Info", $"Read {run.RowsRead} rows from source", ct);

        // Reconcile: drop any fields absent from the source DataTable
        var activeFields = await ReconcileFieldsAsync(job, data, dest, db, run.Id, ct);

        // Truncate then bulk insert
        var parts = job.DestinationTable.Split('.');
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : job.DestinationTable;

        var csb = new SqlConnectionStringBuilder(dest.ConnectionString) { InitialCatalog = job.DestinationDatabase };

        await ExecuteWithRetryAsync(dest.RetryCount, dest.RetryDelaySeconds, async () =>
        {
            await using var conn = new SqlConnection(csb.ConnectionString);
            await conn.OpenAsync(ct);

            // Wrap truncate + bulk copy in a transaction so a failed load never leaves the table empty
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
            try
            {
                await new SqlCommand($"TRUNCATE TABLE [{schema}].[{table}]", conn, tx)
                    .ExecuteNonQueryAsync(ct);

                using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
                {
                    DestinationTableName = $"[{schema}].[{table}]",
                    BulkCopyTimeout = 0  // 0 = no timeout
                };
                foreach (var f in activeFields)                              // ← was job.JobFields
                    bulk.ColumnMappings.Add(f.SourceFieldName, f.DestinationFieldName ?? f.SourceFieldName);

                await bulk.WriteToServerAsync(data, ct);
                run.RowsInserted = data.Rows.Count;

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }, run, db, ct,
        context: $"Job '{job.Name}' | {src.Name} → {dest.Name} | {job.SourceTable} → [{job.DestinationDatabase}].[{job.DestinationTable}] | FullReplace");

        await AddLog(db, run.Id, "Info", $"Inserted {run.RowsInserted} rows", ct);
        await db.SaveChangesAsync(ct);
    }

    // ──────────────────────────────────────────────────────────
    // Upsert Mode (windowed by DaysPerBatch, with overlap buffer)
    // ──────────────────────────────────────────────────────────

    private async Task ExecuteUpsertAsync(Job job, SourceServer src, DestinationServer dest,
        JobRun run, ApplicationDbContext db, CancellationToken ct)
    {
        await AddLog(db, run.Id, "Info", "Mode: Upsert", ct);

        // ── Determine sync window start ──────────────────────────────────────────
        //
        // Strategy: CompletedAt of the last successful run, minus a configurable
        // overlap buffer. The overlap re-scans the tail of the previous window,
        // which is safe because MERGE is idempotent — duplicate rows hit WHEN MATCHED
        // and produce a no-op update. This guards against:
        //   • In-flight writes that committed after the previous run's window closed
        //   • Source DB clock skew (especially relevant for ODBC sources)
        //   • Rows backfilled with a ChangeDateField near the last window boundary
        //
        // MaxSourceTimestamp is recorded separately as a diagnostic watermark.
        // It is NOT used to drive the window — that keeps the time-anchor as the
        // safety net if a source stops updating ChangeDateField correctly.

        var lastRun = await db.JobRuns
            .Where(r => r.JobId == job.Id && r.Status == RunStatus.Succeeded)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(ct);

        DateTime syncFrom;
        string windowReason;

        if (lastRun?.CompletedAt is not null)
        {
            var overlap = TimeSpan.FromMinutes(job.SyncOverlapMinutes);
            syncFrom = lastRun.CompletedAt.Value - overlap;
            windowReason = $"last successful run completed {lastRun.CompletedAt.Value:yyyy-MM-dd HH:mm} UTC, minus {job.SyncOverlapMinutes}m overlap";

            // Warn if the previous run's MaxSourceTimestamp looks stale — more than
            // 2× the expected schedule gap behind the window start. This is a signal,
            // not an error; log it so the operator can investigate.
            if (lastRun.MaxSourceTimestamp.HasValue)
            {
                var staleness = syncFrom - lastRun.MaxSourceTimestamp.Value;
                if (staleness > TimeSpan.FromDays((double)job.DaysPerBatch * 2))
                {
                    await AddLog(db, run.Id, "Warning",
                        $"Previous run's MaxSourceTimestamp ({lastRun.MaxSourceTimestamp.Value:yyyy-MM-dd HH:mm} UTC) " +
                        $"is {staleness.TotalHours:F1}h behind the window start. " +
                        $"Source data may be stale, or '{job.ChangeDateField}' is not being updated correctly.", ct);
                }
            }
        }
        else if (job.SyncStartDate.HasValue)
        {
            syncFrom = DateTime.SpecifyKind(job.SyncStartDate.Value, DateTimeKind.Utc);
            windowReason = "SyncStartDate (first run)";
        }
        else
        {
            syncFrom = DateTime.UtcNow.AddDays(-(double)job.DaysPerBatch);
            windowReason = $"fallback: now minus {job.DaysPerBatch}d (no prior run, no SyncStartDate)";
        }

        // Record the window start on the run for audit purposes
        run.SyncWindowStart = syncFrom;

        var syncTo = DateTime.UtcNow;
        var estimatedBatches = (int)Math.Ceiling((syncTo - syncFrom).TotalDays / (double)job.DaysPerBatch);

        await AddLog(db, run.Id, "Info",
            $"Sync window: {syncFrom:yyyy-MM-dd HH:mm} UTC → {syncTo:yyyy-MM-dd HH:mm} UTC " +
            $"(~{estimatedBatches} batch{(estimatedBatches == 1 ? "" : "es")} of {job.DaysPerBatch}d) — {windowReason}", ct);
        await db.SaveChangesAsync(ct);

        // ── Batch loop ───────────────────────────────────────────────────────────

        var parts = job.DestinationTable.Split('.');
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : job.DestinationTable;

        //var keys = (job.UniqueKeyFields ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        //var fieldList = job.JobFields.Select(f => f.DestinationFieldName ?? f.SourceFieldName).ToList();
        //var onClause = string.Join(" AND ", keys.Select(k => $"t.[{k.Trim()}] = s.[{k.Trim()}]"));
        //var setClause = string.Join(", ", fieldList.Where(f => !keys.Contains(f.Trim())).Select(f => $"t.[{f}] = s.[{f}]"));
        //var insColList = string.Join(", ", fieldList.Select(f => $"[{f}]"));
        //var insValList = string.Join(", ", fieldList.Select(f => $"s.[{f}]"));
        var keys = (job.UniqueKeyFields ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

        // These are built lazily on the first batch after field reconciliation
        List<JobField>? activeFields = null;
        var onClause = string.Empty;
        var setClause = string.Empty;
        var insColList = string.Empty;
        var insValList = string.Empty;

        var csb = new SqlConnectionStringBuilder(dest.ConnectionString) { InitialCatalog = job.DestinationDatabase };

        var batchStart = syncFrom;
        var batchNum = 0;
        DateTime? maxSourceTs = null;   // tracks watermark across all batches

        while (batchStart < syncTo)
        {
            ct.ThrowIfCancellationRequested();

            var batchEnd = batchStart.AddDays((double)job.DaysPerBatch);
            if (batchEnd > syncTo) batchEnd = syncTo;
            batchNum++;

            var data = await FetchSourceDataAsync(job, src, batchStart, batchEnd, ct);
            run.RowsRead += data.Rows.Count;

            // Reconcile once — detect any source columns that have disappeared
            if (activeFields is null)
            {
                activeFields = await ReconcileFieldsAsync(job, data, dest, db, run.Id, ct);

                var activeFieldList = activeFields.Select(f => f.DestinationFieldName ?? f.SourceFieldName).ToList();
                onClause = string.Join(" AND ", keys.Select(k => $"t.[{k.Trim()}] = s.[{k.Trim()}]"));
                setClause = string.Join(", ", activeFieldList.Where(f => !keys.Select(k => k.Trim()).Contains(f)).Select(f => $"t.[{f}] = s.[{f}]"));
                insColList = string.Join(", ", activeFieldList.Select(f => $"[{f}]"));
                insValList = string.Join(", ", activeFieldList.Select(f => $"s.[{f}]"));
            }

            if (estimatedBatches > 1)
            {
                await AddLog(db, run.Id, "Info",
                    $"Batch {batchNum}/{estimatedBatches} ({batchStart:yyyy-MM-dd} → {batchEnd:yyyy-MM-dd}): {data.Rows.Count} rows", ct);
                await db.SaveChangesAsync(ct);
            }

            if (data.Rows.Count > 0)
            {
                // Update the running watermark from this batch
                var batchMax = GetMaxTimestamp(data, job.ChangeDateField);
                if (batchMax.HasValue && (maxSourceTs is null || batchMax.Value > maxSourceTs.Value))
                    maxSourceTs = batchMax;

                var staging = $"#Stg_{Guid.NewGuid():N}";
                var merge = $"""
                    MERGE [{schema}].[{table}] AS t
                    USING {staging} AS s ON {onClause}
                    WHEN MATCHED THEN UPDATE SET {setClause}, [_SyncedAt] = GETUTCDATE()
                    WHEN NOT MATCHED BY TARGET THEN INSERT ({insColList}, [_SyncedAt]) VALUES ({insValList}, GETUTCDATE())
                    OUTPUT $action;
                    """;

                await ExecuteWithRetryAsync(dest.RetryCount, dest.RetryDelaySeconds, async () =>
                {
                    await using var conn = new SqlConnection(csb.ConnectionString);
                    await conn.OpenAsync(ct);

                    var stagingCols = string.Join(", ", job.JobFields.Select(f => $"[{f.DestinationFieldName ?? f.SourceFieldName}]"));
                    await new SqlCommand($"SELECT TOP 0 {stagingCols} INTO {staging} FROM [{schema}].[{table}]", conn)
                    {
                        CommandTimeout = 0
                    }.ExecuteNonQueryAsync(ct);

                    using var bulk = new SqlBulkCopy(conn) { DestinationTableName = staging };
                    foreach (var f in activeFields!)
                        bulk.ColumnMappings.Add(f.SourceFieldName, f.DestinationFieldName ?? f.SourceFieldName);
                    await bulk.WriteToServerAsync(data, ct);

                    //await using var mergeCmd = new SqlCommand(merge, conn);
                    //await using var rdr = await mergeCmd.ExecuteReaderAsync(ct);
                    var mergeCmd = new SqlCommand(merge, conn)
                    {
                        CommandTimeout = 0  // 0 = no timeout; or use e.g. 600 for 10 minutes
                    };
                    await using var mergeReader = await mergeCmd.ExecuteReaderAsync(ct);
                    while (await mergeReader.ReadAsync(ct))
                    {
                        if (mergeReader.GetString(0) == "INSERT") run.RowsInserted++;
                        else run.RowsUpdated++;
                    }
                }, run, db, ct,
                context: $"Job '{job.Name}' | {src.Name} → {dest.Name} | {job.SourceTable} → [{job.DestinationDatabase}].[{job.DestinationTable}] | Upsert");
            }

            batchStart = batchEnd;
        }

        // ── Persist watermark and summary ────────────────────────────────────────

        run.MaxSourceTimestamp = maxSourceTs;

        if (run.RowsRead == 0)
        {
            await AddLog(db, run.Id, "Info", "No rows to upsert", ct);
        }
        else
        {
            var watermarkMsg = maxSourceTs.HasValue
                ? $", max source timestamp: {maxSourceTs.Value:yyyy-MM-dd HH:mm:ss} UTC"
                : "";
            await AddLog(db, run.Id, "Info",
                $"Upsert complete: {run.RowsRead} read, {run.RowsInserted} inserted, {run.RowsUpdated} updated{watermarkMsg}", ct);
        }

        await db.SaveChangesAsync(ct);
    }

    // ──────────────────────────────────────────────────────────
    // Watermark helper — max value of ChangeDateField in a DataTable
    // ──────────────────────────────────────────────────────────

    private static DateTime? GetMaxTimestamp(DataTable data, string? changeDateField)
    {
        if (string.IsNullOrEmpty(changeDateField) || !data.Columns.Contains(changeDateField))
            return null;

        DateTime? max = null;
        foreach (DataRow row in data.Rows)
        {
            var raw = row[changeDateField];
            if (raw is DBNull || raw is null) continue;

            DateTime parsed;
            if (raw is DateTime dt)
                parsed = dt;
            else if (!DateTime.TryParse(raw.ToString(), out parsed))
                continue;

            if (max is null || parsed > max.Value)
                max = parsed;
        }
        return max;
    }

    // ──────────────────────────────────────────────────────────
    // Data Fetching
    // ──────────────────────────────────────────────────────────

    private async Task<DataTable> FetchSourceDataAsync(Job job, SourceServer src,
    DateTime? from, DateTime? to, CancellationToken ct)
    {
        // Window bounds are computed in UTC, but the source's ChangeDateField is stored
        // in the source server's local timezone. Convert the bounds into that zone so the
        // comparison is like-for-like; otherwise the newest (offset-sized) slice of data
        // is always excluded from the window.
        from = ToSourceLocal(from, src);
        to = ToSourceLocal(to, src);

        return src.SourceType switch
        {
            SourceType.SqlServer => await FetchSqlServerDataAsync(job, src, from, to, ct),
            SourceType.Odbc => await FetchOdbcDataAsync(job, src, from, to, ct),
            SourceType.RestApi => await FetchRestApiDataAsync(job, src, from, to, ct),
            _ => new DataTable()
        };
    }

    private static DateTime? ToSourceLocal(DateTime? utc, SourceServer src)
    {
        if (utc is null) return null;
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(src.SourceTimeZone ?? "UTC"); }
        catch { tz = TimeZoneInfo.Utc; }
        var asUtc = DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(asUtc, tz); // Unspecified-kind local wall-clock
    }

    private async Task<DataTable> FetchSqlServerDataAsync(Job job, SourceServer src,
    DateTime? from, DateTime? to, CancellationToken ct)
    {
        var dt = new DataTable();
        string sql;

        if (!string.IsNullOrWhiteSpace(job.SourceQuery))
        {
            // Custom query — wrap as CTE for incremental filtering
            if (from.HasValue && to.HasValue && !string.IsNullOrEmpty(job.ChangeDateField))
            {
                sql = $"""
                WITH _src AS ({job.SourceQuery})
                SELECT * FROM _src
                WHERE [{job.ChangeDateField}] >= @from AND [{job.ChangeDateField}] < @to
                """;
            }
            else
            {
                sql = job.SourceQuery;
            }
        }
        else
        {
            // Original table-based path
            var fields = string.Join(", ", job.JobFields.Select(f => $"[{f.SourceFieldName}]"));
            sql = $"SELECT {fields} FROM {job.SourceTable}";
            if (from.HasValue && to.HasValue && !string.IsNullOrEmpty(job.ChangeDateField))
                sql += $" WHERE [{job.ChangeDateField}] >= @from AND [{job.ChangeDateField}] < @to";
        }

        await ExecuteWithRetryAsync(src.RetryCount, src.RetryDelaySeconds, async () =>
        {
            await using var conn = new SqlConnection(src.ConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, conn);
            if (from.HasValue) cmd.Parameters.AddWithValue("@from", from.Value);
            if (to.HasValue) cmd.Parameters.AddWithValue("@to", to.Value);
            using var adapter = new SqlDataAdapter(cmd);
            adapter.Fill(dt);
        }, null, null, ct);

        return dt;
    }

    private Task<DataTable> FetchOdbcDataAsync(Job job, SourceServer src,
    DateTime? from, DateTime? to, CancellationToken ct)
    {
        var dt = new DataTable();
        string sql;

        if (!string.IsNullOrWhiteSpace(job.SourceQuery))
        {
            if (from.HasValue && to.HasValue && !string.IsNullOrEmpty(job.ChangeDateField))
            {
                var fmt = src.SourceDateFormat;
                sql = $"SELECT * FROM ({job.SourceQuery}) _q " +
                      $"WHERE [{job.ChangeDateField}] >= '{from.Value.ToString(fmt)}' " +
                      $"AND [{job.ChangeDateField}] < '{to.Value.ToString(fmt)}'";
            }
            else
            {
                sql = job.SourceQuery;
            }
        }
        else
        {
            var fields = string.Join(", ", job.JobFields.Select(f => $"[{f.SourceFieldName}]"));
            sql = $"SELECT {fields} FROM {job.SourceTable}";

            if (from.HasValue && to.HasValue && !string.IsNullOrEmpty(job.ChangeDateField))
            {
                var fmt = src.SourceDateFormat;
                sql += $" WHERE [{job.ChangeDateField}] >= '{from.Value.ToString(fmt)}' " +
                       $"AND [{job.ChangeDateField}] < '{to.Value.ToString(fmt)}'";
            }
        }

        using var conn = new OdbcConnection(src.ConnectionString);
        conn.Open();
        using var cmd = new OdbcCommand(sql, conn) { CommandTimeout = src.OdbcCommandTimeout };
        using var adapter = new OdbcDataAdapter(cmd);
        adapter.Fill(dt);

        return Task.FromResult(dt);
    }

    private async Task<DataTable> FetchRestApiDataAsync(Job job, SourceServer src,
        DateTime? from, DateTime? to, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient();
        if (!string.IsNullOrEmpty(src.AuthHeader))
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", src.AuthHeader);

        var url = $"{src.BaseUrl!.TrimEnd('/')}/{job.SourceTable.TrimStart('/')}";
        if (from.HasValue && to.HasValue)
            url += $"?from={from:yyyy-MM-ddTHH:mm:ss}&to={to:yyyy-MM-ddTHH:mm:ss}";

        var json = await client.GetStringAsync(url, ct);
        var records = JsonConvert.DeserializeObject<List<Dictionary<string, object?>>>(json) ?? new();

        var dt = new DataTable();
        foreach (var f in job.JobFields)
            dt.Columns.Add(f.SourceFieldName);

        foreach (var record in records)
        {
            var row = dt.NewRow();
            foreach (var f in job.JobFields)
            {
                if (record.TryGetValue(f.SourceFieldName, out var val))
                    row[f.SourceFieldName] = val ?? DBNull.Value;
            }
            dt.Rows.Add(row);
        }

        return dt;
    }

    // ──────────────────────────────────────────────────────────
    // Retry Helper
    // ──────────────────────────────────────────────────────────

    private async Task ExecuteWithRetryAsync(int retryCount, int retryDelaySeconds,
    Func<Task> action, JobRun? run, ApplicationDbContext? db, CancellationToken ct,
    string context = "")
    {
        var attempts = 0;
        while (true)
        {
            try
            {
                await action();
                if (run != null) run.RetryAttempts = attempts;
                return;
            }
            catch (Exception ex)
            {
                attempts++;
                if (attempts > retryCount)
                {
                    if (run != null) run.RetryAttempts = attempts;
                    throw;
                }

                var contextPrefix = string.IsNullOrWhiteSpace(context) ? "" : $"[{context}] ";

                _log.LogWarning(ex,
                    "{Context}Attempt {Attempt} failed ({ExceptionType}: {Message}); retrying in {Delay}s",
                    contextPrefix, attempts, ex.GetType().Name, ex.Message, retryDelaySeconds);

                if (run != null && db != null)
                {
                    await AddLog(db, run.Id, "Warning",
                        $"{contextPrefix}Attempt {attempts} failed — {ex.GetType().Name}: {ex.Message}. Retrying in {retryDelaySeconds}s...", ct);
                    await db.SaveChangesAsync(ct);
                }

                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), ct);
            }
        }
    }

    // ──────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────

    private async Task AddLog(ApplicationDbContext db, long runId, string level, string message, CancellationToken ct)
    {
        db.JobRunLogs.Add(new JobRunLog
        {
            JobRunId = runId,
            Level = level,
            Message = message,
            LoggedAt = DateTime.UtcNow
        });
        // Batch saves happen at the caller level
    }

    private async Task SendJobAlertAsync(Job job, JobRun run, CancellationToken ct)
    {
        if (job.JobAlertOn == AlertOn.None || string.IsNullOrEmpty(job.AlertEmailAddresses))
            return;

        bool shouldSend = job.JobAlertOn == AlertOn.All
            || (job.JobAlertOn.HasFlag(AlertOn.Success) && run.Status == RunStatus.Succeeded)
            || (job.JobAlertOn.HasFlag(AlertOn.Failure) && run.Status == RunStatus.Failed)
            || (job.JobAlertOn.HasFlag(AlertOn.Error) && run.Status == RunStatus.PartialSuccess);

        if (!shouldSend) return;

        var to = job.AlertEmailAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var subject = $"[DataSync] Job '{job.Name}' — {run.Status}";
        var body = BuildJobEmailBody(job, run);

        await _email.SendAsync(to, subject, body);
    }

    // ──────────────────────────────────────────────────────────
    // Field reconciliation — strips JobFields whose source column
    // is absent from the fetched DataTable, logging each omission.
    // Also relaxes any NOT NULL destination columns that are now
    // orphaned, so they don't block future MERGE/INSERT.
    // ──────────────────────────────────────────────────────────

    private async Task<List<JobField>> ReconcileFieldsAsync(
        Job job, DataTable data, DestinationServer dest,
        ApplicationDbContext db, long runId, CancellationToken ct)
    {
        var active = new List<JobField>();
        var dropped = new List<JobField>();

        foreach (var f in job.JobFields)
        {
            if (data.Columns.Contains(f.SourceFieldName))
            {
                active.Add(f);
            }
            else
            {
                dropped.Add(f);
                await AddLog(db, runId, "Warn",
                    $"Source column [{f.SourceFieldName}] not present in source data — skipped for this run.", ct);
            }
        }

        // For any dropped field whose destination column is NOT NULL,
        // ALTER it to NULL so future runs don't get a constraint violation.
        if (dropped.Count > 0)
        {
            var parts = job.DestinationTable.Split('.');
            var schema = parts.Length == 2 ? parts[0] : "dbo";
            var table = parts.Length == 2 ? parts[1] : job.DestinationTable;
            var csb = new SqlConnectionStringBuilder(dest.ConnectionString)
            { InitialCatalog = job.DestinationDatabase };

            await using var conn = new SqlConnection(csb.ConnectionString);
            await conn.OpenAsync(ct);

            foreach (var f in dropped)
            {
                var destCol = f.DestinationFieldName ?? f.SourceFieldName;

                // Fetch the current nullability from INFORMATION_SCHEMA
                var nullableCmd = new SqlCommand("""
                SELECT IS_NULLABLE, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH,
                       NUMERIC_PRECISION, NUMERIC_SCALE
                FROM   INFORMATION_SCHEMA.COLUMNS
                WHERE  TABLE_SCHEMA = @s AND TABLE_NAME = @t AND COLUMN_NAME = @c
                """, conn);
                nullableCmd.Parameters.AddWithValue("@s", schema);
                nullableCmd.Parameters.AddWithValue("@t", table);
                nullableCmd.Parameters.AddWithValue("@c", destCol);

                await using var rdr = await nullableCmd.ExecuteReaderAsync(ct);
                if (!await rdr.ReadAsync(ct)) { await rdr.CloseAsync(); continue; }

                var isNullable = rdr.GetString(0) == "YES";
                var dataType = rdr.GetString(1).ToUpper();
                var charMaxLen = rdr.IsDBNull(2) ? (int?)null : rdr.GetInt32(2);
                var numPrec = rdr.IsDBNull(3) ? (int?)null : rdr.GetInt32(3);
                var numScale = rdr.IsDBNull(4) ? (int?)null : rdr.GetInt32(4);
                await rdr.CloseAsync();

                if (isNullable) continue;   // already nullable — nothing to do

                // Build the type string for ALTER COLUMN
                var typeDef = dataType switch
                {
                    "NVARCHAR" or "VARCHAR" or "CHAR" or "NCHAR" =>
                        charMaxLen is null or <= 0 or > 4000
                            ? $"{dataType}(MAX)"
                            : $"{dataType}({charMaxLen})",
                    "DECIMAL" or "NUMERIC" =>
                        $"{dataType}({numPrec ?? 18},{numScale ?? 6})",
                    _ => dataType
                };

                var alterSql = $"ALTER TABLE [{schema}].[{table}] ALTER COLUMN [{destCol}] {typeDef} NULL";
                await new SqlCommand(alterSql, conn).ExecuteNonQueryAsync(ct);
                await AddLog(db, runId, "Warn",
                    $"Destination column [{destCol}] relaxed to NULL (source field no longer present).", ct);
            }
        }

        await db.SaveChangesAsync(ct);
        return active;
    }

    private static string BuildJobEmailBody(Job job, JobRun run) => $"""
        <h2>Job: {job.Name}</h2>
        <p><strong>Status:</strong> {run.Status}</p>
        <p><strong>Started:</strong> {run.StartedAt:yyyy-MM-dd HH:mm:ss} UTC</p>
        <p><strong>Completed:</strong> {run.CompletedAt:yyyy-MM-dd HH:mm:ss} UTC</p>
        <hr/>
        <table border="1" cellpadding="4" cellspacing="0">
          <tr><td>Rows Read</td><td>{run.RowsRead:N0}</td></tr>
          <tr><td>Rows Inserted</td><td>{run.RowsInserted:N0}</td></tr>
          <tr><td>Rows Updated</td><td>{run.RowsUpdated:N0}</td></tr>
          <tr><td>Retry Attempts</td><td>{run.RetryAttempts}</td></tr>
        </table>
        {(run.ErrorMessage is not null ? $"<p style='color:red'><strong>Error:</strong> {run.ErrorMessage}</p>" : "")}
        """;
}
