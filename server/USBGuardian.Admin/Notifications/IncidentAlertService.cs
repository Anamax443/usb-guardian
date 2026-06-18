// ============================================================
// IncidentAlertService.cs
// Background notifier: e-mail souhrn NOVÝCH neschválených incidentů
// (Blocked/Warned). Baseline při prvním běhu (neposílá historii),
// throttle = interval, stav posledního ID v AppSettings.
// Běží jen když je e-mail v Nastavení zapnutý a vyplněný.
// ============================================================

using System.Text;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Admin.Notifications;

public class IncidentAlertService : BackgroundService
{
    private readonly ILogger<IncidentAlertService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public IncidentAlertService(ILogger<IncidentAlertService> logger, IDbContextFactory<AppDbContext> dbFactory)
    {
        _logger    = logger;
        _dbFactory = dbFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = 15;
            try { interval = await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Incident alert selhal"); }
            try { await Task.Delay(TimeSpan.FromMinutes(interval), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cfg = await EmailSender.LoadAsync(db);
        if (!cfg.IsComplete) return cfg.AlertIntervalMinutes;   // e-mail vypnutý/neúplný

        var lastRow = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "email.lastNotifiedId", ct);
        if (lastRow is null)
        {
            // první běh – nastav baseline, historii neposílej
            var maxId = await db.Incidents.MaxAsync(i => (int?)i.Id, ct) ?? 0;
            db.AppSettings.Add(new AppSetting { Key = "email.lastNotifiedId", Value = maxId.ToString() });
            await db.SaveChangesAsync(ct);
            return cfg.AlertIntervalMinutes;
        }

        var lastId = int.TryParse(lastRow.Value, out var v) ? v : 0;
        var news = await db.Incidents
            .Where(i => i.Id > lastId && i.Action != "Allowed")
            .OrderBy(i => i.Id).Take(500).ToListAsync(ct);

        if (news.Count == 0) return cfg.AlertIntervalMinutes;

        var blocked = news.Count(i => i.Action == "Blocked");
        var warned  = news.Count(i => i.Action == "Warned");
        var subject = $"[USB Guardian] {news.Count} novych incidentu (blok {blocked}, varovani {warned})";

        var sb = new StringBuilder();
        sb.AppendLine($"USB Guardian – {news.Count} novych incidentu neschvalenych medii:");
        sb.AppendLine();
        foreach (var i in news.Take(100))
            sb.AppendLine($"{i.Timestamp.ToLocalTime():dd.MM.yyyy HH:mm}  {i.Hostname}  {i.Username}  " +
                          $"{i.FriendlyName} [{i.VendorId}:{i.ProductId}:{i.SerialNumber}]  {i.Action}");
        if (news.Count > 100) sb.AppendLine($"... a dalsich {news.Count - 100}");

        await EmailSender.SendAsync(cfg, subject, sb.ToString());

        lastRow.Value = news.Max(i => i.Id).ToString();
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Incident alert odeslan: {N} incidentu na {To}",
            news.Count, string.Join(", ", cfg.Recipients));

        return cfg.AlertIntervalMinutes;
    }
}
