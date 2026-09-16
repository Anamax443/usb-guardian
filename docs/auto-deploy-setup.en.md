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

> **The selection for one run is shuffled:** the SQL query has no `ORDER BY`, so without shuffling
> the console would keep offering the same first `MaxPerRun` stations every time – one permanently
> broken/unreachable station could then block the rest of the fleet forever. The list is therefore
> shuffled (Fisher–Yates) before being capped at `MaxPerRun`, so later runs give the rest of the
> fleet a turn too (found 2026-09-16: "keeps retrying the same few that have a problem").

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

> **`Deploy-AgentFleet.ps1` on the app server is NOT a `dotnet publish` output** – it is a manual
> copy of the one in `scripts\` in git; the console's build does not overwrite it. After every edit
> to the script it must be re-signed (Authenticode, the company cert `CN=powershell.domena.loc`)
> and copied by hand into `C:\Apps\USBGuardianConsole\scripts\` on the app server, otherwise the
> old signed version keeps running there.

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

> **Reinstalling a broken, already-registered service (`-ReinstallExisting`):** the script now
> stops it and waits for STOPPED BEFORE copying – previously it went straight to `robocopy`, which
> on a locked/running exe could overwrite only part of the package. If anything fails AFTER that
> stop (e.g. robocopy), the `catch` block makes a best-effort attempt to start the service again
> (`sc start`), so a failed reinstall doesn't leave the station with no running protection at all –
> previously it could stay "stopped, with nothing restarting it". If the service won't stop within
> 10 s, the error message says exactly what to do next: RDP to the station, end
> `USBGuardian.exe` in Task Manager (or reboot the station), then retry.
>
> The `USBGuardian-AutoDeploy` task above deliberately does NOT get `-ReinstallExisting` – it runs
> unattended against the whole fleet, so it just SKIPs an existing-but-broken service instead of
> touching it. Reinstalling is left to the human-triggered `USBGuardian-ManualInstall` task (below).

### 7. Switch it on in the console (Settings → Agent auto-enrollment)

First **master ON + dry-run ON** → check the report "N stations would be deployed" →
then **turn dry-run OFF** → the console starts writing `deploy-targets.txt` and the task installs.
Pilot: an allowlist of a single machine first, then a second one, then an empty allowlist = the whole fleet.

## Deploying a single station by hand: `USBGuardian-ManualInstall`

The **"Deploy now"** button next to a station (`DeployTrigger.cs`) is a sibling of the
`USBGuardian-AutoDeploy` task above – it runs under the **same** `gmsa-deploy$`, no separate
account or GPO. It does have its **own** targets file and **own** task, though, so a manual click
and the automatic cycle can't overwrite each other's targets mid-run (incident 2026-09-10: a click
on one station produced a result for a different one from auto-enrollment):

```
Target: C:\ProgramData\USBGuardian\deploy\manual-targets.txt   (the one chosen station only)
Task:   \USBGuardian\USBGuardian-ManualInstall
```

It is created the same way as in step 6 (`schtasks /Create /XML`, `LogonType=Password` under
`gmsa-deploy$`) – the same S4U trap described below under `USBGuardian-ApiDeploy` applies to it too.

> **With `-ReinstallExisting` since 2026-09-16:** the task's argument was edited directly on the
> app server (`schtasks /Query ... /XML` → a targeted replace in the `-Command` string →
> `schtasks /Create ... /XML ... /F`, preserving the existing `Principal`/`LogonType=Password`) –
> clicking "Deploy now" on a station with an existing-but-broken registration now stops and
> reinstalls it (see step 6 above) instead of permanently SKIPping it. `USBGuardian-AutoDeploy`
> (unattended, whole fleet) deliberately still does not have `-ReinstallExisting`.

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
> and created via `schtasks /Create /XML`. The same trap applies to `USBGuardian-UpdateAgent` and
> `USBGuardian-ManualInstall`.

## Alternative: running the console under the deploy account

If you wanted the console process itself to perform the installation (without a scheduled task), the
**console service would have to run as `gmsa-deploy$`** – but then that account also needs the **same SQL
rights** as today's machine account (read everything + write Computers/WhitelistDevices/WhitelistVersions/
AppSettings) and "Log on as a service" on the app server. Less clean (more rights on one account) – hence the
separate task recommended above.
