# ============================================================
# Register-Watchdog.ps1
# Registrace Scheduled Task pro USB Guardian Watchdog.
#
# Ucel:
#   Vytvoří nebo aktualizuje Scheduled Task ktera spousti
#   Watch-USBGuardian.ps1 kazde 3 minuty pod uctem SYSTEM.
#
# Poznamka:
#   Skript si sam vyzada UAC elevaci pokud neni spusten
#   jako Administrator.
# ============================================================

# Auto-elevace
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$currentUser).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator
)

if (-not $isAdmin) {
    Write-Host "Vyzaduji opravneni spravce - zobrazuji UAC dialog..."
    Start-Process powershell.exe -Verb RunAs `
        -ArgumentList "-NonInteractive -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

# Konfigurace
$TaskName        = "USBGuardian-Watchdog"
$TaskDesc        = "Hlida sluzbu USB Guardian a restartuje ji pokud neni spustena."
$WatchdogScript  = "C:\Program Files\USBGuardian\scripts\Watch-USBGuardian.ps1"
$IntervalMinutes = 3

# Akce
$action = New-ScheduledTaskAction `
    -Execute  "powershell.exe" `
    -Argument "-NonInteractive -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$WatchdogScript`""

# Triggery
$triggerRepeat = New-ScheduledTaskTrigger `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) `
    -Once -At (Get-Date)

$triggerBoot = New-ScheduledTaskTrigger -AtStartup

# Principal - SYSTEM
$taskPrincipal = New-ScheduledTaskPrincipal `
    -UserId    "SYSTEM" `
    -LogonType ServiceAccount `
    -RunLevel  Highest

# Nastaveni
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 2) `
    -RestartCount       3 `
    -RestartInterval    (New-TimeSpan -Minutes 1) `
    -StartWhenAvailable `
    -MultipleInstances  IgnoreNew

# Registrace
try {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

    Register-ScheduledTask `
        -TaskName    $TaskName `
        -TaskPath    "\USBGuardian\" `
        -Action      $action `
        -Trigger     @($triggerRepeat, $triggerBoot) `
        -Principal   $taskPrincipal `
        -Settings    $settings `
        -Description $TaskDesc `
        -Force

    Write-Host ""
    Write-Host "Watchdog task '$TaskName' uspesne zaregistrovan." -ForegroundColor Green
    Write-Host "   Interval: kazde $IntervalMinutes minuty + pri startu systemu"
    Write-Host "   Skript:   $WatchdogScript"
    Write-Host "   Ucet:     SYSTEM"
    Write-Host ""
}
catch {
    Write-Host ""
    Write-Host "Registrace watchdog tasku selhala: $_" -ForegroundColor Red
    exit 1
}
