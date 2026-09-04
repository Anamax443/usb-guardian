# Agent auto-enrollment – setting up the deploy account

*[🇨🇿 Čeština](auto-deploy-setup.md) · 🇬🇧 English*

The console on the app server can deploy the agent by itself to AD stations that have none
(`AgentDeployService` + `scripts\Deploy-AgentFleet.ps1`). To do that "for real" it needs a
**deploy identity that is a local admin on the clients**.

## Recommended account: gMSA `DOMENA\gmsa-deploy$`

No password (auto-rotated in AD), cannot be used to log on interactively – ideal for a service.
Consistent with `gmsa-api$` used by the API.

### Least-privilege model (recommended)

The deploy account needs **only local admin on the clients** – nothing else (no SQL, no change to the
console's identity):

```
Console (machine account, unchanged) → finds stations without an agent → writes deploy-targets.txt
Scheduled task on the app server (as gmsa-deploy$) → Deploy-AgentFleet.ps1 -TargetsFile … → installation
```

## Steps

### 1. (DC, Domain Admin) A group for local admin on the clients

```powershell
New-ADGroup -Name "USB-Guardian-Deployers" -GroupScope Global `
    -Path "OU=Service Accounts,DC=domena,DC=loc"   # adjust the OU
```

### 2. (DC) gMSA + allow the app server to retrieve the password + add to the group

```powershell
# KDS root key – once per domain (you most likely already have one because of gmsa-api$):
#   Add-KdsRootKey -EffectiveImmediately
#   (in a lab, immediately: Add-KdsRootKey -EffectiveTime ((Get-Date).AddHours(-10)))

New-ADServiceAccount -Name "gmsa-deploy" `
    -DNSHostName "gmsa-deploy.domena.loc" `
    -PrincipalsAllowedToRetrieveManagedPassword "APP_SERVER$"   # the app server's machine account

Add-ADGroupMember -Identity "USB-Guardian-Deployers" -Members "gmsa-deploy$"
```

### 3. (on the app server) Install the gMSA

```powershell
Install-ADServiceAccount gmsa-deploy
Test-ADServiceAccount  gmsa-deploy      # must return True
```

### 4. (you) GPO – local admin on the clients

A **Restricted Groups** GPO (or Group Policy Preferences → Local Users and Groups):
add the `USB-Guardian-Deployers` group into **local Administrators** on the OU holding client stations.
*(This is the "give it admin rights on the PCs" part.)*

### 5. (on the app server) Deployment materials

```powershell
# self-contained publish of the agent + the scripts on the app server:
#   C:\Apps\USBGuardianAgentPublish\         (dotnet publish ... agent)
#   C:\Apps\USBGuardianConsole\scripts\Deploy-AgentFleet.ps1
#   C:\Apps\USBGuardianConsole\scripts\Watch-USBGuardian.ps1
```

### 6. (on the app server) A scheduled task under the gMSA

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

### 7. Switch it on in the console (Settings → Agent auto-enrollment)

First **master ON + dry-run ON** → check the report "N stations would be deployed" →
then **turn dry-run OFF** → the console starts writing `deploy-targets.txt` and the task installs.
Pilot: an allowlist of a single machine first, then a second one, then an empty allowlist = the whole fleet.

## The second deploy account: `gmsa-srvdeploy$` (API deployment)

The client deploy account **must not** be an admin on the API server — otherwise compromising one identity
would reach both the fleet and the server. API deployment therefore uses a separate gMSA that is a
**local admin on the API server only** (deliberately outside the server-admins group, which would grant admin
on every server):

```powershell
# (DC) the account + allow the app server to retrieve the password
New-ADServiceAccount -Name "gmsa-srvdeploy" `
    -DNSHostName "gmsa-srvdeploy.domena.loc" `
    -PrincipalsAllowedToRetrieveManagedPassword "APP_SERVER$"

# (on the API server) add to the local administrators of THAT ONE machine
Add-LocalGroupMember -Group Administrators -Member "DOMENA\gmsa-srvdeploy$"

# (on the app server) install it
Install-ADServiceAccount gmsa-srvdeploy
```

The `USBGuardian-ApiDeploy` task on the app server then runs `Deploy-Api.cmd`:

```
cmd /c C:\Apps\USBGuardianConsole\scripts\Deploy-Api.cmd "C:\Apps\USBGuardianApiPublish" "API-SERVER" "C$\USBGuardian.Api"
```

> **Creating a task under a gMSA:** `schtasks /Create /RU "…gmsa$"` without a password produces
> `LogonType=InteractiveToken` → the task never runs (event 332). S4U (`/NP`) has no network credentials and
> cannot reach `\\HOST\C$`. The only thing that works is **XML with `LogonType=Password` saved as UTF-16**
> and created via `schtasks /Create /XML`. The same trap applies to `USBGuardian-UpdateAgent`.

## Alternative: running the console under the deploy account

If you wanted the console process itself to perform the installation (without a scheduled task), the
**console service would have to run as `gmsa-deploy$`** – but then that account also needs the **same SQL
rights** as today's machine account (read everything + write Computers/WhitelistDevices/WhitelistVersions/
AppSettings) and "Log on as a service" on the app server. Less clean (more rights on one account) – hence the
separate task recommended above.
