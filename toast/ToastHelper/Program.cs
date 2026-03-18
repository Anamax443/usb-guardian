// ============================================================
// Program.cs – USBGuardian ToastHelper
//
// Čte frontu Toast zpráv z C:\ProgramData\USBGuardian\toast-queue\
// a zobrazuje Windows Toast notifikace uživateli.
//
// Spouštění:
//   Task Scheduler – trigger při přihlášení + při odemčení.
//   Běží v kontextu přihlášeného uživatele (ne SYSTEM).
//
// Tok:
//   1. Registrovat AppUserModelId (nutné pro unpackaged Win32 app)
//   2. Načíst *.json ze toast-queue\
//   3. Zobrazit Toast pro každou zprávu
//   4. Smazat zpracované soubory
//   5. Skončit (jednorázový proces)
// ============================================================

using System.Text.Json;
using Microsoft.Win32;
using USBGuardian.ToastHelper;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

const string QueuePath = @"C:\ProgramData\USBGuardian\toast-queue";
const string AppId     = "USBGuardian.ToastHelper";
const int    MaxToasts = 5;

// ── Registrace AppUserModelId ─────────────────────────────────
// Windows Toast API vyžaduje registraci v registry pro unpackaged app.
// Bez tohoto záznamu CreateToastNotifier() vrátí null.
RegisterAppId(AppId, "USB Guardian");

// ── Zkontrolovat frontu ───────────────────────────────────────
if (!Directory.Exists(QueuePath))
    return;

var queueFiles = Directory.GetFiles(QueuePath, "toast_*.json")
                           .OrderBy(f => f)      // chronologicky
                           .Take(MaxToasts)       // max zpráv najednou
                           .ToArray();

if (queueFiles.Length == 0)
    return;

var processed = new List<string>();

foreach (var file in queueFiles)
{
    try
    {
        var json    = await File.ReadAllTextAsync(file);
        var message = JsonSerializer.Deserialize<ToastMessage>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (message is null)
        {
            processed.Add(file);
            continue;
        }

        var localTime  = message.DetectedAt.ToLocalTime().ToString("HH:mm:ss dd.MM.yyyy");
        var actionText = message.Action switch
        {
            "Blocked" => "Zarizeni bylo ZABLOKOVANO",
            "Warned"  => "Varovani - medium neni na whitelistu",
            _         => $"Akce: {message.Action}"
        };

        // Toast XML – přímé WinRT API, bez NuGet toolkit závislostí
        var xml = $"""
            <toast>
              <visual>
                <binding template="ToastGeneric">
                  <text>USB Guardian - Nepovolene medium</text>
                  <text>{EscapeXml(message.DeviceName)} ({EscapeXml(message.DeviceSize)})</text>
                  <text>{EscapeXml(actionText)}</text>
                  <text>{EscapeXml(message.ContactMessage)}</text>
                  <text placement="attribution">{EscapeXml(localTime)} | {EscapeXml(message.UserName)}@{EscapeXml(message.MachineName)}</text>
                </binding>
              </visual>
            </toast>
            """;

        var xmlDoc   = new XmlDocument();
        xmlDoc.LoadXml(xml);

        var notifier = ToastNotificationManager.CreateToastNotifier(AppId);
        if (notifier?.Setting == NotificationSetting.Enabled)
        {
            notifier.Show(new ToastNotification(xmlDoc));
        }

        processed.Add(file);
        await Task.Delay(500);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Chyba: {Path.GetFileName(file)}: {ex.Message}");
        processed.Add(file);
    }
}

// ── Smazat zpracované soubory ─────────────────────────────────
foreach (var file in processed)
{
    try   { File.Delete(file); }
    catch { /* ignorovat */ }
}

// ── Registrace AppUserModelId v registry ─────────────────────
static void RegisterAppId(string appId, string displayName)
{
    try
    {
        var keyPath = $@"SOFTWARE\Classes\AppUserModelId\{appId}";
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue("DisplayName", displayName, RegistryValueKind.String);
    }
    catch { /* pokračovat */ }
}

// ── Escapování XML speciálních znaků ─────────────────────────
static string EscapeXml(string input) => input
    .Replace("&",  "&amp;")
    .Replace("<",  "&lt;")
    .Replace(">",  "&gt;")
    .Replace("\"", "&quot;")
    .Replace("'",  "&apos;");
