using DataSyncManager.Web.Data;
using DataSyncManager.Web.Models;
using DataSyncManager.Web.Services;
using DataSyncManager.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
            Id                = s.Id,
            Name              = s.Name,
            SourceType        = s.SourceType,
            ConnectionString  = s.ConnectionString,
            BaseUrl           = s.BaseUrl,
            AuthHeader        = s.AuthHeader,
            RetryCount        = s.RetryCount,
            RetryDelaySeconds = s.RetryDelaySeconds
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
            RetryDelaySeconds = s.RetryDelaySeconds, IsActive = s.IsActive
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
        if (!ModelState.IsValid) return View(vm);

        var server = new DestinationServer
        {
            Name = vm.Name,
            ConnectionString = vm.ConnectionString,
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

        return View(new DestinationServerViewModel
        {
            Id = s.Id, Name = s.Name, ConnectionString = s.ConnectionString,
            DefaultDatabase = s.DefaultDatabase, RetryCount = s.RetryCount,
            RetryDelaySeconds = s.RetryDelaySeconds, IsActive = s.IsActive
        });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = "Admin")]
    public async Task<IActionResult> EditDestination(int id, DestinationServerViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);

        var s = await _db.DestinationServers.FindAsync(id);
        if (s is null) return NotFound();

        s.Name = vm.Name; s.ConnectionString = vm.ConnectionString;
        s.DefaultDatabase = vm.DefaultDatabase; s.RetryCount = vm.RetryCount;
        s.RetryDelaySeconds = vm.RetryDelaySeconds; s.IsActive = vm.IsActive;

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
    public async Task<IActionResult> GetDestinationTables(int serverId, string database)
    {
        var s = await _db.DestinationServers.FindAsync(serverId);
        if (s is null) return Json(new List<string>());
        var tables = await _schema.GetDestinationTablesAsync(s, database);
        return Json(tables);
    }

    [HttpGet]
    public async Task<IActionResult> GetDestinationColumns(int serverId, string database, string tableName)
    {
        var s = await _db.DestinationServers.FindAsync(serverId);
        if (s is null) return Json(new List<object>());
        var cols = await _schema.GetDestinationColumnsAsync(s, database, tableName);
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
}
