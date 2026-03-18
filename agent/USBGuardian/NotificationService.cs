// ============================================================
// NotificationService.cs
// Zapisuje Toast zprávy do fronty pro ToastHelper.
//
// Agent běží pod SYSTEM – nemůže zobrazit Toast přímo.
// Řešení: zapíše JSON do toast-queue\ a ToastHelper
// (spuštěný v user session při přihlášení/odemčení) zobrazí Toast.
//
// Fronta: C:\ProgramData\USBGuardian\toast-queue\toast_*.json
// ============================================================

using System.Text.Json;
using Microsoft.Extensions.Logging;
using USBGuardian.Models;

namespace USBGuardian;

public class NotificationService
{
    private readonly ILogger<NotificationService> _logger;
    private readonly bool   _enabled;
    private readonly string _contactMessage;
    private readonly string _queuePath;

    public NotificationService(
        ILogger<NotificationService> logger,
        bool   enabled,
        string contactMessage,
        string queuePath = @"C:\ProgramData\USBGuardian\toast-queue")
    {
        _logger         = logger;
        _enabled        = enabled;
        _contactMessage = contactMessage;
        _queuePath      = queuePath;
    }

    // --------------------------------------------------------
    // Zobrazí varování – zapíše zprávu do Toast fronty.
    // ToastHelper ji přečte při příštím přihlášení / odemčení.
    // --------------------------------------------------------
    public void ShowWarning(string title, string message)
    {
        if (!_enabled)
        {
            _logger.LogDebug("Toast notifikace vypnuty v konfiguraci");
            return;
        }

        try
        {
            // Zajistit existenci složky fronty
            Directory.CreateDirectory(_queuePath);

            // Sestavit zprávu – název a velikost z message stringu
            // Pro přesnější data použij ShowWarningForDevice()
            var toastMsg = new ToastQueueMessage
            {
                DetectedAt     = DateTime.UtcNow,
                Title          = title,
                Body           = message,
                ContactMessage = _contactMessage
            };

            // Unikátní název souboru – timestamp + náhodný suffix (bez kolizí)
            var fileName = $"toast_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N[..8]}.json";
            var filePath = Path.Combine(_queuePath, fileName);

            var json = JsonSerializer.Serialize(toastMsg,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(filePath, json);

            _logger.LogInformation("Toast notifikace zobrazena: {Title}", title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při zápisu Toast zprávy do fronty");
        }
    }

    // --------------------------------------------------------
    // Přetížení s DeviceInfo – bohatší data v notifikaci
    // Volat z PolicyEnforcer pro lepší Toast zprávy
    // --------------------------------------------------------
    public void ShowWarningForDevice(string title, DeviceInfo device, string action)
    {
        if (!_enabled)
        {
            _logger.LogDebug("Toast notifikace vypnuty v konfiguraci");
            return;
        }

        try
        {
            Directory.CreateDirectory(_queuePath);

            var toastMsg = new ToastQueueMessage
            {
                DetectedAt     = DateTime.UtcNow,
                Title          = title,
                Body           = $"Médium \"{device.FriendlyName}\" nebylo schváleno IT oddělením.",
                DeviceName     = device.FriendlyName,
                DeviceSize     = device.SizeFormatted,
                UserName       = Environment.UserName,
                MachineName    = Environment.MachineName,
                Action         = action,
                ContactMessage = _contactMessage
            };

            var fileName = $"toast_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..8]}.json";
            var filePath = Path.Combine(_queuePath, fileName);

            var json = JsonSerializer.Serialize(toastMsg,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(filePath, json);

            _logger.LogInformation("Toast notifikace zobrazena: {Title}", title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při zápisu Toast zprávy do fronty");
        }
    }
}

// --------------------------------------------------------
// ToastQueueMessage – interní model pro JSON soubor ve frontě
// Odpovídá ToastMessage.cs v ToastHelper projektu
// --------------------------------------------------------
public class ToastQueueMessage
{
    public DateTime DetectedAt     { get; set; }
    public string   Title          { get; set; } = string.Empty;
    public string   Body           { get; set; } = string.Empty;
    public string   DeviceName     { get; set; } = string.Empty;
    public string   DeviceSize     { get; set; } = string.Empty;
    public string   UserName       { get; set; } = string.Empty;
    public string   MachineName    { get; set; } = string.Empty;
    public string   Action         { get; set; } = string.Empty;
    public string   ContactMessage { get; set; } = string.Empty;
}
