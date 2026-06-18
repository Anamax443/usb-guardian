# Auto-enrollment agenta – nastavení deploy účtu

Konzole na `.213` umí sama nasazovat agenta na stanice z AD bez agenta
(`AgentDeployService` + `scripts\Deploy-AgentFleet.ps1`). Aby to šlo „naostro",
potřebuje **deploy identitu s lokálním adminem na klientech**.

## Doporučený účet: gMSA `AXINETWORK\gmsa-USBGdep$`

Bez hesla (auto-rotace v AD), nejde se s ním interaktivně přihlásit – ideál pro službu.
Konzistentní s `gmsa-SQL$` u API.

### Least-privilege model (doporučeno)

Deploy účet potřebuje **JEN admin na klientech** – nic víc (žádný SQL, žádná změna identity konzole):

```
Konzole (B-S-W-MIKOS$, beze změny) → najde stanice bez agenta → zapíše deploy-targets.txt
Scheduled task na .213 (pod gmsa-USBGdep$) → Deploy-AgentFleet.ps1 -TargetsFile … → instalace
```

## Kroky

### 1. (DC, Domain Admin) Skupina pro lokální admina na klientech

```powershell
New-ADGroup -Name "USB-Guardian-Deployers" -GroupScope Global `
    -Path "OU=Service Accounts,DC=axinetwork,DC=loc"   # uprav OU
```

### 2. (DC) gMSA + povolit .213 číst heslo + zařadit do skupiny

```powershell
# KDS root key – jen jednou per doména (u vás nejspíš už je kvůli gmsa-SQL$):
#   Add-KdsRootKey -EffectiveImmediately
#   (v labu hned: Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10)))

New-ADServiceAccount -Name "gmsa-USBGdep" `
    -DNSHostName "gmsa-USBGdep.axinetwork.loc" `
    -PrincipalsAllowedToRetrieveManagedPassword "B-S-W-MIKOS$"   # strojový účet .213

Add-ADGroupMember -Identity "USB-Guardian-Deployers" -Members "gmsa-USBGdep$"
```

### 3. (na .213) Nainstalovat gMSA

```powershell
Install-ADServiceAccount gmsa-USBGdep
Test-ADServiceAccount  gmsa-USBGdep      # musí vrátit True
```

### 4. (TY) GPO – lokální admin na klientech

GPO **Restricted Groups** (nebo Group Policy Preferences → Local Users and Groups):
přidat skupinu `USB-Guardian-Deployers` do **local Administrators** na OU s klientskými stanicemi.
*(Toto je to „dám mu admin přístupy na PC".)*

### 5. (na .213) Materiály pro deploy

```powershell
# self-contained publish agenta + skripty na .213:
#   C:\Apps\USBGuardianAgentPublish\         (dotnet publish ... agent)
#   C:\Apps\USBGuardianConsole\scripts\Deploy-AgentFleet.ps1
#   C:\Apps\USBGuardianConsole\scripts\Watch-USBGuardian.ps1
```

### 6. (na .213) Scheduled task pod gMSA

```powershell
$ps   = "C:\Apps\USBGuardianConsole\scripts\Deploy-AgentFleet.ps1"
$args = "-NonInteractive -NoProfile -ExecutionPolicy Bypass -File `"$ps`" " +
        "-TargetsFile C:\Apps\USBGuardianConsole\deploy-targets.txt " +
        "-SourcePath C:\Apps\USBGuardianAgentPublish"
$action    = New-ScheduledTaskAction -Execute powershell.exe -Argument $args
$principal = New-ScheduledTaskPrincipal -UserId "AXINETWORK\gmsa-USBGdep$" -LogonType Password -RunLevel Highest
$trigger   = New-ScheduledTaskTrigger -RepetitionInterval (New-TimeSpan -Minutes 30) -Once -At (Get-Date)
Register-ScheduledTask -TaskName "USBGuardian-AutoDeploy" -TaskPath "\USBGuardian\" `
    -Action $action -Principal $principal -Trigger $trigger -Force
```

### 7. Zapnout v konzoli (Nastavení → Auto-enrollment agenta)

Nejdřív **master ZAPNUTO + dry-run ZAPNUTO** → ověřit report „nasadilo by se N" →
pak **dry-run VYPNOUT** → konzole začne psát `deploy-targets.txt`, task instaluje.
Pilot: allowlist jen `.181`, pak `.180`, pak prázdný allowlist = celý fleet.

## Alternativa: konzole běží přímo pod deploy účtem

Pokud bys chtěl, aby instalaci dělal přímo proces konzole (bez scheduled tasku),
musí **služba konzole běžet pod `gmsa-USBGdep$`** – pak ale ten účet potřebuje i
**stejná SQL práva** jako dnešní `B-S-W-MIKOS$` (read vše + write
Computers/WhitelistDevices/WhitelistVersions/AppSettings) a „Log on as a service" na .213.
Méně čisté (víc práv na jednom účtu) – proto doporučuju oddělený task výše.
