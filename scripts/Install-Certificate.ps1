#Requires -RunAsAdministrator
# ============================================================
# Install-Certificate.ps1
# Nainstaluje USB Guardian API certifikat do Trusted Root
# na klientske stanici. Agent pak overi TLS bez chyby.
#
# Distribuovat pres GPO nebo spustit pri instalaci agenta.
#
# Pouziti:
#   .\Install-Certificate.ps1 -CertPath "\\SERVER\share\usb-guardian.cer"
# ============================================================

param(
    [Parameter(Mandatory)]
    [string]$CertPath
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $CertPath)) {
    Write-Error "Certifikat nenalezen: $CertPath"
    exit 1
}

# Importovat do Trusted Root CA (LocalMachine)
Import-Certificate `
    -FilePath $CertPath `
    -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null

Write-Host "Certifikat nainstalovan do Trusted Root: $CertPath" -ForegroundColor Green
Write-Host "  Agent bude overovat TLS spojeni se serverem." -ForegroundColor Gray
