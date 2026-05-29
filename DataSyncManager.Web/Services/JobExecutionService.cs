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

        // Truncate then bulk insert
        var parts = job.DestinationTable.Split('.');
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : job.DestinationTable;

        var csb = new SqlConnectionStringBuilder(dest.ConnectionString) { InitialCatalog = job.DestinationDatabase };

        await ExecuteWithRetryAsync(dest.RetryCount, dest.RetryDelaySeconds, async () =>
        {
            await using var conn = new SqlConnection(csb.ConnectionString);
            await conn.OpenAsync(ct);

            // Truncate
            await new SqlCommand($"TRUNCATE TABLE [{schema}].[{table}]", conn).ExecuteNonQueryAsync(ct);

            // Bulk copy
            using var bulk = new SqlBulkCopy(conn) { DestinationTableName = $"[{schema}].[{table}]" };
            foreach (var f in job.JobFields)
                bulk.ColumnMappings.Add(f.SourceFieldName, f.DestinationFieldName ?? f.SourceFieldName);

            await bulk.WriteToServerAsync(data, ct);
            run.RowsInserted = data.Rows.Count;
        }, run, db, ct);

        await AddLog(db, run.Id, "Info", $"Inserted {run.RowsInserted} rows", ct);
        await db.SaveChangesAsync(ct);
    }

    // ──────────────────────────────────────────────────────────
    // Upsert Mode (windowed by DaysPerBatch)
    // ──────────────────────────────────────────────────────────

    private async Task ExecuteUpsertAsync(Job job, SourceServer src, DestinationServer dest,
        JobRun run, ApplicationDbContext db, CancellationToken ct)
    {
        await AddLog(db, run.Id, "Info", "Mode: Upsert", ct);

        // Determine date window: fetch last successful run to determine from date
        var lastRun = await db.JobRuns
            .Where(r => r.JobId == job.Id && r.Status == RunStatus.Succeeded)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(ct);

        var windowStart = lastRun?.StartedAt.AddDays(-(double)job.DaysPerBatch) ?? DateTime.UtcNow.AddDays(-(double)job.DaysPerBatch);
        var windowEnd = DateTime.UtcNow;

        await AddLog(db, run.Id, "Info", $"Date window: {windowStart:u} → {windowEnd:u}", ct);
        await db.SaveChangesAsync(ct);

        var data = await FetchSourceDataAsync(job, src, windowStart, windowEnd, ct);
        run.RowsRead = data.Rows.Count;
        await AddLog(db, run.Id, "Info", $"Read {run.RowsRead} rows from source", ct);

        if (data.Rows.Count == 0)
        {
            await AddLog(db, run.Id, "Info", "No rows to upsert", ct);
            await db.SaveChangesAsync(ct);
            return;
        }

        var parts = job.DestinationTable.Split('.');
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : job.DestinationTable;
        var stagingTable = $"#Stg_{table}_{Guid.NewGuid():N}";

        var csb = new SqlConnectionStringBuilder(dest.ConnectionString) { InitialCatalog = job.DestinationDatabase };

        await ExecuteWithRetryAsync(dest.RetryCount, dest.RetryDelaySeconds, async () =>
        {
            await using var conn = new SqlConnection(csb.ConnectionString);
            await conn.OpenAsync(ct);

            // Create staging table
            var createStg = $"SELECT TOP 0 * INTO {stagingTable} FROM [{schema}].[{table}]";
            await new SqlCommand(createStg, conn).ExecuteNonQueryAsync(ct);

            // Bulk into staging
            using var bulk = new SqlBulkCopy(conn) { DestinationTableName = stagingTable };
            foreach (var f in job.JobFields)
                bulk.ColumnMappings.Add(f.SourceFieldName, f.DestinationFieldName ?? f.SourceFieldName);
            await bulk.WriteToServerAsync(data, ct);

            // Build MERGE
            var keys = (job.UniqueKeyFields ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var onClause = string.Join(" AND ", keys.Select(k => $"t.[{k.Trim()}] = s.[{k.Trim()}]"));
            var fieldList = job.JobFields.Select(f => f.DestinationFieldName ?? f.SourceFieldName).ToList();
            var setClause = string.Join(", ", fieldList.Where(f => !keys.Contains(f)).Select(f => $"t.[{f}] = s.[{f}]"));
            var insColList = string.Join(", ", fieldList.Select(f => $"[{f}]"));
            var insValList = string.Join(", ", fieldList.Select(f => $"s.[{f}]"));

            var merge = $"""
                MERGE [{schema}].[{table}] AS t
                USING {stagingTable} AS s ON {onClause}
                WHEN MATCHED THEN UPDATE SET {setClause}, [_SyncedAt] = GETUTCDATE()
                WHEN NOT MATCHED BY TARGET THEN INSERT ({insColList}, [_SyncedAt]) VALUES ({insValList}, GETUTCDATE())
                OUTPUT $action;
                """;

            await using var mergeCmd = new SqlCommand(merge, conn);
            await using var rdr = await mergeCmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                if (rdr.GetString(0) == "INSERT") run.RowsInserted++;
                else run.RowsUpdated++;
            }
        }, run, db, ct);

        await AddLog(db, run.Id, "Info",
            $"Upsert complete: {run.RowsInserted} inserted, {run.RowsUpdated} updated", ct);
        await db.SaveChangesAsync(ct);
    }

    // ──────────────────────────────────────────────────────────
    // Data Fetching
    // ──────────────────────────────────────────────────────────

    private async Task<DataTable> FetchSourceDataAsync(Job job, SourceServer src,
        DateTime? from, DateTime? to, CancellationToken ct)
    {
        return src.SourceType switch
        {
            SourceType.SqlServer => await FetchSqlServerDataAsync(job, src, from, to, ct),
            SourceType.Odbc => await FetchOdbcDataAsync(job, src, from, to, ct),
            SourceType.RestApi => await FetchRestApiDataAsync(job, src, from, to, ct),
            _ => new DataTable()
        };
    }

    private async Task<DataTable> FetchSqlServerDataAsync(Job job, SourceServer src,
        DateTime? from, DateTime? to, CancellationToken ct)
    {
        var dt = new DataTable();
        var fields = string.Join(", ", job.JobFields.Select(f => $"[{f.SourceFieldName}]"));
        var sql = $"SELECT {fields} FROM {job.SourceTable}";

        if (from.HasValue && to.HasValue && !string.IsNullOrEmpty(job.ChangeDateField))
            sql += $" WHERE [{job.ChangeDateField}] >= @from AND [{job.ChangeDateField}] < @to";

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
        var fields = string.Join(", ", job.JobFields.Select(f => $"[{f.SourceFieldName}]"));
        var sql = $"SELECT {fields} FROM {job.SourceTable}";

        if (from.HasValue && to.HasValue && !string.IsNullOrEmpty(job.ChangeDateField))
        {
            var fmt = src.SourceDateFormat;
            sql += $" WHERE [{job.ChangeDateField}] >= '{from.Value.ToString(fmt)}' AND [{job.ChangeDateField}] < '{to.Value.ToString(fmt)}'";
        }

        using var conn = new OdbcConnection(src.ConnectionString);
        conn.Open();
        using var cmd = new OdbcCommand(sql, conn);
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
        Func<Task> action, JobRun? run, ApplicationDbContext? db, CancellationToken ct)
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
                _log.LogWarning(ex, "Attempt {Attempt} failed; retrying in {Delay}s", attempts, retryDelaySeconds);
                if (run != null && db != null)
                {
                    await AddLog(db, run.Id, "Warning",
                        $"Attempt {attempts} failed ({ex.Message}). Retrying in {retryDelaySeconds}s...", ct);
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
