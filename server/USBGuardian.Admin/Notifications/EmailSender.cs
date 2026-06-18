// ============================================================
// EmailSender.cs
// Odeslání e-mailu přes SMTP relay (M365 Direct Send – bez přihlášení).
// Konfigurace z AppSettings (email.*). Sdílí test i alert služba.
// ============================================================

using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;

namespace USBGuardian.Admin.Notifications;

public record EmailConfig(
    bool Enabled, string Host, int Port, bool StartTls, string From, string[] Recipients, int AlertIntervalMinutes)
{
    public bool IsComplete =>
        Enabled && !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From) && Recipients.Length > 0;
}

public static class EmailSender
{
    public static async Task<EmailConfig> LoadAsync(AppDbContext db)
    {
        var all = (await db.AppSettings.ToListAsync())
            .ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase);
        string G(string k) => all.TryGetValue(k, out var v) ? v : "";

        return new EmailConfig(
            string.Equals(G("email.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            G("email.host"),
            int.TryParse(G("email.port"), out var p) ? p : 25,
            !string.Equals(G("email.starttls"), "false", StringComparison.OrdinalIgnoreCase),
            G("email.from"),
            G("email.recipients").Split(new[] { ',', ';', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            int.TryParse(G("email.alertIntervalMinutes"), out var i) && i > 0 ? i : 15);
    }

    public static async Task SendAsync(EmailConfig cfg, string subject, string body)
    {
#pragma warning disable SYSLIB0014
        using var client = new SmtpClient(cfg.Host, cfg.Port)
        { EnableSsl = cfg.StartTls, DeliveryMethod = SmtpDeliveryMethod.Network };
#pragma warning restore SYSLIB0014
        using var msg = new MailMessage { From = new MailAddress(cfg.From), Subject = subject, Body = body };
        foreach (var r in cfg.Recipients) msg.To.Add(r);
        await client.SendMailAsync(msg);
    }
}
