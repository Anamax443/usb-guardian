#Requires -RunAsAdministrator
# ============================================================
# New-Certificate.ps1
# Vygeneruje self-signed TLS certifikat pro USB Guardian API
# a nainstaluje ho do Windows Certificate Store.
#
# Spoustet na API serveru (SQL_SERVER) jako Administrator.
# Certifikat je platny 3 roky a exportuje se jako .cer
# pro distribuci na klientske stanice (agent trust).
#
# Pouziti:
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

Write-Host "USB Guardian - generovani TLS certifikatu" -ForegroundColor Cyan
Write-Host "  Hostname: $ServerHostname"
Write-Host "  FQDN:     $ServerFqdn"
Write-Host "  Platnost: $ValidYears let"
Write-Host ""

# -- Zkontrolovat existujici certifikat --
$existing = Get-ChildItem -Path "Cert:\LocalMachine\My" |
    Where-Object { $_.Subject -like "*CN=$ServerHostname*" } |
    Where-Object { $_.NotAfter -gt (Get-Date).AddDays(30) }

if ($existing) {
    Write-Host "Existujici platny certifikat nalezen:" -ForegroundColor Yellow
    Write-Host "  Thumbprint: $($existing.Thumbprint)"
    Write-Host "  Plati do:   $($existing.NotAfter)"
    $overwrite = Read-Host "Prepsat? [y/N]"
    if ($overwrite -ne "y") {
        Write-Host "Zruseno - pouzivam existujici certifikat." -ForegroundColor Green
        exit 0
    }
}

# -- Generovani certifikatu --
Write-Host "Generuji certifikat..." -ForegroundColor Cyan

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

Write-Host "Certifikat vygenerovan:" -ForegroundColor Green
Write-Host "  Thumbprint: $($cert.Thumbprint)"
Write-Host "  Subject:    $($cert.Subject)"
Write-Host "  Plati do:   $($cert.NotAfter)"

# -- Export verejneho certifikatu (.cer) pro agenty --
$exportDir = Split-Path $ExportPath -Parent
if (-not (Test-Path $exportDir)) {
    New-Item -ItemType Directory -Force -Path $exportDir | Out-Null
}

Export-Certificate -Cert $cert -FilePath $ExportPath -Type CERT | Out-Null
Write-Host ""
Write-Host "Verejny certifikat exportovan: $ExportPath" -ForegroundColor Green

# -- Aktualizace appsettings.local.json --
$appSettingsPath = "C:\USBGuardian.Api\appsettings.local.json"

if (Test-Path $appSettingsPath) {
    $settings = Get-Content $appSettingsPath -Raw | ConvertFrom-Json
} else {
    $settings = [PSCustomObject]@{}
}

# Pridat/aktualizovat Kestrel sekci
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

Write-Host "appsettings.local.json aktualizovan: $appSettingsPath" -ForegroundColor Green

# -- Firewall - otevrit HTTPS port --
$fwRule = Get-NetFirewallRule -DisplayName "USB Guardian API HTTPS" -ErrorAction SilentlyContinue
if (-not $fwRule) {
    New-NetFirewallRule `
        -DisplayName "USB Guardian API HTTPS" `
        -Direction Inbound `
        -Protocol TCP `
        -LocalPort 5443 `
        -Action Allow | Out-Null
    Write-Host "Firewall pravidlo pridano: port 5443" -ForegroundColor Green
} else {
    Write-Host "Firewall pravidlo jiz existuje: port 5443" -ForegroundColor Yellow
}

# -- Restart sluzby --
$svc = Get-Service -Name "USB Guardian API" -ErrorAction SilentlyContinue
if ($svc) {
    Restart-Service "USB Guardian API"
    Write-Host "Sluzba restartovana." -ForegroundColor Green
}

Write-Host ""
Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "Dalsi kroky:" -ForegroundColor Cyan
Write-Host "  1. Zkopirovat $ExportPath na agenty"
Write-Host "  2. Na kazde stanici spustit Install-Certificate.ps1"
Write-Host "  3. Aktualizovat agent.config.local.json: syncUrl -> https://$ServerHostname:5443"
Write-Host "======================================================" -ForegroundColor Cyan
