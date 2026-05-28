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
    private readonly IEmailTemplateService _emailTemplate;
    private readonly UserManager<ApplicationUser> _users;

    public SettingsController(
        IEmailSettingsService emailSettings,
        IEmailService email,
        IEmailTemplateService emailTemplate,
        UserManager<ApplicationUser> users)
    {
        _emailSettings = emailSettings;
        _email = email;
        _emailTemplate = emailTemplate;
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

    // GET /Settings/EmailTemplate
    public async Task<IActionResult> EmailTemplate()
    {
        var t = await _emailTemplate.GetAsync();
        var vm = new EmailTemplateViewModel
        {
            HtmlTemplate = t.HtmlTemplate,
            UpdatedAt = t.UpdatedAt == default ? null : t.UpdatedAt,
        };

        if (t.UpdatedByUserId is not null)
        {
            var updater = await _users.FindByIdAsync(t.UpdatedByUserId);
            vm.UpdatedByDisplayName = updater?.DisplayName ?? updater?.Email;
        }

        return View(vm);
    }

    // POST /Settings/EmailTemplate
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EmailTemplate(EmailTemplateViewModel vm)
    {
        if (!ModelState.IsValid) return View(vm);

        await _emailTemplate.SaveAsync(new EmailTemplate
        {
            HtmlTemplate = vm.HtmlTemplate,
            UpdatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
        });

        TempData["Success"] = "Email template saved.";
        return RedirectToAction(nameof(EmailTemplate));
    }

    // POST /Settings/ResetEmailTemplate
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetEmailTemplate()
    {
        await _emailTemplate.SaveAsync(new EmailTemplate
        {
            HtmlTemplate = EmailTemplateService.DefaultHtmlTemplate,
            SiteBaseUrl = null,
            UpdatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
        });

        TempData["Success"] = "Email template reset to default.";
        return RedirectToAction(nameof(EmailTemplate));
    }

    // GET /Settings/EmailTemplatePreview
    [HttpGet]
    public async Task<IActionResult> EmailTemplatePreview()
    {
        var sampleContent =
            "<h2 style=\"margin-top:0;color:#1f2937;\">Sample Notification</h2>" +
            "<p>This is a preview of how your email notifications will look when jobs complete.</p>" +
            "<table style=\"width:100%;border-collapse:collapse;margin:16px 0;font-size:14px;\">" +
            "<tr style=\"background-color:#f8f9fa;\"><th style=\"padding:8px 12px;text-align:left;border:1px solid #dee2e6;\">Field</th><th style=\"padding:8px 12px;text-align:left;border:1px solid #dee2e6;\">Value</th></tr>" +
            "<tr><td style=\"padding:8px 12px;border:1px solid #dee2e6;\">Job Name</td><td style=\"padding:8px 12px;border:1px solid #dee2e6;\">Sync Incidents</td></tr>" +
            "<tr style=\"background-color:#f8f9fa;\"><td style=\"padding:8px 12px;border:1px solid #dee2e6;\">Status</td><td style=\"padding:8px 12px;border:1px solid #dee2e6;\"><span style=\"color:#198754;font-weight:bold;\">Succeeded</span></td></tr>" +
            "<tr><td style=\"padding:8px 12px;border:1px solid #dee2e6;\">Rows Read</td><td style=\"padding:8px 12px;border:1px solid #dee2e6;\">1,234</td></tr>" +
            "<tr style=\"background-color:#f8f9fa;\"><td style=\"padding:8px 12px;border:1px solid #dee2e6;\">Rows Inserted</td><td style=\"padding:8px 12px;border:1px solid #dee2e6;\">42</td></tr>" +
            "<tr><td style=\"padding:8px 12px;border:1px solid #dee2e6;\">Rows Updated</td><td style=\"padding:8px 12px;border:1px solid #dee2e6;\">1,192</td></tr>" +
            "<tr style=\"background-color:#f8f9fa;\"><td style=\"padding:8px 12px;border:1px solid #dee2e6;\">Duration</td><td style=\"padding:8px 12px;border:1px solid #dee2e6;\">0m 47s</td></tr>" +
            "</table>";

        var html = await _emailTemplate.ApplyAsync(sampleContent);
        return Content(html, "text/html");
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