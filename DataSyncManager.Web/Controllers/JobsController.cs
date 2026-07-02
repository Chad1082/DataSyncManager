using DataSyncManager.Web.Data;
using DataSyncManager.Web.Jobs;
using DataSyncManager.Web.Models;
using DataSyncManager.Web.Services;
using DataSyncManager.Web.ViewModels;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace DataSyncManager.Web.Controllers;

[Authorize]
public class JobsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ISchemaService _schema;
    private readonly IBackgroundJobClient _bgJobs;

    public JobsController(ApplicationDbContext db, ISchemaService schema, IBackgroundJobClient bgJobs)
    {
        _db = db;
        _schema = schema;
        _bgJobs = bgJobs;
    }

    // ── Create ───────────────────────────────────────

    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(int projectId)
    {
        var project = await _db.Projects.Include(p => p.SourceServer).FirstOrDefaultAsync(p => p.Id == projectId);
        if (project is null) return NotFound();

        var vm = new JobFormViewModel
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            ProjectSourceServer = project.SourceServer,
            AvailableDestinations = await _db.DestinationServers
                .Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync()
        };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(JobFormViewModel vm)
    {
        // Remove server/project navigation props from validation
        ModelState.Remove("ProjectSourceServer");
        ModelState.Remove("AvailableDestinations");
        ModelState.Remove("ProjectName");
        ModelState.Remove("DestinationDatabase");



        // Auto-fill DestinationDatabase from the selected server's DefaultDatabase
        if (string.IsNullOrEmpty(vm.DestinationDatabase) && vm.DestinationServerId > 0)
        {
            var dest = await _db.DestinationServers.FindAsync(vm.DestinationServerId);
            vm.DestinationDatabase = dest?.DefaultDatabase ?? "";
        }
        if (string.IsNullOrEmpty(vm.DestinationDatabase))
            ModelState.AddModelError("DestinationDatabase", "The selected destination server has no default database configured.");

        if (vm.SyncMode == SyncMode.Upsert && !vm.SyncStartDate.HasValue)
            ModelState.AddModelError(nameof(vm.SyncStartDate), "A start date is required for Upsert jobs.");

        // Near the top of each POST action, before ModelState.IsValid check:
        if (!string.IsNullOrWhiteSpace(vm.SourceQuery))
            ModelState.Remove(nameof(vm.SourceTable)); // SourceTable not needed when using a query

        if (!ModelState.IsValid)
        {
            await PopulateVmAsync(vm);
            return View(vm);
        }

        var job = new Job
        {
            ProjectId = vm.ProjectId,
            Name = vm.Name,
            Description = vm.Description,
            SourceTable = vm.SourceTable,
            DestinationServerId = vm.DestinationServerId,
            DestinationDatabase = vm.DestinationDatabase,
            DestinationTable = vm.DestinationTable,
            CreateDestinationTableIfMissing = vm.CreateDestinationTableIfMissing,
            SyncMode = vm.SyncMode,
            UniqueKeyFields = vm.UniqueKeyFields,
            ChangeDateField = vm.ChangeDateField,
            DaysPerBatch = vm.DaysPerBatch,
            SyncStartDate = vm.SyncStartDate.HasValue
                ? DateTime.SpecifyKind(vm.SyncStartDate.Value, DateTimeKind.Utc)
                : null,
            JobAlertOn = vm.JobAlertOn,
            AlertEmailAddresses = vm.AlertEmailAddresses,
            SortOrder = vm.SortOrder,
            IsActive = vm.IsActive,
            CreatedByUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            SyncOverlapMinutes = vm.SyncOverlapMinutes
        };
        // When mapping vm → job entity, add:
        job.SourceQuery = string.IsNullOrWhiteSpace(vm.SourceQuery) ? null : vm.SourceQuery.Trim();
        // If using a query, SourceTable stores a user-friendly label (optional, or derive one):
        if (!string.IsNullOrWhiteSpace(vm.SourceQuery) && string.IsNullOrWhiteSpace(vm.SourceTable))
            job.SourceTable = "(Custom Query)";

        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        // Persist selected fields
        await SaveFieldsAsync(job.Id, vm.SelectedFieldsJson);

        TempData["Success"] = "Job created.";
        return RedirectToAction("Details", "Projects", new { id = vm.ProjectId });
    }

    // ── Edit ─────────────────────────────────────────

    [Authorize(Roles = "Admin,ReadOnly")]
    public async Task<IActionResult> Edit(int id)
    {
        var job = await _db.Jobs
            .Include(j => j.JobFields.OrderBy(f => f.SortOrder))
            .Include(j => j.Project).ThenInclude(p => p.SourceServer)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (job is null) return NotFound();

        var vm = new JobFormViewModel
        {
            Id = job.Id,
            ProjectId = job.ProjectId,
            ProjectName = job.Project.Name,
            ProjectSourceServer = job.Project.SourceServer,
            Name = job.Name,
            Description = job.Description,
            SourceTable = job.SourceTable,
            DestinationServerId = job.DestinationServerId,
            DestinationDatabase = job.DestinationDatabase,
            DestinationTable = job.DestinationTable,
            CreateDestinationTableIfMissing = job.CreateDestinationTableIfMissing,
            SyncMode = job.SyncMode,
            UniqueKeyFields = job.UniqueKeyFields,
            ChangeDateField = job.ChangeDateField,
            DaysPerBatch = job.DaysPerBatch,
            SyncStartDate = job.SyncStartDate,
            JobAlertOn = job.JobAlertOn,
            AlertEmailAddresses = job.AlertEmailAddresses,
            SortOrder = job.SortOrder,
            IsActive = job.IsActive,
            SyncOverlapMinutes = job.SyncOverlapMinutes,
            SourceQuery = job.SourceQuery,
            SelectedFieldsJson = JsonConvert.SerializeObject(job.JobFields.Select(f => new
            {
                sourceFieldName = f.SourceFieldName,
                destinationFieldName = f.DestinationFieldName,
                dataType = f.DataType,
                maxLength = f.MaxLength,
                isNullable = f.IsNullable,
                isIncluded = f.IsIncluded
            })),
            AvailableDestinations = await _db.DestinationServers
                .Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync()
        };
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> Edit(int id, JobFormViewModel vm)
    {
        ModelState.Remove("ProjectSourceServer");
        ModelState.Remove("AvailableDestinations");
        ModelState.Remove("ProjectName");
        ModelState.Remove("DestinationDatabase");

        // Auto-fill DestinationDatabase from the selected server's DefaultDatabase
        if (string.IsNullOrEmpty(vm.DestinationDatabase) && vm.DestinationServerId > 0)
        {
            var dest = await _db.DestinationServers.FindAsync(vm.DestinationServerId);
            vm.DestinationDatabase = dest?.DefaultDatabase ?? "";
        }
        if (string.IsNullOrEmpty(vm.DestinationDatabase))
            ModelState.AddModelError("DestinationDatabase", "The selected destination server has no default database configured.");

        if (!string.IsNullOrWhiteSpace(vm.SourceQuery))
            ModelState.Remove(nameof(vm.SourceTable)); // SourceTable not needed when using a query

        if (!ModelState.IsValid)
        {
            await PopulateVmAsync(vm);
            return View(vm);
        }

        var job = await _db.Jobs.Include(j => j.JobFields).FirstOrDefaultAsync(j => j.Id == id);
        if (job is null) return NotFound();

        job.Name = vm.Name;
        job.Description = vm.Description;
        job.SourceTable = vm.SourceTable;
        job.DestinationServerId = vm.DestinationServerId;
        job.DestinationDatabase = vm.DestinationDatabase;
        job.DestinationTable = vm.DestinationTable;
        job.CreateDestinationTableIfMissing = vm.CreateDestinationTableIfMissing;
        job.SyncMode = vm.SyncMode;
        job.UniqueKeyFields = vm.UniqueKeyFields;
        job.ChangeDateField = vm.ChangeDateField;
        job.DaysPerBatch = vm.DaysPerBatch;
        job.SyncStartDate = vm.SyncStartDate.HasValue
            ? DateTime.SpecifyKind(vm.SyncStartDate.Value, DateTimeKind.Utc)
            : null;
        job.JobAlertOn = vm.JobAlertOn;
        job.AlertEmailAddresses = vm.AlertEmailAddresses;
        job.SortOrder = vm.SortOrder;
        job.IsActive = vm.IsActive;
        job.UpdatedAt = DateTime.UtcNow;
        job.UpdatedByUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        job.SyncOverlapMinutes = vm.SyncOverlapMinutes;

        // When mapping vm → job entity, add:
        job.SourceQuery = string.IsNullOrWhiteSpace(vm.SourceQuery) ? null : vm.SourceQuery.Trim();
        // If using a query, SourceTable stores a user-friendly label (optional, or derive one):
        if (!string.IsNullOrWhiteSpace(vm.SourceQuery) && string.IsNullOrWhiteSpace(vm.SourceTable))
            job.SourceTable = "(Custom Query)";

        // Replace fields
        _db.JobFields.RemoveRange(job.JobFields);
        await _db.SaveChangesAsync();
        await SaveFieldsAsync(job.Id, vm.SelectedFieldsJson);

        TempData["Success"] = "Job updated.";
        return RedirectToAction("Details", "Projects", new { id = job.ProjectId });
    }

    // ── Run Now ──────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> RunNow(int id)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(j => j.Id == id);
        if (job is null) return NotFound();

        _bgJobs.Enqueue<ProjectRunner>(r => r.RunSingleJobAsync(id, CancellationToken.None));
        TempData["Success"] = $"Job '{job.Name}' queued for immediate execution.";
        return RedirectToAction("Details", "Projects", new { id = job.ProjectId });
    }

    // ── Delete ───────────────────────────────────────

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int id)
    {
        var job = await _db.Jobs.FindAsync(id);
        if (job is null) return NotFound();

        var projectId = job.ProjectId;
        job.IsActive = false;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Job deactivated.";
        return RedirectToAction("Details", "Projects", new { id = projectId });
    }

    // ── Job Run Details ───────────────────────────────

    public async Task<IActionResult> RunDetails(long runId)
    {
        var run = await _db.JobRuns
            .Include(r => r.Job).ThenInclude(j => j.Project)
            .Include(r => r.Logs.OrderBy(l => l.LoggedAt))
            .FirstOrDefaultAsync(r => r.Id == runId);

        if (run is null) return NotFound();
        return View(run);
    }

    // ── Helpers ──────────────────────────────────────

    private async Task PopulateVmAsync(JobFormViewModel vm)
    {
        var project = await _db.Projects.Include(p => p.SourceServer)
            .FirstOrDefaultAsync(p => p.Id == vm.ProjectId);
        vm.ProjectName = project?.Name ?? "";
        vm.ProjectSourceServer = project?.SourceServer;
        vm.AvailableDestinations = await _db.DestinationServers
            .Where(s => s.IsActive).OrderBy(s => s.Name).ToListAsync();
    }

    private async Task SaveFieldsAsync(int jobId, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            var fields = JsonConvert.DeserializeObject<List<JobFieldDto>>(json) ?? new();
            int order = 0;
            foreach (var f in fields)
            {
                _db.JobFields.Add(new JobField
                {
                    JobId = jobId,
                    SourceFieldName = f.SourceFieldName,
                    DestinationFieldName = f.DestinationFieldName,
                    DataType = f.DataType,
                    MaxLength = f.MaxLength,
                    IsNullable = f.IsNullable,
                    IsIncluded = f.IsIncluded,
                    SortOrder = order++
                });
            }
            await _db.SaveChangesAsync();
        }
        catch { /* ignore JSON parse errors */ }
    }

    private class JobFieldDto
    {
        public string SourceFieldName { get; set; } = string.Empty;
        public string? DestinationFieldName { get; set; }
        public string? DataType { get; set; }
        public int MaxLength { get; set; } = -1;
        public bool IsNullable { get; set; } = true;
        public bool IsIncluded { get; set; } = true;
    }
}
