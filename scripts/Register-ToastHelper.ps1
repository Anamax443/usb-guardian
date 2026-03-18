# ============================================================
# Register-ToastHelper.ps1
# Registrace Scheduled Task pro USB Guardian ToastHelper.
#
# Triggery:
#   1. Při přihlášení uživatele (logon)
#   2. Při odemčení pracovní stanice (unlock)
#
# Spouštění:
#   Jednorázově při nasazení agenta (součást instalačního skriptu).
#   Skript si sám vyžádá UAC elevaci.
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
$TaskName    = "USBGuardian-ToastHelper"
$TaskDesc    = "Zobrazuje Toast notifikace o nepovolených USB zařízeních po přihlášení nebo odemčení."
$HelperExe   = "C:\Program Files\USBGuardian\ToastHelper.exe"

# Akce – spustit ToastHelper.exe
$action = New-ScheduledTaskAction -Execute $HelperExe

# Trigger 1 – při přihlášení jakéhokoli uživatele
$triggerLogon = New-ScheduledTaskTrigger -AtLogOn

# Trigger 2 – při odemčení pracovní stanice
# StateChange 8 = WorkstationUnlock
$triggerUnlock = New-CimInstance -Namespace ROOT\Microsoft\Windows\TaskScheduler `
    -ClassName MSFT_TaskSessionStateChangeTrigger `
    -Property @{ StateChange = 8; Enabled = $true } `
    -ClientOnly

# Principal – spustit jako aktuálně přihlášený uživatel (Interactive)
$taskPrincipal = New-ScheduledTaskPrincipal `
    -GroupId    "BUILTIN\Users" `
    -RunLevel   Limited          # bez admin práv – záměrně

# Nastavení
$settings = New-ScheduledTaskSettingsSet `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 1) `
    -MultipleInstances  IgnoreNew `
    -StartWhenAvailable

# Registrace
try {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue

    Register-ScheduledTask `
        -TaskName    $TaskName `
        -TaskPath    "\USBGuardian\" `
        -Action      $action `
        -Trigger     @($triggerLogon, $triggerUnlock) `
        -Principal   $taskPrincipal `
        -Settings    $settings `
        -Description $TaskDesc `
        -Force

    Write-Host ""
    Write-Host "ToastHelper task '$TaskName' uspesne zaregistrovan." -ForegroundColor Green
    Write-Host "   Triggery: pri prihlaseni + pri odemceni"
    Write-Host "   Exe:      $HelperExe"
    Write-Host "   Ucet:     BUILTIN\Users (bez admin prav)"
    Write-Host ""
}
catch {
    Write-Host ""
    Write-Host "Registrace ToastHelper tasku selhala: $_" -ForegroundColor Red
    exit 1
}
