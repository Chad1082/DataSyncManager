using DataSyncManager.Web.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DataSyncManager.Web.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    public DbSet<SourceServer> SourceServers => Set<SourceServer>();
    public DbSet<DestinationServer> DestinationServers => Set<DestinationServer>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobField> JobFields => Set<JobField>();
    public DbSet<ProjectRun> ProjectRuns => Set<ProjectRun>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<JobRunLog> JobRunLogs => Set<JobRunLog>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // ─── SourceServer ───────────────────────────
        builder.Entity<SourceServer>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.SourceType).HasConversion<string>();
        });

        // ─── DestinationServer ──────────────────────
        builder.Entity<DestinationServer>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
        });

        // ─── Project ────────────────────────────────
        builder.Entity<Project>(e =>
        {
            e.HasOne(p => p.SourceServer)
             .WithMany(s => s.Projects)
             .HasForeignKey(p => p.SourceServerId)
             .OnDelete(DeleteBehavior.Restrict);

            e.Property(x => x.ProjectAlertOn).HasConversion<int>();
        });

        // ─── Job ────────────────────────────────────
        builder.Entity<Job>(e =>
        {
            e.HasOne(j => j.Project)
             .WithMany(p => p.Jobs)
             .HasForeignKey(j => j.ProjectId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(j => j.DestinationServer)
             .WithMany(s => s.Jobs)
             .HasForeignKey(j => j.DestinationServerId)
             .OnDelete(DeleteBehavior.Restrict);

            e.Property(x => x.SyncMode).HasConversion<string>();
            e.Property(x => x.JobAlertOn).HasConversion<int>();
            e.Property(x => x.DaysPerBatch).HasPrecision(5, 2);
        });

        // ─── JobField ───────────────────────────────
        builder.Entity<JobField>(e =>
        {
            e.HasOne(f => f.Job)
             .WithMany(j => j.JobFields)
             .HasForeignKey(f => f.JobId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ─── ProjectRun ─────────────────────────────
        builder.Entity<ProjectRun>(e =>
        {
            e.HasOne(r => r.Project)
             .WithMany(p => p.ProjectRuns)
             .HasForeignKey(r => r.ProjectId)
             .OnDelete(DeleteBehavior.Cascade);

            e.Property(x => x.Status).HasConversion<string>();
        });

        // ─── JobRun ─────────────────────────────────
        builder.Entity<JobRun>(e =>
        {
            e.HasOne(r => r.Job)
             .WithMany(j => j.JobRuns)
             .HasForeignKey(r => r.JobId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(r => r.ProjectRun)
             .WithMany(pr => pr.JobRuns)
             .HasForeignKey(r => r.ProjectRunId)
             .OnDelete(DeleteBehavior.NoAction);

            e.Property(x => x.Status).HasConversion<string>();
        });

        // ─── JobRunLog ──────────────────────────────
        builder.Entity<JobRunLog>(e =>
        {
            e.HasOne(l => l.JobRun)
             .WithMany(r => r.Logs)
             .HasForeignKey(l => l.JobRunId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(l => l.JobRunId);
            e.HasIndex(l => l.LoggedAt);
        });
    }
}
