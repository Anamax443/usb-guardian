# ============================================================
# Install-Agent.ps1
# Instalace USB Guardian agenta jako Windows služby (SYSTEM).
#
# Co dělá:
#   1. Zkopíruje self-contained publish do C:\Program Files\USBGuardian
#      (ZACHOVÁ existující Config\agent.config.local.json – per-machine
#       sync nastavení se NEPŘEPÍŠE).
#   2. Vytvoří/aktualizuje službu "USB Guardian" (start=auto, LocalSystem)
#      + recovery (restart při pádu).
#   3. Zaregistruje watchdog scheduled task (Register-Watchdog.ps1).
#   4. Spustí službu.
#
# Použití (z publish výstupu):
#   dotnet publish agent\USBGuardian -c Release -r win-x64 --self-contained -o D:\deploy\USBGuardianAgent
#   .\Install-Agent.ps1 -SourcePath D:\deploy\USBGuardianAgent
#
# Pozn.: agent potřebuje Config\agent.config.local.json se syncUrl +
#   tls.pinnedThumbprint (jinak whitelist sync nepojede). Skript na to upozorní.
#
# Skript si sám vyžádá UAC elevaci.
# ============================================================

param(
    [Parameter(Mandatory = $true)]
    [string] $SourcePath,
    [string] $InstallDir  = "C:\Program Files\USBGuardian",
    [string] $ServiceName = "USB Guardian"
)

# ── Auto-elevace ─────────────────────────────────────────────
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$currentUser).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Vyzaduji opravneni spravce - zobrazuji UAC dialog..."
    Start-Process powershell.exe -Verb RunAs `
        -ArgumentList "-NonInteractive -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -SourcePath `"$SourcePath`" -InstallDir `"$InstallDir`" -ServiceName `"$ServiceName`""
    exit
}

$ErrorActionPreference = "Stop"
$exeName = "USBGuardian.exe"
$srcExe  = Join-Path $SourcePath $exeName

# ── Validace zdroje ──────────────────────────────────────────
if (-not (Test-Path $srcExe)) {
    Write-Host "CHYBA: $exeName nenalezen v '$SourcePath'." -ForegroundColor Red
    Write-Host "Nejdriv publish: dotnet publish agent\USBGuardian -c Release -r win-x64 --self-contained -o <SourcePath>"
    exit 1
}

Write-Host "USB Guardian – instalace agenta" -ForegroundColor Cyan
Write-Host "  Zdroj:  $SourcePath"
Write-Host "  Cil:    $InstallDir"
Write-Host "  Sluzba: $ServiceName"
Write-Host ""

# ── 1) Zastavit existující službu (kvuli zamceni souboru) ────
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Sluzba existuje – zastavuji pred kopirovanim..."

    # PROC TAK DUKLADNE: "Stopped" ve sprave sluzeb jeste neznamena uvolneno. Soubory
    # i registraci portu lokalni konzole (http.sys) drzi PROCES a pousti je az kdyz
    # skutecne skonci. Drive bylo cekani v try/catch, takze se timeout spolkl a slo se
    # dal i s bezicim procesem. Nova instance pak port nezabrala, konzole zustala mrtva
    # do dalsiho restartu a na stanici to vypadalo jako donekonecna nacitajici stranka.
    # Proto: zastavit -> OVERIT -> kdyz nestoji, ukoncit natvrdo -> pockat na konec procesu.
    $svcPid = 0
    $svcWmi = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if ($svcWmi) { $svcPid = [int] $svcWmi.ProcessId }

    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        $limit = (Get-Date).AddSeconds(30)
        while ((Get-Service $ServiceName).Status -ne 'Stopped' -and (Get-Date) -lt $limit) {
            Start-Sleep -Milliseconds 500
        }
    }

    $stav = (Get-Service $ServiceName).Status
    if ($stav -ne 'Stopped') {
        Write-Host "  Sluzba po 30 s hlasi porad '$stav' – ukoncuji proces natvrdo." -ForegroundColor Yellow
        if ($svcPid -gt 0) { Stop-Process -Id $svcPid -Force -ErrorAction SilentlyContinue }
    }

    if ($svcPid -gt 0) {
        $limit = (Get-Date).AddSeconds(15)
        while ((Get-Process -Id $svcPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $limit) {
            Start-Sleep -Milliseconds 500
        }
        if (Get-Process -Id $svcPid -ErrorAction SilentlyContinue) {
            Write-Host "CHYBA: proces agenta (PID $svcPid) porad bezi." -ForegroundColor Red
            Write-Host "  Instalace by nechala na stanici smes stare a nove verze – koncim bez zasahu." -ForegroundColor Red
            exit 1
        }
    }

    Write-Host "  Sluzba zastavena, proces ukoncen, soubory i port uvolnene."
}

# ── 2) Kopie souboru (ZACHOVAT agent.config.local.json) ──────
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Write-Host "Kopiruji soubory (zachovavam Config\agent.config.local.json)..."
# /XO nekopiruje starsi; /XF vynecha per-machine local config, aby se neprepsal
robocopy $SourcePath $InstallDir /E /XF agent.config.local.json /R:2 /W:2 /NFL /NDL /NP | Out-Null
if ($LASTEXITCODE -ge 8) {
    Write-Host "CHYBA: robocopy selhal (kod $LASTEXITCODE)." -ForegroundColor Red
    exit 1
}

# Fresh install: nasadit per-machine config ze zdroje (upgrade ho /XF zachova).
$tgtLocal = Join-Path $InstallDir "Config\agent.config.local.json"
$srcLocal = Join-Path $SourcePath "Config\agent.config.local.json"
if ((Test-Path $srcLocal) -and -not (Test-Path $tgtLocal)) {
    Copy-Item $srcLocal $tgtLocal -Force
    Write-Host "Per-machine config nasazen ze zdroje (fresh install)."
}

# Skripty (watchdog) vedle agenta
$scriptsDst = Join-Path $InstallDir "scripts"
New-Item -ItemType Directory -Force -Path $scriptsDst | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'Watch-USBGuardian.ps1') -Destination $scriptsDst -Force
Copy-Item -Path (Join-Path $PSScriptRoot 'Register-Watchdog.ps1')  -Destination $scriptsDst -Force

# ── 3) Vytvorit / aktualizovat sluzbu ────────────────────────
$binPath = Join-Path $InstallDir $exeName
if ($existing) {
    Write-Host "Aktualizuji binPath existujici sluzby..."
    & sc.exe config $ServiceName binPath= "`"$binPath`"" start= auto obj= LocalSystem | Out-Null
} else {
    Write-Host "Vytvarim sluzbu..."
    New-Service -Name $ServiceName -BinaryPathName "`"$binPath`"" `
        -DisplayName $ServiceName -StartupType Automatic `
        -Description "USB Guardian – monitoring pametovych medii (NIS2)." | Out-Null
}

# Recovery: restart pri padu (1. a 2. selhani po 60s, reset citace po 1 dni)
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

# ── 4) Watchdog ──────────────────────────────────────────────
Write-Host "Registruji watchdog..."
& powershell.exe -NonInteractive -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scriptsDst 'Register-Watchdog.ps1')

# ── 5) Kontrola per-machine configu + start ──────────────────
$localCfg = Join-Path $InstallDir "Config\agent.config.local.json"
if (-not (Test-Path $localCfg)) {
    Write-Host ""
    Write-Host "UPOZORNENI: chybi $localCfg" -ForegroundColor Yellow
    Write-Host "  Agent pobezi, ale BEZ sync nastaveni. Vytvor soubor se syncUrl + tls.pinnedThumbprint:"
    Write-Host '  { "whitelist": { "syncUrl": "https://SQL_SERVER:5443" }, "tls": { "pinnedThumbprint": "API_CERT_THUMBPRINT" } }'
}

# ── 6) Zdroj v Event Logu ────────────────────────────────────
# Agent bezi pod SYSTEM a zadnou konzoli nema – co nenapise do Event Logu, po sobe
# na stanici nenecha stopu. Zdroj zalozit tady, kde instalace bezi elevovane.
if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) {
    try {
        New-EventLog -LogName Application -Source $ServiceName -ErrorAction Stop
        Write-Host "Event Log: zalozen zdroj '$ServiceName' v logu Application."
    } catch {
        Write-Host "VAROVANI: zdroj '$ServiceName' v Event Logu se nepodarilo zalozit: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Spoustim sluzbu..."
Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
$limit = (Get-Date).AddSeconds(30)
while ((Get-Service $ServiceName).Status -ne 'Running' -and (Get-Date) -lt $limit) {
    Start-Sleep -Milliseconds 500
}
$st = (Get-Service $ServiceName).Status

# Verze z nasazeneho exe – aby bylo cerne na bilem, ze bezi nova, ne stara.
$verze = "?"
try { $verze = (Get-Item $binPath).VersionInfo.ProductVersion } catch { }

Write-Host ""
if ($st -ne 'Running') {
    Write-Host "CHYBA: sluzba '$ServiceName' po instalaci nebezi (stav: $st)." -ForegroundColor Red
    Write-Host "  Duvod: Get-WinEvent -LogName Application -ProviderName '$ServiceName' -MaxEvents 20"
    exit 1
}

Write-Host "Hotovo. Sluzba '$ServiceName' bezi (verze $verze)." -ForegroundColor Green
Write-Host "  Odinstalace: .\Uninstall-Agent.ps1"
