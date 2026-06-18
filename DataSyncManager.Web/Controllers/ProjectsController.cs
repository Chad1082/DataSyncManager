using DataSyncManager.Web.Data;
using DataSyncManager.Web.Jobs;
using DataSyncManager.Web.Models;
using DataSyncManager.Web.ViewModels;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DataSyncManager.Web.Controllers;

[Authorize]
public class ProjectsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IBackgroundJobClient _bgJobs;
    private readonly IRecurringJobManager _recurringJobs;

    public ProjectsController(
        ApplicationDbContext db,
        IBackgroundJobClient bgJobs,
        IRecurringJobManager recurringJobs)
    {
        _db = db;
        _bgJobs = bgJobs;
        _recurringJobs = recurringJobs;
    }

    // ── List ─────────────────────────────────────────

    public async Task<IActionResult> Index()
    {
        var projects = await _db.Projects
            .Include(p => p.SourceServer)
            .Include(p => p.Jobs)
            .Include(p => p.ProjectRuns)
            .OrderBy(p => p.Name)
            .ToListAsync();
        return View(projects);
    }

    // ── Details ──────────────────────────────────────

    public async Task<IActionResult> Details(int id)
    {
        var project = await _db.Projects
            .Include(p => p.SourceServer)
            .Include(p => p.Jobs.OrderBy(j => j.SortOrder))
                .ThenInclude(j => j.DestinationServer)
            .Include(p => p.ProjectRuns.OrderByDescending(r => r.StartedAt).Take(20))
                .ThenInclude(r => r.JobRuns)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (project is null) return NotFound();

        var lastRuns = await _db.JobRuns
            .Where(r => r.Job.ProjectId == id)
            .GroupBy(r => r.JobId)
            .Select(g => g.OrderByDescending(r => r.StartedAt).First())
            .ToListAsync();

        var vm = new ProjectDetailsViewModel
        {
            Project = project,
            RecentRuns = project.ProjectRuns.OrderByDescending(r => r.StartedAt).Take(10).ToList(),
            LastRun = project.ProjectRuns.OrderByDescending(r => r.StartedAt).FirstOrDefault(),
            Jobs = project.Jobs.Select(j => new JobSummaryItem
            {
                Id = j.Id,
                Name = j.Name,
                SourceTable = j.SourceTable,
                DestinationTable = j.DestinationTable,
                SyncMode = j.SyncMode,
                SortOrder = j.SortOrder,
                IsActive = j.IsActive,
                LastStatus = lastRuns.FirstOrDefault(r => r.JobId == j.Id)?.Status,
                LastRun = lastRuns.FirstOrDefault(r => r.JobId == j.Id)?.StartedAt
            }).ToList()
        };

        return View(vm);
    }

    // ── Create ───────────────────────────────────────

    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create()
    {
        var vm = new ProjectFormViewModel
        {
            AvailableSourceServers = await _db.SourceServers
                .Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync()
        };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(ProjectFormViewModel vm)
    {
        if (!ModelState.IsValid)
        {
            vm.AvailableSourceServers = await _db.SourceServers
                .Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
            return View(vm);
        }

        var project = new Project
        {
            Name = vm.Name,
            Description = vm.Description,
            SourceServerId = vm.SourceServerId,
            ScheduledStartTime = vm.ScheduledStartTime,
            CronExpression = vm.CronExpression,
            IsScheduled = vm.IsScheduled,
            ScheduleTimezone = vm.ScheduleTimezone,
            IsActive = vm.IsActive,
            ProjectAlertOn = vm.ProjectAlertOn,
            AlertEmailAddresses = vm.AlertEmailAddresses,
            CreatedByUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        if (project.IsScheduled && !string.IsNullOrEmpty(project.CronExpression))
            ScheduleRecurring(project);

        TempData["Success"] = "Project created.";
        return RedirectToAction(nameof(Details), new { id = project.Id });
    }

    // ── Edit ─────────────────────────────────────────

    [Authorize(Roles = "Admin,ReadOnly")]
    public async Task<IActionResult> Edit(int id)
    {
        var project = await _db.Projects.FindAsync(id);
        if (project is null) return NotFound();

        var vm = new ProjectFormViewModel
        {
            Id = project.Id,
            Name = project.Name,
            Description = project.Description,
            SourceServerId = project.SourceServerId,
            ScheduledStartTime = project.ScheduledStartTime,
            CronExpression = project.CronExpression,
            IsScheduled = project.IsScheduled,
            ScheduleTimezone = project.ScheduleTimezone,
            IsActive = project.IsActive,
            ProjectAlertOn = project.ProjectAlertOn,
            AlertEmailAddresses = project.AlertEmailAddresses,
            AvailableSourceServers = await _db.SourceServers
                .Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync()
        };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> Edit(int id, ProjectFormViewModel vm)
    {
        if (!ModelState.IsValid)
        {
            vm.AvailableSourceServers = await _db.SourceServers
                .Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
            return View(vm);
        }

        var project = await _db.Projects.FindAsync(id);
        if (project is null) return NotFound();

        project.Name = vm.Name;
        project.Description = vm.Description;
        project.SourceServerId = vm.SourceServerId;
        project.ScheduledStartTime = vm.ScheduledStartTime;
        project.CronExpression = vm.CronExpression;
        project.IsScheduled = vm.IsScheduled;
        project.ScheduleTimezone = vm.ScheduleTimezone;
        project.IsActive = vm.IsActive;
        project.ProjectAlertOn = vm.ProjectAlertOn;
        project.AlertEmailAddresses = vm.AlertEmailAddresses;
        project.UpdatedAt = DateTime.UtcNow;
        project.UpdatedByUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        await _db.SaveChangesAsync();

        // Update or remove Hangfire recurring job
        if (project.IsScheduled && !string.IsNullOrEmpty(project.CronExpression))
            ScheduleRecurring(project);
        else
            _recurringJobs.RemoveIfExists($"project-{project.Id}");

        TempData["Success"] = "Project updated.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ── Run Now ──────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> RunNow(int id)
    {
        var project = await _db.Projects.FindAsync(id);
        if (project is null) return NotFound();

        _bgJobs.Enqueue<ProjectRunner>(r => r.RunProjectAsync(id, CancellationToken.None));
        TempData["Success"] = $"Project '{project.Name}' queued for immediate execution.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ── Reorder Jobs ─────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> ReorderJobs(int id, [FromBody] List<int> jobIds)
    {
        var jobs = await _db.Jobs.Where(j => j.ProjectId == id).ToListAsync();
        for (int i = 0; i < jobIds.Count; i++)
        {
            var job = jobs.FirstOrDefault(j => j.Id == jobIds[i]);
            if (job is not null) job.SortOrder = i;
        }
        await _db.SaveChangesAsync();
        return Ok();
    }

    // ── Delete ───────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var project = await _db.Projects.FindAsync(id);
        if (project is null) return NotFound();

        _recurringJobs.RemoveIfExists($"project-{id}");
        _db.Projects.Remove(project);
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Project '{project.Name}' deleted.";
        return RedirectToAction(nameof(Index));
    }

    // ── Helpers ──────────────────────────────────────

    private void ScheduleRecurring(Project project)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(project.ScheduleTimezone ?? "UTC"); }
        catch { tz = TimeZoneInfo.Utc; }

        _recurringJobs.AddOrUpdate<ProjectRunner>(
            $"project-{project.Id}",
            r => r.RunProjectAsync(project.Id, CancellationToken.None),
            project.CronExpression,
            new RecurringJobOptions { TimeZone = tz });
    }
}
