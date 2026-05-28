# DataSyncManager - Project Context for Claude Code

## Project Overview

A C# ASP.NET web application that manages scheduled synchronisation of data from source databases
(initially ServiceNow via ODBC) into a SQL Server reporting database. The application provides a
web UI for configuring sync jobs, managing schedules, monitoring execution history, and configuring
email notifications.

## Technology Stack

- **Application**: C# ASP.NET (Razor Pages / MVC), Bootstrap
- **Database**: SQL Server 2016
- **Source connectivity**: ServiceNow via ODBC (`System.Data.Odbc`)
- **ORM / data access**: Direct ADO.NET (`SqlConnection`, `SqlBulkCopy`)
- **Scheduling**: Cron expressions (5-field standard format)
- **Notifications**: SMTP HTML email
- **Target environment**: Windows, scheduled via Windows Task Scheduler (original CLI) / internal
  scheduler (web app)

## Architecture

```
Source DB (ServiceNow ODBC)
    ↓
[C# Sync Engine] ← reads config from SQL Server ETL schema
    ↓
[Date-range batches] ← configurable DaysPerBatch per table (default: 2)
    ↓
[SQL Server temp tables] ← SqlBulkCopy bulk insert
    ↓
[MERGE operation] ← upsert keyed on sys_id
    ↓
[ETL metadata update] ← LastSyncTimestamp updated on success
    ↓
[HTML email notification] ← configurable per table and recipient
```

The web UI wraps this engine, allowing jobs to be created, edited, scheduled, and monitored
without touching config files or SQL directly.

## Database Schema

All ETL metadata lives in the `ETL` schema on SQL Server.

### Core Tables

**ETL.SyncConfiguration** — one row per sync job
- `ConfigID` (PK)
- `ServiceNowTableName` — source table
- `TargetTableName` — destination table in SQL Server
- `LastSyncTimestamp` — last successful sync; set to NULL to force full reload
- `DaysPerBatch` INT DEFAULT 2 — days of data pulled per ODBC query
- `ColumnSelectionMode` — `'All'` or `'Specified'`
- `SendEmailNotifications` BIT
- `IsActive` BIT
- `CronExpression` — 5-field cron string (e.g. `0 19 * * *`)

**ETL.ColumnSelection** — column filter when ColumnSelectionMode = 'Specified'
- `ConfigID` (FK)
- `ColumnName`
- `ColumnOrder`
- `IsRequired`
- Note: `sys_id` and `updated_on` are always included automatically

**ETL.SyncExecutionLog** — audit trail
- `ExecutionID` (PK)
- `ConfigID` (FK)
- `ExecutionStartTime` / `ExecutionEndTime`
- `Status` — `'Running'` | `'Success'` | `'Failed'` | `'PartialSuccess'`
- `RowsProcessed` / `RowsInserted` / `RowsUpdated`
- `ErrorMessage` / `ErrorDetails`

**ETL.EmailRecipient**
- `EmailRecipientID` (PK)
- `RecipientName`
- `EmailAddress`
- `IsActive` BIT

**ETL.EmailNotificationRule**
- `ConfigID` (FK) — NULL = global rule
- `EmailRecipientID` (FK)
- `NotifyOnSuccess` / `NotifyOnFailure` / `NotifyOnPartialSuccess` BIT
- `IsActive` BIT
- Table-specific rules take precedence over global rules

### Key Views

- `ETL.vw_SyncStatus` — current status, hours since last sync, last row counts, any errors
- `ETL.vw_ColumnConfiguration` — column selection settings per table

### Key Stored Procedures

- `ETL.usp_StartSyncExecution` — initialises a log row, returns `@ExecutionID` output param
- `ETL.usp_CompleteSyncExecution` — writes final status, counts, timestamps atomically

## Source Data Conventions

Every ServiceNow table being synced has:
- `sys_id` — GUID, used as the merge/upsert key
- `updated_on` — datetime, used for incremental sync filtering

## Sync Engine Rules

- Incremental by default: pulls records where `updated_on > LastSyncTimestamp`
- Batches date ranges into `DaysPerBatch`-sized chunks to avoid ODBC connection crashes
- Uses `SqlBulkCopy` into a temp table, then a `MERGE` statement keyed on `sys_id`
- Auto-creates target tables if they don't exist (schema inferred from ODBC metadata)
- Always syncs `sys_id` and `updated_on` regardless of `ColumnSelectionMode`
- Force full reload: set `LastSyncTimestamp = NULL` in `ETL.SyncConfiguration`

## Web UI Features

- **Jobs (Projects)** — Create / Edit sync job configuration including source table, destination
  table, column selection, batch size, schedule, and email settings
- **Schedule builder** — friendly UI that generates a 5-field cron expression; supports hourly,
  daily, weekdays, weekly, monthly, and custom modes
- **Servers** — configurable source and destination server connections; supports loading available
  tables and columns dynamically from a selected server/database
- **Email settings** — configurable SMTP server settings; manage recipients and notification rules
- **Execution history** — log viewer showing status, row counts, duration, and error details
- **Sync status dashboard** — `ETL.vw_SyncStatus` surfaced in the UI

## Common SQL Reference

```sql
-- Check all table sync status
SELECT * FROM ETL.vw_SyncStatus;

-- Recent execution history
SELECT TOP 50 * FROM ETL.SyncExecutionLog ORDER BY ExecutionStartTime DESC;

-- Add a new table to sync
INSERT INTO ETL.SyncConfiguration (ServiceNowTableName, TargetTableName, IsActive)
VALUES ('sys_user', 'User', 1);

-- Force full reload of a specific table
UPDATE ETL.SyncConfiguration
SET LastSyncTimestamp = NULL
WHERE ServiceNowTableName = 'incident';

-- Set a large table to 1-day batches
UPDATE ETL.SyncConfiguration
SET DaysPerBatch = 1
WHERE ServiceNowTableName = 'sys_audit';
```

## Known Constraints

- ODBC connection to ServiceNow is fragile under large query volumes — always use batching
- SQL Server version is 2016; avoid syntax or features introduced after 2016
- Target environment is Windows; file paths and scheduling are Windows-native
- Email notifications use HTML formatting built via `StringBuilder` with inline styles
  (no external CSS frameworks in emails)


## Commands

**Build:**
```powershell
dotnet build
```

**Run (development):**
```powershell
dotnet run --project DataSyncManager.Web
# Launches at https://localhost:5001
# Hangfire dashboard at /hangfire (Admin role required)
```

**EF Core migrations:**
```powershell
cd DataSyncManager.Web
dotnet ef migrations add <MigrationName>
dotnet ef database update
```
Migrations are auto-applied on startup (`db.Database.MigrateAsync()` in `Program.cs`).

There are no test or lint projects in this solution.

## Architecture

### Layers

- **Controllers** (`Controllers/`) — 6 controllers handling HTTP + UI: Dashboard, Account, Projects, Jobs, Servers, Logs, Settings
- **Services** (`Services/`) — Business logic: `JobExecutionService`, `SchemaService`, `EmailService`, `EmailSettingsService`
- **Jobs** (`Jobs/ProjectRunner.cs`) — Hangfire background job that executes a project's jobs consecutively (never in parallel)
- **Data** (`Data/`) — `ApplicationDbContext` (inherits `IdentityDbContext<ApplicationUser>`), `DbSeeder`, `DesignTimeDbContextFactory`
- **Models** (`Models/Models.cs`) — All entity models in one file
- **ViewModels** (`ViewModels/ViewModels.cs`) — All view models in one file

### Core Data Model

```
SourceServer ──< Project ──< Job ──< JobField
DestinationServer ──< Job
Project ──< ProjectRun ──< JobRun ──< JobRunLog
```

Cascade delete: Jobs cascade-delete when a Project is deleted. Server deletion is restricted if Jobs reference it.

### Sync Execution Flow

1. **Hangfire cron** triggers `ProjectRunner`, or user clicks "Run Now"
2. `ProjectRunner` executes each job in the project **consecutively**
3. `JobExecutionService` per job:
   - **DDL phase**: auto-creates/alters destination table (adds `_SyncId` identity + `_SyncedAt` columns)
   - **Full Replace**: `TRUNCATE` then `SqlBulkCopy`
   - **Upsert**: staging table + `MERGE` with windowed change detection by timestamp
4. Retry logic is configurable per `SourceServer`
5. Project-level status aggregates: Succeeded / Failed / PartialSuccess / Cancelled

### Key Enums

- `SourceType`: `SqlServer=1`, `Odbc=2`, `RestApi=3`
- `SyncMode`: `FullReplace=1`, `Upsert=2`
- `RunStatus`: `Pending=0`, `Running=1`, `Succeeded=2`, `Failed=3`, `PartialSuccess=4`, `Cancelled=5`
- `AlertOn`: bitmask — `None=0`, `Success=1`, `Failure=2`, `Error=4`, `All=7`

### REST API Source Convention

REST API sources must expose three endpoints relative to their base URL:
- `GET /meta/tables` — list of table names
- `GET /meta/tables/{name}/columns` — column metadata
- `GET /meta/ping` — health check

### Authorization Roles

Three roles with policy names used in `[Authorize(Policy = "...")]`:
- `ViewerOrAbove` — Viewer, ReadOnly, Admin
- `ReadOnlyOrAbove` — ReadOnly, Admin
- `AdminOnly` — Admin only

### Logging

Serilog writes to three sinks simultaneously: console, monthly rolling file (`logs/datasync-.log`), and a `SerilogEvents` SQL table. Per-job audit logs are also stored in `JobRunLog` rows visible in the UI.

## Configuration

**`appsettings.json`** (never commit secrets — use `dotnet user-secrets` with `UserSecretsId: datasyncmanager-secrets`):

| Key | Purpose |
|-----|---------|
| `ConnectionStrings:DefaultConnection` | SQL Server connection string |
| `Logging:FileDirectory` | Serilog rolling file output path |
| `Email:SmtpHost/Port/User/Pass/FromAddress/UseSsl` | SMTP for alerts |
| `Seeding:AdminEmail` / `AdminPassword` | Initial admin account seeded on first run |

Password policy: min 8 chars, requires uppercase + digit + non-alphanumeric. Lockout: 5 attempts → 15 min. Session expiry: 8 hours sliding.
