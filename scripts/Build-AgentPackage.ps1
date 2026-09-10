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
# Balíček se pak stage-uje na APP_SERVER a rozváží přes Deploy-AgentFleet.ps1 (gMSA).
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

# ── Kontrola konfigurace balicku ─────────────────────────────
# Lokalni konzole MA byt na fleetu zapnuta: je to break-glass. Dostane se do
# ni jen LOKALNI ADMIN te stanice (u nas ucty pcadmin.*, ne bezny ucet
# uzivatele) a docasne pres ni vypne blokovani na stroji, ktery na server
# nedosahne. Bezny uzivatel se dovnitr nedostane a ma to tak byt — vypnuti
# blokovani je zasah do vynucovane politiky. Bez konzole nema ani technik
# u te stanice zadnou cestu, jak blok docasne sundat.
$cfgPath = Join-Path $Output "Config\agent.config.local.json"
if (Test-Path $cfgPath) {
    $cfg = Get-Content $cfgPath -Raw
    if ($cfg -notmatch '"localConsole"') {
        Write-Host "VAROVANI: balicek nema localConsole - na stanici nepujde break-glass." -ForegroundColor Yellow
    } elseif ($cfg -match '"localConsole"\s*:\s*{[^}]*"enabled"\s*:\s*false') {
        Write-Host "VAROVANI: lokalni konzole je VYPNUTA - lokalni admin te stanice nevypne blokovani offline." -ForegroundColor Yellow
    }
    if ($cfg -match 'YOUR_API_SERVER') {
        Write-Host "VAROVANI: sablonovy syncUrl (YOUR_API_SERVER) - agent se nepripoji." -ForegroundColor Yellow
    }
} else {
    Write-Host "VAROVANI: balicek nema Config\agent.config.local.json - agent nebude vedet, kam se hlasit." -ForegroundColor Yellow
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
Write-Host "  Dalsi krok: stage na APP_SERVER (robocopy -> C:\Apps\USBGuardianAgentPublish) + Deploy-AgentFleet.ps1"

# SIG # Begin signature block
# MIIeXQYJKoZIhvcNAQcCoIIeTjCCHkoCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCCqxmPrwMcHqtdQ
# PFlWFTOxxUpA89HSg3Gkb88rGxNs0aCCGRgwggWNMIIEdaADAgECAhAOmxiO+dAt
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
# LJq1/xHWFKPNK903zJZA9P2DMYIEmzCCBJcCAQEwXTBGMRMwEQYKCZImiZPyLGQB
# GRYDbG9jMRowGAYKCZImiZPyLGQBGRYKYXhpbmV0d29yazETMBEGA1UEAxMKQi1T
# LVctQ0EyMgITGQAAAIUH74IjTpBYbgAAAAAAhTANBglghkgBZQMEAgEFAKCBhDAY
# BgorBgEEAYI3AgEMMQowCKACgAChAoAAMBkGCSqGSIb3DQEJAzEMBgorBgEEAYI3
# AgEEMBwGCisGAQQBgjcCAQsxDjAMBgorBgEEAYI3AgEVMC8GCSqGSIb3DQEJBDEi
# BCD7ncDyWZA3Q2UGD5bo7IUhM5yRxye4yAvVKn0ownfexzALBgcqhkjOPQIBBQAE
# ZjBkAjAW8wYCyRa+N6kXmU8Mh8b1Kml36/KKgomEkdQtJQix0Zx5fmalqbFxp0BL
# H4nfn58CMEyAw9xNCDxvE6LOhyZom6sGZxuA2aul/oAbEsp80MXmfkzqcJOIZIso
# SCZM5/t7G6GCAyYwggMiBgkqhkiG9w0BCQYxggMTMIIDDwIBATB9MGkxCzAJBgNV
# BAYTAlVTMRcwFQYDVQQKEw5EaWdpQ2VydCwgSW5jLjFBMD8GA1UEAxM4RGlnaUNl
# cnQgVHJ1c3RlZCBHNCBUaW1lU3RhbXBpbmcgUlNBNDA5NiBTSEEyNTYgMjAyNSBD
# QTECEAhP3DNPfkVO28MPj/mSGDUwDQYJYIZIAWUDBAIBBQCgaTAYBgkqhkiG9w0B
# CQMxCwYJKoZIhvcNAQcBMBwGCSqGSIb3DQEJBTEPFw0yNjA5MTAwNzQ4NThaMC8G
# CSqGSIb3DQEJBDEiBCAY/K8/Q39Au56owfScgs8KlwjdErCa4R/WINdmMs5DuzAN
# BgkqhkiG9w0BAQEFAASCAgCaM/spChgEy6it6yRW3AbxZd6+M+3ToJa/XbxOp0zE
# YB9yu4efFpXZKa1Z/SCFYEI6saSoxmlBR3BW3Px1Vz2n/yCXOuwzvLVTV9QLtEOI
# fexAf9qMglzeEN4+ZgCihD1YfNoeJTzSxGrL7WBnfbLK7s2hUfElgQPshouAYAel
# qyvUg0DZCWOfAz9t+tG+Yr5SlB3mDRJH9HygVVtpZYEVytI0fgy3STgtzwt+63s8
# cLs0KjIDBCDwaGD9D24o+wHCafc4n37SmyeQZRgjJ9XM+WLTtUxR1MF3i97BBynz
# b6OqHHfimHHIfqWVtMP7vPrDiLn6i/8joV4+eHlYAOUwXj0LxNVAU9wgKBPA90l8
# Bsv3Otgh5Ogi07RoR4n53b/nNV7uzLGv49JzXCffW3RLO+do+lp+iGNsoRpTwXu1
# QL5Z8pnI4gMtOl1xJfUQHQFzp6XfVGCAlIFIhLozqDLQVgTF6ToXwQpmhnP9EJUD
# Vcq+V+sFmGSmpALCFNehEylUygAjY/Cg6lkgYW4ofeu+kwClXFdUjTWkZR1sW9h3
# B3AoSJoUZ51MpKxG483xdEu8AJEpWql1OQwDqRcZqVI1MTnCcDGwMO1sIsyNxfqn
# IAHesiOGcJ1DMgu/bU9W+UVghMfG+Kw+g7//Xtt1OOx2Ce1ozGSadKPNFbNieW07
# Ig==
# SIG # End signature block
