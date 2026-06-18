using DataSyncManager.Web.Data;
using DataSyncManager.Web.Jobs;
using DataSyncManager.Web.Models;
using DataSyncManager.Web.Services;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Events;

// ─────────────────────────────────────────────────────────────────────────────
// Bootstrap Serilog early so startup errors are captured
// ─────────────────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ─── Serilog ──────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, cfg) =>
    {
        var conn = ctx.Configuration.GetConnectionString("DefaultConnection")!;
        var logDir = ctx.Configuration["Logging:FileDirectory"] ?? "logs";

        cfg.ReadFrom.Configuration(ctx.Configuration)
           .ReadFrom.Services(services)
           .Enrich.FromLogContext()
           .WriteTo.Console()
           .WriteTo.File(
               path: Path.Combine(logDir, "datasync-.log"),
               rollingInterval: RollingInterval.Month, 
               retainedFileCountLimit: 6,
               outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
           .WriteTo.MSSqlServer(
               connectionString: conn,
               sinkOptions: new Serilog.Sinks.MSSqlServer.MSSqlServerSinkOptions
               {
                   TableName = "SerilogEvents",
                   AutoCreateSqlTable = true
               });
    });

    // ─── Database ─────────────────────────────────────────────
    var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not configured.");

    builder.Services.AddDbContext<ApplicationDbContext>(opts =>
        opts.UseSqlServer(connStr));

    builder.Services.AddDbContextFactory<ApplicationDbContext>(opts =>
        opts.UseSqlServer(connStr), ServiceLifetime.Scoped);

    // ─── Identity ─────────────────────────────────────────────
    builder.Services.AddIdentity<ApplicationUser, IdentityRole>(opts =>
    {
        opts.Password.RequireDigit = true;
        opts.Password.RequiredLength = 8;
        opts.Password.RequireUppercase = true;
        opts.Password.RequireNonAlphanumeric = true;
        opts.Lockout.MaxFailedAccessAttempts = 5;
        opts.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        opts.SignIn.RequireConfirmedEmail = false;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

    builder.Services.ConfigureApplicationCookie(opts =>
    {
        opts.LoginPath = "/Account/Login";
        opts.AccessDeniedPath = "/Account/AccessDenied";
        opts.ExpireTimeSpan = TimeSpan.FromHours(8);
        opts.SlidingExpiration = true;
    });

    // ─── Hangfire ─────────────────────────────────────────────
    builder.Services.AddHangfire(cfg => cfg
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(connStr, new SqlServerStorageOptions
        {
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            QueuePollInterval = TimeSpan.Zero,
            UseRecommendedIsolationLevel = true,
            DisableGlobalLocks = true
        }));

    builder.Services.AddHangfireServer(opts =>
    {
        opts.WorkerCount = 2;  // Keep low to avoid overwhelming sources
        opts.Queues = new[] { "default" };
    });

    // ─── Application Services ─────────────────────────────────
    builder.Services.AddHttpClient();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<IEmailSettingsService, EmailSettingsService>();
    builder.Services.AddScoped<IEmailTemplateService, EmailTemplateService>();
    builder.Services.AddScoped<IEmailService, EmailService>();
    builder.Services.AddScoped<ISchemaService, SchemaService>();
    builder.Services.AddScoped<IJobExecutionService, JobExecutionService>();
    builder.Services.AddScoped<ProjectRunner>();
    builder.Services.AddScoped<LogPurgeService>();

    // ─── MVC ──────────────────────────────────────────────────
    builder.Services.AddControllersWithViews()
        .AddRazorRuntimeCompilation()
        .AddNewtonsoftJson();

    // ─── Authorization Policies ───────────────────────────────
    builder.Services.AddAuthorization(opts =>
    {
        opts.AddPolicy("ViewerOrAbove", p => p.RequireRole("Viewer", "ReadOnly", "Admin"));
        opts.AddPolicy("ReadOnlyOrAbove", p => p.RequireRole("ReadOnly", "Admin"));
        opts.AddPolicy("AdminOnly", p => p.RequireRole("Admin"));
    });

    // ─────────────────────────────────────────────────────────
    var app = builder.Build();
    // ─────────────────────────────────────────────────────────

    // Auto-migrate and seed on startup
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        await DbSeeder.SeedAsync(userManager, roleManager, config);
    }

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Home/Error");
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles();
    app.UseSerilogRequestLogging();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();

    // Hangfire dashboard — Admins only
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = new[]
        {
            new HangfireAuthorizationFilter(
                app.Services.GetRequiredService<IHttpContextAccessor>())
        }
    });

    // Schedule nightly log purge at 02:30 UTC
    RecurringJob.AddOrUpdate<LogPurgeService>(
        recurringJobId: "log-purge-nightly",
        methodCall: svc => svc.PurgeOldLogsAsync(CancellationToken.None),
        cronExpression: "30 2 * * *",           // 02:30 UTC every day
        options: new RecurringJobOptions
        {
            TimeZone = TimeZoneInfo.Utc
        });


    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Dashboard}/{action=Index}/{id?}");

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application startup failed");
}
finally
{
    await Log.CloseAndFlushAsync();
}

// ─────────────────────────────────────────────────────────────────────────────
// Hangfire auth filter — restricts dashboard to Admin role
// ─────────────────────────────────────────────────────────────────────────────
public class HangfireAuthorizationFilter : Hangfire.Dashboard.IDashboardAuthorizationFilter
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HangfireAuthorizationFilter(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public bool Authorize(Hangfire.Dashboard.DashboardContext context)
    {
        var http = _httpContextAccessor.HttpContext;
        return http?.User.Identity?.IsAuthenticated == true
            && http.User.IsInRole("Admin");
    }
}
