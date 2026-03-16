// ============================================================
// NotificationService.cs
// Zobrazí Windows Toast notifikaci uživateli.
// Používá PowerShell → funguje bez nutnosti instalace extra knihoven.
// ============================================================

using Microsoft.Extensions.Logging;

namespace USBGuardian;

public class NotificationService
{
    private readonly ILogger<NotificationService> _logger;
    private readonly bool _enabled;
    private readonly string _contactMessage;

    // Název aplikace zobrazený v notifikaci (Windows akční centrum)
    private const string AppName = "USB Guardian – IT Security";

    public NotificationService(ILogger<NotificationService> logger,
        bool enabled, string contactMessage)
    {
        _logger         = logger;
        _enabled        = enabled;
        _contactMessage = contactMessage;
    }

    // --------------------------------------------------------
    // Zobrazí varování přes Windows Toast Notification
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
            // Sestavíme PowerShell skript pro zobrazení Toast notifikace
            // Tato metoda funguje na Windows 10/11 bez extra závislostí
            var fullMessage = $"{message}\n{_contactMessage}";

            var psScript = $@"
                [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
                [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null

                $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent(
                    [Windows.UI.Notifications.ToastTemplateType]::ToastText02)

                $textNodes = $template.GetElementsByTagName('text')
                $textNodes[0].AppendChild($template.CreateTextNode('{EscapeForPs(title)}')) | Out-Null
                $textNodes[1].AppendChild($template.CreateTextNode('{EscapeForPs(fullMessage)}')) | Out-Null

                $toast = [Windows.UI.Notifications.ToastNotification]::new($template)
                $notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{AppName}')
                $notifier.Show($toast)
            ";

            // Spustíme PowerShell jako oddělený proces (nezablokuje service thread)
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = $"-NoProfile -NonInteractive -Command \"{psScript}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(5000);    // max 5 sekund čekání

            _logger.LogInformation("Toast notifikace zobrazena: {Title}", title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při zobrazení toast notifikace");
        }
    }

    // --------------------------------------------------------
    // Escapování speciálních znaků pro PowerShell string
    // --------------------------------------------------------
    private static string EscapeForPs(string input) =>
        input.Replace("'", "''").Replace("\n", " | ");
}
