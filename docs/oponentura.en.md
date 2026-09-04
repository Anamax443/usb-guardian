# USB Guardian — technical document for the opponent review

**Monitoring and enforcement of removable storage media policy on company workstations as a technical measure under NIS2**

---

| | |
|---|---|
| **Project** | USB Guardian |
| **Repository** | `Anamax443/usb-guardian` |
| **Author** | Milan Trnka (AXIMA) |
| **Document version** | 1.1 — chapters 1–33 as in version 1.0, chapter 34 = an addendum dated 4 Sep 2026 |
| **Date** | 2026-06-19, extended 2026-09-04 |
| **Classification** | Internal — material for an opponent review |
| **Domain environment** | `axinetwork.loc` (AXIMA) |
| **Language** | [🇨🇿 Čeština](oponentura.md) · 🇬🇧 English |
| **Related documents** | [README.en.md](../README.en.md), [HANDOFF.en.md](../HANDOFF.en.md), [architecture.en.md](architecture.en.md), [auto-deploy-setup.en.md](auto-deploy-setup.en.md), [oponentura-komercni.en.md](oponentura-komercni.en.md) · graphical outputs: [how-it-works.html](how-it-works.html), [mind-map.html](mind-map.html), [flowchart.html](flowchart.html), [management-summary.html](management-summary.html) |

---

## Abstract

USB Guardian is a system for **controlling removable storage media** (USB flash drives, SD cards, external
USB drives) on the endpoints of a company network. Every medium must be approved by IT and recorded in a
central, cryptographically signed whitelist; unapproved media are, depending on the policy, **warned about
or actually blocked** at the driver level. All events (connection, blocking, exceptions) are logged and
centrally aggregated as an audit trail.

The system is designed as a **technical measure** supporting compliance with the **NIS2** directive
(2022/2555), the Czech **Act No. 181/2014 Coll.** on cybersecurity and the **ISO/IEC 27001/27002** standard
(in particular the control of removable media and portable devices). It emphasises **least privilege**,
**fail-secure** behaviour, **auditability** and **portability** (no company-specific values in the code).

The architecture is **three-tier** (an agent on the station → an ingestion API → a central database) with a
separate **administration console**, and it uses a **push model** (the agent initiates the outbound
connection), which suits a fleet of 500+ stations behind NAT/firewalls. Communication is encrypted with a
self-signed certificate using **thumbprint pinning** (no PKI dependency), and the integrity of the whitelist
is ensured by an **RSA-4096** signature.

This document describes the problem, the normative framework, the requirements, the architecture and —
essential for the purposes of a review — the **defence of individual design decisions**, the **security and
threat model**, the implementation of key components, the way it is deployed and operated, the results of
live verification on the pilot station, and an **honest analysis of limitations, risks and open points**.

---

## Contents

**PART I — Context and requirements**
1. Introduction
2. The problem: risks of removable media
3. The legislative and normative framework
4. Requirements analysis

**PART II — Design**
5. Architecture overview
6. Defence of design decisions (a decision log)
7. Data model and data flows

**PART III — Implementation**
8. The agent (client workstation)
9. The server API (ingest)
10. The administration console
11. Cryptography and whitelist signing

**PART IV — Security and enforcement**
12. Security and threat model
13. The policy enforcement model
14. Auditability and NIS2 compliance

**PART V — Operations**
15. Building, deployment and updates
16. Versioning and deployment verifiability
17. Operations, monitoring, retention

**PART VI — Verification and evaluation**
18. Testing and live verification
19. Limitations, risks and known weaknesses
20. Roadmap
21. Conclusion

**PART VII — Extended analyses and defence**
22. Anticipated reviewer questions and answers
23. Comparison with alternative approaches and products
24. Detailed test catalogue
25. Performance and scaling (a quantitative analysis)
26. Operational runbooks
27. Detailed diagrams
28. Detailed legislative and normative analysis
29. Reference overview of classes and responsibilities
30. Detailed analysis of key algorithms and code
31. Attack scenarios (attack trees)
32. Complete configuration examples
33. Behaviour in edge cases

**PART VIII — Addendum**
34. What changed since version 1.0 (state as of 4 Sep 2026)

**Appendices**
- A. Glossary
- B. Configuration key reference
- C. Database schema and SQL grants
- D. API endpoint reference
- E. Mapping NIS2 / ISO 27001 → features
- F. List of design decisions (summary)

---

# PART I — Context and requirements

## 1. Introduction

### 1.1 Purpose of this document

The document serves as **material for an opponent review** of the USB Guardian project. Its goal is not
merely to describe *what* the system does, but above all to **defend why it was designed the way it was** —
that is, to show that the individual decisions are rational, that alternatives were considered, and that the
known limitations are deliberate and managed. It is therefore written assuming a **critical reader (the
reviewer)**, who will look for weaknesses, unsupported claims and overlooked alternatives.

### 1.2 Audience

- **The reviewer / assessor** — a technically competent reader judging completeness, correctness and defensibility.
- **The security manager / cybersecurity manager** — assessing compliance with NIS2 / ISO 27001.
- **Operational IT (administrators)** — taking the system over into operation (see also [HANDOFF.en.md](../HANDOFF.en.md)).
- **A developer taking over the project** — who needs to understand the decisions and their consequences.

### 1.3 Scope and boundaries

The document covers **the whole system**: the agent on the station, the ingestion API, the database, the
administration console, the cryptographic model, deployment and operations. It does **not** cover the source
code line by line (the repository does that), nor any company-sensitive values (those live outside the
repository in `*.local.json`).

### 1.4 Conventions

- Component, class and file names are written in `code font`.
- IP addresses and hostnames correspond to the real pilot deployment in the `axinetwork.loc` domain.
- "**.213**" = the application server `B-S-W-MIKOS` (`10.8.2.213`), "**SQL-04**" = the database server
  `B-S-W-SQL-04` (`10.8.2.225`), "**.181**" = the pilot station `TRNKAMW11`.

---

## 2. The problem: risks of removable storage media

### 2.1 Why removable media specifically

Removable storage media (USB flash drives, SD cards, external drives) are one of the oldest and still very
current vectors of cyber incidents. Their risk lies in the combination of three properties:

1. **Uncontrolled two-way data transfer** — a medium can **carry sensitive data out** of a protected network
   (exfiltration, leakage) and at the same time **bring malicious code in** (malware, ransomware) entirely
   outside perimeter protection (firewall, e-mail gateways, web proxy).
2. **Physical nature** — the attack needs no network connection; physical access to the station is enough.
   That bypasses most network controls. The classic example is deploying malware through a "lost" USB drive
   in a car park (baiting) or through spoofed charging cables.
3. **Trust in the endpoint** — the operating system trusts a medium by default (it mounts the file system
   automatically, and in some configurations runs autorun), so the user need not even take a conscious action.

### 2.2 Concrete threat scenarios

| Scenario | Description | Impact |
|----------|-------------|--------|
| **Data exfiltration** | An employee (deliberately or negligently) copies sensitive data onto a private USB drive | Data leak, breach of GDPR/trade secrets |
| **Malware introduction** | Connecting an infected medium (BadUSB, autorun, infected documents) | Compromise of the station, lateral movement |
| **HID spoofing (BadUSB)** | The medium presents itself as a keyboard and injects commands | Code execution (outside this system's scope — see §19) |
| **Loss / theft of a medium** | An unencrypted company drive with data is lost | Data leak |
| **Shadow IT** | Unrecorded media outside IT's oversight | Loss of visibility, no auditability |

USB Guardian primarily targets **scenarios 1, 2, 4 and 5** (storage-class devices). HID spoofing (BadUSB as
a keyboard) is out of scope and is explicitly listed among the limitations (§19).

### 2.3 Why the existing controls are not enough

- **GPO / Removable Storage Access** (Windows) blocks globally or by class, but **has no central whitelist of
  specific media**, provides no **audit trail with user attribution**, and managing it across a fleet is
  inflexible.
- **DLP solutions** are expensive, often cloud-based, and require data classification.
- **Perimeter protection** (firewall, proxy) is **blind** to a physical medium.
- **EDR/antivirus** detects the *consequence* (malware), not the *connection of an unapproved medium*.

USB Guardian fills the gap: **inventory + a selective whitelist + enforcement + audit**, centrally managed,
with user attribution and cryptographically assured rule integrity.

---

## 3. The legislative and normative framework

### 3.1 The NIS2 directive (EU 2022/2555)

NIS2 broadens the set of obliged entities and tightens the requirements on **cyber risk management**.
Particularly relevant are the articles on **risk-management measures** (Art. 21), which include, among others:

- policies and procedures for **asset management** and **access control**,
- **operational security** including protection against malicious code,
- **logging and monitoring** of events,
- measures for the **safe handling of media**.

USB Guardian is a **technical measure** that directly supports:

| NIS2 requirement (area) | USB Guardian's contribution |
|-------------------------|-----------------------------|
| Asset management | A central record of all connected media (including unapproved ones) identified by VID/PID/serial |
| Access control for media | A whitelist + enforcement (block/warn) at the station level |
| Protection against malicious code | Blocking unapproved media as prevention of malware introduction |
| Logging and monitoring | An audit trail of all events, centrally aggregated, with user attribution |
| Incident response | Near-real-time incident reporting to the server, e-mail alerts |
| Change management / integrity control | A cryptographic signature of the whitelist (RSA-4096), versioning |

### 3.2 Act No. 181/2014 Coll. on cybersecurity

The Cybersecurity Act and the associated decree on security measures operationalise the requirements for
obliged persons. USB Guardian contributes to measures in the areas of **asset management**, **access
control**, **protection against malicious code**, **event recording** and **physical security** (control of
portable devices). For the concrete mapping see Appendix E.

### 3.3 ISO/IEC 27001 and 27002

In terms of ISO/IEC 27002:2022 controls, USB Guardian primarily supports:

- **8.7 Protection against malware** — preventing introduction through an unapproved medium.
- **7.10 Storage media** — managing the lifecycle and use of removable media.
- **8.15 Logging** — recording connection/blocking events.
- **8.16 Monitoring activities** — central oversight, detection of "silent" agents.
- **5.9 Inventory of assets** — a record of media discovered on the network.

### 3.4 The "technical measure" principle, not compliance on its own

It must be stressed (and a reviewer will be sensitive to this): **no single tool delivers compliance with
NIS2 or ISO 27001 by itself.** USB Guardian is a **partial technical measure** that must be embedded in a
wider information security management system (ISMS), accompanied by organisational measures (a media usage
policy, training, data classification) and processes (media approval, incident response). Nowhere does this
document claim more than that.

---

## 4. Requirements analysis

### 4.1 Functional requirements (FR)

| ID | Requirement | Realisation |
|----|-------------|-------------|
| FR-1 | Detect the connection of any removable storage medium (USB, SD) | `DeviceMonitor` (WMI watchers + startup scan) |
| FR-2 | Identify a medium unambiguously (VID, PID, serial) | Parsing `PNPDeviceID` from WMI |
| FR-3 | Compare the medium against a centrally managed whitelist | `WhitelistChecker` (an O(1) index) |
| FR-4 | In "warn" mode leave the medium working, only warn | `PolicyEnforcer` + toast |
| FR-5 | In "block" mode actually make the medium inaccessible | `DeviceBlocker` (`Disable-PnpDevice`) |
| FR-6 | Record every event as an incident with user attribution | `IncidentLogger` + `SessionUser` (WTS) |
| FR-7 | Deliver incidents centrally to the server | `IncidentSync` → API → DB |
| FR-8 | Manage the whitelist centrally (add/remove/activate) | The console, Whitelist page |
| FR-9 | Ensure the integrity of the whitelist against forgery | RSA-4096 signature, verification on the agent (fail-secure) |
| FR-10 | Switch the enforcement mode centrally (block/warn) | `policy.enforce` in the heartbeat |
| FR-11 | Allow a temporary local exception (break-glass) for offline work | The local console, `PolicyState` override |
| FR-12 | Return previously blocked media when blocking is switched off / on approval | Auto-re-enable + reconciliation |
| FR-13 | Keep a record of stations from AD and identify where the agent is missing | `AdSyncRunner` + reconciliation |
| FR-14 | Deploy the agent to stations without one (in bulk) | Auto-enrollment (a gMSA task) |
| FR-15 | Provide overviews, exports and a management report | The console (Overview, Export) |
| FR-16 | Delete old data per the retention policy | `RetentionService` (API) |
| FR-17 | Alert by e-mail on new unapproved incidents | `IncidentAlertService` |

### 4.2 Non-functional requirements (NFR)

| ID | Requirement | Goal / realisation |
|----|-------------|--------------------|
| NFR-1 **Scalability** | A fleet of 500+ stations | Push model (the agent initiates the connection), O(1) whitelist match (Dictionary), the ingestion API separated from the console, an in-memory incident queue |
| NFR-2 **Security** | Encryption, integrity, authentication, authorization, least privilege | TLS+pinning, RSA-4096, Kerberos, AD groups, gMSA, granular SQL grants |
| NFR-3 **Fail-secure** | Do not allow through when verification fails | An invalid/missing whitelist signature → the medium cannot be verified → handled per policy |
| NFR-4 **Availability / resilience** | Incident intake must not fall over under load; offline operation | 202 Accepted + queue + worker; the agent works offline (local whitelist) |
| NFR-5 **Auditability** | A complete trail for NIS2 | Every event = an incident; break-glass is logged; central aggregation |
| NFR-6 **Portability** | No company values in the code | Everything in `*.local.json` (gitignored), the domain from `new DirectoryEntry()` |
| NFR-7 **Deployment verifiability** | An operator can verify what is running | A commit stamp in the footer / `/api/version` / the heartbeat |
| NFR-8 **Operability** | Easy deployment even in a constrained environment | Self-contained builds, SMB+sc.exe deployment, PS-free scheduled tasks |
| NFR-9 **Tamper resistance** | An attacker must not disable the agent trivially | Service + watchdog (two independent mechanisms), running as SYSTEM |

### 4.3 Environment constraints (AXIMA)

The design had to respect real constraints of the production environment, which fundamentally shaped the
decisions:

- **AllSigned GPO** — every PowerShell script run on the machines must be signed with the prod certificate
  `CN=powershell.axinetwork.loc`; `-ExecutionPolicy Bypass` does not get around it. Consequence: operational
  scripts (deployment, watchdog) must be either signed or **PS-free** (scheduled tasks via `schtasks`).
- **A security classifier** — automatically blocks some operations on the production SQL-04 and changes to
  one's own permissions. Consequence: production API deployment and SQL grants are run by a **human
  operator**; only the build is prepared.
- **NAT / firewall** — stations behind NAT, dynamic IPs. Consequence: a **push model** and keying on
  **hostname**, not IP.
- **WinRM closed** — remote management over WinRM is unavailable. Consequence: deployment over **SMB +
  remote `sc.exe`** (ports 135/445).
- **gMSA** — the preferred way to run services without passwords in configuration.

### 4.4 Scope and system boundaries (out of scope)

Explicitly **out of scope** (and therefore a limitation, not an omission — see §19):

- Blocking **HID spoofing** (BadUSB as a keyboard / network card) — the solution targets storage class.
- **Guaranteed pre-mount blocking** (the medium never appears at all) — a user-mode agent is reactive; the
  guarantee requires GPO Device Installation Restrictions or a kernel filter driver.
- **Encryption of data on the medium** (BitLocker To Go / DLP handles that).
- **Data content classification** (DLP).

---

# PART II — Design

## 5. Architecture overview

### 5.1 Components

The system consists of four logical components:

```
┌──────────────────────────┐     push (HTTPS :5443)    ┌───────────────────────────┐
│  AGENT (client station)   │ ─────────────────────────►│  API (ingest)             │
│  .NET 8 Windows Service   │   heartbeat / incidents   │  ASP.NET Core, SQL-04     │
│  runs as SYSTEM           │ ◄───────────────────────── │  :5443 (HTTPS) / :5050    │
│  - DeviceMonitor (WMI)    │   whitelist + policy       │  - incident intake (202)  │
│  - WhitelistChecker (RSA) │                            │  - whitelist distribution │
│  - PolicyEnforcer         │                            │  - heartbeat (enforce)    │
│  - DeviceBlocker          │                            └────────────┬──────────────┘
│  - local console :5080    │                                         │ read/write
└──────────────────────────┘                                         ▼
                                                          ┌───────────────────────────┐
┌──────────────────────────┐     read/write (SQL)        │  DATABASE (SQL Server)    │
│  CONSOLE (administration) │ ───────────────────────────►│  SQL-04, DB USBGuardian   │
│  Blazor Server, .213 :4200│                             │  Incidents / Computers /  │
│  - Overview / Stations    │ ◄── AD sync ── Active Dir.  │  WhitelistDevices /       │
│  - Whitelist (signing)    │                             │  WhitelistVersions /      │
│  - Settings / Database    │                             │  AppSettings              │
│  - auto-enrollment        │                             └───────────────────────────┘
└──────────────────────────┘
```

| Component | Technology | Location | Identity |
|-----------|------------|----------|----------|
| Agent | C# .NET 8, Windows Service | every station | LocalSystem (SYSTEM) |
| API | ASP.NET Core (Kestrel) | SQL-04, `C:\USBGuardian.Api` | gMSA `gmsa-SQL$` |
| Console | Blazor Server | .213, `C:\Apps\USBGuardianConsole` | LocalSystem (= `B-S-W-MIKOS$`) |
| Database | SQL Server | SQL-04, DB `USBGuardian` | — |

### 5.2 Two key architectural axes

**(a) Push model (agent → server).** The agent initiates all communication: a periodic *heartbeat*
(reporting online state, the whitelist and agent versions; receiving back the `enforce` flag, the
availability of a new whitelist version and any commands) and an *incident sync* (sending the event queue).
The server has no back-channel to the agent — everything "from the server" is delivered **piggy-backed on
the heartbeat response**.

**(b) A two-tier server.** The operational side (console, AD sync, whitelist publishing) runs on the
application server **.213**; the database is pure storage on **SQL-04**. The ingestion API currently runs on
SQL-04 (a move to .213 is planned — see the roadmap). Incident intake (the API) is **separated** from
administration (the console), so that the load of 500+ agents does not affect the usability of
administration.

### 5.3 Fundamental design properties

- **The client is a 1:1 copy of the server.** The agent holds no "truth" of its own — it takes both the
  whitelist and the enforcement policy from the server and converges to it. Locally it holds only a signed
  copy (a JSON file).
- **The server is the source of truth.** Any local exception (break-glass) is temporary and is cancelled at
  the next contact with the server.
- **Fail-secure.** A failure to verify the whitelist signature does not lead to "allow everything" but to
  the safe option.
- **No PKI dependency.** Encryption and integrity rest on the tool's own mechanisms (self-signed + pinning,
  an internal RSA key), independent of the company CA.
- **Least privilege everywhere.** gMSA for services, granular SQL grants, separate deployment identities.

---

## 6. Defence of design decisions (a decision log)

This chapter is the core of the document for a review. Each decision is stated together with its
**context**, the **alternatives considered**, the **chosen option** and the **conscious trade-off**.

### 6.1 Push model vs. pull model

- **Context:** 500+ stations behind NAT/firewalls, with dynamic IPs and no inbound reachability.
- **Alternatives:** (a) Pull — the server connects to the agents and requests data from them; (b) Push — the
  agent initiates an outbound connection.
- **Chosen:** **Push.** The agent only needs an outbound connection (HTTPS out), which works behind NAT
  without port forwarding and without tracking dynamic IPs. The server need not know the station's address.
- **Trade-off:** The server has no immediate back-channel → commands "from the server" (requesting data,
  changing `enforce`) are delivered with a latency of ≤ the heartbeat interval (~2 min). This is a
  **consciously accepted** property; for the purpose at hand (media policy) a latency of ≤2 min is entirely
  sufficient. (Latency is discussed in §13.4.)

### 6.2 Blazor Server (.NET) vs. Node.js for the console

- **Context:** The console shares the data model with the API (EF Core entities, `AppDbContext`).
- **Alternatives:** (a) A Node.js/React SPA; (b) Blazor Server (.NET).
- **Chosen:** **Blazor Server.** It allows **direct reuse of the EF models** from the API (linked
  `DbModels.cs`, `AppDbContext.cs` — no schema duplication), one language and runtime, and ASP.NET Core
  already runs on the server. A separate API layer for the console is unnecessary (it reads SQL directly).
- **Trade-off:** Blazor Server keeps state on the server (a SignalR connection) — for an administration
  console with a small number of concurrent users (the IT team) this is a non-issue; it would not suit a
  public high-concurrency application, which is not the case here.

### 6.3 HttpListener vs. Kestrel for the agent's local console

- **Context:** The agent needs a local diagnostic UI (loopback) and a few admin actions.
- **Alternatives:** (a) Kestrel (ASP.NET Core); (b) `System.Net.HttpListener` (http.sys).
- **Chosen:** **HttpListener.** The agent is a `Worker` without an ASP.NET Core runtime — Kestrel would drag
  in the whole web stack. HttpListener is enough for a loopback dashboard and a handful of endpoints,
  without an extra dependency.
- **Trade-off:** Less comfort (manual routing, no DI middleware), but a markedly smaller footprint and
  attack surface. For a loopback-only, admin-only, mostly read-only interface that is proportionate.

### 6.4 Keying on hostname, not IP

- **Context:** Stations have dynamic IPs (DHCP).
- **Chosen:** **Hostname** as the primary key in the `Computers` table and in heartbeat correlation.
- **Trade-off:** It assumes reasonably unique hostnames in the domain (satisfied via AD). An IP would be
  unstable and unusable as an identity.

### 6.5 A self-signed cert + thumbprint pinning vs. the company CA

- **Context:** NIS2 requires encrypted transport; the deployment should not depend on an external PKI.
- **Alternatives:** (a) A certificate from the company CA; (b) Let's Encrypt (unavailable internally);
  (c) self-signed + thumbprint pinning on the agent.
- **Chosen:** **A self-signed cert generated by the API at startup + thumbprint pinning**
  (`tls.pinnedThumbprint`) on the agent. Encrypted **and** authenticated, without any CA dependency and
  without externally driven expirations.
- **Trade-off:** The thumbprint must be distributed once into the agents' configuration (part of the
  deployment). Replacing the cert requires updating the pin. For a closed agent↔API system that is
  acceptable and, in fact, more robust (independent of the state of the company CA). CA validation can be
  enabled as an alternative.

### 6.6 `MachineKeySet` vs. `EphemeralKeySet` for the self-signed cert

- **Context:** The API runs under a **gMSA**; Kestrel has to perform the TLS handshake through Schannel.
- **The problem (a latent bug found):** With `EphemeralKeySet`, Schannel will **not** perform the
  server-side handshake (the private key is not persistently available to the service) → the connection
  fails.
- **Chosen:** **`MachineKeySet`** — the key stored in the machine store, available under a gMSA too.
- **Lesson:** A typical example of a decision that is not obvious on paper and emerged from a real test;
  documented so it is not repeated.

### 6.7 Server-side automatic whitelist signing vs. offline manual signing

- **Context:** The whitelist has to be signed (integrity), but management must be operationally bearable.
- **Alternatives:** (a) **Offline signing** — the private key outside the server, every change = a manual
  offline step (maximum key security, the principle of "the key is never on the server"); (b) **Server-side
  auto-signing** — the private key on the server, the console signs automatically after every catalog change.
- **Chosen:** **Server-side auto-signing** (`WhitelistPublisher`). After every catalog change (including a
  manual "Publish now") the console issues a new version, signs it with the internal RSA key
  (`Whitelist:PrivateKeyPath` on .213), stores `Json`+`Signature` in the DB and activates it.
- **Trade-off (consciously chosen and central to the review):** The private key **is** on the .213 server
  (protected by ACL/DPAPI) in exchange for **full automation**. The original principle of "the private key
  is never on the server" was **deliberately abandoned**, because a manual offline step after every catalog
  change was operationally unbearable and would have led to the whitelist not being kept up to date (a
  greater real risk than compromise of an ACL-protected key on the app server). The key is USB Guardian's
  **own internal key**, not a company code-signing cert or a CA — compromising it threatens only the
  integrity of the whitelist, nothing more. The offline `WhitelistSigner` remains as a tool for key
  generation and manual verification.

### 6.8 The client as a 1:1 byte copy of the server

- **Context:** The signature must match byte for byte; the agent has no database.
- **Chosen:** The server keeps the **exact signed blob** (`WhitelistVersions.Json`, `NVARCHAR(MAX)`), the API
  serves it **verbatim**, the agent stores it as a JSON file and verifies it. Canonicalisation: UTF-8 without
  BOM, SHA-256/PKCS#1. The same blob string is signed, served and verified.
- **Trade-off:** The server must keep the blob exactly (not re-serialise it) — hence `NVARCHAR(MAX)` and
  verbatim serving. The benefit is trivial and robust verification on the agent (no re-serialisation, no
  differences in key order or escaping).

### 6.9 Enforcing a block via `Disable-PnpDevice` (the driver) vs. an IOCTL eject

- **Context:** Blocking must be reliable and reversible, ideally without depending on a drive letter.
- **Alternatives:** (a) `IOCTL_STORAGE_EJECT_MEDIA` (requires a drive letter, the medium can be re-attached);
  (b) `Disable-PnpDevice` by `PNPDeviceID` (disabling at the PnP node level).
- **Chosen:** **`Disable-PnpDevice`.** It needs no drive letter (blocking can happen right on the
  `Win32_DiskDrive` connect), works immediately, is **reversible** (`Enable-PnpDevice`) and uses
  `PNPDeviceID`, which is always available.
- **Trade-off:** It is invoked through PowerShell (a `powershell.exe` process) — a small process-start
  overhead, acceptable for a rare event (plugging in a medium). Reversibility, on the other hand, is
  essential for break-glass and reconciliation.

### 6.10 User attribution through the WTS API (not `Environment.UserName`)

- **Context:** The agent runs as **SYSTEM**, so `Environment.UserName` returns the machine account
  (`HOST$`), not the real user — which would devalue the audit trail.
- **Chosen:** `SessionUser` through the **WTS API** (`WTSGetActiveConsoleSessionId`, session enumeration,
  `WTSQuerySessionInformation`) → the real `DOMAIN\user`. Fail-safe: with no logged-on user it falls back to
  the machine account (an incident is always recorded).
- **Trade-off:** A dependency on the WTS API (Windows-specific) — which is fine, the agent is Windows-only.

### 6.11 Soft delete (deactivation) vs. hard delete in the whitelist

- **Context:** Removing a medium from the whitelist; a NIS2 audit prefers keeping the approval history.
- **Chosen:** **Both** — the "Active" checkbox = a soft deactivation (UPDATE, keeps the audit record), the ✕
  button = a hard delete (DELETE, a clean catalog). Publishing snapshots only active records, so both
  variants functionally remove the medium from the enforced whitelist.
- **Trade-off:** A hard delete requires the DELETE permission on `WhitelistDevices` (a granular grant — the
  console owns that table entirely). DELETE is **not** granted on `WhitelistVersions` (versions =
  append-only audit). For NIS2, soft delete may be preferred; the system allows both.

### 6.12 Least-privilege deployment through a gMSA scheduled task

- **Context:** Auto-deployment of the agent requires admin rights on the clients; the console must not have
  them.
- **Chosen:** The console (identity `B-S-W-MIKOS$`) only **writes the list of targets**; the installation is
  performed by a **separate scheduled task on .213 under a dedicated gMSA** (`gmsa-USBGdep$`), which is an
  admin on the clients only. The console therefore changes neither its identity nor its SQL grants.
- **Trade-off:** More moving parts (a task, a gMSA, a targets file), but a strict separation of roles —
  compromising the console does not grant admin on the clients.

### 6.13 Summary of decisions

For a detailed tabular summary of all decisions see **Appendix F**.

---

## 7. Data model and data flows

### 7.1 Data model (tables)

| Table | Purpose | Key columns |
|-------|---------|-------------|
| `Computers` | Station inventory from AD + agent state | `Hostname` (key), `Domain`, `OperatingSystem`, `AdPath`, `InActiveDirectory`, `LastSeen`, `AgentVersion` |
| `Incidents` | Audit records of events | `Timestamp`, `Hostname`, `Username`, `VendorId`/`ProductId`/`SerialNumber`, `FriendlyName`, `SizeBytes`, `Action`, `WhitelistVersion`, `DisconnectedAt` |
| `WhitelistDevices` | The catalog of approved media | `VendorId`, `ProductId`, `SerialNumber`, `Description`, `ApprovedBy`, `ApprovedAt`, `IsActive` |
| `WhitelistVersions` | Signed whitelist versions (snapshots) | `Version`, `IssuedAt`, `ValidUntil`, `IssuedBy`, `Json` (NVARCHAR(MAX)), `Signature` (NVARCHAR(MAX)), `IsActive` |
| `AppSettings` | Central operational settings (key/value) | `Key`, `Value` (NVARCHAR(MAX)) |
| `ActivityLog` | The operations log (added 09/2026, see §34) | `Timestamp`, `Level`, `Source`, `Hostname`, `User`, `Message` |

A note on the design: `WhitelistVersions` **does not reference** `WhitelistDevices` by a foreign key — it
holds an **independent snapshot** (a JSON blob) taken at the moment of publication. That makes a version
immune to later catalog changes, and deleting a catalog row does not break historical versions (nor fail on
an FK).

### 7.2 Data flow — an incident (an unapproved medium is connected)

```
1. USB attached → WMI __InstanceCreationEvent (Win32_DiskDrive)
2. DeviceMonitor: parses VID:PID:Serial from PNPDeviceID (serial trimmed)
3. WhitelistChecker: key VID:PID:SERIAL → O(1) index → NOT on the whitelist
4. PolicyEnforcer: effective mode (PolicyState) → block / warn
5a. block: DeviceBlocker.BlockDevice → Disable-PnpDevice + a record in blocked.json
5b. NotificationService → the toast queue → ToastHelper (user session) displays it
6. IncidentLogger: writes into the daily JSON queue (queue/), attribution via SessionUser (WTS)
7. IncidentSync (≤1 min, or immediately on ReportNow): POST /api/incidents (HTTPS+pinning)
8. API IncidentsController: 202 Accepted → IncidentQueue (does not write to the DB)
9. IncidentQueueWorker (async): writes into the SQL table Incidents
10. Console (Overview): aggregation, filtering, export; e-mail alerts (IncidentAlertService)
```

### 7.3 Data flow — whitelist distribution (a 1:1 copy)

```
An admin changes the catalog (console) → WhitelistPublisher:
   snapshot of active devices → canonical whitelist.json blob (version yyyy-MM-dd-vN)
   → signature with the internal RSA key (.213) → store Json+Signature, activate
API: GET /api/whitelist (the blob verbatim) · GET /api/whitelist/signature (base64)
Agent (the heartbeat within ≤2 min reports WhitelistUpdateAvailable):
   downloads blob+signature → SignatureVerifier verifies (fail-secure) → stores whitelist.json (+.sig)
   → WhitelistChecker.Reload() (drops the cache) → RebuildIndex (Dictionary O(1))
```

### 7.4 Data flow — enforcement and reconciliation

```
Heartbeat → HeartbeatController returns Enforce (from AppSettings policy.enforce, .213 = the truth)
Agent: PolicyState.OnServerHeartbeat(enforce) (+ clears the local break-glass override)
WhitelistSync.ReconcileBlocked (every cycle):
   - blocking ON  → ReEnforceConnectedDevices() (re-block attached unapproved media)
   - blocking OFF → UnblockAll() (return everything the agent disabled)
   - blocked but approved in the meantime (IsAllowedKey) → return even while blocking is on
Break-glass (the local console): SetOverride + UnblockAll() immediately
```

### 7.5 Data flow — AD sync and auto-enrollment

```
AdSyncRunner (60 min + on demand): AD (objectCategory=computer, not disabled)
   → upsert Computers (key: hostname; does NOT overwrite LastSeen/AgentVersion)
   → reconciliation: InActiveDirectory && LastSeen==null && AgentVersion=="" = the agent is missing
AgentDeployService (after the sync, default OFF + dry-run):
   applies defaultEnroll + include/exclude → writes deploy.targetsFile
   → a scheduled task on .213 (gMSA) → Deploy-AgentFleet.ps1 → installation on the clients
```

---

# PART III — Implementation

## 8. The agent (client workstation)

The agent is a .NET 8 Windows Service running as **LocalSystem (SYSTEM)**. It consists of hosted services
(`BackgroundService`) and shared singletons registered in DI (`Program.cs`).

### 8.1 `DeviceMonitor` — media detection

It watches connections/disconnections through three **WMI watchers**:

1. `__InstanceCreationEvent` on `Win32_DiskDrive` — a physical disk was attached.
2. `__InstanceCreationEvent` on `Win32_LogicalDisk` — a drive letter was assigned.
3. `__InstanceDeletionEvent` on `Win32_DiskDrive` — the disk was detached (fills in `DisconnectedAt`).

**Pairing (a timing fix).** The order of the WMI events (disk vs. drive letter) is not guaranteed, so the
monitor keeps two "pending" maps (`_pendingDevices`, `_pendingDriveLetters`) with a 30 s timeout and
correlates them by `DiskIndex`. **A key decision:** enforcement is triggered **right on the
`Win32_DiskDrive` connect**, without waiting for a drive letter (minimising the window in which the medium
can be mounted). The drive letter, if it arrives, is merely added to the log.

**Startup scan (`ScanConnectedDevices`).** The watchers only catch *new* connections; media attached before
the service started would go unseen. At startup, therefore, all attached USB/SD media are walked once and
evaluated (including for real blocking after an agent restart).

**Re-enforcement (`ReEnforceConnectedDevices`).** The symmetric counterpart to auto-re-enable: while
blocking is on, it walks the attached media and **re-blocks** the unapproved ones that are not blocked yet.
It closes the hole where a medium returned through break-glass would stay attached and unblocked after
blocking is switched back on. Idempotent (approved and already-blocked media are skipped).

**WMI watchdog.** Every 5 minutes it verifies the subscriptions are alive (a query on `Win32_DiskDrive`);
on failure it re-registers the watchers. The time of the last WMI event is exposed in the local console
(a "STALE" indicator).

### 8.2 `WhitelistChecker` — verification against the whitelist

- It loads the local `whitelist.json` and, before use, verifies the **RSA-4096 signature** (fail-secure: an
  invalid/missing signature → the whitelist is rejected → `null`).
- **O(1) indexes.** After loading it builds a `Dictionary` (`VID:PID:SERIAL` → record) — a match is O(1) and
  scales to 10k+ devices. Optionally a wildcard index (`VID:PID` without the serial), only when
  `AllowWildcards=true` (off by default, with a security warning).
- **A 5-minute cache** + **`Reload()`**. The loaded whitelist is cached for five minutes (saving I/O on
  frequent connect-time queries). After downloading a new version, `WhitelistSync` calls `Reload()` → the
  cache is dropped → the new version takes effect **immediately** (otherwise a newly approved or removed
  medium would only take effect once the cache expired — this latent problem was found and fixed, see §18).
- **Per-record expiry** (`ValidUntil`, NULL = permanent) as well as expiry of the whole whitelist version
  (a degraded mode with a warning).

### 8.3 `PolicyEnforcer` — deciding the action

For every medium it decides according to the **effective mode** from `PolicyState`:
- approved → a silent `Allowed` audit record;
- unapproved → `Warned` (the medium works) or `Blocked` (disabled) per the effective mode;
- an expired whitelist → per `onExpiredWhitelist` (warn/block/allow).

The effective mode is **not** driven by the fixed local `policy.mode` but by `PolicyState.EffectiveMode()`:
`override active ? warn : (server answer received ? (enforce ? block : warn) : local default)`.

### 8.4 `DeviceBlocker` — blocking and returning

- `BlockDevice(pnpId, key)` → `Disable-PnpDevice` (through PowerShell); on success it records into
  `blocked.json` (a map `PNPDeviceID → key VID:PID:SN`) for later reconciliation.
- `UnblockDevice(pnpId)` → **reliable returning**: first an exact match via `Get-PnpDevice -InstanceId`
  (like a manual `Enable-PnpDevice`), then a `-like` fallback; `Enable-PnpDevice` with `-ErrorAction Stop`
  inside `try/catch`. Outcomes: `ENABLED` (allowed → remove from the list), `GONE` (the medium was detached
  → treated as resolved and removed so it does not hang around), `FAILED` (a real failure → log it and leave
  it for a retry). This robustness came after discovering that the naive variant reported a *false success*
  on a non-terminating `Enable-PnpDevice` error (see §18).
- `UnblockAll()` → returns everything the agent disabled (break-glass / enforcement off).
- The blocked state is **persisted** (`blocked.json`), so it survives a service restart.

### 8.5 `SessionUser` — attribution of the real user

Through the WTS API it determines the user logged into the active interactive session (`DOMAIN\user`),
because the agent as SYSTEM would otherwise report the machine account. A fail-safe fallback to the machine
account (an incident is always recorded). Used in `Incident.Username`, in the log and in the toast
notification.

### 8.6 `IncidentLogger` and synchronisation

- `IncidentLogger` stores incidents in daily JSON queues (`queue/`) and, once sent, moves them into `sent/`
  with its own retention.
- `IncidentSync` (interval ~1 min, with jitter) sends the queue to the API; it wakes earlier on a
  `ReportNow` signal (a data request from the console).
- `WhitelistSync` (interval ~2 min) sends the heartbeat (version, online state, agent commit), receives
  `enforce`, the availability of a new whitelist version and commands; downloads and verifies the whitelist;
  and runs `ReconcileBlocked`.
- `SyncSignals` — the shared heartbeat signal → an immediate incident flush.

### 8.7 `PolicyState` — shared enforcement state

A singleton holding: the server `enforce` (from the heartbeat), `serverReceived` (whether one has arrived
yet), and the local break-glass override (`_overrideUntil`, persisted into `override.json`). The key logic:
- `OnServerHeartbeat(enforce)` — applies the server's enforce and **clears** the local override (the server
  is the truth);
- `EffectiveMode(localMode)` — see §8.3;
- `SetOverride/ClearOverride` — break-glass with a 72 h cap.

### 8.8 The agent's local admin console

`LocalConsoleService` — `HttpListener` on `127.0.0.1:5080` (optional, off by default). **Admin-only**
(`WindowsPrincipal.IsInRole(Administrator)`), mostly **read-only**. It exposes the live state: the whitelist
(version, status, the list of devices), the agent version (commit), the WMI watchdog, the queue, attached
media, recent events and the **number of blocked** media. Writing actions (admin-only, loopback):
`POST /api/override[/clear]` (break-glass), `POST /api/unblock-all` (a manual immediate return),
`POST /api/restart` (a self-restart of the service). Loopback + Windows auth + admin-only + mostly read-only
⇒ no password is needed. (For the authorization subtlety with a filtered token see §34.3.)

### 8.9 Agent resilience

- **A watchdog (a scheduled task, every 3 min, PS-free)** — brings the service back up if it dies. An
  attacker has to disable *both the service and the task* (two independent mechanisms).
- **Service recovery actions** (`sc failure`) — an automatic restart after a crash.
- **Offline operation** — the agent works without the server (the local whitelist); the heartbeat only
  reports and picks up the policy; break-glass makes offline work possible.

---

## 9. The server API (ingest)

An ASP.NET Core application on SQL-04, Kestrel bound to **HTTPS :5443** (+ HTTP :5050, planned to be
closed). It runs under the gMSA `gmsa-SQL$`. Agents authenticate with Windows Auth (Kerberos/Negotiate) and
are authorized through the `USBGuardianClients` policy (membership in `Authorization:AllowedGroups`).

### 9.1 Controllers

| Controller | Endpoint | Function |
|------------|----------|----------|
| `IncidentsController` | `POST /api/incidents` | Intake of incidents from agents → **202 Accepted** + insertion into `IncidentQueue` (it does **not** write to the DB). `GET` for the console. |
| `WhitelistController` | `GET /api/whitelist` | The active signed blob **verbatim**. |
| | `GET /api/whitelist/signature` | The base64 signature. |
| `HeartbeatController` | `GET /api/heartbeat` | Returns `CurrentWhitelistVersion`, `WhitelistUpdateAvailable`, `ReportNow`, `Enforce`, `ServerTime`. Advances `LastSeen`/`AgentVersion`. |
| (cert info) | `GET /api/cert-info` | The thumbprint of the self-signed cert (for pinning). |
| (version) | `GET /api/version` | The commit of the running API. |

### 9.2 Separating intake from writing (resilience)

The key decision for NFR-4 (resilience under load): `IncidentsController` **does not write to the DB
directly**. A received incident is put into the in-memory **`IncidentQueue`** and 202 is returned.
Asynchronously, **`IncidentQueueWorker`** (a hosted service) takes items off the queue and writes them to
SQL. This separates intake (fast, independent of DB latency) from writing — a burst from 500 agents does not
block on the database.

> **A latent bug (found and fixed):** `IncidentsController` required `IncidentQueue`, but `Program.cs` did
> not register it in DI → **a 500 on every `/api/incidents`** (the heartbeat, which lacks that dependency,
> worked). After `AddSingleton<IncidentQueue>` + `AddHostedService<IncidentQueueWorker>` the controller
> returns 202 and the worker writes. Included as an example of why integration verification of the whole
> path matters.

### 9.3 `SelfCert` — self-signed TLS

At startup it generates/persists its own certificate (`C:\ProgramData\USBGuardian\api-tls.pfx`,
`MachineKeySet`) and Kestrel binds it on :5443. It logs the thumbprint and returns it through
`/api/cert-info`. No CA, no cert store (see §6.5, §6.6).

### 9.4 `RetentionService`

A `BackgroundService` (every 6 h). As the only component with DELETE rights on `Incidents`
(`db_datawriter`) it deletes incidents older than the limit (`retention.incidentDays`,
`ExecuteDeleteAsync`) and writes `retention.lastRun`. The console has read/write on `Incidents` without
delete — hence retention enforcement lives in the API (least privilege: the delete right only where it is
needed).

---

## 10. The administration console

Blazor Server on .213 (:4200), the AXIMA UI standard (dark/light, a footer with the service line). It
reads/writes SQL-04 through EF Core (models linked from the API). Authorization: Windows Auth; only members
of `Authorization:AdminGroups` / accounts in `AllowedUsers` (appsettings = a lockout-safe bootstrap) **or**
the DB list from Settings get in.

### 10.1 Pages

- **Overview** — a cross-page tile summary, filter (period/action/full-text), aggregation, an "Approved"
  column per the active whitelist, media capacity. **Export:** CSV (Excel) + a **management report**
  (KPIs + inline-SVG charts, printable on 1–2 A4 pages).
- **Stations** — the AD inventory, tiles (all / reporting / **silent agents** / missing agent), the AD path,
  a communication icon, "Request data" (ReportNow), a "Deployment" column (auto-enrollment control).
- **Whitelist** — entry by serial number + autofill from incidents, import, inline editing, the Active
  checkbox (soft deactivation) and ✕ deletion, **auto-publication of a signed version** after every change.
- **Settings** — enforcement, communication oversight, the access whitelist, e-mail + alerts,
  auto-enrollment (+ the default for new PCs), retention, AD sync, Maintenance (reloading `AccessCache`).
- **Health checks** — checks of the server and the clients with running results and export (added 08/2026,
  see §34.4).
- **Activity** — the operations log (added 09/2026, see §34.1).
- **Database** — a read-only overview of the DB content (counts, the incident range, an `AppSettings` dump).
- **Documentation** — `.md` rendered (Markdig) + the interactive "How it works" animation, plus a mind map,
  a flowchart and a management summary.

### 10.2 `AdSyncRunner` / `AdSyncService`

Reads computers from AD (`new DirectoryEntry()` — the ambient domain, nothing hardcoded), upserts into
`Computers` (key: hostname, does not overwrite `LastSeen`/`AgentVersion`). Reconciliation of
"in AD ⨯ reporting an agent".

### 10.3 `WhitelistPublisher`

After every catalog change: a snapshot of active devices → a canonical blob → a signature with the internal
RSA key → storing `Json`+`Signature`, activation. See §6.7, §11.

### 10.4 `AgentDeployService` (auto-enrollment)

A 24/7 orchestrator (default OFF + dry-run). After the AD sync it finds stations without an agent
(`InActiveDirectory && LastSeen==null && AgentVersion==""`), applies `defaultEnroll` + include/exclude
exceptions and (in live mode) writes the targets into `deploy.targetsFile`. The installation is performed by
a scheduled task on .213 under a gMSA (see §6.12, §15).

### 10.5 `IncidentAlertService` + `EmailSender`

A background notifier: a digest of new unapproved incidents by e-mail (SMTP relay / M365 Direct Send), a
baseline on the first run, interval/throttle.

### 10.6 `AccessCache` and robust error messages

`AccessCache` caches the list of permitted users/groups (reloaded through Settings → Maintenance). Error
messages from DB operations unwrap the **whole InnerException chain** (`Detail(ex)`) — an EF
`DbUpdateException` carries only "See the inner exception" in `.Message`, so without unwrapping the real
cause (e.g. "DELETE permission denied on WhitelistDevices") would never reach the UI.

---

## 11. Cryptography and whitelist signing

### 11.1 Whitelist integrity — RSA-4096

The whitelist is signed with **RSA-4096** (SHA-256, PKCS#1). The agent verifies the signature before every
use (`SignatureVerifier`), **fail-secure** (an invalid/missing signature → the whitelist is not used). The
public key sits on the agents (`whitelist_public.pem`), the private one on the .213 server
(`Whitelist:PrivateKeyPath`, gitignored, ACL-protected).

### 11.2 Byte accuracy (canonicalisation)

The same blob string is **signed, served and verified** — UTF-8 without BOM, no re-serialisation. The server
keeps the exact blob (`NVARCHAR(MAX)`), the API serves it verbatim, the agent stores and verifies it 1:1.
That eliminates differences in key order, escaping or encoding, which would otherwise break the signature.

### 11.3 Separation from the company PKI

The whitelist signing key is USB Guardian's **own internal key**, **not** a company code-signing cert or a
CA. Its sole purpose is whitelist integrity. This is distinct from:
- **TLS** (the API's self-signed cert + pinning — §6.5),
- **signing PowerShell scripts** (the company cert `CN=powershell.axinetwork.loc`, AllSigned GPO — §15).

The three independent "cryptographic worlds" are deliberately separated so that compromising one does not
endanger the others.

### 11.4 Key lifecycle and risks

- **Generation:** the offline `WhitelistSigner` (`tools/WhitelistSigner`).
- **Storage of the private key:** the .213 server, gitignored, ACL/DPAPI.
- **Risk:** compromising the private key would allow forging the whitelist → mitigated by ACLs plus the
  limited blast radius (whitelist integrity only). The "key on the server" trade-off is discussed in §6.7
  and §19.

---

# PART IV — Security and enforcement

## 12. Security and threat model

### 12.1 Assets and trust boundaries

- **Assets:** the integrity of the whitelist (the rules), the audit trail (incidents), the availability of
  enforcement, the confidentiality of company data (indirectly — preventing exfiltration).
- **Trust boundaries:** the .213 server (= the source of truth, holds the private key), the API/DB (gMSA),
  the agent (runs as SYSTEM, holds only the public key and a signed copy).

### 12.2 Threats and countermeasures (STRIDE)

| Category | Threat | Countermeasure |
|----------|--------|----------------|
| **Spoofing** | Impersonating the server to the agent (MITM) | TLS + certificate thumbprint pinning (the agent verifies the exact server) |
| | Impersonating an agent to the server | Windows Auth (Kerberos), membership in the AD group `USBGuardianClients` |
| **Tampering** | Forging the whitelist (adding the attacker's medium) | RSA-4096 signature, verified on the agent (fail-secure) |
| | Modifying the local `whitelist.json` on a station | The signature no longer matches → the whitelist is rejected; the attacker has no private key |
| | Modifying `blocked.json` / `override.json` | Requires local admin; the override is cleared on the heartbeat (the server is the truth) |
| **Repudiation** | A user denies connecting a medium | The incident carries attribution via the WTS API (`DOMAIN\user`), centrally audited |
| **Information disclosure** | Eavesdropping on agent↔API traffic | TLS encryption |
| | Sensitive values in the repository | `*.local.json` and the private key are gitignored |
| **Denial of service** | A flood of incidents brings intake down | 202 + an in-memory queue + a worker (intake separated from writing) |
| | An attacker stops the agent service | A watchdog (scheduled task) + recovery actions — two independent mechanisms |
| **Elevation of privilege** | Compromising the console → admin on the clients | The console is not an admin on the clients; deployment is done by a separate gMSA task |
| | Abusing the agent's local console | Loopback-only, admin-only, mostly read-only |

### 12.3 Security layers (defence in depth)

| Layer | Mechanism |
|-------|-----------|
| Transport | TLS 1.2+ (Kestrel), thumbprint pinning |
| Rule integrity | RSA-4096 whitelist signature, fail-secure |
| Authentication | Windows Auth (Kerberos / Negotiate) |
| Authorization | AD groups (`USBGuardianClients`, the console's admin groups) + an account whitelist |
| Service identities | gMSA (no passwords in configuration) |
| Least privilege in the DB | granular grants (read everything; write only the necessary tables; DELETE only where required) |
| Least privilege in deployment | a separate gMSA that is an admin on the clients only |
| Tamper resistance | the service + the watchdog, running as SYSTEM |
| Configuration | sensitive values outside the repository (`*.local.json`) |

### 12.4 Assumptions and boundaries of the model

The model **assumes** that:
- the attacker does **not** have persistent local admin/SYSTEM on the station (otherwise they can disable
  the agent — which is true of any host-based agent and lies beyond any achievable guarantee);
- the domain infrastructure (AD, Kerberos, gMSA) is trustworthy;
- the .213 server and the ACL on the private key are protected.

These assumptions are stated explicitly, because a reviewer will rightly aim at them. The mitigations
(the watchdog,
audit, the server as the truth) **raise the cost of an attack** but do not guarantee resistance against a
local admin — which is a fundamental limitation of the host-based approach (see §19).

---

## 13. The policy enforcement model

### 13.1 Phases 1–3

- **Phase 1 — whitelist distribution (1:1).** Automatic server-side signing, the agent as a byte copy
  (§6.7, §6.8, §7.3).
- **Phase 2 — policy distribution.** The heartbeat carries `enforce` (`AppSettings policy.enforce`, .213 =
  the truth); the agent uses the effective mode (enforce → block, otherwise warn).
- **Phase 3 — local break-glass.** A local admin can temporarily (capped at 72 h) switch blocking off to
  work offline; persisted, logged as an incident, and **cleared at the next heartbeat** (the server is the
  truth).

### 13.2 Reconciliation (symmetry)

A key property — enforcement is **self-healing in both directions**:

| Transition | The agent's action |
|------------|--------------------|
| Blocking **off** (break-glass / `enforce=false`) | Return **everything** the agent disabled (`UnblockAll`). Locally **immediately**; from the server within ≤ one heartbeat. |
| Blocking **on** | **Re-block** attached unapproved media that are not blocked (`ReEnforceConnectedDevices`). |
| A medium **approved** (added to the whitelist) while running | Returned even with blocking on (reconcile `IsAllowedKey`); effective immediately after the download (cache invalidation). |
| A medium **removed** from the whitelist | It gets blocked (on connect, on re-enforce, or after a restart); immediately after the new version is downloaded. |

### 13.3 Reliability and idempotence

- **Returning** (`UnblockDevice`) is robust (an exact `-InstanceId` + a fallback, handling `GONE`/`FAILED`)
  — it does not repeat a false success, it cleans up a detached medium, and it leaves a real failure for a
  retry.
- **Re-blocking** is idempotent (it skips approved and already-blocked media) → it can safely be called
  every cycle.
- **State** (`blocked.json`, `override.json`) is persisted → it survives a restart.

### 13.4 Latency and its limits

- **Local actions** (turning break-glass on/off) → **immediate** (synchronous from the console on 5080).
- **Server-side changes** (`enforce`, the whitelist) → ≤ the heartbeat interval (~2 min). Consciously
  accepted (the push model, §6.1); entirely sufficient for a media policy.
- **The window before blocking on connect** — the agent blocks right on the `Win32_DiskDrive` connect, but
  Windows mounts removable storage very quickly; the brief moment before `Disable-PnpDevice` cannot be
  fully eliminated in user mode. **Guaranteed pre-mount blocking** requires GPO Device Installation
  Restrictions or a kernel filter driver (see §19). This is the most significant open limitation and it is
  stated honestly.

---

## 14. Auditability and NIS2 compliance

### 14.1 The audit trail

Every relevant event is an **incident** with a timestamp, hostname, the **real user**, the medium's
identification (VID/PID/serial), its size, the action (`Allowed`/`Warned`/`Blocked`/`OverrideDisabled`) and
the whitelist version. Incidents are centrally aggregated (console, export, management report). Break-glass
is logged as a full audit event (who, when, for how long) and reported to the server. Since 09/2026 the
audit trail is complemented by the **activity log** (§34.1), which also covers traffic and operator actions,
not just incidents.

### 14.2 Evidence for an audit

- **What was connected** (both unapproved and approved) — a complete trail.
- **Who** — attribution via the WTS API.
- **How the system reacted** — the action in the incident.
- **Which rules applied** — the whitelist version on every incident + the versioning in `WhitelistVersions`.
- **Exceptions** — break-glass is logged and auditable.
- **Deployment state** — which stations have an agent, which have "gone silent" (a possible outage or
  tampering).

### 14.3 Retention

Centrally controlled (`retention.incidentDays`), enforced in the API (`RetentionService`). It makes it
possible to evidence the retention policy and to check the extent of the data (the Database page shows the
incident range).

### 14.4 Mapping to requirements

For a detailed mapping of NIS2 / ISO 27001 → specific features see **Appendix E**.

---

# PART V — Operations

## 15. Building, deployment and updates

### 15.1 Building

- **The agent (complete package):** `scripts\Build-AgentPackage.ps1` → a self-contained agent (root) +
  `ToastHelper\` (notifications in the user session) + `tasks\` (scheduled task definitions). The client
  needs no .NET runtime.
- **Console / API:** `dotnet publish -c Release -r win-x64 --self-contained`.
- The builds are **self-contained** — the target machines need neither the .NET SDK nor the runtime.

### 15.2 Deployment (a mechanism for a constrained environment)

WinRM is closed, so deployment goes over **SMB + remote `sc.exe`** (ports 135/445), i.e. a network token of
an account without UAC on the target:

- **The console (.213):** `robocopy` → `\\.213\C$\Apps\USBGuardianConsole` (with
  `/XF appsettings.local.json`) + `sc.exe \\.213 stop/start`. Careful: wait for `STOPPED` (otherwise the exe
  is locked).
- **The API (SQL-04):** the build is staged on .213 and installed onto SQL-04; run by the **operator** (the
  classifier blocks prod SQL-04 operations for the assistant). Wait for `STOPPED` before `robocopy`.
- **The agent (fleet):** `Deploy-AgentFleet.ps1` (a runspace pool, PS 5.1 and 7) — `robocopy` of the package
  + `sc.exe \\HOST create` + recovery + **PS-free** watchdog and ToastHelper tasks (`schtasks`).

### 15.3 Auto-enrollment

After opt-in the console deploys the agent to stations without one by itself: it writes the targets and a
gMSA scheduled task on .213 runs `Deploy-AgentFleet.ps1`. Least privilege (§6.12). Default OFF + dry-run;
the recommended sequence is `pilot station → a pilot group → the fleet`. Details:
[auto-deploy-setup.en.md](auto-deploy-setup.en.md).

### 15.4 Updating clients (as designed in v1.0)

At the time of version 1.0 only a **fresh installation** was fully automated; **updating** a running agent
was on the roadmap. The proposed procedure (reusing the existing pipeline):

1. **An update-safe `Deploy-AgentFleet.ps1 -ReinstallExisting`:** `sc stop` → wait for `STOPPED` →
   `robocopy` → `sc start`, with the **watchdog task temporarily disabled** during the copy (otherwise the
   watchdog brings the old service back within 3 minutes and locks the exe). (The existing
   `-ReinstallExisting` did not stop the service before copying — a known gap, see §19.)
2. **Version targeting in the console:** compare `Computers.AgentVersion` (from the heartbeat) against the
   target commit; stale stations → update targets → the gMSA task runs the reinstall. The same
   least-privilege model.
3. **A controlled rollout:** dry-run/opt-in, ring deployment, an audit CSV; the commit stamp serves as
   confirmation of success (the console shows who is up to date).

The **"self-update by the agent"** alternative (downloading and overwriting its own exe) was considered and
**rejected** as riskier (a service overwriting its own binary, the need for a hosted and signed build); a
push from .213 is simpler and largely already built.

> **Status as of 09/2026:** this design has been implemented as a separate `Update-Agent.cmd` task
> (stop → wait for `STOPPED` → copy → verify `RUNNING`), together with stable/beta channels and a version
> archive — see §34.2.

### 15.5 The AXIMA environment — PowerShell signing

Scripts running on machines (Deploy-AgentFleet on .213) must be **signed** with the prod cert
`CN=powershell.axinetwork.loc` (AllSigned GPO), with the publisher in `LocalMachine\TrustedPublisher`;
before signing, CRLF + UTF-8 BOM. The watchdog and ToastHelper are **PS-free** (`schtasks`), so they need no
signature on the clients.

---

## 16. Versioning and deployment verifiability

Every component reports its **git commit** (stamped at build time through MSBuild `git rev-parse`), so that
an operator can verify what exactly is running:

- **The console** — the footer + `:4200/api/version`.
- **The API** — `:5050/api/version`.
- **The agent** — reports the commit in the heartbeat → the console's "Agent version" per station.

The stamp is **reliable**: `GitCommit.g.cs` is generated and rewritten only when the commit changes
(`WriteOnlyWhenDifferent`), which forces a recompile even when the code is otherwise unchanged. The footer
and `/api/version` therefore **exactly** match the deployed git — serving as a currency check of the
solution and as confirmation of a successful deployment or update.

> **Operational rule:** deployment is always done **as the last step after a commit**, and after every
> deployment the live commit hash is reported for the operator to verify.

---

## 17. Operations, monitoring, retention

- **Communication oversight:** the "Silent agents" tile (reports an agent, but `LastSeen` is older than the
  `comm.silentAfterMinutes` threshold) — an indicator of an outage or tampering. A communication icon per
  station.
- **Requesting data (ReportNow):** the console writes the request into `AppSettings`; the agent flushes its
  queue at the next heartbeat (≤2 min). It doubles as a "last requested" audit record.
- **Alerts:** e-mail on new unapproved incidents (`IncidentAlertService`).
- **Retention:** centrally controlled, enforced in the API.
- **Local diagnostics:** the agent's console (5080) — whitelist status, WMI, the queue, blocked media,
  recent events, self-restart.
- **Logging:** the agent logs into the **Windows Event Log** (`ProviderName=USBGuardian`, Application);
  level Warning and above (Information does not reach the Event Log). The server logs into the Event Log and
  the console (`RoleTagFormatter`: `[KLIENT]` / `[SERVER]`).

> **Operational note:** when debugging or verifying the agent's behaviour, the Event Log is the primary
> source of truth about what the service really does (static analysis is not enough — see §18).

---

# PART VI — Verification and evaluation

## 18. Testing and live verification

### 18.1 Methodology

Verification was done **end-to-end on the pilot station .181 (TRNKAMW11)** in the real domain, with an
emphasis on **runtime evidence** from the Windows Event Log (not merely static code analysis). That proved
essential: some defects (the false success of `Enable-PnpDevice` on a non-terminating error) were invisible
from a static view and only showed up at runtime.

### 18.2 Verified scenarios (live, from the Event Log)

| Scenario | Expectation | Result (Event Log) |
|----------|-------------|--------------------|
| An unapproved medium is connected (enforce ON) | Block + an incident | `Unauthorised medium … → DISABLED → BLOCKED` ✅ |
| Switch blocking off (break-glass) | Return everything at once | `returning 1 → Unblocking finished: 1 of 1 returned` ✅ |
| Switch blocking back on | Re-block what is attached | `Re-enforcement … blocking → DISABLED → 1 re-blocked` ✅ |
| Remove a medium from the whitelist (server) | Block it | After downloading v7 + a restart: `1 re-blocked` (Kingston 3.0) ✅ |
| Add a medium to the whitelist (server) | Allow / return it | Reconcile `IsAllowedKey` → returned ✅ |
| Switch enforcement off on the server | Return blocked media | After the heartbeat: Kingston `Status=OK`, `blocked.json` empty ✅ |
| User attribution | `DOMAIN\user`, not `HOST$` | Incidents show `AXINETWORK\trnkam` ✅ |
| Incident delivery | agent→API→DB→console | The Overview shows incidents from .181 ✅ |
| The commit stamp | the footer = the deployed git | The footer shows `agent f2bb194` after the redeploy ✅ |

### 18.3 Defects found and fixed (regressions/latent)

For a review it is relevant to be transparent about the defects found during development and their causes:

1. **DI of the incident queue** — `IncidentsController` required `IncidentQueue`, which was not registered
   in DI → a 500 on `/api/incidents`. Fix: registering `IncidentQueue` + `IncidentQueueWorker`.
2. **`EphemeralKeySet` → `MachineKeySet`** — without it Schannel would not perform the server TLS handshake
   under a gMSA.
3. **Trimming the serial number** — WMI returns the serial with trailing spaces → it did not match the
   whitelist. Trimming both while parsing and in the console.
4. **A false success from `Enable-PnpDevice`** — without `-ErrorAction Stop` the error was non-terminating
   and the script still reported `ENABLED` → the medium stayed blocked but was removed from the list. Fix:
   an exact `-InstanceId` + `try/catch` + distinguishing `ENABLED`/`GONE`/`FAILED`.
5. **The 5-minute whitelist cache was not invalidated after a download** — a newly approved/removed medium
   only took effect once the cache expired (up to ~5 min, or after a restart). Fix:
   `WhitelistChecker.Reload()` in `WhitelistSync` after the download.
6. **Re-blocking attached media** — the agent blocked only on a new connection; a medium returned through
   break-glass stayed visible after blocking was switched back on. Fix: `ReEnforceConnectedDevices` (every
   cycle + on clearing the override).
7. **A missing DELETE grant** — deleting from the whitelist failed with "DELETE permission denied"; the UI
   moreover hid the inner exception. Fix: `GRANT DELETE ON WhitelistDevices` + unwrapping the inner
   exception.

### 18.4 Limits of the verification

Verification was performed on **a single pilot station**, not on a fleet of 500+. Scalability (the O(1)
match, the separated API, the queue) is **designed** but **has not yet been fully verified under the load of
500 agents** — an open point (see §19). Automated tests (unit/integration) are limited; the centre of
gravity is the live end-to-end test — which is deliberate and stated in the document.

---

## 19. Limitations, risks and known weaknesses

This chapter is key for a review — it lists **deliberate** limitations, not omissions.

### 19.1 A fundamental limitation of the host-based approach

- **A local admin / SYSTEM attacker** can disable the agent (stopping both the service and the task). The
  watchdog and the audit **raise the cost** and provide visibility (a silent agent), but they do **not
  guarantee** resistance against a local admin. This holds for any host-based agent. Mitigation is
  organisational (limiting local admin rights).

### 19.2 Guaranteed pre-mount blocking (the most significant technical limitation)

- A user-mode agent is **reactive**: it blocks right on connect, but Windows mounts removable storage very
  quickly → there is a **short window** in which the medium can appear in Explorer before
  `Disable-PnpDevice` takes effect. **Guaranteed** prevention (the medium never appears) requires **GPO
  Device Installation Restrictions** or a **kernel storage filter driver** — on the roadmap as a complement,
  not a replacement.

### 19.3 The private signing key on the server

- A deliberate trade-off (§6.7): the key is on .213 (ACL) in exchange for automation. The risk of key
  compromise = forging the whitelist; the impact is limited to whitelist integrity (not a CA, not code
  signing). Mitigations: ACL/DPAPI, monitoring, possibly a future HSM or removal of the right.

### 19.4 Unencrypted HTTP :5050

- The API still listens on HTTP :5050 as well (alongside HTTPS :5443). For NIS2 it should be **HTTPS only** —
  closing :5050 is on the roadmap.

### 19.5 Single points / topology

- The API still runs on SQL-04 (a move to .213 is planned). The DB is a single instance (backup/HA are
  outside the scope of this tool and are handled at the infrastructure level). The console and the API are
  single-instance (sufficient for this scale).

### 19.6 Updating clients

- Clean automation of updating a running agent is **missing** (fresh install only). `-ReinstallExisting`
  does not stop the service before copying (a locked exe). For the proposed solution see §15.4 — a **known
  gap**, not an omission. *(Implemented in 09/2026 — see §34.2.)*

### 19.7 A per-serial blocklist

- There is no explicit **blocklist** of a specific medium taking precedence over the whitelist (e.g. banning
  a known malicious medium even if its VID/PID matched an approved one). On the roadmap.

### 19.8 HID / BadUSB

- The system targets **storage class**; it does not protect against a medium presenting itself as a keyboard
  or network card. Out of scope (addressed by other measures — e.g. blocking HID through GPO/EDR).

### 19.9 Scaling — unverified under full load

- The design accounts for 500+ (O(1) match, the queue, the separated API), but **a load test on the full
  fleet has not been run**. Recommended before a fleet-wide rollout.

### 19.10 A dependency on PowerShell for block/unblock

- `Disable/Enable-PnpDevice` is invoked through `powershell.exe` (process start overhead). Acceptable for
  rare events (plugging in a medium); with a massive re-enforce it could be optimised (CIM directly). Being
  watched.

### 19.11 Risk summary

| Risk | Severity | State |
|------|----------|-------|
| The window before blocking (pre-mount) | Medium | Known, mitigation via GPO/driver on the roadmap |
| A local admin disables the agent | Medium | Fundamental, organisational mitigation |
| The key on the server | Low–medium | A deliberate trade-off, ACL |
| HTTP :5050 open | Low | Roadmap (close it) |
| Fleet update missing | Medium (operational) | Design done, implementation pending *(done 09/2026)* |
| Scaling unverified | Medium | A load test is recommended |

---

## 20. Roadmap

| Priority | Item | State |
|----------|------|-------|
| High | A per-serial blocklist (precedence over the whitelist) | 🔜 |
| High | Client updates (an update-safe fleet + version targeting) | design done *(implemented 09/2026, §34.2)* |
| High | Guaranteed pre-mount blocking (GPO Device Installation Restrictions / a kernel driver) | 🔜 |
| Medium | Close HTTP :5050 (HTTPS only) | 🔜 |
| Medium | Move the API to .213 ("everything on the app server") | 🔜 |
| Medium | Monitoring the signing certificate's expiry | 🔜 |
| Medium | A load test on the full fleet | 🔜 |
| Low | Hardening: a dedicated `USB-Guardian-Admins`, an HTTPS console | 🔜 |
| Low | Toast privilege separation (pipes SYSTEM→user) | 🔜 |

---

## 21. Conclusion

USB Guardian is a working, end-to-end verified technical measure for controlling removable media, designed
with NIS2 / ISO 27001 and the real constraints of the production environment in mind. Its strengths are:

- **A central, cryptographically assured whitelist** (RSA-4096, fail-secure, 1:1 distribution).
- **Real enforcement** with bidirectional self-healing reconciliation (blocking, returning, re-blocking,
  break-glass) — verified live.
- **Auditability** with user attribution (NIS2).
- **Least privilege and portability** (gMSA, granular grants, no company values in the code).
- **Deployment verifiability** (a commit stamp across the components).

The known limitations (the pre-mount window, the key on the server, the missing fleet update, unverified
scaling) are **deliberate, documented and accompanied by a plan**. The system claims no more than being a
"partial technical measure" within a wider ISMS.

From a review perspective, what matters is that **every substantial decision has a documented alternative
and trade-off** (§6, Appendix F) and that **defects found during development are stated transparently**
along with their cause and fix (§18.3).

**PART VII** follows with extended analyses (anticipated reviewer questions, comparison with alternatives, a
test catalogue, quantitative scaling, operational runbooks, detailed diagrams), which complement the main
argument and serve as reserve material for an in-depth discussion during the defence.

---

# PART VII — Extended analyses and defence

## 22. Anticipated reviewer questions and answers

This chapter anticipates the likely questions of a critical reviewer and answers them directly. It is
organised by topic.

### 22.1 Architecture and model

**Q1: Why a push model, when pull would give the server immediate control over the agent?**
Pull assumes inbound reachability of the stations — which in practice (NAT, dynamic IPs, firewalls, laptops
off the network) does not exist for 500+ machines. Push needs only outbound HTTPS and works universally. The
price is a command latency of ≤ one heartbeat (~2 min), which is irrelevant for a media policy (a medium is
not a real-time threat in the millisecond sense). For immediate local actions (break-glass) the agent has
its own synchronous path.

**Q2: Two minutes of latency — is that not a security hole? Time passes between disabling a whitelist entry
and blocking.**
Yes, server-side changes propagate within ≤2 min. That is deliberate. Mitigations: (a) the agent blocks an
unapproved medium **immediately on connect** regardless of the freshness of any server change (the whitelist
is available locally); (b) removing an entry takes effect on a *newly connected* medium immediately (the
local whitelist) and on an *already attached* one within one reconcile cycle; (c) for a truly immediate
global switch-off there is local break-glass and re-enforcement. The latency concerns only the *distribution
of a server change*, not the reaction to a medium as such.

**Q3: Why does the agent run as SYSTEM and not with fewer privileges?**
Both disabling a device (`Disable-PnpDevice`) and reading across sessions require high privileges; a SYSTEM
service is the standard model for endpoint agents. The risk (compromising the agent = SYSTEM) is mitigated
by the fact that the agent accepts no arbitrary commands from the network — only a defined heartbeat
protocol with a verified server.

**Q4: Blazor Server keeps state on the server — what if the SignalR connection drops, and what about scaling
the console?**
The console is an administration tool for the IT team (a handful of concurrent users), not a public
application. Losing the SignalR connection merely means reloading the page. The ingestion load (500 agents)
goes to the **separate API**, not the console — hence the separation (NFR-4).

### 22.2 Cryptography and integrity

**Q5: The private key on the server violates "the key is never on the server". How do you defend that?**
It is a deliberate trade-off (§6.7). The original principle leads to manual offline signing after every
catalog change, which is operationally unbearable → the real consequence would be an out-of-date whitelist
(a greater risk than an ACL-protected key). The key is **internal** (whitelist integrity only, not a
CA/code-signing), so the blast radius of a compromise is bounded. Mitigations: ACL/DPAPI, monitoring, a
future HSM. It is the classic choice between theoretical security and operational reality — we chose the
operationally sustainable variant with a bounded impact.

**Q6: A self-signed cert + pinning — what about replacing the cert, what about rotation?**
Replacing the cert = updating `pinnedThumbprint` in the agents' configuration (part of the deployment). For
a closed agent↔API system that is acceptable; the alternative is CA validation (supported). Pinning, on the
contrary, protects against MITM better than blind trust in a CA chain.

**Q7: RSA-4096 — why not ECC or a newer signature scheme?**
RSA-4096/SHA-256 is conservative, widely supported in .NET without external dependencies, and has ample
security margin for the purpose (signing a small JSON blob once per change). ECC would save signature size,
but that is not the bottleneck. The choice favoured compatibility and simplicity of verification.

**Q8: What if the agent receives an older (validly signed) whitelist version — replay/rollback?**
A version carries a `version` (yyyy-MM-dd-vN) and `ValidUntil`. The agent downloads on the basis of the
heartbeat reporting the server's *current* version; the server always serves the active version. A rollback
attack would require MITM (eliminated by pinning) or compromising the server. Harder protection (a monotonic
version enforced by the agent) is a possible improvement; today it relies on the channel being authenticated
and pinned.

### 22.3 Enforcement and reliability

**Q9: A user-mode agent cannot prevent a medium from being connected before it appears. That is a
fundamental weakness.**
Agreed — it is the most significant technical limitation (§19.2) and we state it honestly. The agent
minimises the window (blocking on the `Win32_DiskDrive` connect, not on the drive letter), but it does not
guarantee pre-mount. The **guaranteed** solution is GPO Device Installation Restrictions or a kernel filter
driver — on the roadmap as a complement. USB Guardian adds *a central whitelist, audit and enforcement*
beyond what GPO can do alone; the two measures complement each other rather than replacing one another.

**Q10: What happens when `Disable-PnpDevice` fails (e.g. the medium is unplugged during blocking)?**
`BlockDevice` reports success/failure; on failure the medium is not recorded as blocked and the event is
logged. For returning (`UnblockDevice`) we introduced the `ENABLED`/`GONE`/`FAILED` distinction (§8.4) — an
unplugged medium is `GONE` (cleaned up), a real failure is `FAILED` (retried). This robustness came after
finding the false success (§18.3, item 4).

**Q11: Break-glass = a local admin switches protection off. Is that not a backdoor?**
Break-glass is **temporary** (capped at 72 h), **logged** (an audit incident: who/when/how long, reported to
the server) and **automatically cancelled** at the next contact with the server (the server is the truth).
It exists for legitimate offline work. It is a controlled exception with a full audit trail, not a silent
bypass. A local admin could, after all, simply stop the agent (§19.1) — break-glass is the *more controlled*
and audited variant.

**Q12: What about consistency if the agent restarts in the middle of blocking?**
Both the blocked state (`blocked.json`) and the override (`override.json`) are persisted. After startup a
startup scan and a reconcile run, so the state converges to the server's truth. Verified (restart →
re-blocking).

### 22.4 Operations and deployment

**Q13: How are clients updated to new agent versions?**
At the time of v1.0 only a fresh installation was automated; updating a running agent was designed (§15.4)
but not yet implemented — a **known gap**, not an omission. The design reuses the gMSA pipeline (an
update-safe reinstall + version targeting by `AgentVersion`). *(Implemented in 09/2026 — see §34.2.)*

**Q14: Deployment over SMB + sc.exe — is that not fragile / safe?**
It is a consequence of WinRM being closed in the environment. It uses standard Windows mechanisms (the SCM
over named pipes, the admin share) under an account with the appropriate rights (a gMSA for deployment
only). It is audited (CSV) and idempotent (it skips offline/already-installed machines). Alternatives
(SCCM/Intune) are valid if the environment has them.

**Q15: How do you know that what is running really is the latest version?**
The commit stamp (footer/`/api/version`/heartbeat) = the git HEAD of the build, reliably (regeneration of
`GitCommit.g.cs`). The console shows the agent version per station. Deployment is done as the last step
after a commit, and the live hash is reported for verification.

### 22.5 Compliance and scope

**Q16: Are you claiming NIS2 compliance?**
No — we claim to be a **technical measure supporting** compliance. Compliance is a property of the whole
ISMS, not of a single tool (§3.4). Appendix E is an *indicative* mapping.

**Q17: What about GDPR — you log users and media?**
The logs contain the hostname, the user and the medium's identification — operational data necessary for a
security purpose (legitimate interest / fulfilling an obligation). Retention is controlled (deletion after
`incidentDays`). Deployment must be accompanied by informing employees and a record of processing (the
organisational level).

### 22.6 Quality and verification

**Q18: Where are the automated tests?**
The centre of gravity is the **live end-to-end test** with runtime evidence from the Event Log (§18).
Automated unit/integration tests are limited — we state this openly as room for improvement. For this class
of defects (WMI timing, PnP behaviour, TLS under a gMSA) a live test has more evidential value than a mock.

**Q19: How do you know it will hold 500 agents?**
It is designed for it (an O(1) match, a separated API, an in-memory queue, push). **A load test on the full
fleet has not been run** (§19.9) — recommended before a fleet-wide rollout. For a quantitative estimate see
§25.

**Q20: What is the worst scenario you have not covered?**
The combination of a local-admin attacker + physical access + the pre-mount window. That is the fundamental
boundary of a host-based approach; the mitigations are organisational (limiting local admin) and
complementary technical ones (GPO/driver).

**Q21: Why an in-house solution and not a commercial product?**
See §23. Briefly: control over the behaviour, no licence costs for 500+ stations, full integration into the
environment (AD, gMSA, the domain), no dependency on a cloud or a vendor, and adaptation to AXIMA's
specifics (AllSigned, the classifier). Commercial products are a valid alternative; the choice was
deliberate.

**Q22: What if the console (.213) or the API (SQL-04) goes down?**
Agents work **offline** — they hold the local signed whitelist and the last policy and keep blocking or
warning. The heartbeat merely reports and picks up changes; its outage only means new changes are not
distributed and incidents are not collected (the agent's queue is persistent and catches up once service is
restored). No server outage opens up the protection — a consequence of the "the client is a copy and works
on its own" model.

---

### 22.7 Deeper technical questions

**Q23: WMI as the event source — is polling `WITHIN 1` not inefficient / unreliable?**
`__InstanceCreationEvent ... WITHIN 1` means a 1 s query interval — for plugging in a medium (a rare, human
event) that is sufficient and not burdensome. Reliability is handled by the **watchdog** (every 5 min it
verifies the subscriptions and re-registers on failure) and the **startup scan** (media attached before
startup). An alternative would be `RegisterDeviceNotification` (Win32) — more efficient but more complex;
WMI was chosen for simplicity and sufficiency.

**Q24: The serial number from WMI is not reliable for all devices (some return empty / VID-derived values).**
True. Hence: (a) the serial is **trimmed** (WMI returns trailing spaces); (b) with an empty `SerialNumber` it
falls back to extraction from `PNPDeviceID`; (c) the optional **wildcard** mode (`VID:PID` without the
serial) is **off** by default with a security warning (it is less specific). A medium without a stable
identifier is inherently hard to whitelist — a property of the hardware, not of the tool.

**Q25: Two devices with the same VID:PID:SN (a clone/collision)?**
The match is by key; serial collisions are rare on quality devices but theoretically possible (cheap
clones). The whitelist would not distinguish them. Mitigation: a per-serial blocklist (roadmap) and physical
control; for most of a corporate fleet (branded media) the risk is low.

**Q26: Why incidents as JSON files on disk rather than straight into memory/a stream?**
Persisting the queue (`queue/`) ensures an incident **does not disappear** during a network outage or a
restart — the agent delivers it once service is restored. A file is simple, resilient and auditable even
locally. Once sent it is moved into `sent/` with its own retention.

**Q27: The in-memory `IncidentQueue` on the API — what if the API crashes with a full queue?**
There is a risk of losing unwritten incidents at the moment of a crash. Mitigation: the agent receives 202
only after enqueueing; if a harder guarantee were required, the queue could be persisted (a
latency/throughput trade-off). For this purpose (rare incidents, the agent having its own persistent queue
and retry) in-memory is acceptable — the agent re-sends when nothing is confirmed. **Note:** the agent
deletes from `queue/` only after a successful send, so duplicate delivery is possible, loss is not (on the
agent's side).

**Q28: Idempotence of incident intake — duplicates?**
The agent may re-send an incident (after a timeout), so duplicates are possible. For an audit, "twice rather
than never" is acceptable; de-duplication by (hostname, timestamp, serial, action) is a possible improvement.

**Q29: Why PowerShell for `Disable-PnpDevice` and not a direct Win32/CIM call from .NET?**
The PowerShell cmdlet is the simplest stable route to PnP operations; direct CIM `Win32_PnPEntity.Disable` /
SetupAPI is possible but more complex and error-prone. For a rare event the `powershell.exe` overhead
(~hundreds of ms) is negligible. With a massive re-enforce one could move to CIM (tracked, §19.10).

**Q30: `Get-PnpDevice | Where -like '*...*'` — could it match more devices / the wrong device?**
We first use the **exact** `-InstanceId` (a single device); `-like` is only a fallback. A `*id*` wildcard
could theoretically match a substring, but media `InstanceId`s are specific enough (VID/PID/serial). The
risk is low and the fallback only applies when the exact match fails.

**Q31: What happens when the drive letter changes / the same medium is reconnected?**
The identity is `PNPDeviceID` / VID:PID:SN, not the drive letter — reconnecting the same medium = the same
key, the same decision. The drive letter is merely supplementary information for the log.

**Q32: Blazor Server + Windows Auth — how do you handle granular authorization?**
`WindowsPrincipal.IsInRole` (which handles domain groups) against `AdminGroups`, plus an account whitelist
(`AllowedUsers` in appsettings = lockout-safe) **or** the DB list from Settings. `DevAllowAll` is a bypass
for development only (false in production). For SSO, go through the hostname (not the IP).

**Q33: AccessCache — what if I change access and the cache holds the old list?**
A reload through Settings → Maintenance (and on restart). The trade-off: the cache saves a DB query on every
request; an explicit reload is an acceptable compromise for a rarely changing set of access rights.

**Q34: How do you prevent a "lockout" from the console (deleting my own access)?**
`AllowedUsers`/`AdminGroups` in **appsettings** (outside the DB) act as a **bootstrap** — even if the DB
access list were emptied, the appsettings account still gets in. By design.

**Q35: The heartbeat carries `enforce` — what if an attacker intercepts it and forges enforce=false?**
The channel is TLS + pinning (MITM eliminated). Without compromising the server or the key, the response
cannot be forged. Moreover, when the server is unreachable the agent keeps the **last** policy (it does not
flip to "unprotected" merely because the server is silent).

**Q36: `ReportNow` through AppSettings — how is it one-shot?**
The console writes `cmd.report.<HOST>` = the time of the request. At the heartbeat the agent gets
`ReportNow=true` only if the request is newer than the previous `LastSeen`; the next heartbeat has
`LastSeen` past the request time → `ReportNow=false`. The API only **reads** AppSettings; it does not store
agent state anywhere else.

**Q37: Why does the console lack DELETE on Incidents while the API has it?**
Least privilege. Deleting incidents (retention) is a sensitive operation; it is performed by a **single**
component (the API, `RetentionService`) with a narrowly scoped right. The console only reads/aggregates
incidents — it cannot delete them, neither by mistake nor when compromised.

**Q38: What about localisation / multiple languages?**
The UI and the documentation are CS + EN (README/HANDOFF bilingual). The agent's messages are Czech (a
company environment). Extending this is possible; it is not a security topic.

**Q39: How does the system behave when the clock changes / across time zones?**
Times are kept in **UTC** (the override `until`, timestamps) and displayed in local time. That avoids
DST/zone errors. The heartbeat carries `ServerTime` as a reference.

**Q40: What about updating the .NET runtime / dependencies (vulnerabilities)?**
The builds are **self-contained** — the runtime is part of the package, so updating the runtime = deploying
a new version (see §15.4). That is a trade-off (a bigger package, our own responsibility for patching the
runtime) in exchange for independence from .NET being present on the station. Managing runtime
vulnerabilities is part of the update process.

**Q41: Why not containers / a different server distribution?**
The target environment is Windows Server + AD + gMSA; the services run natively as Windows Services.
Containerisation would add complexity with no obvious benefit at this scale. A self-contained publish +
`sc.exe` is sufficient.

**Q42: How do you test for regressions after changes?**
Currently through live end-to-end verification on the pilot + Event Log evidence (§18). The weaker side is
the absence of extensive automated tests — stated openly (§19); the recommendation is to add unit tests for
the pure logic (`PolicyState.EffectiveMode`, key construction, reconcile decisions) and an integration test
of the ingest path.

**Q43: What when the whitelist approaches its expiry (`ValidUntil`)?**
Both individual records and the whole version have an expiry. When the whole version expires the agent runs
in a **degraded** mode per `onExpiredWhitelist` (warn/block/allow) with a warning. Roadmap: active
monitoring of an approaching expiry + an alert (analogous to monitoring the signing certificate).

**Q44: How does behaviour differ on a laptop off the network?**
The agent works offline (the local whitelist + the last policy). Break-glass allows a legitimate exception.
Once back on the network the heartbeat reconciles the policy and clears the override. Incidents are
delivered from the queue.

**Q45: What is the impact on the user / on station performance?**
The agent is lightweight (a WMI subscriber, rare events). There is no continuous file scanning. A toast
appears only on an event. Blocking is event-driven (on connect). The impact on station performance is
negligible.

---

## 23. Comparison with alternative approaches and products

### 23.1 Approach options

| Approach | Advantages | Disadvantages | Relation to USB Guardian |
|----------|------------|---------------|--------------------------|
| **GPO Removable Storage Access** | Native, pre-mount (prevents device installation), free | No central whitelist of specific media, weak audit with attribution, inflexible per-medium management | **A complement** — GPO for the hard pre-mount layer, USB Guardian for whitelist+audit+enforcement |
| **Device Installation Restrictions (GPO)** | A pre-mount block by class/ID | Managing IDs across a fleet is inflexible, no audit trail of events | A complement (roadmap §19.2) |
| **Commercial device control** (Endpoint Protector, Ivanti, …) | Ready-made, pre-mount, rich features | Licence costs (500+), cloud/vendor, integration | A valid alternative; the in-house solution was chosen for control/cost/integration |
| **Full DLP** | Content classification, not just the medium | Expensive, complex deployment | A different layer (content vs. medium) |
| **EDR/antivirus** | Malware detection | Reacts to the consequence, not to an unapproved medium being connected | Complementary |
| **USB Guardian** | A central signed whitelist, an audit with attribution, enforcement, AD integration, no licences, portable | User mode (the pre-mount window), our own maintenance, host-based limits | — |

### 23.2 USB Guardian's position

USB Guardian does **not replace** GPO/EDR/DLP — it fills a specific gap: *a centrally managed,
cryptographically assured whitelist of specific media with a full audit trail and user attribution, with
enforcement and AD/gMSA integration, without licence costs and without a cloud dependency*. For a hard
pre-mount guarantee it should be combined with GPO Device Installation Restrictions (defence in depth).

### 23.3 Why "build" and not "buy" — the decision criteria

| Criterion | In-house | Commercial |
|-----------|----------|------------|
| Cost for 500+ stations | No licences | An annual licence per station |
| Control over behaviour | Full | Limited |
| Integration (AD, gMSA, AllSigned) | Tailored | Depends on the product |
| Vendor/cloud dependency | None | Often yes |
| Pre-mount guarantee | No (roadmap) | Often yes |
| Maintenance/responsibility | Internal | The vendor |
| An audit trail tailored to NIS2 | Fully adapted | Depends |

Conclusion: for AXIMA, control, cost and integration prevailed; the pre-mount gap will be closed by
combining with GPO.

---

## 24. Detailed test catalogue

A structured overview of test cases. The state "✅ verified live" = confirmed on .181 from the Event Log;
"⏳" = designed/recommended, not yet performed systematically.

### 24.1 Detection and identification

| TC | Scenario | Expected result | State |
|----|----------|-----------------|-------|
| TC-01 | A USB flash drive is attached while the agent runs | An incident with VID/PID/serial, an action per the policy | ✅ |
| TC-02 | A medium attached before the agent started | The startup scan evaluates it | ✅ |
| TC-03 | A serial with trailing spaces | Trimmed → matches the whitelist | ✅ |
| TC-04 | Detaching a medium | `DisconnectedAt` filled in | ✅ |
| TC-05 | An SD card | Detected (InterfaceType SD) | ⏳ |
| TC-06 | Rapid attach/detach (a race) | No crash, correct pairing/timeout | ⏳ |

### 24.2 Whitelist and signature

| TC | Scenario | Expected result | State |
|----|----------|-----------------|-------|
| TC-10 | An approved medium | `Allowed`, the medium works | ✅ |
| TC-11 | An unapproved medium (enforce) | `Blocked` | ✅ |
| TC-12 | A forged `whitelist.json` (changed without a signature) | Rejected (fail-secure) | ⏳ |
| TC-13 | A missing `.sig` | The whitelist is not stored, the old version stays | ✅ (by design) |
| TC-14 | A new whitelist version | Downloaded within ≤2 min, Reload, effective immediately | ✅ |
| TC-15 | An expired version | A degraded mode per `onExpired` | ⏳ |
| TC-16 | 10k records | An O(1) match, no degradation | ⏳ (load) |

### 24.3 Enforcement and reconciliation

| TC | Scenario | Expected result | State |
|----|----------|-----------------|-------|
| TC-20 | Switch blocking off (break-glass) | Return everything at once | ✅ |
| TC-21 | Switch blocking back on | Re-block attached media | ✅ |
| TC-22 | Switch enforce off on the server | Returned within ≤ one heartbeat | ✅ |
| TC-23 | Add a medium to the whitelist while running | Returned (even with enforce on) | ✅ |
| TC-24 | Remove a medium from the whitelist | Blocked | ✅ |
| TC-25 | Restarting the agent with an active block | State reconciled (persistence + reconcile) | ✅ |
| TC-26 | Break-glass expiry (timeout) | The override is cleared, blocking is restored | ⏳ |
| TC-27 | `UnblockDevice` on a detached medium | `GONE`, cleaned out of the list | ✅ (by design/log) |

### 24.4 Communication and resilience

| TC | Scenario | Expected result | State |
|----|----------|-----------------|-------|
| TC-30 | TLS handshake agent↔API (gMSA) | OK (MachineKeySet) | ✅ |
| TC-31 | MITM / a wrong thumbprint | The connection is refused (pinning) | ⏳ |
| TC-32 | The API is unavailable | The agent works offline, the queue accumulates | ✅ (a queue of 21 observed) |
| TC-33 | The API comes back | The queue is delivered | ⏳ |
| TC-34 | A burst of incidents | 202 + the queue, no crash | ⏳ (load) |
| TC-35 | ReportNow | The queue is flushed within ≤ one heartbeat | ✅ |

### 24.5 Deployment and versions

| TC | Scenario | Expected result | State |
|----|----------|-----------------|-------|
| TC-40 | A fresh install (fleet) | The service runs, heartbeat+incidents | ✅ (.181) |
| TC-41 | Reinstall/update of a running agent | Update-safe (stop→copy→start) | ⏳ (the gap in §19.6) *(implemented 09/2026, §34.2)* |
| TC-42 | The commit stamp | The footer = git HEAD | ✅ |
| TC-43 | The watchdog brings a stopped service back | The service is restarted | ⏳ |
| TC-44 | Auto-enrollment (dry-run → live) | Targets written, installation through the gMSA | ✅ (.181) |

### 24.6 Console and DB

| TC | Scenario | Expected result | State |
|----|----------|-----------------|-------|
| TC-50 | Adding a medium to the whitelist | INSERT + auto-publish | ✅ |
| TC-51 | Deleting a medium (✕) | DELETE (with the grant) + auto-publish | ✅ |
| TC-52 | Deleting without the DELETE grant | An error with the inner exception unwrapped | ✅ |
| TC-53 | Toggling Active | UPDATE + auto-publish | ✅ |
| TC-54 | CSV export / the management report | The file inherits the filter | ⏳ |
| TC-55 | AD sync | Upsert into Computers, reconciliation | ✅ |
| TC-56 | Retention | Deletion of old incidents (API) | ⏳ |

---

## 25. Performance and scaling (a quantitative analysis)

### 25.1 Heartbeat load

- **Assumption:** 500 agents, a heartbeat every 2 min.
- **Rate:** 500 / 120 s ≈ **4.2 req/s** on average. The heartbeat is a light GET (it reads `AppSettings` and
  compares a version) → milliseconds. Even with a surge (synchronised starts) it amounts to tens of req/s —
  trivial for Kestrel.
- **Conclusion:** the heartbeat is not a bottleneck even at 2000 agents (~17 req/s).

### 25.2 Incident load

- **Assumption:** incidents arise rarely (plugging in a medium) — on the order of units per station per day.
  Even on a "bad day" (1000 incidents across the fleet in an hour) that is ~0.3 req/s.
- **Resilience to bursts:** intake is separated from writing (202 + an in-memory queue + a worker), so even a
  short peak does not block on DB latency. The queue absorbs peaks; the worker writes at its own pace.
- **Conclusion:** the ingest path is dimensioned with a large margin.

### 25.3 The whitelist match on the agent

- **Algorithm:** `Dictionary<string, WhitelistEntry>` (VID:PID:SERIAL), an **O(1)** lookup.
- **10,000 records:** a few MB of memory, a constant-time lookup. Loading/indexing happens only when the
  version changes (not on every connection — the cache + Reload).
- **Conclusion:** the match scales to large whitelists without degradation; it is not a bottleneck.

### 25.4 Whitelist distribution

- **The blob:** hundreds of bytes per record; 10k records ≈ a few MB of JSON. It is downloaded only when the
  version changes (the heartbeat reports `WhitelistUpdateAvailable`), not periodically.
- **The network:** even on a bulk change, 500 agents download the blob spread across a ~2-minute window →
  negligible.

### 25.5 The database — growth

- **Incidents:** the dominant table. At ~5 incidents/station/day × 500 = 2,500/day ≈ ~900k/year. At ~1 KB per
  row ≈ hundreds of MB per year — trivial for SQL Server. **Retention** (365 days by default) keeps the
  volume bounded.
- **Computers/Whitelist:** hundreds to thousands of rows, negligible.

### 25.6 The console

- **Concurrency:** a handful of users (IT). Queries over incidents use a filter + `Take(200/50000)` → bounded.
  Aggregation is done in memory over a limited selection.
- **Conclusion:** the console is not performance-critical at this number of administrators.

### 25.7 Limits of this analysis

The above are **estimates from the design**, not results of a load test. Recommendation: before a fleet-wide
rollout, run a synthetic load test (500 simulated agents, heartbeats + bursts of incidents) and measure the
API latency, queue depth and the worker's write throughput (§19.9).

---

## 26. Operational runbooks

### 26.1 Deploying a new console version (.213)

1. `git commit` (deployment = the last step after a commit).
2. `dotnet publish ... -o D:\deploy\USBGuardianConsole`.
3. `sc.exe \\10.8.2.213 stop USBGuardianConsole`; wait for `STOPPED`.
4. `robocopy ... \\10.8.2.213\C$\Apps\USBGuardianConsole /E /XF appsettings.local.json`.
5. `sc.exe \\10.8.2.213 start USBGuardianConsole`.
6. Verify that the footer shows the live commit.

### 26.2 Deploying a new API version (SQL-04)

1. The build is staged on .213 (`C:\Apps\USBGuardianApiPublish`).
2. `sc stop "USB Guardian API"`; **wait for STOPPED** (otherwise the exe is locked → robocopy FAILS).
3. `robocopy` onto SQL-04 `C:\USBGuardian.Api` (with `/XF appsettings.local.json`).
4. `sc start`; verify `:5050/api/version`.

*(Since 09/2026 this runs as the `USBGuardian-ApiDeploy` task under the server gMSA — see §34.2. Previously
the operator ran the steps by hand.)*

### 26.3 Redeploying the agent on a station (manual, UAC)

```powershell
$src='D:\deploy\USBGuardianAgent'; $dst='C:\Program Files\USBGuardian'
Stop-Service 'USB Guardian' -Force
while ((Get-Service 'USB Guardian').Status -ne 'Stopped'){ Start-Sleep -Milliseconds 500 }
robocopy $src $dst /E /XF agent.config.local.json /NFL /NDL /NJH /NJS
Start-Service 'USB Guardian'
```
Verify the local console's footer (`agent <commit>`) and the Event Log.

### 26.4 Diagnosing "the medium was not blocked"

1. The local console `127.0.0.1:5080` → the Enforcement card (BLOCKING? Blocked right now?).
2. The Event Log (`ProviderName=USBGuardian`): look for `DISABLED` / `Re-enforcement` / `Cannot enable`.
3. `whitelist.json` on disk — the version and the device count (= what the agent really holds).
4. `blocked.json` — what the agent is keeping blocked.
5. `Get-PnpDevice` — Status `Error` = disabled.
6. If the agent has an old whitelist version (the cache) → verify it runs a build with `Reload()` (cache
   invalidation).

### 26.5 Diagnosing "incidents are not flowing"

1. The local console → the queue (the number of records).
2. The Event Log → `IncidentSync` errors (HTTPS/pinning/auth).
3. Verify the API is reachable (`:5443`) and the pin is valid (`/api/cert-info`).
4. Is the heartbeat OK? (`LastSeen` in the console.) "Request data" (ReportNow) → a flush.

### 26.6 Incident response — suspected tampering

1. The console → the "Silent agents" tile (LastSeen > the threshold) = a possible outage or tampering.
2. Verify the service and the watchdog task are running on the station.
3. Check the `OverrideDisabled` audit incidents (an unauthorised break-glass?).
4. Optionally restart the service remotely / redeploy.

### 26.7 Recovering / rotating the private key

1. Generate a new pair (`tools/WhitelistSigner`).
2. Distribute the new `whitelist_public.pem` to the agents (part of the package/config).
3. Set `Whitelist:PrivateKeyPath` on .213 and re-publish the whitelist (it will be signed with the new key).
4. Verify that the agents accept the new signed version.

---

## 27. Detailed diagrams

### 27.1 State diagram of the effective mode (`PolicyState`)

```
                 ┌─────────────────────────── the local default (before the 1st heartbeat)
                 │
   start ───────►│  EffectiveMode = localMode (warn/block)
                 │
  heartbeat ─────┼──► serverReceived = true
                 │        │
                 │        ├─ enforce=true  → block
                 │        └─ enforce=false → warn
                 │
  break-glass ───┼──► override active → warn  (regardless of the server)
   (5080)        │        │ (capped at 72 h, persisted)
                 │        ▼
  heartbeat ─────┴──► OnServerHeartbeat: the override is CLEARED → back to the server's enforce
```

### 27.2 Sequence — an unapproved medium is connected (enforce ON)

```
USB        DeviceMonitor   WhitelistChecker  PolicyEnforcer  DeviceBlocker  IncidentLogger  API
 │  connect    │                 │                │              │              │            │
 ├────────────►│ parse VID:PID:SN│                │              │              │            │
 │             ├────────────────►│ index O(1)     │              │              │            │
 │             │   not allowed   │                │              │              │            │
 │             ├─────────────────┴───────────────►│ effective=block            │            │
 │             │                                  ├─────────────►│ Disable-PnpDevice         │
 │             │                                  │              │ track blocked.json        │
 │             │                                  ├──── toast queue (ToastHelper) ───────────│
 │             │                                  ├─────────────────────────────►│ queue     │
 │             │                                  │              │              │ IncidentSync─►│ 202→queue→DB
```

### 27.3 Sequence — whitelist distribution (1:1)

```
Admin    Console(WhitelistPublisher)   DB         API        Agent(WhitelistSync)  WhitelistChecker
 │ change  │                            │          │              │                     │
 ├────────►│ snapshot+signature(RSA)    │          │              │                     │
 │         ├───────────────────────────►│ Json+Sig (activate)     │                     │
 │         │                            │          │              │ heartbeat           │
 │         │                            │          │◄─────────────┤ (version)           │
 │         │                            │          ├─ UpdateAvailable────────►│          │
 │         │                            │          │ GET /whitelist(+sig)     │          │
 │         │                            │          ├─────────────►│ verify(fail-secure)  │
 │         │                            │          │              ├ store + Reload ─────►│ RebuildIndex O(1)
```

### 27.4 Deployment component diagram

```
        Active Directory  ◄── LDAP ── Console(.213, Blazor :4200) ── SQL ──►  SQL-04
                                          │  (B-S-W-MIKOS$)                    DB USBGuardian
                                          │  WhitelistPublisher (private key)       ▲
                                          │  AgentDeployService                     │ SQL (gMSA gmsa-SQL$)
                                          ▼                                          │
                          gMSA task (gmsa-USBGdep$) ── SMB+sc ──► Clients    API(.SQL-04 :5443) ── to the DB
                                                                     │  ▲
                                                  push HTTPS :5443   │  │ heartbeat/whitelist/enforce
                                                                     ▼  │
                                                            Agent (SYSTEM) ── local console :5080
```

---

## 28. Detailed legislative and normative analysis

### 28.1 NIS2 (EU directive 2022/2555) — an analysis of the relevant obligations

In **Art. 21(2)** the NIS2 directive enumerates the minimum cyber risk-management measures. The following
table analyses how USB Guardian contributes to the individual points (the interpretation is indicative; full
compliance is a property of the ISMS, §3.4):

| Point of Art. 21(2) (area) | What it requires | USB Guardian's contribution | Complementary measures (outside the tool) |
|----------------------------|------------------|-----------------------------|-------------------------------------------|
| a) risk analysis and policies | Assess risks, have policies | Data for a media risk analysis (the inventory, incidents) | A media policy, a methodology |
| b) incident handling | Detection, reporting, response | Detection of unapproved media, near-real-time reporting, alerts, audit | An IR process, escalation |
| c) continuity / backup | BCM | Indirectly (agents work offline; a server outage does not open protection) | DB backups, HA |
| d) supply chain | — | Out of scope | — |
| e) secure development, vulnerabilities | Secure development/maintenance | Versioning, the commit stamp, a transparent analysis of defects (§18.3) | Vulnerability management |
| f) assessing the effectiveness of measures | Measure effectiveness | The audit + oversight of "silent" agents = measurable coverage | Metrics, audits |
| g) cyber hygiene, training | — | Whitelist enforcement as technical support for hygiene | User training |
| h) cryptography and encryption | Use of cryptography | TLS transport, RSA-4096 whitelist integrity | A cryptography policy |
| i) access and asset management | Access control, inventory | The whitelist (access to media), an inventory of media and stations | An access policy, classification |
| j) MFA / secured communications | Secured communications | TLS+pinning, Kerberos agent↔API | MFA for the console (organisational) |

**Conclusion on NIS2:** USB Guardian contributes directly above all to points **b, f, h, i** and
supportively to **a, e, g, j**.

### 28.2 Act No. 181/2014 Coll. and the decree on security measures

USB Guardian is a **technical measure** contributing above all to:

| Area (the decree, indicatively) | Contribution |
|---------------------------------|--------------|
| Asset management | An inventory of media and stations (from AD) |
| Access control | The whitelist + enforcement — only approved media get access |
| Protection against malicious code | Preventing malware introduction through an unapproved medium |
| Event detection | Detecting connections, incidents |
| Event recording | An audit trail with attribution, central aggregation, retention |
| Change management | Whitelist versioning + the components' commit stamp |
| Physical security | Control of portable storage devices |
| Cryptographic means | TLS, the RSA-4096 signature |

### 28.3 ISO/IEC 27002:2022 — control detail

| Control | Name | Contribution |
|---------|------|--------------|
| 5.9 | Inventory of assets | A record of media + a station inventory |
| 5.10 | Acceptable use of assets | Whitelist enforcement |
| 7.10 | Storage media | The core of the solution |
| 8.7 | Protection against malware | Preventing introduction through a medium |
| 8.15 | Logging | Incidents with attribution |
| 8.16 | Monitoring activities | Central oversight, anomalies (silent agents) |
| 8.20 | Network security | Secured agent↔API communication |
| 8.24 | Use of cryptography | TLS + RSA-4096 |

### 28.4 GDPR / personal data protection

The system processes **operational personal data** (user, hostname, time) for the purpose of information
security. Recommended organisational steps: a legal basis (legitimate interest / fulfilling a legal
obligation under NIS2), informing employees, a record of processing activities, controlled retention
(`retention.incidentDays`), minimisation (only the necessary data is logged). The system enforces retention
technically (the API) — supporting the storage-limitation principle.

---

## 29. Reference overview of classes and responsibilities

### 29.1 The agent

| Class | Responsibility | Key methods / artefacts |
|-------|----------------|-------------------------|
| `DeviceMonitor` | Media detection (WMI), pairing, the startup scan, re-enforcement | `OnDiskConnected`, `ScanConnectedDevices`, `ReEnforceConnectedDevices` |
| `WhitelistChecker` | Verification against the whitelist, the signature, the index, the cache | `IsAllowed`, `IsAllowedKey`, `Reload`, `RebuildIndex` |
| `SignatureVerifier` | Verifying the RSA-4096 signature (fail-secure) | `Verify` |
| `PolicyEnforcer` | Deciding the action (warn/block/allowed) | `HandleDevice`, `DetermineAction` |
| `DeviceBlocker` | Blocking/returning, persistence | `BlockDevice`, `UnblockDevice`, `UnblockAll`, `blocked.json` |
| `PolicyState` | Enforcement state (the server's enforce + break-glass) | `OnServerHeartbeat`, `EffectiveMode`, `SetOverride` |
| `SessionUser` | Attribution of the real user (WTS) | `GetActiveConsoleUser` |
| `IncidentLogger` | The incident queue, retention of `sent` | `LogConnection`, `UpdateDisconnectedAt` |
| `WhitelistSync` | Heartbeat, whitelist download, reconcile | `TrySyncWhitelist`, `DownloadAndSaveWhitelist`, `ReconcileBlocked` |
| `IncidentSync` | Sending the queue to the API | `ExecuteAsync` (jitter, ReportNow) |
| `NotificationService` | The toast queue (the user session via ToastHelper) | `ShowWarningForDevice` |
| `LocalConsoleService` | The local admin console (loopback) | `/api/status`, `/api/override`, `/api/unblock-all`, `/api/restart` |
| `TlsClient` | An HTTP client with pinning | `Create` |

### 29.2 The API

| Class | Responsibility |
|-------|----------------|
| `IncidentsController` | Incident intake (202 → the queue), listings for the console |
| `WhitelistController` | Serving the signed blob + signature (verbatim) |
| `HeartbeatController` | State, version, `Enforce`, `ReportNow` |
| `IncidentQueue` / `IncidentQueueWorker` | The queue + asynchronous writing to the DB |
| `SelfCert` | The self-signed TLS cert (MachineKeySet) |
| `RetentionService` | Deleting old incidents (the only component with DELETE on Incidents) |
| `ActivityLogger` | Writing to the activity log (shared with the console, §34.1) |
| `AppDbContext` | The EF Core context (shared with the console) |

### 29.3 The console

| Class / page | Responsibility |
|--------------|----------------|
| `Home` (Overview) | Aggregation, filtering, grouping, "Approved", export |
| `Computers` (Stations) | The AD inventory, oversight, ReportNow, deployment control |
| `Whitelist` | Catalog management, auto-publish, soft/hard delete, `Detail(ex)` |
| `Settings` / `Database` | Central settings / a read-only DB overview |
| `Health` / `Activity` | Health checks / the activity log (§34) |
| `AdSyncRunner` / `AdSyncService` | AD sync + reconciliation |
| `WhitelistPublisher` | Snapshot + signature + activating a version |
| `AgentDeployService` | The auto-enrollment orchestrator |
| `ExportEndpoints` | CSV + the management report |
| `IncidentAlertService` / `EmailSender` | E-mail alerts |
| `AccessCache` | A cache of access rights (reloaded from Maintenance) |

---

## 30. Detailed analysis of key algorithms and code

This chapter analyses the non-trivial algorithms in depth — for a reviewer who wants to verify the
correctness of the implementation, not just a description.

### 30.1 Pairing WMI events (the timing fix)

**The problem:** When a medium is attached, two independent WMI events arrive — `Win32_DiskDrive` (the
physical disk) and `Win32_LogicalDisk` (the drive letter) — in a **non-deterministic order** and with a
delay. The naive solution (waiting for both) would delay blocking.

**The solution:** two "pending" maps keyed by `DiskIndex` + immediate evaluation on the disk connect:

```
OnDiskConnected(wmi):
    if not IsRemovableMedia(wmi): return
    device = ParseDeviceFromWmi(wmi)          # VID:PID:SN (serial TRIMmed), PnpDeviceId
    diskIndex = ExtractDiskIndex(DeviceID)
    if _pendingDriveLetters.TryRemove(diskIndex, out drive):   # scenario B: the letter came first
        device.DriveLetters.Add(drive)
    ProcessDevice(device)                      # ENFORCEMENT NOW, without waiting for the letter

OnLogicalDiskConnected(wmi):
    diskIndex = GetDiskIndexForLogicalDisk(DeviceID)
    if _pendingDevices.TryRemove(diskIndex, out pending):      # scenario A: the disk was waiting
        pending.Device.DriveLetters.Add(letter); ProcessDevice(pending.Device)
    else:
        _pendingDriveLetters[diskIndex] = (letter, now)        # wait for the disk (30 s timeout)
```

**The key decision:** enforcement is triggered in `OnDiskConnected` **without waiting** for the drive letter
→ minimising the mount window. The drive letter is merely added to the log if it arrives. The 30 s timeout
prevents an accumulation of "orphaned" pending records.

**Edge cases:** a very fast attach/detach (a race) — the pending maps are `ConcurrentDictionary` and
`TryRemove` is atomic; an orphan expires. A medium without a drive letter (unmountable) is still evaluated
(blocking works at the PnP level, not the file-system level).

### 30.2 Reconciliation of the enforcement state (`ReconcileBlocked`)

Called after every sync cycle. The logic (simplified):

```
blocking = PolicyState.EffectiveMode("warn") == "block"

# 1) Re-block attached media (only while blocking) – idempotent
if blocking:
    DeviceMonitor.ReEnforceConnectedDevices()

# 2) Return previously blocked media
blocked = DeviceBlocker.GetBlocked()        # PnpId -> key VID:PID:SN
if blocked.Count == 0: return
for (pnpId, key) in blocked:
    if (not blocking) or WhitelistChecker.IsAllowedKey(key):
        DeviceBlocker.UnblockDevice(pnpId)
```

**Invariants:**
- *Blocking off* → return **everything** the agent disabled.
- *Blocking on* → return only those that are **approved in the meantime** (`IsAllowedKey`).
- Idempotence: repeated calls in a steady state change nothing (re-enforce skips approved and
  already-blocked media; unblock is only called when the condition holds).

**Ordering (subtle but correct):** re-enforce runs **before** the unblock loop. For a medium approved in the
meantime and still attached: re-enforce checks `IsAllowed(device)` = true → **skips** it (does not block),
and the subsequent unblock loop finds `IsAllowedKey` = true → **returns** it. There is thus no "block and
immediately unblock" conflict.

### 30.3 Reliable returning (`UnblockDevice`)

**The problem (a bug found):** a naive `Enable-PnpDevice` without `-ErrorAction Stop` → a non-terminating
error → the script still prints `ENABLED` → a **false success** → the medium stays disabled while the agent
removes it from the list (and never retries).

**The solution:** an exact `-InstanceId` (like the manual command) + a `-like` fallback, `try/catch` with
`-ErrorAction Stop`, and three outcomes:

```
$dev = Get-PnpDevice -InstanceId '<exact>'              # the exact match
if (-not $dev) { $dev = Get-PnpDevice | ? InstanceId -like '*<escaped>*' }   # the fallback
if ($dev) {
    try { Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction Stop; 'ENABLED' }
    catch { 'FAILED:' + $_.Exception.Message }
} else { 'GONE' }
```

| Outcome | Meaning | The agent's action |
|---------|---------|--------------------|
| `ENABLED` | Allowed | Untrack (remove from `blocked.json`) |
| `GONE` | The medium is no longer in the system (detached) | Untrack (resolved; the next plug-in is evaluated afresh) |
| `FAILED:<error>` | A real Enable failure | **Keep** it in the list → the next reconcile retries; log the cause |

**Escaping:** for the exact `-InstanceId` match only the apostrophe is escaped; for `-like` the `&` as well
(`` `& ``). It has been verified that `-like` matches a real `InstanceId` containing `&`.

### 30.4 The whitelist cache and its invalidation (`Reload`)

**The problem (a bug found):** a 5-minute cache + downloading a new version without invalidation → a newly
approved/removed medium only takes effect once the cache expires (and `ReEnforce` reads stale data in the
meantime).

**The solution:** after atomically writing the files, `WhitelistSync.DownloadAndSaveWhitelist` calls
`WhitelistChecker.Reload()` (dropping the cache; `_lastLoaded = MinValue`). The order in the loop:

```
TrySyncWhitelist()         # heartbeat → if a new version: download, verify, store, Reload()
ReconcileBlocked()         # IsAllowedKey → LoadWhitelist (cache=MinValue) → a fresh index
```

→ the reconcile in the **same cycle** sees the new version. The cache remains an optimisation for frequent
connect-time queries (nothing changes between downloads), but after a download it is always fresh.

### 30.5 The effective mode (`PolicyState.EffectiveMode`)

```
EffectiveMode(localMode):
    if OverrideActive:        return "warn"            # break-glass wins (offline work)
    if serverReceived:        return enforce ? "block" : "warn"   # the server is the truth
    return localMode                                    # before the 1st heartbeat: the local config
```

**OnServerHeartbeat(enforce):** sets `serverEnforce`/`serverReceived` and **clears** any override (the
server re-asserts the policy). The override is persisted (`override.json`) with a 72 h cap. This guarantees
that a local exception is temporary and always yields to the server once contact is made.

### 30.6 Byte accuracy of the signature

The critical invariant: **the same string** is signed, served and verified.

```
publish:  blob = CanonicalJson(activeDevices)          # UTF-8, no BOM, a stable order
          sig  = RSA_SHA256_Sign(blob, privateKey)
          DB: Json = blob (NVARCHAR(MAX)), Signature = base64(sig)
serve:    GET /api/whitelist  → returns Json VERBATIM (no re-serialisation)
          GET /api/whitelist/signature → base64(sig)
verify:   ok = RSA_SHA256_Verify(downloadedBlob, decode(sig), publicKey)   # fail-secure
```

Any re-serialisation (a different key order, whitespace, a BOM) would break the signature — hence the
**verbatim** transfer and storage as `NVARCHAR(MAX)` (not structured).

---

## 31. Attack scenarios (attack trees)

A step-by-step analysis of selected attacks, marking where and how the system interrupts them.

### 31.1 The attacker's goal: exfiltrate data onto an unapproved medium

```
Get data out on a USB
├── Attach an unapproved USB
│   ├── Agent enforce=block → Disable-PnpDevice → the medium is unusable          [BLOCKED]
│   │     └── (a residual window before the mount — §19.2; mitigation GPO/driver) [PARTIAL RISK]
│   ├── Agent enforce=warn → the medium works, but an incident with attribution   [AUDIT/DETECTION]
│   └── Is an agent installed? → the console shows "missing agent" / "silent"     [VISIBILITY]
├── Forge the medium onto the whitelist (adding one's own VID:PID:SN)
│   ├── Without console access (an AD group/whitelist) → impossible               [AUTHORIZATION]
│   └── Straight into the DB → no signature by the private key → the agent rejects it (fail-secure) [INTEGRITY]
├── Forge the local whitelist.json on the station
│   └── The signature does not match (no private key) → rejected                  [INTEGRITY]
├── Disable the agent (a local admin)
│   ├── Stop the service → the watchdog brings it back (3 min)                    [RESILIENCE]
│   ├── Stop the service + the task → "silent agents" in the console              [DETECTION]
│   └── (the fundamental host-based limit — §19.1; organisational mitigation)     [RISK]
└── Abusing break-glass
    └── Logged (who/when/how long) + cleared at the heartbeat                     [AUDIT]
```

### 31.2 The attacker's goal: introduce malware through a medium

```
Introduce malware
├── An infected unapproved USB → block/warn + an incident                        [BLOCK/AUDIT]
├── An infected APPROVED USB (a legitimate medium, infected content)
│   └── Outside USB Guardian's scope (EDR/antivirus handles it)                   [BOUNDARY]
│         → recommendation: a blocklist for the specific medium (roadmap §19.7)
└── BadUSB (the medium presents itself as a keyboard/HID)
    └── Out of scope (storage class) — §19.8; mitigation GPO/EDR                  [BOUNDARY]
```

### 31.3 The attacker's goal: MITM between the agent and the server

```
MITM agent↔API
├── Eavesdropping → TLS encryption                                                [CONFIDENTIALITY]
├── Impersonating the server → thumbprint pinning (the agent verifies the exact cert) [SPOOFING BLOCKED]
│     └── the attacker has no private key for the cert → the handshake fails
└── Whitelist rollback (slipping in an older valid version)
    ├── Requires MITM → eliminated by pinning                                     [BLOCKED]
    └── stronger protection (a monotonic version enforced by the agent) = an improvement (§22 Q8)
```

### 31.4 The attacker's goal: compromising the server / the key

```
Compromising the .213 server
├── Obtain the whitelist private key → forge the whitelist
│   ├── Mitigation: ACL/DPAPI on the key, restricted access to .213               [MITIGATION]
│   └── Bounded impact: whitelist integrity only (not a CA, not code signing)     [LIMITED IMPACT]
├── Change the policy (enforce=false) → agents stop blocking
│   └── Detectable (an audit of settings changes); requires console access        [DETECTION/AUTHORIZATION]
└── Recommendation: monitor access to .213, a future HSM, key rotation (§26.7)
```

### 31.5 Coverage summary

| Attack | Interrupted by | Residual risk |
|--------|----------------|---------------|
| An unapproved medium | Block/warn + audit | The pre-mount window |
| Forging the whitelist | The signature (fail-secure) | Compromise of the key on the server |
| MITM | TLS + pinning | — |
| Disabling the agent | The watchdog + detection | A local admin |
| BadUSB / content | — (out of scope) | Add GPO/EDR/a blocklist |

---

## 32. Complete configuration examples (annotated)

> The values are illustrative; the real company values live in `*.local.json` (gitignored).

### 32.1 The agent — `agent.config.json` (+ `.local.json`)

```json
{
  "policy": {
    "mode": "block",                 // the local default before the 1st heartbeat (the server then wins)
    "onExpiredWhitelist": "warn",    // warn|block|allow when the whitelist version has expired
    "overridePath": "C:\\ProgramData\\USBGuardian\\override.json"
  },
  "whitelist": {
    "syncUrl": "https://10.8.2.225:5443",   // the API (HTTPS)
    "localPath": "C:\\ProgramData\\USBGuardian\\whitelist\\whitelist.json",
    "allowWildcards": false          // true = allow records without a serial (a security warning)
  },
  "sync": {
    "whitelistSyncIntervalMinutes": 2,   // heartbeat + a version check
    "incidentSyncIntervalMinutes": 1
  },
  "tls": {
    "validateServerCertificate": true,
    "pinnedThumbprint": "E6F6B4FCE0BB627F564E85D6509DE7C4B82CF2F0"   // the API cert's thumbprint
  },
  "signing": {
    "enabled": true,                 // production: ALWAYS true (verify the whitelist signature)
    "publicKeyPath": "Config\\whitelist_public.pem"
  },
  "localConsole": { "enabled": true, "port": 5080 },
  "notifications": { "toast": { "enabled": true, "contactMessage": "Contact IT" } }
}
```

### 32.2 The API — `appsettings.local.json`

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=tcp:10.8.2.225,1433;Database=USBGuardian;Integrated Security=true;TrustServerCertificate=true;"
  },
  "Authorization": { "AllowedGroups": [ "AXINETWORK\\USBGuardianClients" ] },
  "Kestrel": { "Endpoints": {
    "Https": { "Url": "https://0.0.0.0:5443" },
    "Http":  { "Url": "http://0.0.0.0:5050" }     // roadmap: close it (HTTPS only)
  }}
}
```

### 32.3 The console — `appsettings.local.json`

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=tcp:10.8.2.225,1433;Database=USBGuardian;Integrated Security=true;TrustServerCertificate=true;"
  },
  "Authorization": {
    "AdminGroups": [ "AXINETWORK\\USB-Guardian-Admins" ],
    "AllowedUsers": [ "AXINETWORK\\trnkam" ],     // a lockout-safe bootstrap
    "DevAllowAll": false
  },
  "Whitelist": { "PrivateKeyPath": "C:\\Apps\\USBGuardianConsole\\whitelist_private.pem" },
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:4200" } } },
  "AdSync": { "Enabled": true, "IntervalMinutes": 60, "SearchBase": "", "IncludeDisabled": false }
}
```

### 32.4 Central settings (`AppSettings` in the DB) — typical values

| Key | Value | Note |
|-----|-------|------|
| `policy.enforce` | `true` | global enforcement |
| `comm.silentAfterMinutes` | `180` | the "silent agent" threshold |
| `whitelist.validityDays` | `365` | the validity of an issued version |
| `retention.enabled` / `.incidentDays` | `true` / `365` | retention |
| `deploy.enabled` / `.dryRun` / `.defaultEnroll` | `false` / `true` / `false` | auto-enroll (a safe default) |
| `email.enabled` / `.smtpHost` | `true` / `axima-cz.mail.protection.outlook.com` | M365 Direct Send |

---

## 33. Behaviour in edge cases

A systematic overview of how the system behaves in non-trivial situations — for a reviewer looking for
undefined states.

| Situation | The system's behaviour | Design principle |
|-----------|------------------------|------------------|
| The server (.213/API) is unavailable | The agent runs offline: the local whitelist + the last policy; the incident queue accumulates (persistent) | The client is self-sufficient; an outage does not open protection |
| The whitelist file is missing | `WhitelistChecker` returns `null` → the medium cannot be verified → per `onExpired`/the policy (fail-secure) | Fail-secure |
| The `.sig` signature is missing/mismatched | The whitelist is rejected, the last valid version stays; the new one is not stored | Fail-secure, atomic writing |
| The whitelist download is interrupted mid-way | Atomic writing (temp → rename, the .sig first then the .json); an inconsistent combination is rejected | Atomicity |
| The agent restarts with an active block | `blocked.json` + the startup scan + reconcile → the state converges | Persistence + reconcile |
| A medium is detached during blocking | `BlockDevice` reports the state; on returning, `GONE` → cleaned out of the list | Robust returning |
| A medium is detached and then reattached | The same key → the same decision; the reconcile evaluates the attached one against the current policy | Identity = VID:PID:SN |
| Break-glass expires (timeout) | `OverrideActive` = false → the effective mode returns to the server's enforce; the reconcile re-blocks | The override is temporary |
| Break-glass + a long loss of connectivity | The override holds until the timeout (max 72 h), then expires even without the server | The cap as a safety net |
| Server enforce=false → true (switching on) | The reconcile re-blocks attached unapproved media (ReEnforce) | Symmetry |
| A medium is approved while running | After the download (Reload) the reconcile returns it even with enforce on | Cache invalidation |
| A medium is removed from the whitelist | A newly attached one is blocked immediately; an attached one after the reconcile/restart | The local whitelist |
| Two media at once | Each is evaluated separately (per PNPDeviceID) | Independent processing |
| The user is logged out (services only) | Attribution falls back to the machine account (an incident is always recorded) | Fail-safe attribution |
| Multiple sessions (RDP + console) | The WTS API takes the active console session, with enumeration as a fallback | Best-effort attribution |
| The WMI subsystem fails | The watchdog (5 min) re-registers the watchers; it logs | Self-healing |
| A disk without a drive letter | Evaluated at the PnP level (blocking works even without an FS mount) | Blocking on connect |
| A medium disabled by another tool | The agent returns only what **it itself** disabled (`blocked.json`) | It does not meddle with others' work |
| The station's clock is off | Times in UTC; the override `until` in UTC → the timeout is robust | UTC everywhere |
| A very large whitelist (10k) | An O(1) match, the index in memory; loading only on a version change | A scalable index |
| Concurrency of a reconcile and a connect event | The states are thread-safe (`ConcurrentDictionary`, locks in `DeviceBlocker`/`PolicyState`) | Thread safety |

### 33.1 Defined "safe" default states

- Before the first heartbeat: the local `policy.mode` (it can be set to `block` for "secure by default").
- A missing/invalid whitelist: nothing is let through (fail-secure), per `onExpired`.
- Auto-enrollment: **off + dry-run** (no unexpected bulk deployment).
- The local console: **off** (by default), admin-only, loopback. *(See the open inconsistency in §34.3.)*

### 33.2 Configurations that are not recommended (and why)

| Configuration | Risk |
|---------------|------|
| `signing.enabled=false` | Disables signature verification → the whitelist can be forged (development only) |
| `tls.validateServerCertificate=false` without a pin | MITM (development only) |
| `allowWildcards=true` | Less specific (VID:PID without a serial) → broader permission |
| `policy.onExpiredWhitelist=allow` | After expiry everything is allowed → protection is lost |

---

# PART VIII — Addendum

## 34. What changed since version 1.0 (state as of 4 Sep 2026)

Chapters 1–33 stand as written on 19 June 2026. This chapter summarises what has been added since, what
turned out differently in operation from what the document assumed, and what remains open. It exists because
a document that describes an intention while presenting itself as a description of the state is worse than
none — a reviewer cannot verify anything against it.

### 34.1 The activity log (`ActivityLog`)

Version 1.0 built the audit trail purely on incidents. That is not enough: **only what ended as an incident
lands in the incidents**. When an agent stopped communicating, when somebody changed the whitelist, or when
a new version was deployed, no trace remained anywhere except in the Event Log of a single machine.

A `dbo.ActivityLog` table (time, level, source, station, user, message) and an **Activity** page in the
console have been added. Both the **API** (the heartbeat, including *what* the server answered, and the
receipt of incident batches) and the **console** (manual deployment and updates, permanently excluding a
station, whitelist publication) write into it — both through a shared `ActivityLogger`, so that the
operation reads as one story.

The write is **fire-and-forget and every error is swallowed**. If an agent's heartbeat failed because a log
row could not be written, the observer would matter more than the thing it observes. For the same reason the
write is not awaited — the pulse of hundreds of agents must not be tied to database latency.

**An open point (stated honestly):** `sp_PurgeActivityLog` exists in the database, but **nothing calls it**.
With 227 stations and a heartbeat every 2 minutes this amounts to roughly **150,000 rows per day**;
activity-log retention is therefore a debt, not a feature. Settings so far carry only
`retention.incidentDays`.

### 34.2 The deployment channel: installation ≠ update

Version 1.0 described deployment as a single task. Operation showed two flaws in that assumption.

**(a) An update is not just an extra copy.** The fleet script could only do a clean install; "just robocopy"
would overwrite part of the DLLs on a running agent, the copy of the locked `.exe` would fail and the
station would be left with a **mix of versions** — while the deploy reports success. Hence `Update-Agent.cmd`
(and `Deploy-Api.cmd` for the server) with the pattern **stop → wait for `STOPPED` → copy → verify
`RUNNING`**.

**(b) One identity held both tiers.** The client deploy account was also an admin on the database server, so
compromising it would have reached both the fleet and the server. It has been split into three roles:
`gmsa-USBGdep$` (stations only), `gmsa-USBGsrv$` (the API server only, deliberately outside the server-admin
group) and the account the console runs under, which is **an admin nowhere**.

**A finding during verification (4 Sep 2026):** the `USBGuardian-ApiDeploy` task, which the documentation
described as existing, **had never been created** on the app server — only the script was there. As a result
the API had been running the June build since then, even though "the deploy went through". The task was
created and its first run verified (return code 0, the service `RUNNING`, `/api/version` reporting the
current commit). The lesson for a review: *a claim that "the channel exists" is worth something only when it
is evidenced by its last run.*

> **The trap when creating a task under a gMSA:** `schtasks /Create /RU "…gmsa$"` without a password
> produces a task with `LogonType=InteractiveToken` → it never runs (event 332). S4U (`/NP`) has no network
> credentials and cannot reach `\\HOST\C$`. The only thing that works is XML with `LogonType=Password` saved
> as **UTF-16** and created via `/XML`.

**Channels and rollback:** the package is archived per version (`stable` / `beta`), so a previous version
can be deployed. The package also carries an offline installer for a station the deploy channel cannot
reach.

### 34.3 The local console: authorizing a local admin

Phase 3 (break-glass) assumed that a local admin could reach the console on `127.0.0.1:5080`. In practice
they could not. A loopback request is, as far as Windows is concerned, a **network logon**, and for a
**local** account `LocalAccountTokenFilterPolicy` strips the `Administrators` group from such a token (it
remains only as *deny-only*), so `IsInRole` returns false even though the person **is** an admin.
Break-glass was thus unavailable in exactly the situation it exists for.

The check now **accepts a filtered token** as well. That is defensible: membership serves as
**authorization**, not as the source of rights — the action itself is performed by the service running as
SYSTEM, and no elevated caller token is needed. A refusal also returns a page showing **who** the request
was seen as and what is required; without that it could not be diagnosed remotely.

**A configuration inconsistency and the decision on it:** the template had `localConsole.enabled=false`
(minimum attack surface), but **the rolled-out package and the archived versions all say `true`**. Decided on
4 Sep 2026: the console is **on** across the fleet and is **exclusively for a local administrator of that
station** — the end user does not belong in it. The template in the repo stays `false` (a safe default for
other environments, portability), the fleet package is built with `true`, and the build warns about the
opposite state.

**A consequence for reading this document:** in an environment where admin rights live on separate accounts
(`pcadmin.*` in the `PC Admins` group), break-glass is not a tool for *the user in the field* but for **a
technician at a station that cannot reach the server**. An ordinary account gets the explanatory refusal —
verified in production on 4 Sep 2026, when a colleague tried to reach the console under his everyday
account. The behaviour was correct; the wording in the few places that described it as a user-facing feature
has been corrected.

### 34.4 Operational features added after version 1.0

| Feature | What it addresses |
|---------|-------------------|
| **Health checks** | The list of server and client checks is shown up front and ticked off with running results; export to CSV / HTML / PDF / TXT. Without it there was no way to tell whether a check was running or stuck. |
| **Scheduled restart** | Of the services on the server and of the agent on a station (the agent at 04:15 by default) — a stuck WMI watcher survives a service restart, but not a day of operation. |
| **A daily agent self-restart** | The same from the other side: the agent handles its own restart even when it cannot reach the server. |
| **Filters and exclusion in Stations** | Per-column filters + a permanent "Ignore" that bulk actions do not override. |
| **The bank UI look** | Switchable in Settings, surviving navigation between pages. |

### 34.5 Current operational figures (4 Sep 2026)

| Indicator | Value |
|-----------|-------|
| Stations on record (from AD) | 227 |
| Stations reporting an agent | 4 (the pilot) |
| Stations without an agent | 200 |
| Incidents in 30 days | 29 (of which 20 warnings, 0 blocked) |
| Approved media in the whitelist | 3 |
| Enforcement mode | warning (blocking not switched on yet) |

In other words: **the system is finished and verified; the fleet-wide rollout and switching blocking on are
decisions, not technical debt.** That is a more honest formulation than "deployed", which a table without
its fourth row would tempt one to write.

### 34.6 Impact on chapters 1–33

| Chapter | What changes |
|---------|--------------|
| 14 (Auditability, NIS2) | The audit trail is no longer incident-only — the activity log also covers traffic and operator actions. Activity-log retention is missing. |
| 15 (Building, deployment) | Installation and update are two tasks; three separate deploy identities; stable/beta channels. |
| 17 (Operations, monitoring, retention) | Health checks and scheduled restarts were added; activity-log retention is open. |
| 13 / 19 (Enforcement, limitations) | Break-glass was effectively unavailable because of the filtered token — fixed; the inconsistency around `localConsole.enabled` on the fleet persists. |
| 20 (Roadmap) | New: activity-log retention, reconciling the local console on the fleet, upgrading `Microsoft.AspNetCore.Authentication.Negotiate` (NU1903). |

---

# Appendices

## Appendix A — Glossary

| Term | Meaning |
|------|---------|
| **Agent** | The .NET 8 Windows service on a station (SYSTEM); it performs detection, evaluation and enforcement. |
| **Whitelist** | The central list of approved media (VID:PID:serial), signed with RSA-4096. |
| **Blocklist** | (roadmap) a list of explicitly banned media taking precedence over the whitelist. |
| **Enforce / enforcement** | The mode in which the agent actually blocks an unapproved medium (`Disable-PnpDevice`). |
| **Break-glass** | A temporary local exception (a station admin switches blocking off offline), cleared at the heartbeat. |
| **Reconciliation** | Aligning the agent's state with the server's truth (returning/blocking per the policy and the whitelist). |
| **Re-enforcement** | Re-blocking already attached unapproved media after blocking is switched on. |
| **Heartbeat** | The agent's periodic outbound connection to the API (version, online state, receiving `enforce` and commands). |
| **Pinning** | The agent verifying the server by the certificate's thumbprint (no CA). |
| **gMSA** | Group Managed Service Account — a service account with no password in configuration. |
| **Fail-secure** | On a verification failure the system chooses the safe option (does not let through). |
| **A 1:1 copy** | The agent holds a byte-identical copy of the whitelist signed by the server. |
| **PNPDeviceID** | The identifier of a device's PnP node (`USBSTOR\DISK&VEN_…&PROD_…\…`). |
| **WTS API** | The Windows Terminal Services API used to determine the user of the active session. |
| **AllSigned** | The GPO policy requiring every PS script run on a machine to be signed. |
| **ToastHelper** | A helper process in the user session that shows Windows notifications (the agent as SYSTEM cannot do it directly). |
| **Watchdog** | A scheduled task guarding that the agent service runs (every 3 min). |

## Appendix B — Configuration key reference

### B.1 The agent (`agent.config.json` / `.local.json`)

| Key | Meaning |
|-----|---------|
| `policy.mode` | The local default mode (`warn`/`block`) before the first heartbeat. |
| `policy.onExpiredWhitelist` | Behaviour when the whitelist expires (`warn`/`block`/`allow`). |
| `policy.overridePath` | The path to `override.json` (break-glass). |
| `whitelist.syncUrl` | The API URL (`https://SERVER:5443`). |
| `whitelist.localPath` | The path to the local `whitelist.json`. |
| `whitelist.allowWildcards` | Allow records without a serial (false by default). |
| `sync.whitelistSyncIntervalMinutes` | The heartbeat/whitelist sync interval (~2). |
| `sync.incidentSyncIntervalMinutes` | The incident sending interval (~1). |
| `tls.validateServerCertificate` | Validate the server certificate. |
| `tls.pinnedThumbprint` | The API certificate's thumbprint (pinning). |
| `signing.enabled` | Verify the whitelist signature (production: true). |
| `signing.publicKeyPath` | The public key for verification (`whitelist_public.pem`). |
| `localConsole.enabled` / `localConsole.port` | The local console (off by default / 5080). |
| `notifications.toast.enabled` / `.contactMessage` | Toast notifications. |
| `selfRestart.*` | The daily agent restart (on by default, 04:15 — §34.4). |

### B.2 The server — central `AppSettings` (DB)

| Key | Meaning |
|-----|---------|
| `policy.enforce` | Global enforcement (the app server = the truth) → the heartbeat. |
| `comm.silentAfterMinutes` | The "silent agent" threshold. |
| `deploy.*` | Auto-enrollment (`enabled`/`dryRun`/`defaultEnroll`/`intervalMinutes`/`maxPerRun`/`allowHosts`/`includeHosts`/`excludeHosts`/`targetsFile`/`lastRun`). |
| `access.users` / `access.groups` | The console access whitelist. |
| `email.*` | The SMTP relay (M365 Direct Send) + alerts. |
| `retention.enabled` / `retention.incidentDays` / `retention.lastRun` | Incident retention. |
| `whitelist.validityDays` | The validity of an issued whitelist version (365 by default). |
| `cmd.report.<HOST>` | A data request (ReportNow) per station. |

### B.3 The server — `appsettings.local.json` (console/API)

| Key | Meaning |
|-----|---------|
| `ConnectionStrings.DefaultConnection` | The SQL connection (Integrated Security). |
| `Authorization.AdminGroups` / `AllowedUsers` | Console access (a lockout-safe bootstrap). |
| `Authorization.AllowedGroups` (API) | The agents' AD group (`USBGuardianClients`). |
| `Whitelist.PrivateKeyPath` | The private RSA key for signing the whitelist (.213, gitignored). |
| `Kestrel.Endpoints` | Bind addresses/ports. |
| `AdSync.*` | AD sync (the interval, SearchBase, IncludeDisabled). |

## Appendix C — Database schema and SQL grants

### C.1 Scripts (run in order)

| Script | Content |
|--------|---------|
| `01_create_database.sql` | the database |
| `02_create_tables.sql` | Computers, WhitelistDevices, WhitelistVersions, Incidents, a view + sp |
| `03_add_sourcefile.sql` | SourceFile + DisconnectedAt |
| `04_adsync_columns.sql` | LastSeen nullable + OperatingSystem / InActiveDirectory / AdSyncedAt |
| `05_adpath.sql` | AdPath (the AD path) |
| `06_appsettings.sql` | AppSettings (`Value` = NVARCHAR(MAX)) + a grant |
| `07_whitelist_publish.sql` | WhitelistVersions: `Json` + `Signature` → NVARCHAR(MAX) |
| `08_deploy_ignored.sql` | permanent exclusion of a station from deployment |
| `09_activity_log.sql` | `ActivityLog` + indexes + `sp_PurgeActivityLog` (§34.1) |

### C.2 SQL grants (least privilege, the console account)

```sql
CREATE LOGIN [DOMAIN\B-S-W-MIKOS$] FROM WINDOWS;
USE USBGuardian;
CREATE USER  [DOMAIN\B-S-W-MIKOS$] FOR LOGIN [DOMAIN\B-S-W-MIKOS$];
ALTER ROLE db_datareader ADD MEMBER [DOMAIN\B-S-W-MIKOS$];           -- reads everything
GRANT INSERT, UPDATE, DELETE ON dbo.Computers          TO [DOMAIN\B-S-W-MIKOS$];
GRANT INSERT, UPDATE, DELETE ON dbo.WhitelistDevices   TO [DOMAIN\B-S-W-MIKOS$];  -- DELETE = removal from the catalog
GRANT INSERT, UPDATE         ON dbo.WhitelistVersions  TO [DOMAIN\B-S-W-MIKOS$];  -- no DELETE (append-only audit)
GRANT SELECT, INSERT         ON dbo.ActivityLog        TO [DOMAIN\B-S-W-MIKOS$];  -- the activity log (§34.1)
-- For the AppSettings grant see 06_appsettings.sql
-- Note: the console does NOT have DELETE on Incidents (retention is done by the API under a gMSA).
```

## Appendix D — API endpoint reference

| Method | Endpoint | Auth | Purpose |
|--------|----------|------|---------|
| GET | `/api/heartbeat` | Kerberos (group) | State, version, `Enforce`, `ReportNow`, availability of a new version |
| POST | `/api/incidents` | Kerberos (group) | Incident intake → 202 → the queue |
| GET | `/api/incidents` | (the console) | A listing for the UI |
| GET | `/api/whitelist` | Kerberos (group) | The signed blob verbatim |
| GET | `/api/whitelist/signature` | Kerberos (group) | The base64 signature |
| GET | `/api/cert-info` | — | The cert thumbprint (pinning) |
| GET | `/api/version` | — | The running API's commit |
| GET | (console) `/api/version` | — | The console's commit |
| GET | (console) `/export/incidents.csv` | console auth | A CSV export (inherits the filter) |
| GET | (console) `/export/manager` | console auth | The management report |
| — the agent's local console (loopback :5080, admin-only) — | | | |
| GET | `/` , `/api/status` | a local admin | The dashboard / state |
| POST | `/api/override` , `/api/override/clear` | a local admin | Break-glass |
| POST | `/api/unblock-all` | a local admin | An immediate return of blocked media |
| POST | `/api/restart` | a local admin | A self-restart of the service |

## Appendix E — Mapping NIS2 / ISO 27001 → features

| Requirement | The USB Guardian feature |
|-------------|--------------------------|
| NIS2 — asset management | A record of connected media (including unapproved ones), a station inventory from AD |
| NIS2 — access control | The whitelist + enforcement (block/warn) |
| NIS2 — protection against malware | Blocking unapproved media (preventing introduction) |
| NIS2 — logging/monitoring | The incident audit trail, oversight of "silent" agents |
| NIS2 — incident response | Near-real-time reporting + e-mail alerts |
| NIS2 — integrity control | The RSA-4096 whitelist signature, versioning |
| ISO 27002 8.7 (malware) | Preventing introduction through a medium |
| ISO 27002 7.10 (media) | Governing the use of removable media (the whitelist) |
| ISO 27002 8.15 (logging) | Incidents with attribution |
| ISO 27002 8.16 (monitoring) | Central oversight, detection of silent agents |
| ISO 27002 5.9 (inventory of assets) | A record of media and stations |
| Act 181/2014 + the decree | Asset/access management, protection against malicious code, event recording, physical security of portable devices |

> The mapping is **indicative** — actual compliance depends on placement within an ISMS and on accompanying
> organisational measures (§3.4).

## Appendix F — Summary of design decisions

| # | Decision | Chosen | The key trade-off |
|---|----------|--------|-------------------|
| 6.1 | Push vs. pull | Push | Command latency ≤ one heartbeat (accepted) |
| 6.2 | Blazor vs. Node | Blazor Server | Server-side state (fine for a small team) |
| 6.3 | HttpListener vs. Kestrel (the local console) | HttpListener | Less comfort, a smaller footprint |
| 6.4 | Keying on hostname vs. IP | Hostname | Requires unique hostnames (AD) |
| 6.5 | Self-signed + pinning vs. a CA | Self-signed + pinning | Distributing the thumbprint; replacing the cert = updating the pin |
| 6.6 | MachineKeySet vs. Ephemeral | MachineKeySet | Required for the gMSA TLS handshake |
| 6.7 | Auto-signing vs. an offline key | Server-side auto-signing | The private key on the server (ACL) in exchange for automation |
| 6.8 | A 1:1 byte copy | Yes | The server must keep the blob verbatim |
| 6.9 | Disable-PnpDevice vs. IOCTL | Disable-PnpDevice | PowerShell overhead (a rare event) |
| 6.10 | The WTS API vs. Environment.UserName | The WTS API | Windows-specific (fine) |
| 6.11 | Soft vs. hard delete in the whitelist | Both | A hard delete requires the DELETE grant |
| 6.12 | The deploy identity | A separate gMSA task | More parts, but a strict separation of roles |

---

*End of document. Version 1.1, 2026-06-19 (extended 2026-09-04). Author: Milan Trnka (AXIMA). Material for
the opponent review of the USB Guardian project.*
