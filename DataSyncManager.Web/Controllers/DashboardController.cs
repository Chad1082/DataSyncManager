using DataSyncManager.Web.Data;
using DataSyncManager.Web.Models;
using DataSyncManager.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
                .ToList()
        };

        return View(vm);
    }
}
