# ============================================================
# Uninstall-Agent.ps1
# Odinstalace USB Guardian agenta (služba + watchdog).
#
# Co dělá:
#   1. Zastaví a smaže službu "USB Guardian".
#   2. Zruší watchdog scheduled task.
#   2b. Zruší OBA scheduled tasky (watchdog i ToastHelper) + prázdnou složku úloh.
#   3. Volitelně (-RemoveFiles) smaže C:\Program Files\USBGuardian.
#   4. Volitelně (-RemoveData) smaže i C:\ProgramData\USBGuardian.
#
# ZACHOVÁVÁ C:\ProgramData\USBGuardian (fronta incidentů, whitelist) –
# smaž ručně jen pokud opravdu chceš přijít o lokální audit.
#
# Skript si sám vyžádá UAC elevaci.
# ============================================================

param(
    [string] $InstallDir  = "C:\Program Files\USBGuardian",
    [string] $ServiceName = "USB Guardian",
    [switch] $RemoveFiles,
    [switch] $RemoveData
)

# ── Auto-elevace ─────────────────────────────────────────────
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$currentUser).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Vyzaduji opravneni spravce - zobrazuji UAC dialog..."
    $rf = if ($RemoveFiles) { "-RemoveFiles" } else { "" }
    if ($RemoveData) { $rf = "$rf -RemoveData" }
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

# ── 2) Scheduled tasky ───────────────────────────────────────
# OBA, ne jen watchdog. ToastHelper se drive nerusil a na stanici zustaval:
# po smazani souboru pak pri kazdem prihlaseni selhaval a v Planovaci uloh
# to vypadalo jako zavadny zbytek po odinstalovanem software.
Write-Host "Rusim scheduled tasky (watchdog + ToastHelper)..."
foreach ($t in "USBGuardian-Watchdog", "USBGuardian-ToastHelper") {
    Unregister-ScheduledTask -TaskName $t -TaskPath "\USBGuardian\" `
        -Confirm:$false -ErrorAction SilentlyContinue
}

# Prazdna slozka uloh by zustala viset taky.
try {
    $svc = New-Object -ComObject Schedule.Service
    $svc.Connect()
    $svc.GetFolder("\").DeleteFolder("USBGuardian", 0)
} catch { }

# ── 3) Soubory (volitelne) ───────────────────────────────────
if ($RemoveFiles) {
    if (Test-Path $InstallDir) {
        Write-Host "Mazu $InstallDir..."
        Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Host "Soubory v '$InstallDir' ponechany (smaz s -RemoveFiles)."
}

# ── 4) Data (jen na vyzadani) ────────────────────────────────
# Fronta neodeslanych incidentu je lokalni auditni stopa - nesmi zmizet
# jen proto, ze nekdo odinstaloval agenta.
$dataDir = Join-Path $env:ProgramData "USBGuardian"
if ($RemoveData) {
    if (Test-Path $dataDir) {
        Write-Host "Mazu $dataDir (fronta a whitelist)..."
        Remove-Item -Path $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
if ($RemoveData) {
    Write-Host "Hotovo. Smazano vcetne ProgramData\USBGuardian." -ForegroundColor Green
} else {
    Write-Host "Hotovo. ProgramData\USBGuardian (fronta/whitelist) zachovano - smaz s -RemoveData." -ForegroundColor Green
}
