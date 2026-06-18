using DataSyncManager.Web.Data;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataSyncManager.Web.Jobs;

/// <summary>
/// Hangfire job that enforces a 90-day retention window across all log stores:
///   - SerilogEvents   (SQL sink)
///   - JobRunLog       (per-job audit rows)
///   - JobRun          (job-level run records, cascade-deletes JobRunLog)
///   - ProjectRun      (project-level run records, cascade-deletes JobRun)
/// Runs nightly at 02:30 UTC. Deletes in capped batches to avoid lock escalation.
/// </summary>
public class LogPurgeService
{
    private const int RetentionDays = 90;
    private const int BatchSize = 2000;   // rows per DELETE batch

    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ILogger<LogPurgeService> _log;

    public LogPurgeService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ILogger<LogPurgeService> log)
    {
        _dbFactory = dbFactory;
        _log = log;
    }

    [AutomaticRetry(Attempts = 2)]
    public async Task PurgeOldLogsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        _log.LogInformation("LogPurgeService: starting purge, cutoff = {Cutoff:u}", cutoff);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var conn = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("No connection string found.");

        int serilogRows = await PurgeSerilogEventsAsync(conn, cutoff, ct);
        int projectRows = await PurgeProjectRunsAsync(db, cutoff, ct);

        _log.LogInformation(
            "LogPurgeService: complete — SerilogEvents -{SerilogRows}, ProjectRuns (+ children) -{ProjectRows}",
            serilogRows, projectRows);
    }

    // ── SerilogEvents ─────────────────────────────────────────────────────────
    // This table is managed entirely by the Serilog sink (no EF model),
    // so we delete directly via ADO.NET in batches to keep transactions small.

    private async Task<int> PurgeSerilogEventsAsync(
        string connStr, DateTime cutoff, CancellationToken ct)
    {
        const string sql = """
            DELETE TOP (@batch) FROM [dbo].[SerilogEvents]
            WHERE [TimeStamp] < @cutoff
            """;

        int total = 0;
        try
        {
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync(ct);

            int deleted;
            do
            {
                await using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@batch", BatchSize);
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                deleted = await cmd.ExecuteNonQueryAsync(ct);
                total += deleted;
            }
            while (deleted == BatchSize && !ct.IsCancellationRequested);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "LogPurgeService: error purging SerilogEvents");
        }

        _log.LogDebug("LogPurgeService: removed {Count} SerilogEvents rows", total);
        return total;
    }

    // ── ProjectRun / JobRun / JobRunLog ───────────────────────────────────────
    // EF cascade-delete handles JobRun and JobRunLog automatically when a
    // ProjectRun is deleted, provided the FK cascade is configured (which it is
    // via EF conventions). We batch by taking IDs to avoid huge single deletes.

    private async Task<int> PurgeProjectRunsAsync(
        ApplicationDbContext db, DateTime cutoff, CancellationToken ct)
    {
        int total = 0;
        try
        {
            List<long> ids;
            do
            {
                ids = await db.ProjectRuns
                    .Where(r => r.StartedAt < cutoff)
                    .OrderBy(r => r.StartedAt)
                    .Select(r => r.Id)
                    .Take(BatchSize)
                    .ToListAsync(ct);

                if (ids.Count == 0) break;

                // Load the tracked entities so EF cascade fires correctly
                var runs = await db.ProjectRuns
                    .Include(r => r.JobRuns)
                        .ThenInclude(jr => jr.Logs)
                    .Where(r => ids.Contains(r.Id))
                    .ToListAsync(ct);

                db.ProjectRuns.RemoveRange(runs);
                await db.SaveChangesAsync(ct);
                total += runs.Count;
            }
            while (ids.Count == BatchSize && !ct.IsCancellationRequested);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "LogPurgeService: error purging ProjectRuns");
        }

        _log.LogDebug("LogPurgeService: removed {Count} ProjectRun records (+ children)", total);
        return total;
    }
}