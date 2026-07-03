using DataSyncManager.Web.Models;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace DataSyncManager.Web.ViewModels;

// ─────────────────────────────────────────────
// Account
// ─────────────────────────────────────────────

public class LoginViewModel
{
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required, DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

public class RegisterViewModel
{
    [Required, EmailAddress, Display(Name = "Email")]
    public string Email { get; set; } = string.Empty;

    [Required, Display(Name = "Display Name")]
    public string DisplayName { get; set; } = string.Empty;

    [Required]
    public string Role { get; set; } = "Viewer";

    [Required, DataType(DataType.Password), MinLength(8)]
    public string Password { get; set; } = string.Empty;

    [DataType(DataType.Password), Compare(nameof(Password))]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class UserListViewModel
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; }
    public string Role { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

// ─────────────────────────────────────────────
// Dashboard
// ─────────────────────────────────────────────

public class DashboardViewModel
{
    public int TotalProjects { get; set; }
    public int TotalJobs { get; set; }
    public int TotalSources { get; set; }
    public int TotalDestinations { get; set; }

    // Last 30 days
    public int RunsSucceeded { get; set; }
    public int RunsFailed { get; set; }
    public int RunsPartial { get; set; }
    public long TotalRowsSynced { get; set; }

    public List<RecentRunItem> RecentProjectRuns { get; set; } = new();
    public List<UpcomingProjectItem> UpcomingProjects { get; set; } = new();
    public List<DailyRunStat> DailyStats { get; set; } = new();
    public LogStorageStats LogStats { get; set; } = new();
}

public class LogStorageStats
{
    // SerilogEvents table
    public long SerilogEventCount { get; set; }
    public long SerilogSizeKb { get; set; }

    // ProjectRun / JobRun / JobRunLog
    public long ProjectRunCount { get; set; }
    public long JobRunCount { get; set; }
    public long JobRunLogCount { get; set; }
    public long RunTablesSizeKb { get; set; }

    // Oldest records still retained
    public DateTime? OldestProjectRun { get; set; }
    public DateTime? OldestSerilogEvent { get; set; }

    // Next purge (filled by controller)
    public DateTime? NextPurgeUtc { get; set; }
}

public class RecentRunItem
{
    public long RunId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public RunStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int JobCount { get; set; }
}

public class UpcomingProjectItem
{
    public int ProjectId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public TimeSpan? ScheduledTime { get; set; }
    public string? CronExpression { get; set; }
}

public class DailyRunStat
{
    public DateTime Date { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Partial { get; set; }
}

// ─────────────────────────────────────────────
// Servers
// ─────────────────────────────────────────────

public class SourceServerViewModel
{
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public SourceType SourceType { get; set; }

    [MaxLength(500)]
    public string? ConnectionString { get; set; }

    [MaxLength(500)]
    public string? BaseUrl { get; set; }

    [MaxLength(500)]
    public string? AuthHeader { get; set; }

    [MaxLength(200)]
    public string? DefaultDatabase { get; set; }

    [Range(1, 10)]
    public int RetryCount { get; set; } = 3;

    [Range(5, 300)]
    public int RetryDelaySeconds { get; set; } = 30;

    [MaxLength(50)]
    [Display(Name = "Date Filter Format")]
    public string SourceDateFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";

    [Range(0, 3600)]
    [Display(Name = "Command Timeout (seconds)")]
    public int OdbcCommandTimeout { get; set; } = 0;

    public bool IsActive { get; set; } = true;
}

public class DestinationServerViewModel
{
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    // Built server-side from the builder fields — not entered directly
    [MaxLength(500)]
    public string ConnectionString { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DefaultDatabase { get; set; }

    [Range(1, 10)]
    public int RetryCount { get; set; } = 3;

    [Range(5, 300)]
    public int RetryDelaySeconds { get; set; } = 30;

    public bool IsActive { get; set; } = true;

    // Connection builder fields
    [Required, MaxLength(500), Display(Name = "Server Address")]
    public string ServerAddress { get; set; } = string.Empty;

    public bool UseWindowsAuth { get; set; } = true;

    [MaxLength(200), Display(Name = "Username")]
    public string? SqlUsername { get; set; }

    [Display(Name = "Password")]
    public string? SqlPassword { get; set; }
}

// ─────────────────────────────────────────────
// Projects
// ─────────────────────────────────────────────

public class ProjectFormViewModel
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    [Required]
    public int SourceServerId { get; set; }

    public TimeSpan? ScheduledStartTime { get; set; }

    [Display(Name = "Cron Expression")]
    public string? CronExpression { get; set; }

    [MaxLength(100)]
    [Display(Name = "Schedule Timezone")]
    public string ScheduleTimezone { get; set; } = "UTC";

    public bool IsScheduled { get; set; }
    public bool IsActive { get; set; } = true;

    public AlertOn ProjectAlertOn { get; set; } = AlertOn.None;

    [MaxLength(2000), Display(Name = "Alert Email Addresses (comma-separated)")]
    public string? AlertEmailAddresses { get; set; }

    // For dropdowns
    public List<SourceServer> AvailableSourceServers { get; set; } = new();
    public List<SelectListItem> SourceServerSelectList =>
        AvailableSourceServers.Select(s => new SelectListItem(s.Name, s.Id.ToString())).ToList();

    public List<SelectListItem> AvailableTimezones =>
        TimeZoneInfo.GetSystemTimeZones()
            .OrderBy(tz => tz.BaseUtcOffset).ThenBy(tz => tz.DisplayName)
            .Select(tz => new SelectListItem(tz.DisplayName, tz.Id))
            .ToList();

    // Convenience alias for AlertOn
    public AlertOn AlertOn => ProjectAlertOn;
}

public class ProjectDetailsViewModel
{
    public Project Project { get; set; } = null!;
    public List<JobSummaryItem> Jobs { get; set; } = new();
    public List<ProjectRun> RecentRuns { get; set; } = new();
    public ProjectRun? LastRun { get; set; }
}

public class JobSummaryItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SourceTable { get; set; } = string.Empty;
    public string DestinationTable { get; set; } = string.Empty;
    public SyncMode SyncMode { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public RunStatus? LastStatus { get; set; }
    public DateTime? LastRun { get; set; }
}

// ─────────────────────────────────────────────
// Jobs
// ─────────────────────────────────────────────

public class JobFormViewModel
{
    public int Id { get; set; }

    [Required]
    public int ProjectId { get; set; }

    public string ProjectName { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    // Source
    [Required]
    public string SourceTable { get; set; } = string.Empty;

    public string? SourceQuery { get; set; }

    // Convenience: true when this job uses a custom query
    public bool IsCustomQuery => !string.IsNullOrWhiteSpace(SourceQuery);

    // Destination
    [Required]
    public int DestinationServerId { get; set; }

    // Populated automatically from the selected DestinationServer.DefaultDatabase
    public string DestinationDatabase { get; set; } = string.Empty;

    [Required]
    public string DestinationTable { get; set; } = string.Empty;

    public bool CreateDestinationTableIfMissing { get; set; } = true;

    // Sync
    public SyncMode SyncMode { get; set; } = SyncMode.Upsert;

    public string? UniqueKeyFields { get; set; }

    public string? ChangeDateField { get; set; }

    [Range(0.04, 31)]
    public decimal DaysPerBatch { get; set; } = 1;

    [Display(Name = "Sync Start Date")]
    public DateTime? SyncStartDate { get; set; }

    // Alerts
    public AlertOn JobAlertOn { get; set; } = AlertOn.None;
    // Convenience alias used in views
    public AlertOn AlertOn => JobAlertOn;

    [MaxLength(2000)]
    public string? AlertEmailAddresses { get; set; }

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    // Field selection (posted as JSON)
    public string SelectedFieldsJson { get; set; } = "[]";

    // For dropdowns / UI
    public List<DestinationServer> AvailableDestinations { get; set; } = new();
    public List<SelectListItem> AvailableDestinationServers =>
        AvailableDestinations.Select(d => new SelectListItem(d.Name, d.Id.ToString())).ToList();
    public SourceServer? ProjectSourceServer { get; set; }

    // Convenience: source server id for Edit view JS
    public int SourceServerId => ProjectSourceServer?.Id ?? 0;

    [Range(0, 1440, ErrorMessage = "Overlap must be between 0 and 1440 minutes.")]
    [Display(Name = "Overlap Buffer (minutes)")]
    public int SyncOverlapMinutes { get; set; } = 5;

    // Convenience alias: Job name = Name
    public string JobName
    {
        get => Name;
        set => Name = value;
    }
}

// ─────────────────────────────────────────────
// Logs
// ─────────────────────────────────────────────

public class LogsFilterViewModel
{
    public int? ProjectId { get; set; }
    public int? JobId { get; set; }
    public RunStatus? Status { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;

    public List<Project> Projects { get; set; } = new();
    public List<Job> Jobs { get; set; } = new();
}

public class LogsViewModel
{
    public LogsFilterViewModel Filter { get; set; } = new();
    public List<ProjectRunSummary> ProjectRuns { get; set; } = new();
    public int TotalCount { get; set; }
}

public class ProjectRunSummary
{
    public long RunId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public RunStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int JobCount { get; set; }
    public int FailedJobCount { get; set; }
    public long TotalRowsRead { get; set; }
    public long TotalRowsInserted { get; set; }
    public long TotalRowsUpdated { get; set; }
}

public class ChangePasswordViewModel
{
    [Required, DataType(DataType.Password), Display(Name = "Current Password")]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required, DataType(DataType.Password), MinLength(8), Display(Name = "New Password")]
    public string NewPassword { get; set; } = string.Empty;

    [DataType(DataType.Password), Compare(nameof(NewPassword)), Display(Name = "Confirm New Password")]
    public string ConfirmNewPassword { get; set; } = string.Empty;
}

public class EmailTemplateViewModel
{
    [Required]
    public string HtmlTemplate { get; set; } = string.Empty;

    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByDisplayName { get; set; }
}

public class EmailSettingsViewModel
{
    [Required, MaxLength(250)]
    [Display(Name = "SMTP Host")]
    public string SmtpHost { get; set; } = string.Empty;

    [Required, Range(1, 65535)]
    [Display(Name = "SMTP Port")]
    public int SmtpPort { get; set; } = 587;

    [MaxLength(250)]
    [Display(Name = "SMTP Username")]
    public string? SmtpUser { get; set; }

    // Never pre-populated — blank = keep existing
    [MaxLength(500)]
    [Display(Name = "SMTP Password")]
    public string? SmtpPass { get; set; }

    [Required, MaxLength(250), EmailAddress]
    [Display(Name = "From Address")]
    public string FromAddress { get; set; } = "noreply@datasyncmanager.local";

    [MaxLength(150)]
    [Display(Name = "From Name")]
    public string FromName { get; set; } = "DataSync Manager";

    [Display(Name = "Use STARTTLS")]
    public bool UseSsl { get; set; } = true;

    // Test email helper
    [EmailAddress]
    public string? TestEmailAddress { get; set; }

    // Display-only metadata
    public bool HasExistingPassword { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByDisplayName { get; set; }
}