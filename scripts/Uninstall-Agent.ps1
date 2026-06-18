# ============================================================
# Uninstall-Agent.ps1
# Odinstalace USB Guardian agenta (služba + watchdog).
#
# Co dělá:
#   1. Zastaví a smaže službu "USB Guardian".
#   2. Zruší watchdog scheduled task.
#   3. Volitelně (-RemoveFiles) smaže C:\Program Files\USBGuardian.
#
# ZACHOVÁVÁ C:\ProgramData\USBGuardian (fronta incidentů, whitelist) –
# smaž ručně jen pokud opravdu chceš přijít o lokální audit.
#
# Skript si sám vyžádá UAC elevaci.
# ============================================================

param(
    [string] $InstallDir  = "C:\Program Files\USBGuardian",
    [string] $ServiceName = "USB Guardian",
    [switch] $RemoveFiles
)

# ── Auto-elevace ─────────────────────────────────────────────
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$currentUser).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Vyzaduji opravneni spravce - zobrazuji UAC dialog..."
    $rf = if ($RemoveFiles) { "-RemoveFiles" } else { "" }
    Start-Process powershell.exe -Verb RunAs `
        -ArgumentList "-NonInteractive -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -InstallDir `"$InstallDir`" -ServiceName `"$ServiceName`" $rf"
    exit
}

Write-Host "USB Guardian – odinstalace agenta" -ForegroundColor Cyan

# ── 1) Sluzba ────────────────────────────────────────────────
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') {
        Write-Host "Zastavuji sluzbu..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        try { (Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } catch {}
    }
    Write-Host "Mazu sluzbu..."
    & sc.exe delete $ServiceName | Out-Null
} else {
    Write-Host "Sluzba '$ServiceName' neexistuje – preskakuji."
}

# ── 2) Watchdog task ─────────────────────────────────────────
Write-Host "Rusim watchdog task..."
Unregister-ScheduledTask -TaskName "USBGuardian-Watchdog" -TaskPath "\USBGuardian\" `
    -Confirm:$false -ErrorAction SilentlyContinue

# ── 3) Soubory (volitelne) ───────────────────────────────────
if ($RemoveFiles) {
    if (Test-Path $InstallDir) {
        Write-Host "Mazu $InstallDir..."
        Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Host "Soubory v '$InstallDir' ponechany (smaz s -RemoveFiles)."
}

Write-Host ""
Write-Host "Hotovo. ProgramData\USBGuardian (fronta/whitelist) zachovano." -ForegroundColor Green
