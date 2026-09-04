#Requires -RunAsAdministrator
# ============================================================
# New-Certificate.ps1
# Vygeneruje self-signed TLS certifikát pro USB Guardian API
# a nainstaluje ho do Windows Certificate Store.
#
# Spouštět na API serveru (SQL_SERVER) jako Administrator.
# Certifikát je platný 3 roky a exportuje se jako .cer
# pro distribuci na klientské stanice (agent trust).
#
# Použití:
#   .\New-Certificate.ps1
#   .\New-Certificate.ps1 -ServerHostname "muj-server" -ValidYears 5
# ============================================================

param(
    [string]$ServerHostname = $env:COMPUTERNAME,
    [string]$ServerFqdn     = "$env:COMPUTERNAME.$env:USERDNSDOMAIN",
    [int]   $ValidYears     = 3,
    [string]$ExportPath     = "C:\USBGuardian.Api\usb-guardian.cer"
)

$ErrorActionPreference = "Stop"

Write-Host "USB Guardian – generování TLS certifikátu" -ForegroundColor Cyan
Write-Host "  Hostname: $ServerHostname"
Write-Host "  FQDN:     $ServerFqdn"
Write-Host "  Platnost: $ValidYears let"
Write-Host ""

# ── Zkontrolovat existující certifikát ───────────────────────
$existing = Get-ChildItem -Path "Cert:\LocalMachine\My" |
    Where-Object { $_.Subject -like "*CN=$ServerHostname*" } |
    Where-Object { $_.NotAfter -gt (Get-Date).AddDays(30) }

if ($existing) {
    Write-Host "Existující platný certifikát nalezen:" -ForegroundColor Yellow
    Write-Host "  Thumbprint: $($existing.Thumbprint)"
    Write-Host "  Platí do:   $($existing.NotAfter)"
    $overwrite = Read-Host "Přepsat? [y/N]"
    if ($overwrite -ne "y") {
        Write-Host "Zrušeno – používám existující certifikát." -ForegroundColor Green
        exit 0
    }
}

# ── Generování certifikátu ───────────────────────────────────
Write-Host "Generuji certifikát..." -ForegroundColor Cyan

$cert = New-SelfSignedCertificate `
    -DnsName @($ServerHostname, $ServerFqdn, "localhost") `
    -CertStoreLocation "Cert:\LocalMachine\My" `
    -NotAfter (Get-Date).AddYears($ValidYears) `
    -KeyUsage DigitalSignature, KeyEncipherment `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -FriendlyName "USB Guardian API TLS" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.1") # Server Authentication EKU

Write-Host "Certifikát vygenerován:" -ForegroundColor Green
Write-Host "  Thumbprint: $($cert.Thumbprint)"
Write-Host "  Subject:    $($cert.Subject)"
Write-Host "  Platí do:   $($cert.NotAfter)"

# ── Export veřejného certifikátu (.cer) pro agenty ──────────
$exportDir = Split-Path $ExportPath -Parent
if (-not (Test-Path $exportDir)) {
    New-Item -ItemType Directory -Force -Path $exportDir | Out-Null
}

Export-Certificate -Cert $cert -FilePath $ExportPath -Type CERT | Out-Null
Write-Host ""
Write-Host "Veřejný certifikát exportován: $ExportPath" -ForegroundColor Green

# ── Aktualizace appsettings.local.json ───────────────────────
$appSettingsPath = "C:\USBGuardian.Api\appsettings.local.json"

if (Test-Path $appSettingsPath) {
    $settings = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
} else {
    $settings = [PSCustomObject]@{}
}

# Přidat/aktualizovat Kestrel sekci
$settings | Add-Member -NotePropertyName "Kestrel" -NotePropertyValue ([PSCustomObject]@{
    Endpoints = [PSCustomObject]@{
        Https = [PSCustomObject]@{
            Url = "https://0.0.0.0:5443"
            Certificate = [PSCustomObject]@{
                Subject     = $ServerHostname
                Store       = "My"
                Location    = "LocalMachine"
                AllowInvalid = $false
            }
        }
        Http = [PSCustomObject]@{
            Url = "http://0.0.0.0:5050"
        }
    }
}) -Force

$settings | ConvertTo-Json -Depth 10 |
    Set-Content -Path $appSettingsPath -Encoding UTF8

Write-Host "appsettings.local.json aktualizován: $appSettingsPath" -ForegroundColor Green

# ── Firewall – otevřít HTTPS port ────────────────────────────
$fwRule = Get-NetFirewallRule -DisplayName "USB Guardian API HTTPS" -ErrorAction SilentlyContinue
if (-not $fwRule) {
    New-NetFirewallRule `
        -DisplayName "USB Guardian API HTTPS" `
        -Direction Inbound `
        -Protocol TCP `
        -LocalPort 5443 `
        -Action Allow | Out-Null
    Write-Host "Firewall pravidlo přidáno: port 5443" -ForegroundColor Green
} else {
    Write-Host "Firewall pravidlo již existuje: port 5443" -ForegroundColor Yellow
}

# ── Restart služby ────────────────────────────────────────────
$svc = Get-Service -Name "USB Guardian API" -ErrorAction SilentlyContinue
if ($svc) {
    Restart-Service "USB Guardian API"
    Write-Host "Služba restartována." -ForegroundColor Green
}

Write-Host ""
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "Další kroky:" -ForegroundColor Cyan
Write-Host "  1. Zkopírovat $ExportPath na agenty"
Write-Host "  2. Na každé stanici spustit Install-Certificate.ps1"
Write-Host "  3. Aktualizovat agent.config.local.json: syncUrl → https://$ServerHostname:5443"
Write-Host "═══════════════════════════════════════════" -ForegroundColor Cyan
