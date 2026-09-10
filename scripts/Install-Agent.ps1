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

# SIG # Begin signature block
# MIIeXgYJKoZIhvcNAQcCoIIeTzCCHksCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCC1sAucWwxhI4Xo
# lpbe5SMdaW+Gd7FCXULrf2NutIH1G6CCGRgwggWNMIIEdaADAgECAhAOmxiO+dAt
# 5+/bUOIIQBhaMA0GCSqGSIb3DQEBDAUAMGUxCzAJBgNVBAYTAlVTMRUwEwYDVQQK
# EwxEaWdpQ2VydCBJbmMxGTAXBgNVBAsTEHd3dy5kaWdpY2VydC5jb20xJDAiBgNV
# BAMTG0RpZ2lDZXJ0IEFzc3VyZWQgSUQgUm9vdCBDQTAeFw0yMjA4MDEwMDAwMDBa
# Fw0zMTExMDkyMzU5NTlaMGIxCzAJBgNVBAYTAlVTMRUwEwYDVQQKEwxEaWdpQ2Vy
# dCBJbmMxGTAXBgNVBAsTEHd3dy5kaWdpY2VydC5jb20xITAfBgNVBAMTGERpZ2lD
# ZXJ0IFRydXN0ZWQgUm9vdCBHNDCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoC
# ggIBAL/mkHNo3rvkXUo8MCIwaTPswqclLskhPfKK2FnC4SmnPVirdprNrnsbhA3E
# MB/zG6Q4FutWxpdtHauyefLKEdLkX9YFPFIPUh/GnhWlfr6fqVcWWVVyr2iTcMKy
# unWZanMylNEQRBAu34LzB4TmdDttceItDBvuINXJIB1jKS3O7F5OyJP4IWGbNOsF
# xl7sWxq868nPzaw0QF+xembud8hIqGZXV59UWI4MK7dPpzDZVu7Ke13jrclPXuU1
# 5zHL2pNe3I6PgNq2kZhAkHnDeMe2scS1ahg4AxCN2NQ3pC4FfYj1gj4QkXCrVYJB
# MtfbBHMqbpEBfCFM1LyuGwN1XXhm2ToxRJozQL8I11pJpMLmqaBn3aQnvKFPObUR
# WBf3JFxGj2T3wWmIdph2PVldQnaHiZdpekjw4KISG2aadMreSx7nDmOu5tTvkpI6
# nj3cAORFJYm2mkQZK37AlLTSYW3rM9nF30sEAMx9HJXDj/chsrIRt7t/8tWMcCxB
# YKqxYxhElRp2Yn72gLD76GSmM9GJB+G9t+ZDpBi4pncB4Q+UDCEdslQpJYls5Q5S
# UUd0viastkF13nqsX40/ybzTQRESW+UQUOsxxcpyFiIJ33xMdT9j7CFfxCBRa2+x
# q4aLT8LWRV+dIPyhHsXAj6KxfgommfXkaS+YHS312amyHeUbAgMBAAGjggE6MIIB
# NjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBTs1+OC0nFdZEzfLmc/57qYrhwP
# TzAfBgNVHSMEGDAWgBRF66Kv9JLLgjEtUYunpyGd823IDzAOBgNVHQ8BAf8EBAMC
# AYYweQYIKwYBBQUHAQEEbTBrMCQGCCsGAQUFBzABhhhodHRwOi8vb2NzcC5kaWdp
# Y2VydC5jb20wQwYIKwYBBQUHMAKGN2h0dHA6Ly9jYWNlcnRzLmRpZ2ljZXJ0LmNv
# bS9EaWdpQ2VydEFzc3VyZWRJRFJvb3RDQS5jcnQwRQYDVR0fBD4wPDA6oDigNoY0
# aHR0cDovL2NybDMuZGlnaWNlcnQuY29tL0RpZ2lDZXJ0QXNzdXJlZElEUm9vdENB
# LmNybDARBgNVHSAECjAIMAYGBFUdIAAwDQYJKoZIhvcNAQEMBQADggEBAHCgv0Nc
# Vec4X6CjdBs9thbX979XB72arKGHLOyFXqkauyL4hxppVCLtpIh3bb0aFPQTSnov
# Lbc47/T/gLn4offyct4kvFIDyE7QKt76LVbP+fT3rDB6mouyXtTP0UNEm0Mh65Zy
# oUi0mcudT6cGAxN3J0TU53/oWajwvy8LpunyNDzs9wPHh6jSTEAZNUZqaVSwuKFW
# juyk1T3osdz9HNj0d1pcVIxv76FQPfx2CWiEn2/K2yCNNWAcAgPLILCsWKAOQGPF
# mCLBsln1VWvPJ6tsds5vIy30fnFqI2si/xK4VC0nftg62fC2h5b9W9FcrBjDTZ9z
# twGpn1eqXijiuZQwggXaMIIDwqADAgECAhMZAAAAhQfvgiNOkFhuAAAAAACFMA0G
# CSqGSIb3DQEBDQUAMEYxEzARBgoJkiaJk/IsZAEZFgNsb2MxGjAYBgoJkiaJk/Is
# ZAEZFgpheGluZXR3b3JrMRMwEQYDVQQDEwpCLVMtVy1DQTIyMB4XDTI2MDYxNzEx
# MDkzM1oXDTI4MDYxNzExMTkzM1owXTELMAkGA1UEBhMCQ1oxDTALBgNVBAcTBEJy
# bm8xDjAMBgNVBAoTBUF4aW1hMQswCQYDVQQLEwJJVDEiMCAGA1UEAxMZcG93ZXJz
# aGVsbC5heGluZXR3b3JrLmxvYzB2MBAGByqGSM49AgEGBSuBBAAiA2IABMRThCb2
# dEtPjW08Gl+dFvco9KymXyCT3+9CLJ1R9iRyL3QnC41JyqcZTHNWV+RrArLYxBtS
# mspg9WkwavaYsivg4DA5f9e03a6v7P6oo1MYi7jJFzIsozYNccs1febLJ6OCAlYw
# ggJSMA4GA1UdDwEB/wQEAwIHgDATBgNVHSUEDDAKBggrBgEFBQcDAzAdBgNVHQ4E
# FgQUJvJxE/zq3s8V8DdPq9tV8ph79TIwHwYDVR0jBBgwFoAU5WQVg9uY/k5uW+Qo
# fYFsPHB9icEwgcwGA1UdHwSBxDCBwTCBvqCBu6CBuIaBtWxkYXA6Ly8vQ049Qi1T
# LVctQ0EyMixDTj1CLVMtVy1DQSxDTj1DRFAsQ049UHVibGljJTIwS2V5JTIwU2Vy
# dmljZXMsQ049U2VydmljZXMsQ049Q29uZmlndXJhdGlvbixEQz1heGluZXR3b3Jr
# LERDPWxvYz9jZXJ0aWZpY2F0ZVJldm9jYXRpb25MaXN0P2Jhc2U/b2JqZWN0Q2xh
# c3M9Y1JMRGlzdHJpYnV0aW9uUG9pbnQwgb8GCCsGAQUFBwEBBIGyMIGvMIGsBggr
# BgEFBQcwAoaBn2xkYXA6Ly8vQ049Qi1TLVctQ0EyMixDTj1BSUEsQ049UHVibGlj
# JTIwS2V5JTIwU2VydmljZXMsQ049U2VydmljZXMsQ049Q29uZmlndXJhdGlvbixE
# Qz1heGluZXR3b3JrLERDPWxvYz9jQUNlcnRpZmljYXRlP2Jhc2U/b2JqZWN0Q2xh
# c3M9Y2VydGlmaWNhdGlvbkF1dGhvcml0eTA9BgkrBgEEAYI3FQcEMDAuBiYrBgEE
# AYI3FQiFtqJghrGOEYXRmyeD4OUlh/eZcHKGnPdcgqLaaAIBZAIBDzAbBgkrBgEE
# AYI3FQoEDjAMMAoGCCsGAQUFBwMDMA0GCSqGSIb3DQEBDQUAA4ICAQBGEz8TYbhm
# n4zjFKUzh4W5LpslzsYTaEXBaGHSHRexg2n4B81jjyQVDtkBTAZ67WOw5OJIdNEc
# VR+iuUjsjQH8K9I0Z9vk/AlcnDQlO9e7par2YzLSQ+am9E1BGo+KJH7T+n2sQHrp
# C4VoOtWzyz0q7cfu1uwDQ2kc2WKThdK5COT1PDrPaZ7zpPLdu+bCTaVzK/Y2jOCJ
# m9VGpsiluTKQY9n1h7kQrq5643qY3Js8gedWNgMzTii0Y25VNvkRfophP8Zc2NwD
# bGLszFV7Ac2vlFppQdG8mRxXu9XxAozQqECbk1beQFR68I3ZM5GSEwBrjEYPeVoz
# XO6s9xe+Jdcox7RzPFBAaUb36qD0yjB6MswNpN6mX1uRsSygiIcPs0ARhr+VB8Sm
# CKK7AB8ojL2o/PFqvez67Nvy0bCcJNEa0vEACyqsCD5GpVlaTN3KQt7Xes+/PyPT
# Vf80Gm5iO/jDSrmxwdMdxzKBTffCIZG7QFi0D+pINKU+0v7HALhI13jJneaEiIjc
# Vz8/mS/0npRX3cXjEBb2hKDInl7MKeRbMTFyNkyUzAjmf2q6NypdaaRUDtD3XBxM
# KrNgrbqSGcIc1FTGmlmMjsguuJyQQk+y69RoKXCcUJHgBdSO2DS5ewksBLtk16Rx
# 2BSSQ5iCFYkLV4z0aJRfZ+UfQ+pE3IwxSDCCBrQwggScoAMCAQICEA3HrFcF/yGZ
# LkBDIgw6SYYwDQYJKoZIhvcNAQELBQAwYjELMAkGA1UEBhMCVVMxFTATBgNVBAoT
# DERpZ2lDZXJ0IEluYzEZMBcGA1UECxMQd3d3LmRpZ2ljZXJ0LmNvbTEhMB8GA1UE
# AxMYRGlnaUNlcnQgVHJ1c3RlZCBSb290IEc0MB4XDTI1MDUwNzAwMDAwMFoXDTM4
# MDExNDIzNTk1OVowaTELMAkGA1UEBhMCVVMxFzAVBgNVBAoTDkRpZ2lDZXJ0LCBJ
# bmMuMUEwPwYDVQQDEzhEaWdpQ2VydCBUcnVzdGVkIEc0IFRpbWVTdGFtcGluZyBS
# U0E0MDk2IFNIQTI1NiAyMDI1IENBMTCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCC
# AgoCggIBALR4MdMKmEFyvjxGwBysddujRmh0tFEXnU2tjQ2UtZmWgyxU7UNqEY81
# FzJsQqr5G7A6c+Gh/qm8Xi4aPCOo2N8S9SLrC6Kbltqn7SWCWgzbNfiR+2fkHUil
# jNOqnIVD/gG3SYDEAd4dg2dDGpeZGKe+42DFUF0mR/vtLa4+gKPsYfwEu7EEbkC9
# +0F2w4QJLVSTEG8yAR2CQWIM1iI5PHg62IVwxKSpO0XaF9DPfNBKS7Zazch8NF5v
# p7eaZ2CVNxpqumzTCNSOxm+SAWSuIr21Qomb+zzQWKhxKTVVgtmUPAW35xUUFREm
# DrMxSNlr/NsJyUXzdtFUUt4aS4CEeIY8y9IaaGBpPNXKFifinT7zL2gdFpBP9qh8
# SdLnEut/GcalNeJQ55IuwnKCgs+nrpuQNfVmUB5KlCX3ZA4x5HHKS+rqBvKWxdCy
# QEEGcbLe1b8Aw4wJkhU1JrPsFfxW1gaou30yZ46t4Y9F20HHfIY4/6vHespYMQmU
# iote8ladjS/nJ0+k6MvqzfpzPDOy5y6gqztiT96Fv/9bH7mQyogxG9QEPHrPV6/7
# umw052AkyiLA6tQbZl1KhBtTasySkuJDpsZGKdlsjg4u70EwgWbVRSX1Wd4+zoFp
# p4Ra+MlKM2baoD6x0VR4RjSpWM8o5a6D8bpfm4CLKczsG7ZrIGNTAgMBAAGjggFd
# MIIBWTASBgNVHRMBAf8ECDAGAQH/AgEAMB0GA1UdDgQWBBTvb1NK6eQGfHrK4pBW
# 9i/USezLTjAfBgNVHSMEGDAWgBTs1+OC0nFdZEzfLmc/57qYrhwPTzAOBgNVHQ8B
# Af8EBAMCAYYwEwYDVR0lBAwwCgYIKwYBBQUHAwgwdwYIKwYBBQUHAQEEazBpMCQG
# CCsGAQUFBzABhhhodHRwOi8vb2NzcC5kaWdpY2VydC5jb20wQQYIKwYBBQUHMAKG
# NWh0dHA6Ly9jYWNlcnRzLmRpZ2ljZXJ0LmNvbS9EaWdpQ2VydFRydXN0ZWRSb290
# RzQuY3J0MEMGA1UdHwQ8MDowOKA2oDSGMmh0dHA6Ly9jcmwzLmRpZ2ljZXJ0LmNv
# bS9EaWdpQ2VydFRydXN0ZWRSb290RzQuY3JsMCAGA1UdIAQZMBcwCAYGZ4EMAQQC
# MAsGCWCGSAGG/WwHATANBgkqhkiG9w0BAQsFAAOCAgEAF877FoAc/gc9EXZxML2+
# C8i1NKZ/zdCHxYgaMH9Pw5tcBnPw6O6FTGNpoV2V4wzSUGvI9NAzaoQk97frPBtI
# j+ZLzdp+yXdhOP4hCFATuNT+ReOPK0mCefSG+tXqGpYZ3essBS3q8nL2UwM+NMvE
# uBd/2vmdYxDCvwzJv2sRUoKEfJ+nN57mQfQXwcAEGCvRR2qKtntujB71WPYAgwPy
# WLKu6RnaID/B0ba2H3LUiwDRAXx1Neq9ydOal95CHfmTnM4I+ZI2rVQfjXQA1WSj
# jf4J2a7jLzWGNqNX+DF0SQzHU0pTi4dBwp9nEC8EAqoxW6q17r0z0noDjs6+BFo+
# z7bKSBwZXTRNivYuve3L2oiKNqetRHdqfMTCW/NmKLJ9M+MtucVGyOxiDf06VXxy
# KkOirv6o02OoXN4bFzK0vlNMsvhlqgF2puE6FndlENSmE+9JGYxOGLS/D284NHNb
# oDGcmWXfwXRy4kbu4QFhOm0xJuF2EZAOk5eCkhSxZON3rGlHqhpB/8MluDezooIs
# 8CVnrpHMiD2wL40mm53+/j7tFaxYKIqL0Q4ssd8xHZnIn/7GELH3IdvG2XlM9q7W
# P/UwgOkw/HQtyRN62JK4S1C8uw3PdBunvAZapsiI5YKdvlarEvf8EA+8hcpSM9LH
# JmyrxaFtoza2zNaQ9k+5t1wwggbtMIIE1aADAgECAhAIT9wzT35FTtvDD4/5khg1
# MA0GCSqGSIb3DQEBCwUAMGkxCzAJBgNVBAYTAlVTMRcwFQYDVQQKEw5EaWdpQ2Vy
# dCwgSW5jLjFBMD8GA1UEAxM4RGlnaUNlcnQgVHJ1c3RlZCBHNCBUaW1lU3RhbXBp
# bmcgUlNBNDA5NiBTSEEyNTYgMjAyNSBDQTEwHhcNMjYwODA1MDAwMDAwWhcNMzcx
# MTA0MjM1OTU5WjBjMQswCQYDVQQGEwJVUzEXMBUGA1UEChMORGlnaUNlcnQsIElu
# Yy4xOzA5BgNVBAMTMkRpZ2lDZXJ0IFNIQTI1NiBSU0E0MDk2IFRpbWVzdGFtcCBS
# ZXNwb25kZXIgMjAyNiAxMIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEA
# tnum8sn+zUr41JtMZbP9OMYw+HwJDpG5xkIu/lqcfNYmMX81YmsUiHLbh9ykpeWB
# GKTLhYBrAN9Tdg/QEzG32XcObmgIblnr0CoQ3WSAeDZ6nH6X6VkFyYkJw3QBJREw
# vm4UhLzSxmwPA7cFKRTEOMsmEEj6qJk/dqLEAL+oQYuOwE2UuiX1Vnul8YReIyWd
# 4kgLn9gq6LNXM0UplkR6jL/QHxmb6fMoGBJYbnaUI7XD6cKDpekK2SVMld4iDbze
# HDtOaaxldH5IxuNusQ69nd8/ZXEiB5Hbxj3RlK13cX1W4DlFXKdv/CEhM8Cj1vvl
# mvhNroyPdRGbbpBlgyf8Wdu5N6ByhFwURn0U6ozlPoxN22v+fviUhP+6DR547OZn
# pBMWDfei1f5sVGwiiW/KQTWOK97g+4RJpPzPNV4VYMAwO2jM2Aty2QYPVmOQTJm0
# msuXnJrSbl2gf9JylpkJlWXqk1Q4LJsxz+TELoQCZIljbgvTJgoPU2R12ydv8i1U
# qL/adelA0y7U9Pmmtbze9Xx3rtajC5SzQd1jgfwAwsa90v9YcSPdmeoyoBBA/27c
# CL237l5DTYYPDLQ4ON3OLTGWnvRb6jDrf/T75gMRfUzSLCBQfBusm9+mSWRlC/Df
# 6S/e9Q8i13CuhzOT2Jx+V/nlbXM4QoBwlUAhelwwJT0CAwEAAaOCAZUwggGRMAwG
# A1UdEwEB/wQCMAAwHQYDVR0OBBYEFBTJY4owLtRK+26U8+bjQH717M3iMB8GA1Ud
# IwQYMBaAFO9vU0rp5AZ8esrikFb2L9RJ7MtOMA4GA1UdDwEB/wQEAwIHgDAWBgNV
# HSUBAf8EDDAKBggrBgEFBQcDCDCBlQYIKwYBBQUHAQEEgYgwgYUwJAYIKwYBBQUH
# MAGGGGh0dHA6Ly9vY3NwLmRpZ2ljZXJ0LmNvbTBdBggrBgEFBQcwAoZRaHR0cDov
# L2NhY2VydHMuZGlnaWNlcnQuY29tL0RpZ2lDZXJ0VHJ1c3RlZEc0VGltZVN0YW1w
# aW5nUlNBNDA5NlNIQTI1NjIwMjVDQTEuY3J0MF8GA1UdHwRYMFYwVKBSoFCGTmh0
# dHA6Ly9jcmwzLmRpZ2ljZXJ0LmNvbS9EaWdpQ2VydFRydXN0ZWRHNFRpbWVTdGFt
# cGluZ1JTQTQwOTZTSEEyNTYyMDI1Q0ExLmNybDAgBgNVHSAEGTAXMAgGBmeBDAEE
# AjALBglghkgBhv1sBwEwDQYJKoZIhvcNAQELBQADggIBAI3FOmEenVIK35msCYB+
# fShAsWvSYvLBItoNdAgQ2jIqrGsVsluXMJU/+mRebBc52s6lbKAvOVPXaizmKkML
# LflEEKDZQx4CkS2t8aHPjkXha3hYZ010htFa3dhNgmalH5vuWvh3tTCf4frTS7gP
# tGc4Z/xaPhQ2AB1mR8eEe/WbH0RWHvVIl6VwQ3+g5FKNfN2N/DWJkf13w2H+2Gfq
# Efbd35Ww8CvoYBjLNIDTadcPWdgsjsiOaK/7EsKJgLjUNIVgvcaFOLLQ/GlrA+0Z
# HJoFUbOr5SJN8zykPspXIXlpDJY/gqFUZRROeab9GVgmhbdOJcD/63RhxPahFUGb
# ckRONqMe6DYAv6/mOG0pWd3cPStsdcS7buj5DyniwRY8yooMH6ptx5vpP/pZzBPB
# eZD2U4IsthyxB5Jaa8qrOkB5z160TXiM5ADMspZ0TfD9MJoq0tFpFPssKRFhWeED
# YPvcUuN7U7lvcdHl4ezQ3NT/7Ffs1sR1yh/LRbdZ3B3Vc6q2WmD8mDC0p9kzl2o7
# 3iVtS946IkEj7FkRsZGww1teYxERROC745xrtjvcw9ZyyUjHZWGRIpJeMNsPquCD
# f0fkyHtB+J4AiNZqCQk23rxh+KbpyMTNVKItJ5l92Svl20U9NbqMBOVYl1h54NEY
# LJq1/xHWFKPNK903zJZA9P2DMYIEnDCCBJgCAQEwXTBGMRMwEQYKCZImiZPyLGQB
# GRYDbG9jMRowGAYKCZImiZPyLGQBGRYKYXhpbmV0d29yazETMBEGA1UEAxMKQi1T
# LVctQ0EyMgITGQAAAIUH74IjTpBYbgAAAAAAhTANBglghkgBZQMEAgEFAKCBhDAY
# BgorBgEEAYI3AgEMMQowCKACgAChAoAAMBkGCSqGSIb3DQEJAzEMBgorBgEEAYI3
# AgEEMBwGCisGAQQBgjcCAQsxDjAMBgorBgEEAYI3AgEVMC8GCSqGSIb3DQEJBDEi
# BCC3ohwF+rcrSskpz+ityNCfxNDN83cXJ2xjBnjlT02eaTALBgcqhkjOPQIBBQAE
# ZzBlAjEAq/ZTu1YaUoq3Rd2eCPpzSoOGbdIQb+QqxFvQ+1za5kfaVflrj1RVxDEL
# h7J85W8gAjBVlZda4gYtZC2e7CB1o7XFvOJHbqfcmfhoLnCyFkkDJ7SFZI3Cbv6j
# Dx4VNl0pyEWhggMmMIIDIgYJKoZIhvcNAQkGMYIDEzCCAw8CAQEwfTBpMQswCQYD
# VQQGEwJVUzEXMBUGA1UEChMORGlnaUNlcnQsIEluYy4xQTA/BgNVBAMTOERpZ2lD
# ZXJ0IFRydXN0ZWQgRzQgVGltZVN0YW1waW5nIFJTQTQwOTYgU0hBMjU2IDIwMjUg
# Q0ExAhAIT9wzT35FTtvDD4/5khg1MA0GCWCGSAFlAwQCAQUAoGkwGAYJKoZIhvcN
# AQkDMQsGCSqGSIb3DQEHATAcBgkqhkiG9w0BCQUxDxcNMjYwOTEwMDc0ODU5WjAv
# BgkqhkiG9w0BCQQxIgQgzp/GIbaUAVRGNGPmazwM34jj9Dt8G/ZpjsRz0pBjorMw
# DQYJKoZIhvcNAQEBBQAEggIAaXvI7+66o9eWqKCeynC3SqzjqqiBNv58ftpPJqO0
# 2W4H9yPHQEi/DZhcL8Ywsb0EdUIuj7U/J7/jaw4q0TgnJlbKbUDDQdtmutJ2W0rW
# qv+V3gMwxJxu1vosEL43lLdEGMse8lh+j8p4YtiBfWwY+di5uaieNZKgVySxQa2x
# ScTdxnnFKnT3yFREZCT+Vu4pIgN6hn56drmHBWhS4vfR9WeYiJzH0I9LPliuYUTJ
# F5o5iiDjwisZ31AJaEICdJ2g2gIG25+N7PLgaMVQU8C6A7JXHoRtHCfbc8Gw6Hwx
# XZRYwCTeXaMUNstFcQh+NHnNpsbFt0k+0bZUKdiHyceESJO9byC582RETu9CV6H1
# dl31u7bTJgHOO1l4ClPrT/Kj+9i2XgfPRi5C5W/rJ/LNcbBc6Yb+HEpwfO1AimkA
# 82Yx5bj52L4NY6dZ8HlSvd+PhV3rEmlsFpZnu2T5p1wyvoMAjHAYDTtwqCMtFqzK
# /6+vpaNeyHkoO2v99n659gqlZ0pqBAe3rGzj84OnAfkheQVOTTBO+abm2vqZemLV
# FRYHIoNIjvVLtrdHreE+u80vRvFRTi1aG0PspXson4ZmiR1pNyALIdRFePQ6HW8/
# HMt8X/7pzwiatd5skh9IkCMsXx/guG5K/7qPARyHVjcEz/gWfbN0QEiSbPOIc4gA
# 5sg=
# SIG # End signature block
