using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DataSyncManager.Web.Models;

// ─────────────────────────────────────────────
// Identity
// ─────────────────────────────────────────────

public class ApplicationUser : IdentityUser
{
    [MaxLength(100)]
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ─────────────────────────────────────────────
// Enumerations
// ─────────────────────────────────────────────

public enum SourceType
{
    SqlServer = 1,
    Odbc = 2,
    RestApi = 3
}

public enum SyncMode
{
    FullReplace = 1,
    Upsert = 2
}

public enum RunStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    PartialSuccess = 4,
    Cancelled = 5
}

public enum AlertOn
{
    None = 0,
    Success = 1,
    Failure = 2,
    Error = 4,
    All = 7  // bitmask: 1|2|4
}

// ─────────────────────────────────────────────
// Server Pool
// ─────────────────────────────────────────────

public class SourceServer
{
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    public SourceType SourceType { get; set; }

    [MaxLength(500)]
    public string? ConnectionString { get; set; }  // SQL Server / ODBC

    [MaxLength(500)]
    public string? BaseUrl { get; set; }            // REST API

    [MaxLength(500)]
    public string? AuthHeader { get; set; }         // REST API bearer / key header

    [MaxLength(200)]
    public string? DefaultDatabase { get; set; }

    public int RetryCount { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 30;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }

    public ICollection<Project> Projects { get; set; } = new List<Project>();
}

public class DestinationServer
{
    public int Id { get; set; }

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(500)]
    public string ConnectionString { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DefaultDatabase { get; set; }

    public int RetryCount { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 30;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }

    public ICollection<Job> Jobs { get; set; } = new List<Job>();
}

// ─────────────────────────────────────────────
// Projects & Jobs
// ─────────────────────────────────────────────

public class Project
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    public int SourceServerId { get; set; }
    public SourceServer SourceServer { get; set; } = null!;

    // Scheduling
    public TimeSpan? ScheduledStartTime { get; set; }
    public string? CronExpression { get; set; }          // Hangfire cron
    public string? HangfireJobId { get; set; }
    public bool IsScheduled { get; set; } = false;
    public bool IsActive { get; set; } = true;

    // Alerts
    public AlertOn ProjectAlertOn { get; set; } = AlertOn.None;
    [MaxLength(2000)]
    public string? AlertEmailAddresses { get; set; }     // comma-separated

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByUserId { get; set; }

    public ICollection<Job> Jobs { get; set; } = new List<Job>();
    public ICollection<ProjectRun> ProjectRuns { get; set; } = new List<ProjectRun>();
}

public class Job
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    // Source
    [Required, MaxLength(300)]
    public string SourceTable { get; set; } = string.Empty;   // schema.table or endpoint path

    // Destination
    public int DestinationServerId { get; set; }
    public DestinationServer DestinationServer { get; set; } = null!;

    [Required, MaxLength(300)]
    public string DestinationTable { get; set; } = string.Empty;

    [Required, MaxLength(150)]
    public string DestinationDatabase { get; set; } = string.Empty;

    public bool CreateDestinationTableIfMissing { get; set; } = true;

    // Sync behaviour
    public SyncMode SyncMode { get; set; } = SyncMode.Upsert;

    [MaxLength(500)]
    public string? UniqueKeyFields { get; set; }             // comma-separated for upsert

    [MaxLength(200)]
    public string? ChangeDateField { get; set; }             // date field for upsert change detection

    public decimal DaysPerBatch { get; set; } = 1;           // 0.25 – 31

    // Alerts
    public AlertOn JobAlertOn { get; set; } = AlertOn.None;

    [MaxLength(2000)]
    public string? AlertEmailAddresses { get; set; }

    // Order within project
    public int SortOrder { get; set; } = 0;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByUserId { get; set; }

    public ICollection<JobField> JobFields { get; set; } = new List<JobField>();
    public ICollection<JobRun> JobRuns { get; set; } = new List<JobRun>();
}

public class JobField
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public Job Job { get; set; } = null!;

    [Required, MaxLength(200)]
    public string SourceFieldName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DestinationFieldName { get; set; }    // null = same as source

    [MaxLength(100)]
    public string? DataType { get; set; }                // e.g. nvarchar, int, datetime2

    public int MaxLength { get; set; } = -1;             // -1 = MAX
    public bool IsNullable { get; set; } = true;
    public bool IsIncluded { get; set; } = true;
    public int SortOrder { get; set; } = 0;
}

// ─────────────────────────────────────────────
// Logging / Run History
// ─────────────────────────────────────────────

public class ProjectRun
{
    public long Id { get; set; }
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    public string? HangfireJobId { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string? Notes { get; set; }

    public ICollection<JobRun> JobRuns { get; set; } = new List<JobRun>();
}

public class JobRun
{
    public long Id { get; set; }
    public int JobId { get; set; }
    public Job Job { get; set; } = null!;

    public long? ProjectRunId { get; set; }
    public ProjectRun? ProjectRun { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Pending;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public long RowsRead { get; set; } = 0;
    public long RowsInserted { get; set; } = 0;
    public long RowsUpdated { get; set; } = 0;
    public long RowsDeleted { get; set; } = 0;
    public int RetryAttempts { get; set; } = 0;

    [MaxLength(4000)]
    public string? ErrorMessage { get; set; }

    public ICollection<JobRunLog> Logs { get; set; } = new List<JobRunLog>();
}

public class JobRunLog
{
    public long Id { get; set; }
    public long JobRunId { get; set; }
    public JobRun JobRun { get; set; } = null!;

    public DateTime LoggedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(20)]
    public string Level { get; set; } = "Info";    // Info | Warning | Error

    [MaxLength(4000)]
    public string Message { get; set; } = string.Empty;
}

// Models/Models.cs  — add after the JobRunLog class

// ─────────────────────────────────────────────
// Application Settings
// ─────────────────────────────────────────────

public class EmailSettings
{
    public int Id { get; set; }             // Always 1 — singleton row

    [MaxLength(250)]
    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    [MaxLength(250)]
    public string? SmtpUser { get; set; }

    [MaxLength(500)]
    public string? SmtpPass { get; set; }   // Stored as-is; same trust boundary as connection strings

    [Required, MaxLength(250)]
    public string FromAddress { get; set; } = "noreply@datasyncmanager.local";

    [MaxLength(150)]
    public string FromName { get; set; } = "DataSync Manager";

    public bool UseSsl { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(450)]
    public string? UpdatedByUserId { get; set; }
}