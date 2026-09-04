# Auto-enrollment agenta – nastavení deploy účtu

*🇨🇿 Čeština · [🇬🇧 English](auto-deploy-setup.en.md)*

Konzole na `APP_SERVER` umí sama nasazovat agenta na stanice z AD bez agenta
(`AgentDeployService` + `scripts\Deploy-AgentFleet.ps1`). Aby to šlo „naostro",
potřebuje **deploy identitu s lokálním adminem na klientech**.

## Doporučený účet: gMSA `DOMENA\gmsa-deploy$`

Bez hesla (auto-rotace v AD), nejde se s ním interaktivně přihlásit – ideál pro službu.
Konzistentní s `gmsa-api$` u API.

### Least-privilege model (doporučeno)

Deploy účet potřebuje **JEN admin na klientech** – nic víc (žádný SQL, žádná změna identity konzole):

```
Konzole (APP_SERVER$, beze změny) → najde stanice bez agenta → zapíše deploy-targets.txt
Scheduled task na APP_SERVER (pod gmsa-deploy$) → Deploy-AgentFleet.ps1 -TargetsFile … → instalace
```

## Kroky

### 1. (DC, Domain Admin) Skupina pro lokální admina na klientech

```powershell
New-ADGroup -Name "USB-Guardian-Deployers" -GroupScope Global `
    -Path "OU=Service Accounts,DC=domena,DC=loc"   # uprav OU
```

### 2. (DC) gMSA + povolit APP_SERVER číst heslo + zařadit do skupiny

```powershell
# KDS root key – jen jednou per doména (u vás nejspíš už je kvůli gmsa-api$):
#   Add-KdsRootKey -EffectiveImmediately
#   (v labu hned: Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10)))

New-ADServiceAccount -Name "gmsa-deploy" `
    -DNSHostName "gmsa-deploy.domena.loc" `
    -PrincipalsAllowedToRetrieveManagedPassword "APP_SERVER$"   # strojový účet APP_SERVER

Add-ADGroupMember -Identity "USB-Guardian-Deployers" -Members "gmsa-deploy$"
```

### 3. (na APP_SERVER) Nainstalovat gMSA

```powershell
Install-ADServiceAccount gmsa-deploy
Test-ADServiceAccount  gmsa-deploy      # musí vrátit True
```

### 4. (TY) GPO – lokální admin na klientech

GPO **Restricted Groups** (nebo Group Policy Preferences → Local Users and Groups):
přidat skupinu `USB-Guardian-Deployers` do **local Administrators** na OU s klientskými stanicemi.
*(Toto je to „dám mu admin přístupy na PC".)*

### 5. (na APP_SERVER) Materiály pro deploy

```powershell
# self-contained publish agenta + skripty na APP_SERVER:
#   C:\Apps\USBGuardianAgentPublish\         (dotnet publish ... agent)
#   C:\Apps\USBGuardianConsole\scripts\Deploy-AgentFleet.ps1
#   C:\Apps\USBGuardianConsole\scripts\Watch-USBGuardian.ps1
```

### 6. (na APP_SERVER) Scheduled task pod gMSA

```powershell
$ps   = "C:\Apps\USBGuardianConsole\scripts\Deploy-AgentFleet.ps1"
$args = "-NonInteractive -NoProfile -ExecutionPolicy Bypass -File `"$ps`" " +
        "-TargetsFile C:\Apps\USBGuardianConsole\deploy-targets.txt " +
        "-SourcePath C:\Apps\USBGuardianAgentPublish"
$action    = New-ScheduledTaskAction -Execute powershell.exe -Argument $args
$principal = New-ScheduledTaskPrincipal -UserId "DOMENA\gmsa-deploy$" -LogonType Password -RunLevel Highest
$trigger   = New-ScheduledTaskTrigger -RepetitionInterval (New-TimeSpan -Minutes 30) -Once -At (Get-Date)
Register-ScheduledTask -TaskName "USBGuardian-AutoDeploy" -TaskPath "\USBGuardian\" `
    -Action $action -Principal $principal -Trigger $trigger -Force
```

### 7. Zapnout v konzoli (Nastavení → Auto-enrollment agenta)

Nejdřív **master ZAPNUTO + dry-run ZAPNUTO** → ověřit report „nasadilo by se N" →
pak **dry-run VYPNOUT** → konzole začne psát `deploy-targets.txt`, task instaluje.
Pilot: allowlist jen `PC-01`, pak `.180`, pak prázdný allowlist = celý fleet.

## Druhý deploy účet: `gmsa-srvdeploy$` (nasazení API)

Klientský deploy účet **nesmí** být admin na serveru API — jinak by kompromitace jedné identity sáhla na fleet
i na server současně. Pro nasazení API je proto samostatné gMSA, které je **lokální admin jen na serveru API**
(záměrně mimo skupinu serverových adminů — ta by dala admina na všechny servery):

```powershell
# (DC) účet + povolit APP_SERVER číst heslo
New-ADServiceAccount -Name "gmsa-srvdeploy" `
    -DNSHostName "gmsa-srvdeploy.domena.loc" `
    -PrincipalsAllowedToRetrieveManagedPassword "APP_SERVER$"

# (na serveru API) přidat do lokálních administrátorů TOHO JEDNOHO stroje
Add-LocalGroupMember -Group Administrators -Member "DOMENA\gmsa-srvdeploy$"

# (na APP_SERVER) nainstalovat
Install-ADServiceAccount gmsa-srvdeploy
```

Úloha `USBGuardian-ApiDeploy` na `APP_SERVER` pak spouští `Deploy-Api.cmd`:

```
cmd /c C:\Apps\USBGuardianConsole\scripts\Deploy-Api.cmd "C:\Apps\USBGuardianApiPublish" "API-SERVER" "C$\USBGuardian.Api"
```

> **Založení úlohy pod gMSA:** `schtasks /Create /RU "…gmsa$"` bez hesla vyrobí `LogonType=InteractiveToken`
> → úloha se nespustí (event 332). S4U (`/NP`) nemá síťové credentials a nedosáhne na `\\HOST\C$`.
> Funguje jedině **XML s `LogonType=Password` uložené v UTF-16** a založené přes `schtasks /Create /XML`.
> Stejná past platí i pro `USBGuardian-UpdateAgent`.

## Alternativa: konzole běží přímo pod deploy účtem

Pokud bys chtěl, aby instalaci dělal přímo proces konzole (bez scheduled tasku),
musí **služba konzole běžet pod `gmsa-deploy$`** – pak ale ten účet potřebuje i
**stejná SQL práva** jako dnešní `APP_SERVER$` (read vše + write
Computers/WhitelistDevices/WhitelistVersions/AppSettings) a „Log on as a service" na APP_SERVER.
Méně čisté (víc práv na jednom účtu) – proto doporučuju oddělený task výše.
