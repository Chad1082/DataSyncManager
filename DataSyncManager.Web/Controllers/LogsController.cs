using DataSyncManager.Web.Data;
using DataSyncManager.Web.Models;
using DataSyncManager.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DataSyncManager.Web.Controllers;

[Authorize]
public class LogsController : Controller
{
    private readonly ApplicationDbContext _db;

    public LogsController(ApplicationDbContext db) => _db = db;

    public async Task<IActionResult> Index(LogsFilterViewModel filter)
    {
        filter.Projects = await _db.Projects.OrderBy(p => p.Name).ToListAsync();
        filter.Jobs = filter.ProjectId.HasValue
            ? await _db.Jobs.Where(j => j.ProjectId == filter.ProjectId).OrderBy(j => j.Name).ToListAsync()
            : new List<Job>();

        var query = _db.ProjectRuns
            .Include(r => r.Project)
            .Include(r => r.JobRuns)
            .AsQueryable();

        if (filter.ProjectId.HasValue)
            query = query.Where(r => r.ProjectId == filter.ProjectId);

        if (filter.JobId.HasValue)
            query = query.Where(r => r.JobRuns.Any(j => j.JobId == filter.JobId));

        if (filter.Status.HasValue)
            query = query.Where(r => r.Status == filter.Status);

        if (filter.From.HasValue)
            query = query.Where(r => r.StartedAt >= filter.From.Value);

        if (filter.To.HasValue)
            query = query.Where(r => r.StartedAt <= filter.To.Value);

        var total = await query.CountAsync();

        var runs = await query
            .OrderByDescending(r => r.StartedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();

        var vm = new LogsViewModel
        {
            Filter = filter,
            TotalCount = total,
            ProjectRuns = runs.Select(r => new ProjectRunSummary
            {
                RunId = r.Id,
                ProjectName = r.Project?.Name ?? "?",
                Status = r.Status,
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt,
                JobCount = r.JobRuns.Count,
                FailedJobCount = r.JobRuns.Count(j => j.Status == RunStatus.Failed),
                TotalRowsRead = r.JobRuns.Sum(j => j.RowsRead),
                TotalRowsInserted = r.JobRuns.Sum(j => j.RowsInserted),
                TotalRowsUpdated = r.JobRuns.Sum(j => j.RowsUpdated)
            }).ToList()
        };

        return View(vm);
    }

    public async Task<IActionResult> ProjectRunDetails(long id)
    {
        var run = await _db.ProjectRuns
            .Include(r => r.Project)
            .Include(r => r.JobRuns.OrderBy(j => j.StartedAt))
                .ThenInclude(j => j.Job)
            .Include(r => r.JobRuns)
                .ThenInclude(j => j.Logs.OrderBy(l => l.LoggedAt))
            .FirstOrDefaultAsync(r => r.Id == id);

        if (run is null) return NotFound();
        return View(run);
    }

    // Export log as CSV
    public async Task<IActionResult> ExportCsv(long projectRunId)
    {
        var run = await _db.ProjectRuns
            .Include(r => r.Project)
            .Include(r => r.JobRuns.OrderBy(j => j.StartedAt))
                .ThenInclude(j => j.Job)
            .Include(r => r.JobRuns)
                .ThenInclude(j => j.Logs.OrderBy(l => l.LoggedAt))
            .FirstOrDefaultAsync(r => r.Id == projectRunId);

        if (run is null) return NotFound();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("JobName,LoggedAt,Level,Message");
        foreach (var jr in run.JobRuns)
            foreach (var l in jr.Logs)
                sb.AppendLine($"\"{jr.Job?.Name}\",\"{l.LoggedAt:u}\",\"{l.Level}\",\"{l.Message.Replace("\"", "\"\"")}\"");

        return File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()),
            "text/csv", $"run_{projectRunId}_{DateTime.Now:yyyyMMdd}.csv");
    }
}
