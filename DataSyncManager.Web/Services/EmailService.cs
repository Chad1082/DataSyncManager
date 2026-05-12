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
    private readonly ILogger<EmailService> _log;

    public EmailService(IEmailSettingsService settings, ILogger<EmailService> log)
    {
        _settings = settings;
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
            await SendCoreAsync(
                new[] { to },
                "DataSync Manager — Test Email",
                "<p>If you received this, your SMTP settings are working correctly.</p>" +
                $"<p style='color:#6b7280;font-size:0.85em'>Sent at {DateTime.UtcNow:u} UTC</p>");
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

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(s.FromName, s.FromAddress));
        foreach (var addr in to) message.To.Add(MailboxAddress.Parse(addr.Trim()));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        await client.ConnectAsync(s.SmtpHost, s.SmtpPort,
            s.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None);

        if (!string.IsNullOrEmpty(s.SmtpUser))
            await client.AuthenticateAsync(s.SmtpUser, s.SmtpPass);

        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }
}