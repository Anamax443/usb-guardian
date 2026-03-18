# ============================================================
# Watch-USBGuardian.ps1
# Watchdog skript pro USB Guardian Windows Service.
#
# Účel:
#   Kontroluje jestli služba "USB Guardian" běží.
#   Pokud ne, restartuje ji a zapíše událost do Windows Event Log.
#
# Spouštění:
#   Automaticky přes Scheduled Task (každé 3 minuty, pod SYSTEM).
#   Registrace: Register-Watchdog.ps1
#
# Event Log:
#   Log:    Application
#   Source: USBGuardian-Watchdog
#   ID 100: Service běží OK (pouze pokud LogOkEvents = true)
#   ID 200: Service byla zastavena – watchdog ji restartoval
#   ID 500: Restart selhal – nutný zásah IT
# ============================================================

$ServiceName = "USB Guardian"
$EventSource = "USBGuardian-Watchdog"
$EventLog    = "Application"
$LogOkEvents = $false

# ── Zajistit Event Log source ────────────────────────────────
# Použijeme try/catch místo SourceExists – SourceExists vyžaduje
# přístup k Security logu, což může selhat bez admin práv
try {
    New-EventLog -LogName $EventLog -Source $EventSource -ErrorAction Stop
}
catch [InvalidOperationException] {
    # Source již existuje – OK, pokračujeme
}
catch {
    # Nelze vytvořit – pokračujeme, zápis selže gracefully níže
}

# ── Pomocná funkce pro zápis do Event Logu ───────────────────
function Write-WatchdogEvent {
    param(
        [int]    $EventId,
        [string] $Message,
        [System.Diagnostics.EventLogEntryType] $EntryType = "Information"
    )
    try {
        Write-EventLog -LogName $EventLog -Source $EventSource `
                       -EventId $EventId -EntryType $EntryType `
                       -Message $Message
    }
    catch {
        Write-Warning "Event Log zapis selhal: $_"
    }
}

# ── Hlavní logika ─────────────────────────────────────────────
try {
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
}
catch {
    Write-WatchdogEvent -EventId 500 -EntryType "Error" `
        -Message "KRITICKA CHYBA: Sluzba '$ServiceName' nebyla nalezena. Agent pravdepodobne neni nainstalovan. Nutny zasah IT."
    exit 2
}

if ($service.Status -eq "Running") {
    if ($LogOkEvents) {
        Write-WatchdogEvent -EventId 100 `
            -Message "Watchdog: Sluzba '$ServiceName' bezi normalne."
    }
    exit 0
}

# Služba neběží – restartovat
$stopReason = $service.Status

Write-WatchdogEvent -EventId 200 -EntryType "Warning" `
    -Message "VAROVANI: Sluzba '$ServiceName' neni spustena (stav: $stopReason). Watchdog provadi restart."

try {
    Start-Service -Name $ServiceName -ErrorAction Stop
    $service.WaitForStatus("Running", [TimeSpan]::FromSeconds(30))

    Write-WatchdogEvent -EventId 200 `
        -Message "INFO: Sluzba '$ServiceName' byla uspesne restartovana watchdogem (predchozi stav: $stopReason)."
    exit 0
}
catch {
    Write-WatchdogEvent -EventId 500 -EntryType "Error" `
        -Message "KRITICKA CHYBA: Nepodarilo se restartovat sluzbu '$ServiceName'. Stanice neni chranena! Chyba: $_"
    exit 1
}
