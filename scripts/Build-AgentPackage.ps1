# ============================================================
# Build-AgentPackage.ps1
# Sestaví KOMPLETNÍ balíček klienta = agent + ToastHelper do jedné složky.
#
# Výstup (self-contained, win-x64, .NET runtime uvnitř – klient nepotřebuje SDK):
#   <Output>\USBGuardian.exe              (agent, Windows služba SYSTEM)
#   <Output>\Config\...                   (agent.config.json + whitelist_public.pem)
#   <Output>\ToastHelper\ToastHelper.exe  (notifikace v user session)
#   <Output>\tasks\*.xml                  (definice scheduled tasků – toast/watchdog)
#   <Output>\scripts\Watch-USBGuardian.ps1 (legacy PS watchdog – jen non-AllSigned)
#
# Tento skript běží na BUILD stroji (dev), NE na klientovi → nemusí být podepsaný.
# Balíček se pak stage-uje na .213 a rozváží přes Deploy-AgentFleet.ps1 (gMSA).
#
# Použití:
#   .\Build-AgentPackage.ps1 -Output D:\deploy\USBGuardianAgent
# ============================================================

param(
    [string] $Output = "D:\deploy\USBGuardianAgent",
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

Write-Host "USB Guardian - build balicku klienta" -ForegroundColor Cyan
Write-Host "  Vystup: $Output"
Write-Host "  Config: $Configuration / $Runtime"
Write-Host ""

# ── 1) Agent (self-contained) do rootu balicku ───────────────
Write-Host "1/3 publish agenta..." -ForegroundColor Cyan
& dotnet publish (Join-Path $repo "agent\USBGuardian") `
    -c $Configuration -r $Runtime --self-contained `
    -o $Output --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "publish agenta selhal ($LASTEXITCODE)" }

# ── 2) ToastHelper (self-contained) do podslozky ToastHelper\ ─
# Podslozka = zadne kolize runtime DLL s agentem; cesta odpovida tasks\toast XML.
Write-Host "2/3 publish ToastHelper..." -ForegroundColor Cyan
& dotnet publish (Join-Path $repo "toast\ToastHelper") `
    -c $Configuration -r $Runtime --self-contained `
    -o (Join-Path $Output "ToastHelper") --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "publish ToastHelper selhal ($LASTEXITCODE)" }

# ── 3) Task definice + legacy skripty vedle balicku ──────────
Write-Host "3/3 kopiruji tasks\ + scripts\..." -ForegroundColor Cyan
$tasksSrc = Join-Path $PSScriptRoot "tasks"
if (Test-Path $tasksSrc) {
    $tasksDst = Join-Path $Output "tasks"
    New-Item -ItemType Directory -Force -Path $tasksDst | Out-Null
    Copy-Item (Join-Path $tasksSrc "*.xml") $tasksDst -Force
}

# ── Offline instalator ───────────────────────────────────────
# Davky patri primo do balicku: kdyz se balicek prenese na stanici, ke ktere
# se deploy kanal nedostane, musi tam byt cim ho nainstalovat a cim ho zase
# uklidit. .cmd schvalne - nepodleha AllSigned z GPO.
foreach ($d in "Install-Agent.cmd", "Uninstall-Agent.cmd") {
    $src = Join-Path $PSScriptRoot $d
    if (Test-Path $src) { Copy-Item $src $Output -Force }
}

Write-Host ""
$exe   = Join-Path $Output "USBGuardian.exe"
$toast = Join-Path $Output "ToastHelper\ToastHelper.exe"
Write-Host ("Hotovo. agent: {0}  toast: {1}" -f (Test-Path $exe), (Test-Path $toast)) -ForegroundColor Green
Write-Host "  Dalsi krok: stage na .213 (robocopy -> C:\Apps\USBGuardianAgentPublish) + Deploy-AgentFleet.ps1"
