using DataSyncManager.Web.Data;
using DataSyncManager.Web.Models;
using DataSyncManager.Web.Services;
using DataSyncManager.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DataSyncManager.Web.Controllers;

[Authorize]
public class ServersController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ISchemaService _schema;

    public ServersController(ApplicationDbContext db, ISchemaService schema)
    {
        _db = db;
        _schema = schema;
    }

    // ── Source Servers ───────────────────────────────

    public async Task<IActionResult> Sources()
    {
        var servers = await _db.SourceServers.OrderBy(s => s.Name).ToListAsync();
        var vms = servers.Select(s => new SourceServerViewModel
        {
            Id                  = s.Id,
            Name                = s.Name,
            SourceType          = s.SourceType,
            ConnectionString    = s.ConnectionString,
            BaseUrl             = s.BaseUrl,
            AuthHeader          = s.AuthHeader,
            RetryCount          = s.RetryCount,
            RetryDelaySeconds   = s.RetryDelaySeconds,
            SourceDateFormat    = s.SourceDateFormat,
            OdbcCommandTimeout  = s.OdbcCommandTimeout
        }).ToList();
        return View(vms);
    }

    [Authorize(Roles = "Admin")]
    public IActionResult CreateSource() => View(new SourceServerViewModel());

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateSource(SourceServerViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);

        var server = new SourceServer
        {
            Name = vm.Name,
            SourceType = vm.SourceType,
            ConnectionString = vm.ConnectionString,
            BaseUrl = vm.BaseUrl,
            AuthHeader = vm.AuthHeader,
            DefaultDatabase = vm.DefaultDatabase,
            RetryCount = vm.RetryCount,
            RetryDelaySeconds = vm.RetryDelaySeconds,
            SourceDateFormat = string.IsNullOrWhiteSpace(vm.SourceDateFormat) ? "yyyy-MM-dd HH:mm:ss" : vm.SourceDateFormat.Trim(),
            OdbcCommandTimeout = vm.OdbcCommandTimeout,
            IsActive = vm.IsActive,
            CreatedByUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        };

        _db.SourceServers.Add(server);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Source server added.";
        return RedirectToAction(nameof(Sources));
    }

    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> EditSource(int id)
    {
        var s = await _db.SourceServers.FindAsync(id);
        if (s is null) return NotFound();

        return View(new SourceServerViewModel
        {
            Id = s.Id, Name = s.Name, SourceType = s.SourceType,
            ConnectionString = s.ConnectionString, BaseUrl = s.BaseUrl, AuthHeader = s.AuthHeader,
            DefaultDatabase = s.DefaultDatabase, RetryCount = s.RetryCount,
            RetryDelaySeconds = s.RetryDelaySeconds, SourceDateFormat = s.SourceDateFormat,
            OdbcCommandTimeout = s.OdbcCommandTimeout, IsActive = s.IsActive
        });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> EditSource(int id, SourceServerViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);

        var s = await _db.SourceServers.FindAsync(id);
        if (s is null) return NotFound();

        s.Name = vm.Name; s.SourceType = vm.SourceType;
        s.ConnectionString = vm.ConnectionString; s.BaseUrl = vm.BaseUrl;
        s.AuthHeader = vm.AuthHeader; s.DefaultDatabase = vm.DefaultDatabase;
        s.RetryCount = vm.RetryCount; s.RetryDelaySeconds = vm.RetryDelaySeconds;
        s.SourceDateFormat = string.IsNullOrWhiteSpace(vm.SourceDateFormat) ? "yyyy-MM-dd HH:mm:ss" : vm.SourceDateFormat.Trim();
        s.OdbcCommandTimeout = vm.OdbcCommandTimeout;
        s.IsActive = vm.IsActive;

        await _db.SaveChangesAsync();
        TempData["Success"] = "Source server updated.";
        return RedirectToAction(nameof(Sources));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteSource(int id)
    {
        var s = await _db.SourceServers.FindAsync(id);
        if (s is null) return NotFound();
        s.IsActive = false;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Source server deactivated.";
        return RedirectToAction(nameof(Sources));
    }

    [HttpGet]
    public async Task<IActionResult> TestSource(int id)
    {
        var s = await _db.SourceServers.FindAsync(id);
        if (s is null) return Json(new { ok = false, message = "Not found" });

        var ok = await _schema.TestSourceConnectionAsync(s);
        return Json(new { ok, message = ok ? "Connection successful" : "Connection failed" });
    }

    // ── Destination Servers ──────────────────────────

    public async Task<IActionResult> Destinations()
    {
        var servers = await _db.DestinationServers.OrderBy(s => s.Name).ToListAsync();
        var vms = servers.Select(s => new DestinationServerViewModel
        {
            Id                = s.Id,
            Name              = s.Name,
            ConnectionString  = s.ConnectionString,
            RetryCount        = s.RetryCount,
            RetryDelaySeconds = s.RetryDelaySeconds
        }).ToList();
        return View(vms);
    }

    [Authorize(Roles = "Admin")]
    public IActionResult CreateDestination() => View(new DestinationServerViewModel());

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateDestination(DestinationServerViewModel vm)
    {
        ModelState.Remove("ConnectionString");
        if (!vm.UseWindowsAuth && string.IsNullOrEmpty(vm.SqlPassword))
            ModelState.AddModelError("SqlPassword", "Password is required for SQL Server Authentication.");
        if (string.IsNullOrEmpty(vm.DefaultDatabase))
            ModelState.AddModelError("DefaultDatabase", "Select a database.");

        if (!ModelState.IsValid) return View(vm);

        var server = new DestinationServer
        {
            Name = vm.Name,
            ConnectionString = BuildDestinationConnectionString(vm.ServerAddress, vm.UseWindowsAuth, vm.SqlUsername, vm.SqlPassword),
            DefaultDatabase = vm.DefaultDatabase,
            RetryCount = vm.RetryCount,
            RetryDelaySeconds = vm.RetryDelaySeconds,
            IsActive = vm.IsActive,
            CreatedByUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        };

        _db.DestinationServers.Add(server);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Destination server added.";
        return RedirectToAction(nameof(Destinations));
    }

    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> EditDestination(int id)
    {
        var s = await _db.DestinationServers.FindAsync(id);
        if (s is null) return NotFound();

        var vm = new DestinationServerViewModel
        {
            Id = s.Id, Name = s.Name, ConnectionString = s.ConnectionString,
            DefaultDatabase = s.DefaultDatabase, RetryCount = s.RetryCount,
            RetryDelaySeconds = s.RetryDelaySeconds, IsActive = s.IsActive,
            UseWindowsAuth = true
        };

        if (!string.IsNullOrEmpty(s.ConnectionString))
        {
            try
            {
                var csb = new SqlConnectionStringBuilder(s.ConnectionString);
                vm.ServerAddress = csb.DataSource;
                vm.UseWindowsAuth = csb.IntegratedSecurity;
                vm.SqlUsername = string.IsNullOrEmpty(csb.UserID) ? null : csb.UserID;
            }
            catch { /* leave fields empty if parse fails */ }
        }

        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> EditDestination(int id, DestinationServerViewModel vm)
    {
        ModelState.Remove("ConnectionString");
        ModelState.Remove("SqlPassword"); // optional on edit — keep existing if blank
        if (string.IsNullOrEmpty(vm.DefaultDatabase))
            ModelState.AddModelError("DefaultDatabase", "Select a database.");

        if (!ModelState.IsValid) return View(vm);

        var s = await _db.DestinationServers.FindAsync(id);
        if (s is null) return NotFound();

        s.Name = vm.Name;
        s.DefaultDatabase = vm.DefaultDatabase;
        s.RetryCount = vm.RetryCount;
        s.RetryDelaySeconds = vm.RetryDelaySeconds;
        s.IsActive = vm.IsActive;

        if (vm.UseWindowsAuth)
        {
            s.ConnectionString = BuildDestinationConnectionString(vm.ServerAddress, true, null, null);
        }
        else if (!string.IsNullOrEmpty(vm.SqlPassword))
        {
            s.ConnectionString = BuildDestinationConnectionString(vm.ServerAddress, false, vm.SqlUsername, vm.SqlPassword);
        }
        else if (!string.IsNullOrEmpty(s.ConnectionString))
        {
            // Keep existing password, update server/username
            var csb = new SqlConnectionStringBuilder(s.ConnectionString)
            {
                DataSource = vm.ServerAddress,
                TrustServerCertificate = true
            };
            if (!string.IsNullOrEmpty(vm.SqlUsername)) csb.UserID = vm.SqlUsername;
            s.ConnectionString = csb.ConnectionString;
        }
        else
        {
            ModelState.AddModelError("SqlPassword", "Password is required.");
            return View(vm);
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = "Destination server updated.";
        return RedirectToAction(nameof(Destinations));
    }

    [HttpGet]
    public async Task<IActionResult> TestDestination(int id)
    {
        var s = await _db.DestinationServers.FindAsync(id);
        if (s is null) return Json(new { ok = false, message = "Not found" });

        var ok = await _schema.TestDestinationConnectionAsync(s);
        return Json(new { ok, message = ok ? "Connection successful" : "Connection failed" });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> ProbeDestinationDatabases([FromBody] ProbeDestinationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Server))
            return Json(new List<string>());
        var cs = BuildProbeConnectionString(req.Server, req.UseWindowsAuth, req.Username, req.Password);
        var databases = await _schema.GetDatabasesAsync(cs);
        return Json(databases);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> ProbeDestinationConnection([FromBody] ProbeDestinationRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Database))
            return Json(new { ok = false, message = "Select a database first." });
        var cs = BuildProbeConnectionString(req.Server, req.UseWindowsAuth, req.Username, req.Password);
        var (ok, message) = await _schema.TestConnectionWithDatabaseAsync(cs, req.Database);
        return Json(new { ok, message });
    }

    // ── Schema API for AJAX ──────────────────────────

    /// <summary>
    /// Returns ODBC DSNs (System + User) available on the application server,
    /// read from the Windows registry. Returns an empty list on non-Windows hosts.
    /// </summary>
    [HttpGet]
    public IActionResult GetOdbcDsns()
    {
        var dsns = new List<object>();

        if (!OperatingSystem.IsWindows())
            return Json(dsns);

        void ReadHive(Microsoft.Win32.RegistryKey? hive)
        {
            if (hive is null) return;
            using var key = hive.OpenSubKey(@"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources");
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                var driver = key.GetValue(name)?.ToString() ?? "";
                dsns.Add(new { name, driver });
            }
        }

        ReadHive(Microsoft.Win32.Registry.LocalMachine);  // System DSNs
        ReadHive(Microsoft.Win32.Registry.CurrentUser);   // User DSNs

        return Json(dsns.OrderBy(d => ((dynamic)d).name));
    }

    [HttpGet]
    public async Task<IActionResult> GetSourceTables(int serverId)
    {
        var s = await _db.SourceServers.FindAsync(serverId);
        if (s is null) return Json(new List<string>());
        var tables = await _schema.GetTablesAsync(s);
        return Json(tables);
    }

    [HttpGet]
    public async Task<IActionResult> GetSourceColumns(int serverId, string tableName)
    {
        var s = await _db.SourceServers.FindAsync(serverId);
        if (s is null) return Json(new List<object>());
        var cols = await _schema.GetColumnsAsync(s, tableName);
        return Json(cols);
    }

    [HttpGet]
    public async Task<IActionResult> GetDestinationTables(int serverId, string? database = null)
    {
        var s = await _db.DestinationServers.FindAsync(serverId);
        if (s is null) return Json(new List<string>());
        var db = !string.IsNullOrEmpty(database) ? database : s.DefaultDatabase;
        if (string.IsNullOrEmpty(db)) return Json(new List<string>());
        var tables = await _schema.GetDestinationTablesAsync(s, db);
        return Json(tables);
    }

    [HttpGet]
    public async Task<IActionResult> GetDestinationColumns(int serverId, string? database = null, string tableName = "")
    {
        var s = await _db.DestinationServers.FindAsync(serverId);
        if (s is null) return Json(new List<object>());
        var db = !string.IsNullOrEmpty(database) ? database : s.DefaultDatabase ?? "";
        if (string.IsNullOrEmpty(db)) return Json(new List<object>());
        var cols = await _schema.GetDestinationColumnsAsync(s, db, tableName);
        return Json(cols);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = DbSeeder.RoleAdmin)]
    public async Task<IActionResult> DeleteDestination(int id)
    {
        var dest = await _db.DestinationServers.FindAsync(id);
        if (dest is not null)
        {
            _db.DestinationServers.Remove(dest);
            await _db.SaveChangesAsync();
            TempData["Success"] = $"Destination '{dest.Name}' deleted.";
        }
        return RedirectToAction(nameof(Destinations));
    }

    [HttpGet]
    [Authorize(Roles = DbSeeder.RoleAdmin)]
    public async Task<IActionResult> TestDestinationPage(int id)
    {
        var dest = await _db.DestinationServers.FindAsync(id);
        if (dest is null) return NotFound();
        var ok = await _schema.TestDestinationConnectionAsync(dest);
        TempData[ok ? "Success" : "Error"] = ok
            ? $"Connection to '{dest.Name}' succeeded."
            : $"Connection to '{dest.Name}' failed.";
        return RedirectToAction(nameof(Destinations));
    }

    // ── Helpers ──────────────────────────────────────

    private static string BuildDestinationConnectionString(string server, bool useWindowsAuth, string? username, string? password)
    {
        var b = new SqlConnectionStringBuilder { DataSource = server, TrustServerCertificate = true };
        if (useWindowsAuth) b.IntegratedSecurity = true;
        else { b.UserID = username ?? ""; b.Password = password ?? ""; }
        return b.ConnectionString;
    }

    private static string BuildProbeConnectionString(string server, bool useWindowsAuth, string? username, string? password)
        => BuildDestinationConnectionString(server, useWindowsAuth, username, password);

    public class ProbeDestinationRequest
    {
        public string Server { get; set; } = string.Empty;
        public bool UseWindowsAuth { get; set; } = true;
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? Database { get; set; }
    }
}
