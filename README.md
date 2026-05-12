# DataSync Manager

An ASP.NET Core 8 MVC application for scheduling, executing, and monitoring data synchronisation jobs between source systems (SQL Server, ODBC, REST API) and a SQL Server reporting database.

---

## Features

- **Multi-source connectors** — SQL Server, ODBC, and REST API sources
- **Projects and Jobs** — group related sync jobs; jobs run consecutively within a project
- **Sync modes** — Full Replace (truncate + bulk insert) or Upsert (staging table + MERGE)
- **DDL automation** — destination tables are created or altered automatically
- **Windowed upsert** — incremental sync using a configurable days-per-batch window
- **Hangfire scheduling** — cron-based recurring runs, manual Run Now, job queue dashboard
- **Retry logic** — configurable retry count and delay per server
- **Email alerts** — per-project and per-job alerts on success, failure, or error
- **Full logging** — Serilog to rolling file and SQL Server; in-app run history with per-job log viewer
- **Role-based access** — Admin, ReadOnly, Viewer roles via ASP.NET Core Identity
- **Dashboard** — 30-day stats, Chart.js daily run chart, upcoming schedules, recent runs

---

## Prerequisites

- .NET 8 SDK
- SQL Server 2016+ (or SQL Server Express/LocalDB for development)
- SMTP server for email alerts (optional — alerts are silently skipped if unconfigured)

---

## Getting Started

### 1. Clone and configure

```bash
git clone <your-repo-url>
cd DataSyncManager
```

Edit `DataSyncManager.Web/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=.;Database=DataSyncManager;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "Email": {
    "SmtpHost": "smtp.example.com",
    "SmtpPort": 587,
    "SmtpUser": "alerts@example.com",
    "SmtpPassword": "your-password",
    "FromAddress": "alerts@example.com",
    "FromName": "DataSync Manager",
    "UseSsl": true
  },
  "Seeding": {
    "AdminEmail": "admin@example.com",
    "AdminPassword": "Change_me_123!"
  },
  "Logging": {
    "FileDirectory": "C:\\Logs\\DataSyncManager"
  }
}
```

### 2. Run EF Core migrations

```bash
cd DataSyncManager.Web
dotnet ef migrations add InitialCreate
dotnet ef database update
```

> The database and all tables are created automatically on first `database update`. The application also auto-migrates and seeds on startup.

### 3. Run the application

```bash
dotnet run
```

Navigate to `https://localhost:5001` (or the port shown in the console).

Log in with the admin credentials from `appsettings.json` → `Seeding`.

---

## First Steps After Login

1. **Add a Source Server** → Servers → Sources → Add Source
2. **Add a Destination Server** → Servers → Destinations → Add Destination  
   Use the **Test** button on each card to verify connectivity.
3. **Create a Project** → Projects → New Project  
   Select a source server, set a cron schedule (optional), configure alert addresses.
4. **Add Jobs to the Project** → open the project → Add Job  
   Pick the source table, select fields, choose sync mode, set the destination table name.
5. **Run Now** → Projects list → Run Now button, or open the project and click Run Now.
6. **View Logs** → Logs menu, or open a project run for a per-job breakdown.

---

## Hangfire Dashboard

The Hangfire dashboard is available at `/hangfire` (Admin users only).  
It shows queued, running, succeeded, and failed background jobs, and lets you trigger retries manually.

---

## REST API Source Convention

For REST API sources, the target API must expose three endpoints:

| Endpoint | Returns |
|---|---|
| `GET /meta/tables` | `["table1", "table2", ...]` |
| `GET /meta/tables/{name}/columns` | `[{"name":"col","dataType":"nvarchar","maxLength":200,"isNullable":true}, ...]` |
| `GET /meta/ping` | Any 2xx response |

Data queries are handled by adding `?from=<iso-date>&to=<iso-date>` parameters to `GET /{tableName}`.  
The response must be a JSON array of objects matching the column schema.

---

## Project Structure

```
DataSyncManager/
├── DataSyncManager.sln
└── DataSyncManager.Web/
    ├── Controllers/
    │   ├── AccountController.cs       # Auth, user management
    │   ├── DashboardController.cs     # Home dashboard
    │   ├── JobsController.cs          # Job CRUD and run details
    │   ├── LogsController.cs          # Run history, CSV export
    │   ├── ProjectsController.cs      # Project CRUD, run now, reorder
    │   └── ServersController.cs       # Source/destination CRUD, AJAX schema
    ├── Data/
    │   ├── ApplicationDbContext.cs    # EF Core DbContext
    │   └── DbSeeder.cs                # Role and admin user seeding
    ├── Jobs/
    │   └── ProjectRunner.cs           # Hangfire background job
    ├── Models/
    │   └── Models.cs                  # All entity models and enums
    ├── Services/
    │   ├── EmailService.cs            # MailKit SMTP
    │   ├── JobExecutionService.cs     # DDL, sync logic, retry
    │   └── SchemaService.cs           # Schema discovery for all source types
    ├── ViewModels/
    │   └── ViewModels.cs              # All view models
    ├── Views/                         # Razor views (MVC)
    ├── wwwroot/
    │   ├── css/site.css
    │   └── js/site.js
    ├── appsettings.json
    └── Program.cs
```

---

## Roles

| Role | Permissions |
|---|---|
| **Admin** | Full access — create/edit/delete everything, run jobs, manage users |
| **ReadOnly** | View all configuration, projects, jobs, and logs; cannot make changes |
| **Viewer** | Dashboard and logs only |

Roles are seeded automatically. The first Admin is created from `appsettings.json → Seeding`.  
Additional users are created via **Account → Users → Register User**.

---

## Sync Modes

### Full Replace
1. Truncates the destination table
2. Bulk copies all source rows using `SqlBulkCopy`
3. Fast for small-to-medium tables; no key required

### Upsert
1. Reads rows from source where `ChangeDateField >= windowStart`
2. Bulk inserts into a staging table (`#Stg_{table}_{guid}`)
3. Runs a `MERGE` statement against the destination using `UniqueKeyFields` as the key
4. Counts inserts vs updates via `OUTPUT`
5. `windowStart` = last successful run end time (falls back to `now - DaysPerBatch` if no prior run)

---

## Destination Table DDL

Tables are created automatically with:
- `_SyncId BIGINT IDENTITY(1,1) PRIMARY KEY`
- `_SyncedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()`
- One column per included source field (mapped data types)

If the table already exists, missing columns are `ALTER TABLE ... ADD` appended. Existing columns are not modified.

---

## Email Alerts

Configure at both project and job level. The bitmask options are:

| Value | Meaning |
|---|---|
| None | No alerts |
| Success | Alert when a run completes with no errors |
| Failure | Alert when a run fails entirely |
| Error | Alert on any job-level error |
| All | All of the above |

SMTP settings in `appsettings.json → Email`. If `SmtpHost` is blank, alerts are silently skipped.

---

## Cron Expression Examples

| Expression | Meaning |
|---|---|
| `0 2 * * *` | Daily at 02:00 |
| `0 */6 * * *` | Every 6 hours |
| `0 8 * * 1` | Mondays at 08:00 |
| `30 7 * * 1-5` | Weekdays at 07:30 |
| `0 0 1 * *` | First of each month at midnight |

Use [crontab.guru](https://crontab.guru) to build and verify expressions.

---

## Troubleshooting

**"Connection failed" on Test button**  
Check the connection string format and ensure the SQL Server instance is reachable from the application server. For ODBC sources, confirm the DSN is configured on the application server.

**Jobs not running on schedule**  
Check the Hangfire dashboard at `/hangfire`. Ensure the application is running (Hangfire is in-process). Check the Serilog file log for errors during startup.

**Destination table columns not appearing after source schema change**  
The DDL auto-alter only adds columns — it does not rename or drop them. Add the new field in the Job's field list and re-run; the column will be added to the destination table.

**Email alerts not arriving**  
Verify SMTP settings in `appsettings.json`. Check the Serilog log for `EmailService` errors. Ensure the SMTP server allows relay from the application server's IP.
