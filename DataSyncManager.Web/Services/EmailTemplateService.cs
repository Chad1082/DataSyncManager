using DataSyncManager.Web.Data;
using DataSyncManager.Web.Models;
using Microsoft.AspNetCore.Hosting;

namespace DataSyncManager.Web.Services;

public interface IEmailTemplateService
{
    Task<EmailTemplate> GetAsync();
    Task SaveAsync(EmailTemplate template);
    Task<string> ApplyAsync(string contentHtml);       // browser preview — uses base64 data URI
    Task<byte[]?> GetLogoBytesAsync();                 // email sending — caller attaches as CID
}

public class EmailTemplateService : IEmailTemplateService
{
    private readonly ApplicationDbContext _db;
    private readonly IWebHostEnvironment _env;

    // Raw bytes cached once per app lifetime; shared by both preview (base64) and email (CID) paths
    private static byte[]? _cachedLogoBytes;
    private static readonly SemaphoreSlim _logoLock = new(1, 1);

    public EmailTemplateService(ApplicationDbContext db, IWebHostEnvironment env)
    {
        _db = db;
        _env = env;
    }

    public static readonly string DefaultHtmlTemplate =
        "<!DOCTYPE html>\n" +
        "<html lang=\"en\">\n" +
        "<head>\n" +
        "  <meta charset=\"utf-8\">\n" +
        "  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n" +
        "</head>\n" +
        "<body style=\"margin:0;padding:0;background-color:#f3f4f6;font-family:Arial,Helvetica,sans-serif;\">\n" +
        "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background-color:#f3f4f6;\">\n" +
        "  <tr>\n" +
        "    <td align=\"center\" style=\"padding:32px 16px;\">\n" +
        "      <table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:600px;width:100%;\">\n" +
        "        <!-- Header -->\n" +
        "        <tr>\n" +
        "          <td style=\"background-color:#0d6efd;border-radius:8px 8px 0 0;padding:0;text-align:center;overflow:hidden;\">\n" +
        "            {{LOGO}}\n" +
        "          </td>\n" +
        "        </tr>\n" +
        "        <!-- Body -->\n" +
        "        <tr>\n" +
        "          <td style=\"background-color:#ffffff;padding:32px;\">\n" +
        "            {{CONTENT}}\n" +
        "          </td>\n" +
        "        </tr>\n" +
        "        <!-- Footer -->\n" +
        "        <tr>\n" +
        "          <td style=\"background-color:#f8f9fa;border-radius:0 0 8px 8px;border-top:1px solid #e9ecef;padding:16px 32px;text-align:center;\">\n" +
        "            <p style=\"margin:0;color:#6c757d;font-size:12px;line-height:1.5;\">\n" +
        "              This is an automated message from <strong>DataSync Manager</strong>.<br>\n" +
        "              Please do not reply to this email.\n" +
        "            </p>\n" +
        "          </td>\n" +
        "        </tr>\n" +
        "      </table>\n" +
        "    </td>\n" +
        "  </tr>\n" +
        "</table>\n" +
        "</body>\n" +
        "</html>";

    public async Task<EmailTemplate> GetAsync()
    {
        var t = await _db.EmailTemplates.FindAsync(1);
        if (t is null)
        {
            t = new EmailTemplate { Id = 1, HtmlTemplate = DefaultHtmlTemplate };
            _db.EmailTemplates.Add(t);
            await _db.SaveChangesAsync();
        }
        return t;
    }

    public async Task SaveAsync(EmailTemplate template)
    {
        template.Id = 1;
        template.UpdatedAt = DateTime.UtcNow;

        var existing = await _db.EmailTemplates.FindAsync(1);
        if (existing is null)
        {
            _db.EmailTemplates.Add(template);
        }
        else
        {
            existing.HtmlTemplate = template.HtmlTemplate;
            existing.UpdatedAt = template.UpdatedAt;
            existing.UpdatedByUserId = template.UpdatedByUserId;
        }

        await _db.SaveChangesAsync();
    }

    // Returns HTML with a base64 data URI — suitable for browser preview only.
    public async Task<string> ApplyAsync(string contentHtml)
    {
        var t = await GetAsync();
        var bytes = await GetLogoBytesAsync();

        var logoHtml = bytes is not null
            ? $"<img src=\"data:image/png;base64,{Convert.ToBase64String(bytes)}\" " +
              "alt=\"DataSync Manager\" style=\"width:100%;height:auto;display:block;border-radius:8px 8px 0 0;\">"
            : "<span style=\"color:#ffffff;font-size:22px;font-weight:bold;\">DataSync Manager</span>";

        return t.HtmlTemplate
            .Replace("{{LOGO}}", logoHtml)
            .Replace("{{CONTENT}}", contentHtml);
    }

    // Returns raw PNG bytes for use as a CID inline attachment in outgoing email.
    public async Task<byte[]?> GetLogoBytesAsync()
    {
        if (_cachedLogoBytes is not null) return _cachedLogoBytes;

        await _logoLock.WaitAsync();
        try
        {
            if (_cachedLogoBytes is not null) return _cachedLogoBytes;

            var logoPath = Path.Combine(_env.WebRootPath, "images", "Data sync Manager.png");
            if (!File.Exists(logoPath)) return null;

            _cachedLogoBytes = await File.ReadAllBytesAsync(logoPath);
            return _cachedLogoBytes;
        }
        finally
        {
            _logoLock.Release();
        }
    }
}
