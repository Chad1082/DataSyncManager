using DataSyncManager.Web.Data;
using DataSyncManager.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace DataSyncManager.Web.Services;

public interface IEmailSettingsService
{
    Task<EmailSettings> GetAsync();
    Task SaveAsync(EmailSettings settings);
}

public class EmailSettingsService : IEmailSettingsService
{
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _config;

    public EmailSettingsService(ApplicationDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<EmailSettings> GetAsync()
    {
        var row = await _db.EmailSettings.FirstOrDefaultAsync(e => e.Id == 1);
        if (row is not null) return row;

        // No DB record yet — bootstrap from appsettings so nothing breaks on first deploy
        var s = _config.GetSection("Email");
        return new EmailSettings
        {
            Id = 1,
            SmtpHost = s["SmtpHost"] ?? string.Empty,
            SmtpPort = int.TryParse(s["SmtpPort"], out var p) ? p : 587,
            SmtpUser = s["SmtpUser"],
            SmtpPass = s["SmtpPass"],
            FromAddress = s["FromAddress"] ?? "noreply@datasyncmanager.local",
            FromName = s["FromName"] ?? "DataSync Manager",
            UseSsl = bool.TryParse(s["UseSsl"], out var ssl) ? ssl : true,
        };
    }

    public async Task SaveAsync(EmailSettings incoming)
    {
        var existing = await _db.EmailSettings.FirstOrDefaultAsync(e => e.Id == 1);

        if (existing is null)
        {
            incoming.Id = 1;
            _db.EmailSettings.Add(incoming);
        }
        else
        {
            existing.SmtpHost = incoming.SmtpHost;
            existing.SmtpPort = incoming.SmtpPort;
            existing.SmtpUser = incoming.SmtpUser;
            existing.FromAddress = incoming.FromAddress;
            existing.FromName = incoming.FromName;
            existing.UseSsl = incoming.UseSsl;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedByUserId = incoming.UpdatedByUserId;

            // Only overwrite the password if a new one was supplied
            if (!string.IsNullOrEmpty(incoming.SmtpPass))
                existing.SmtpPass = incoming.SmtpPass;
        }

        await _db.SaveChangesAsync();
    }
}