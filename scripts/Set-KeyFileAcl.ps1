#Requires -RunAsAdministrator
# ============================================================
# Set-KeyFileAcl.ps1
# ACL na privatni klice serveru (posledni otevreny bod z auditu
# 04.09.2026, znovu potvrzeny oponenturou 11.09.2026): api-tls.pfx,
# admin-tls.pfx a whitelist_private.pem lezi na disku bez explicitni
# ochrany, tj. spolehaji jen na obecna prava adresare (C:\ProgramData).
#
# Odpojuje dedicnost a nastavuje presny seznam pristupu:
#   SYSTEM           FullControl
#   Administrators   FullControl
#   sluzebni ucet    Read (whitelist_private.pem) / Modify (*.pfx - self-cert
#                    si je muze potrebovat pri startu sam prepsat, viz SelfCert.cs)
#   kdokoli jiny     zadny pristup
#
# Spoustet na APP_SERVER (kde bezi API i konzole) jako lokalni
# Administrator. Needs -WhatIf support pro nahled bez zmeny.
#
# Pouziti:
#   .\Set-KeyFileAcl.ps1 -Domain "AXIMA"
#   .\Set-KeyFileAcl.ps1 -Domain "AXIMA" -WhitelistKeyPath "D:\Keys\whitelist_private.pem"
#   .\Set-KeyFileAcl.ps1 -Domain "AXIMA" -WhatIf
# ============================================================

param(
    [Parameter(Mandatory)] [string] $Domain,
    [string] $ApiAccount        = "gmsa-api`$",
    [string] $ConsoleAccount    = "$env:COMPUTERNAME`$",
    [string] $ApiTlsPath        = "C:\ProgramData\USBGuardian\api-tls.pfx",
    [string] $AdminTlsPath      = "C:\ProgramData\USBGuardian\admin-tls.pfx",
    # Skutecna cesta je v serverovem appsettings.local.json pod Whitelist:PrivateKeyPath -
    # tenhle default je jen odhad podle konvence slozky, OVER na APP_SERVER pred spustenim.
    [string] $WhitelistKeyPath  = "C:\ProgramData\USBGuardian\whitelist_private.pem",
    [switch] $WhatIf
)

$ErrorActionPreference = "Stop"

function Set-KeyAcl {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $ServiceAccount,
        [Parameter(Mandatory)] [string] $ServiceRight   # "Read" nebo "Modify"
    )

    if (-not (Test-Path $Path)) {
        Write-Warning "Soubor neexistuje, preskakuji: $Path"
        return
    }

    Write-Host ""
    Write-Host "== $Path ==" -ForegroundColor Cyan

    $acl = Get-Acl $Path
    $acl.SetAccessRuleProtection($true, $false)   # odpojit dedicnost, nekopirovat zdedena pravidla

    $rules = @(
        [System.Security.AccessControl.FileSystemAccessRule]::new(
            "NT AUTHORITY\SYSTEM", "FullControl", "Allow"),
        [System.Security.AccessControl.FileSystemAccessRule]::new(
            "BUILTIN\Administrators", "FullControl", "Allow"),
        [System.Security.AccessControl.FileSystemAccessRule]::new(
            "$Domain\$ServiceAccount", $ServiceRight, "Allow")
    )
    foreach ($rule in $rules) { $acl.AddAccessRule($rule) }

    if ($WhatIf) {
        Write-Host "  [WhatIf] Nastavilo by se:" -ForegroundColor Yellow
        $rules | ForEach-Object { Write-Host "    $($_.IdentityReference)  $($_.FileSystemRights)" }
        return
    }

    Set-Acl -Path $Path -AclObject $acl
    Write-Host "  OK - dedicnost odpojena, nastaveno:" -ForegroundColor Green
    (Get-Acl $Path).Access |
        Format-Table IdentityReference, FileSystemRights, AccessControlType -AutoSize
}

Write-Host "USB Guardian - ACL na privatni klice" -ForegroundColor Cyan
if ($WhatIf) { Write-Host "(WhatIf - nic se nezmeni, jen nahled)" -ForegroundColor Yellow }

# 1) API self-signed TLS klic (SelfCert.cs) - cte/pripadne prepisuje gMSA API sluzby.
Set-KeyAcl -Path $ApiTlsPath -ServiceAccount $ApiAccount -ServiceRight "Modify"

# 2) Konzole self-signed TLS klic (SelfCert.cs, 11.09.2026) - cte/pripadne prepisuje
#    strojovy ucet serveru, na kterem konzole bezi jako LocalSystem.
Set-KeyAcl -Path $AdminTlsPath -ServiceAccount $ConsoleAccount -ServiceRight "Modify"

# 3) RSA podpisovy klic whitelistu (WhitelistPublisher.cs) - konzole ho jen CTE, nikdy
#    sama neregeneruje (rucne provisionovany), proto staci Read.
Set-KeyAcl -Path $WhitelistKeyPath -ServiceAccount $ConsoleAccount -ServiceRight "Read"

Write-Host ""
Write-Host "Hotovo. Restart sluzeb (API/konzole) neni potreba - soubory se ctou znovu az pri" -ForegroundColor Cyan
Write-Host "pristim pristupu, existujici otevrene handly zustavaji platne." -ForegroundColor Cyan
