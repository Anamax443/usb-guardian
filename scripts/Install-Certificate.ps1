#Requires -RunAsAdministrator
# ============================================================
# Install-Certificate.ps1
# Nainstaluje USB Guardian API certifikát do Trusted Root
# na klientské stanici. Agent pak ověří TLS bez chyby.
#
# Distribuovat přes GPO nebo spustit při instalaci agenta.
#
# Použití:
#   .\Install-Certificate.ps1 -CertPath "\\SERVER\share\usb-guardian.cer"
# ============================================================

param(
    [Parameter(Mandatory)]
    [string]$CertPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $CertPath)) {
    Write-Error "Certifikát nenalezen: $CertPath"
    exit 1
}

# Importovat do Trusted Root CA (LocalMachine)
Import-Certificate `
    -FilePath $CertPath `
    -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null

Write-Host "✓ Certifikát nainstalován do Trusted Root: $CertPath" -ForegroundColor Green
Write-Host "  Agent bude ověřovat TLS spojení se serverem." -ForegroundColor Gray
