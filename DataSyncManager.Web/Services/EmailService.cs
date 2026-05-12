using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace DataSyncManager.Web.Services;

public interface IEmailService
{
    Task SendAsync(IEnumerable<string> to, string subject, string htmlBody);
}

public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _log;

    public EmailService(IConfiguration config, ILogger<EmailService> log)
    {
        _config = config;
        _log = log;
    }

    public async Task SendAsync(IEnumerable<string> to, string subject, string htmlBody)
    {
        var section = _config.GetSection("Email");
        var host = section["SmtpHost"] ?? "localhost";
        var port = int.Parse(section["SmtpPort"] ?? "587");
        var user = section["SmtpUser"] ?? string.Empty;
        var pass = section["SmtpPass"] ?? string.Empty;
        var fromAddr = section["FromAddress"] ?? "noreply@datasyncmanager.local";
        var fromName = section["FromName"] ?? "DataSync Manager";
        var useSsl = bool.Parse(section["UseSsl"] ?? "true");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromAddr));

        foreach (var addr in to)
            message.To.Add(MailboxAddress.Parse(addr.Trim()));

        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(host, port,
                useSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None);

            if (!string.IsNullOrEmpty(user))
                await client.AuthenticateAsync(user, pass);

            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to send email to {To}", string.Join(", ", to));
        }
    }
}
