using DataSyncManager.Web.Data;
using DataSyncManager.Web.Models;
using DataSyncManager.Web.Services;
using DataSyncManager.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace DataSyncManager.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
public class SettingsController : Controller
{
    private readonly IEmailSettingsService _emailSettings;
    private readonly IEmailService _email;
    private readonly UserManager<ApplicationUser> _users;

    public SettingsController(
        IEmailSettingsService emailSettings,
        IEmailService email,
        UserManager<ApplicationUser> users)
    {
        _emailSettings = emailSettings;
        _email = email;
        _users = users;
    }

    // GET /Settings/Email
    public async Task<IActionResult> Email()
    {
        var s = await _emailSettings.GetAsync();
        var vm = MapToVm(s);

        if (s.UpdatedByUserId is not null)
        {
            var updater = await _users.FindByIdAsync(s.UpdatedByUserId);
            vm.UpdatedByDisplayName = updater?.DisplayName ?? updater?.Email;
        }

        return View(vm);
    }

    // POST /Settings/Email
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Email(EmailSettingsViewModel vm)
    {
        if (!ModelState.IsValid)
        {
            // Re-populate display-only fields before returning
            var existing = await _emailSettings.GetAsync();
            vm.HasExistingPassword = !string.IsNullOrEmpty(existing.SmtpPass);
            vm.UpdatedAt = existing.UpdatedAt == default ? null : existing.UpdatedAt;
            return View(vm);
        }

        var settings = new EmailSettings
        {
            SmtpHost = vm.SmtpHost.Trim(),
            SmtpPort = vm.SmtpPort,
            SmtpUser = vm.SmtpUser?.Trim(),
            SmtpPass = vm.SmtpPass,  // null/empty = service keeps existing
            FromAddress = vm.FromAddress.Trim(),
            FromName = vm.FromName.Trim(),
            UseSsl = vm.UseSsl,
            UpdatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
        };

        await _emailSettings.SaveAsync(settings);
        TempData["Success"] = "Email settings saved successfully.";
        return RedirectToAction(nameof(Email));
    }

    // POST /Settings/TestEmail
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TestEmail(string testEmailAddress)
    {
        if (string.IsNullOrWhiteSpace(testEmailAddress))
        {
            TempData["Error"] = "Please enter a recipient address before sending a test email.";
            return RedirectToAction(nameof(Email));
        }

        var (ok, error) = await _email.TestAsync(testEmailAddress.Trim());

        if (ok)
            TempData["Success"] = $"Test email sent to {testEmailAddress}. Check your inbox.";
        else
            TempData["Error"] = $"Test failed: {error}";

        return RedirectToAction(nameof(Email));
    }

    // ── Helpers ──────────────────────────────────────────────

    private static EmailSettingsViewModel MapToVm(EmailSettings s) => new()
    {
        SmtpHost = s.SmtpHost,
        SmtpPort = s.SmtpPort,
        SmtpUser = s.SmtpUser,
        SmtpPass = null,             // never echo the password
        FromAddress = s.FromAddress,
        FromName = s.FromName,
        UseSsl = s.UseSsl,
        HasExistingPassword = !string.IsNullOrEmpty(s.SmtpPass),
        UpdatedAt = s.UpdatedAt == default ? null : s.UpdatedAt,
    };
}