using DataSyncManager.Web.Data;
using DataSyncManager.Web.Models;
using DataSyncManager.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataSyncManager.Web.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly ApplicationDbContext _db;

    public DashboardController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);

        var projectRuns = await _db.ProjectRuns
            .Include(r => r.JobRuns)
            .Where(r => r.StartedAt >= cutoff)
            .OrderByDescending(r => r.StartedAt)
            .Take(200)
            .ToListAsync();

        var upcoming = await _db.Projects
            .Where(p => p.IsActive && p.IsScheduled)
            .OrderBy(p => p.ScheduledStartTime)
            .Take(10)
            .ToListAsync();

        var vm = new DashboardViewModel
        {
            TotalProjects = await _db.Projects.CountAsync(p => p.IsActive),
            TotalJobs = await _db.Jobs.CountAsync(j => j.IsActive),
            TotalSources = await _db.SourceServers.CountAsync(s => s.IsActive),
            TotalDestinations = await _db.DestinationServers.CountAsync(s => s.IsActive),

            RunsSucceeded = projectRuns.Count(r => r.Status == RunStatus.Succeeded),
            RunsFailed = projectRuns.Count(r => r.Status == RunStatus.Failed),
            RunsPartial = projectRuns.Count(r => r.Status == RunStatus.PartialSuccess),
            TotalRowsSynced = projectRuns.SelectMany(r => r.JobRuns)
                                         .Sum(j => j.RowsInserted + j.RowsUpdated),

            RecentProjectRuns = projectRuns
                .Take(10)
                .Select(r => new RecentRunItem
                {
                    RunId = r.Id,
                    ProjectName = _db.Projects.Find(r.ProjectId)?.Name ?? "?",
                    Status = r.Status,
                    StartedAt = r.StartedAt,
                    CompletedAt = r.CompletedAt,
                    JobCount = r.JobRuns.Count
                }).ToList(),

            UpcomingProjects = upcoming.Select(p => new UpcomingProjectItem
            {
                ProjectId = p.Id,
                ProjectName = p.Name,
                ScheduledTime = p.ScheduledStartTime,
                CronExpression = p.CronExpression
            }).ToList(),

            DailyStats = projectRuns
                .GroupBy(r => r.StartedAt.Date)
                .Select(g => new DailyRunStat
                {
                    Date = g.Key,
                    Succeeded = g.Count(r => r.Status == RunStatus.Succeeded),
                    Failed = g.Count(r => r.Status == RunStatus.Failed),
                    Partial = g.Count(r => r.Status == RunStatus.PartialSuccess)
                })
                .OrderBy(s => s.Date)
                .ToList(),

            LogStats = await BuildLogStatsAsync()
        };

        return View(vm);
    }

    // ── Log storage stats ─────────────────────────────────────────────────────

    private async Task<LogStorageStats> BuildLogStatsAsync()
    {
        var stats = new LogStorageStats();

        // EF counts — fast index seeks
        stats.ProjectRunCount = await _db.ProjectRuns.LongCountAsync();
        stats.JobRunCount = await _db.JobRuns.LongCountAsync();
        stats.JobRunLogCount = await _db.JobRunLogs.LongCountAsync();
        stats.OldestProjectRun = await _db.ProjectRuns
            .OrderBy(r => r.StartedAt)
            .Select(r => (DateTime?)r.StartedAt)
            .FirstOrDefaultAsync();

        // Raw SQL for SerilogEvents (not an EF entity) and table sizes
        var connStr = _db.Database.GetConnectionString()!;
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync();

        // SerilogEvents count + oldest timestamp.
        // Avoid referencing specific text columns (they vary by sink config) —
        // use sys.dm_db_partition_stats for size instead.
        const string serilogCountSql = """
            SELECT
                COUNT_BIG(*)     AS EventCount,
                MIN([TimeStamp]) AS Oldest
            FROM [dbo].[SerilogEvents]
            """;

        await using (var cmd = new SqlCommand(serilogCountSql, conn))
        await using (var rdr = await cmd.ExecuteReaderAsync())
        {
            if (await rdr.ReadAsync())
            {
                stats.SerilogEventCount = rdr.IsDBNull(0) ? 0 : rdr.GetInt64(0);
                stats.OldestSerilogEvent = rdr.IsDBNull(1) ? null : rdr.GetDateTime(1);
            }
        }

        // On-disk sizes for all four tables via sys.dm_db_partition_stats
        // (column-name agnostic — works regardless of sink schema variant)
        const string sizeSql = """
            SELECT
                o.name                              AS TableName,
                SUM(ps.reserved_page_count * 8)     AS SizeKb
            FROM sys.dm_db_partition_stats ps
            INNER JOIN sys.objects o ON o.object_id = ps.object_id
            WHERE o.name IN ('ProjectRuns', 'JobRuns', 'JobRunLogs', 'SerilogEvents')
              AND ps.index_id IN (0, 1)
            GROUP BY o.name
            """;

        await using (var cmd = new SqlCommand(sizeSql, conn))
        await using (var rdr = await cmd.ExecuteReaderAsync())
        {
            while (await rdr.ReadAsync())
            {
                var name = rdr.GetString(0);
                var sizeKb = rdr.IsDBNull(1) ? 0L : rdr.GetInt64(1);
                switch (name)
                {
                    case "SerilogEvents": stats.SerilogSizeKb = sizeKb; break;
                    default: stats.RunTablesSizeKb += sizeKb; break;
                }
            }
        }

        // Next scheduled purge — Hangfire fires daily at 02:30 UTC
        var now = DateTime.UtcNow;
        var nextPurge = new DateTime(now.Year, now.Month, now.Day, 2, 30, 0, DateTimeKind.Utc);
        if (nextPurge <= now) nextPurge = nextPurge.AddDays(1);
        stats.NextPurgeUtc = nextPurge;

        return stats;
    }
}