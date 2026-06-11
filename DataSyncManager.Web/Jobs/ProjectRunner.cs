using DataSyncManager.Web.Data;
using DataSyncManager.Web.Models;
using DataSyncManager.Web.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace DataSyncManager.Web.Jobs;

public class ProjectRunner
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly IJobExecutionService _jobExecution;
    private readonly IEmailService _email;
    private readonly ILogger<ProjectRunner> _log;

    public ProjectRunner(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IJobExecutionService jobExecution,
        IEmailService email,
        ILogger<ProjectRunner> log)
    {
        _dbFactory = dbFactory;
        _jobExecution = jobExecution;
        _email = email;
        _log = log;
    }

    [AutomaticRetry(Attempts = 0)] // Handled internally
    public async Task RunProjectAsync(int projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var project = await db.Projects
            .Include(p => p.Jobs.Where(j => j.IsActive).OrderBy(j => j.SortOrder))
            .Include(p => p.SourceServer)
            .FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new InvalidOperationException($"Project {projectId} not found");

        // Create project run record
        var projectRun = new ProjectRun
        {
            ProjectId = projectId,
            Status = RunStatus.Running,
            StartedAt = DateTime.UtcNow,
            HangfireJobId = Hangfire.JobStorage.Current?.ToString()
        };
        db.ProjectRuns.Add(projectRun);
        await db.SaveChangesAsync(ct);

        _log.LogInformation("Project '{Name}' run {RunId} started", project.Name, projectRun.Id);

        var anyFailed = false;
        var anyError = false;

        // Execute jobs CONSECUTIVELY
        foreach (var job in project.Jobs)
        {
            if (ct.IsCancellationRequested) break;

            _log.LogInformation("  Running job '{JobName}' ({JobId})", job.Name, job.Id);
            var jobRun = await _jobExecution.ExecuteJobAsync(job.Id, projectRun.Id, ct);

            if (jobRun.Status == RunStatus.Failed) anyFailed = true;
            if (jobRun.Status == RunStatus.PartialSuccess) anyError = true;
        }

        // Reload the run from DB so we can update it
        await using var db2 = await _dbFactory.CreateDbContextAsync(ct);
        var runToUpdate = await db2.ProjectRuns.FindAsync(new object[] { projectRun.Id }, ct)!;
        runToUpdate!.CompletedAt = DateTime.UtcNow;
        runToUpdate.Status = anyFailed ? RunStatus.Failed
                           : anyError ? RunStatus.PartialSuccess
                           : RunStatus.Succeeded;

        if (ct.IsCancellationRequested)
        {
            runToUpdate.Status = RunStatus.Cancelled;
            runToUpdate.Notes = "Cancelled by user or system";
        }

        await db2.SaveChangesAsync(ct);

        _log.LogInformation("Project '{Name}' run {RunId} finished: {Status}",
            project.Name, projectRun.Id, runToUpdate.Status);

        // Send project-level alerts
        await SendProjectAlertAsync(project, runToUpdate, ct);
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task RunSingleJobAsync(int jobId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var job = await db.Jobs
            .Include(j => j.Project).ThenInclude(p => p.SourceServer)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new InvalidOperationException($"Job {jobId} not found");

        var projectRun = new ProjectRun
        {
            ProjectId = job.ProjectId,
            Status = RunStatus.Running,
            StartedAt = DateTime.UtcNow,
            Notes = $"On-demand: {job.Name}"
        };
        db.ProjectRuns.Add(projectRun);
        await db.SaveChangesAsync(ct);

        _log.LogInformation("Single job '{JobName}' ({JobId}) run {RunId} started", job.Name, jobId, projectRun.Id);

        var jobRun = await _jobExecution.ExecuteJobAsync(jobId, projectRun.Id, ct);

        await using var db2 = await _dbFactory.CreateDbContextAsync(ct);
        var runToUpdate = await db2.ProjectRuns.FindAsync(new object[] { projectRun.Id }, ct);
        runToUpdate!.CompletedAt = DateTime.UtcNow;
        runToUpdate.Status = ct.IsCancellationRequested ? RunStatus.Cancelled
                           : jobRun.Status == RunStatus.Failed ? RunStatus.Failed
                           : jobRun.Status == RunStatus.PartialSuccess ? RunStatus.PartialSuccess
                           : RunStatus.Succeeded;
        if (ct.IsCancellationRequested) runToUpdate.Notes = $"On-demand: {job.Name} — Cancelled";
        await db2.SaveChangesAsync(ct);

        _log.LogInformation("Single job '{JobName}' run {RunId} finished: {Status}", job.Name, projectRun.Id, runToUpdate.Status);
    }

    private async Task SendProjectAlertAsync(Project project, ProjectRun run, CancellationToken ct)
    {
        if (project.ProjectAlertOn == AlertOn.None || string.IsNullOrEmpty(project.AlertEmailAddresses))
            return;

        bool shouldSend = project.ProjectAlertOn == AlertOn.All
            || (project.ProjectAlertOn.HasFlag(AlertOn.Success) && run.Status == RunStatus.Succeeded)
            || (project.ProjectAlertOn.HasFlag(AlertOn.Failure) && run.Status == RunStatus.Failed)
            || (project.ProjectAlertOn.HasFlag(AlertOn.Error) && run.Status == RunStatus.PartialSuccess);

        if (!shouldSend) return;

        var to = project.AlertEmailAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var subject = $"[DataSync] Project '{project.Name}' — {run.Status}";

        var duration = run.CompletedAt.HasValue
            ? (run.CompletedAt.Value - run.StartedAt).ToString(@"hh\:mm\:ss")
            : "N/A";

        var body = $"""
            <h2>Project: {project.Name}</h2>
            <p><strong>Status:</strong> {run.Status}</p>
            <p><strong>Started:</strong> {run.StartedAt:yyyy-MM-dd HH:mm:ss} UTC</p>
            <p><strong>Completed:</strong> {run.CompletedAt:yyyy-MM-dd HH:mm:ss} UTC</p>
            <p><strong>Duration:</strong> {duration}</p>
            {(run.Notes is not null ? $"<p><em>{run.Notes}</em></p>" : "")}
            """;

        await _email.SendAsync(to, subject, body);
    }
}
