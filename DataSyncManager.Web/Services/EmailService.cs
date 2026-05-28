using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace DataSyncManager.Web.Services;

public interface IEmailService
{
    Task SendAsync(IEnumerable<string> to, string subject, string htmlBody);
    Task<(bool Ok, string? Error)> TestAsync(string to);
}

public class EmailService : IEmailService
{
    private readonly IEmailSettingsService _settings;
    private readonly IEmailTemplateService _template;
    private readonly ILogger<EmailService> _log;

    public EmailService(IEmailSettingsService settings, IEmailTemplateService template, ILogger<EmailService> log)
    {
        _settings = settings;
        _template = template;
        _log = log;
    }

    // Used by the job runner — swallows exceptions so a bad SMTP config never crashes a sync
    public async Task SendAsync(IEnumerable<string> to, string subject, string htmlBody)
    {
        try { await SendCoreAsync(to, subject, htmlBody); }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to send email to {To}", string.Join(", ", to));
        }
    }

    // Used by the settings test button — surfaces errors to the UI
    public async Task<(bool Ok, string? Error)> TestAsync(string to)
    {
        try
        {
            var content =
                "<h2 style=\"margin-top:0;color:#1f2937;\">Test Email</h2>" +
                "<p>If you received this, your SMTP settings are working correctly.</p>" +
                $"<p style=\"color:#6b7280;font-size:0.85em;\">Sent at {DateTime.UtcNow:u} UTC</p>";
            await SendCoreAsync(new[] { to }, "DataSync Manager — Test Email", content);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task SendCoreAsync(IEnumerable<string> to, string subject, string htmlBody)
    {
        var s = await _settings.GetAsync();

        if (string.IsNullOrWhiteSpace(s.SmtpHost))
        {
            _log.LogWarning("Email skipped — SMTP host is not configured.");
            return;
        }

        const string logoCid = "logo@datasyncmanager";

        var t = await _template.GetAsync();
        var logoBytes = await _template.GetLogoBytesAsync();

        var logoImgTag = logoBytes is not null
            ? $"<img src=\"cid:{logoCid}\" alt=\"DataSync Manager\" " +
              "style=\"width:100%;height:auto;display:block;border-radius:8px 8px 0 0;\">"
            : "<span style=\"color:#ffffff;font-size:22px;font-weight:bold;\">DataSync Manager</span>";

        var html = t.HtmlTemplate
            .Replace("{{LOGO}}", logoImgTag)
            .Replace("{{CONTENT}}", htmlBody);

        var builder = new BodyBuilder { HtmlBody = html };
        if (logoBytes is not null)
        {
            var logoPart = builder.LinkedResources.Add("logo.png", logoBytes, new ContentType("image", "png"));
            logoPart.ContentId = logoCid;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(s.FromName, s.FromAddress));
        foreach (var addr in to) message.To.Add(MailboxAddress.Parse(addr.Trim()));
        message.Subject = subject;
        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(s.SmtpHost, s.SmtpPort,
            s.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None);

        if (!string.IsNullOrEmpty(s.SmtpUser))
            await client.AuthenticateAsync(s.SmtpUser, s.SmtpPass);

        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}