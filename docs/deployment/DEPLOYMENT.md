# Deploying HyFib HealthCare360 at a hospital

HC360 runs on **one dedicated Windows machine (the Server Node)** on the hospital network: by a network cable to the
hospital's switch, as at Nana Hospitals, Theni, or by WiFi. Everything else is a client: **HC360 Desktop** on workstations, kiosks and waiting-area TVs, and a browser on tablets. Nothing needs the
internet to work; SMS/WhatsApp, payment gateways, ABDM and the AI assistant use it when available.

```
  Hospital network: cabled LAN (switches) and/or WiFi (WPA2/WPA3-Enterprise, one SSID for clinical devices)
  ┌─────────────────────────────────────────────────────────────────────────────┐
  │  Server Node (Windows 11 Pro / Server 2022+)                                │
  │   ├─ HC360Server service  :7360 HTTPS (staff app and API)                   │
  │   │                       :7361 HTTPS (patient portal)                      │
  │   ├─ PostgreSQL           localhost only                                    │
  │   ├─ Data  %ProgramData%\HyFib\HealthCare360 (keys, certs, documents, logs) │
  │   └─ UPS (USB)  ·  second disk / NAS for backups                            │
  │                                                                             │
  │  Workstations / kiosks / TVs: HC360 Desktop   Tablets: Edge or Chrome       │
  │  Lab PCs with serial analysers: HC360 lab agent (Windows service)           │
  └─────────────────────────────────────────────────────────────────────────────┘
```

## 1. Before you start

**The release files.** Download from the release `HC360-Server-<version>-win-x64.zip`,
`HC360-Desktop-<version>-win-x64.zip` with its `.manifest.json` and `.manifest.sig`,
`HC360-LabAgent-<version>-win-x64.zip` if analysers are cabled to lab PCs by serial (RS-232) cable, and
`SHA256SUMS.txt`. Nothing in them runs until they are checked:

1. **Check each file** against `SHA256SUMS.txt`: `(Get-FileHash <file> -Algorithm SHA256).Hash` must be the value on
   its line (the case of the letters does not matter). HyFib also sends the checksums to the hospital's IT another way,
   by e-mail from a known HyFib address or read out by phone: compare them with those too. A page that served a changed
   file could serve changed checksums beside it, and the programs carry no Windows (Authenticode) signature.
2. **Unblock each checked zip before extracting it**, for example `Unblock-File .\HC360-Server-<version>-win-x64.zip`.
   A browser marks what it downloads as from the internet, Windows copies the mark to every file extracted from it, and
   PowerShell then refuses the release's scripts, which are not signed. The files extracted from an unblocked zip are
   not marked. Unblock only what you checked.
3. **Extract** the server package. Keep the HC360 Desktop package and its `.manifest.json` and `.manifest.sig` in the
   folder you extract the server package in: the server offers it from there to the workstations, which update
   themselves (section 5).
4. **Let the PowerShell window you work in run the scripts**, as Administrator:

   ```powershell
   Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned
   ```

   It applies to that window alone and ends when it closes. A Windows 10 or 11 PC where nobody has set a policy refuses
   every script until then. If `Get-ExecutionPolicy -List` shows a `MachinePolicy` or `UserPolicy`, a group policy
   decides instead: ask its administrator.

**Read the machine first.** On the machine meant to be the server, from the release's `deploy` folder, in the PowerShell
window of step 4, as Administrator:

```powershell
.\Test-HC360ServerNode.ps1 -ConcurrentUsers <staff using HC360 at once at the busiest hour>
```

It changes nothing. It reads what Settings › About does not show and grades each item against this section and the SRS
server sizing for that many users: which disk Windows is on and whether it is an NVMe SSD, a second disk for backup
copies, BitLocker and the TPM, the network adapter (a cable and the speed it runs at, or WiFi) and profile, a UPS on
USB, power cuts and shutdowns in the last 30 days, the ports HC360 needs, other database engines, remote-access tools,
and whether Windows is activated. It writes a JSON report with no patient
data in it, and exits with code 1 if a check failed. [NANA-THENI-READINESS.md](NANA-THENI-READINESS.md) shows how to
use the report for one hospital.

**Server hardware** (up to about 250 concurrent users):
- 8-core CPU, 32 GB RAM.
- 1 TB NVMe SSD, plus a second disk or NAS share for backup copies.
- A network cable to a gigabit port of the hospital's switch (Cat5e or Cat6), running at 1 Gbps. A server on WiFi
  alone needs a WiFi 6 adapter: every workstation's request passes through that one link.
- A UPS with a USB connection, so HC360 can see power cuts (Administration › System health).
- Before go-live, a load test on the same hardware shows it meets the SRS response times with the hospital's number of
  users: install a test server there first and follow [docs/testing/LOAD-TESTS.md](../testing/LOAD-TESTS.md).
- Windows with BitLocker (Pro, Enterprise or Server), turned on with the TPM for every fixed disk before HC360 holds
  patient data. The keys that open HC360's stored data are on the same machine (SRS SEC-05).
- Windows activated with the hospital's licence. HC360 runs without it, but a hospital's server is to run licensed
  Windows: the preflight warns until it is (Settings › System › Activation, which needs the internet once).

**Network checklist**:
- The server gets a **DHCP reservation** (fixed IP) and keeps a fixed computer name, e.g. `HC360-SRV`.
- The network profile on the server must be **Private**; the installer warns if it is Public.
- Guest / patient WiFi must not be able to reach the server.
- Multicast DNS (UDP 5353) should pass between the hospital's switches and access points so HC360 Desktop can find the
  server automatically. If it can't, staff type the server name once, or IT pre-configures it (step 5).

On a **cabled LAN**, where the server and the workstations are on network cables:
- The server's cable goes to a **gigabit port** of the switch, by a Cat5e or Cat6 cable. The preflight passes 1 Gbps
  and warns at 100 Mbps, which a cable with a broken pair or older than Cat5e, or a 10/100 switch port, gives.
- The **switch the server is on, and those between it and the wards, are on a UPS** too: a server kept running on its
  UPS reaches nobody once its switch is off.
- A managed switch with IGMP snooping on and no IGMP querier can drop mDNS: turn on the querier, or let mDNS through.
- System health watches the server's cable: a cable pulled or its switch turned off is told at once to the
  administrators and the on-call number (`alerts.oncallPhone`), and so is its return, which says when it was lost and
  when it was back. A server that reaches the SMS gateway over that cable alone cannot send the loss while it is out:
  the loss is then withdrawn, and the phone gets the return alone, which says both. If **Last cable drop** in System
  health keeps coming back, change the cable, then the switch port. If it still drops, turn off **Energy-Efficient
  Ethernet** in the network card's Advanced settings (Device Manager): some switches drop the link with it. The
  installer leaves the card's driver settings alone.
- A server with two network cards on cables (the hospital LAN and an imaging network, say) has each cable watched on its
  own. The server reads which one the workstations reach it over from its own traffic: the address their requests
  arrive on. That cable lost is critical even while the other is up; the other cable lost is a warning. With no
  workstation's request in the last 15 minutes it cannot tell, and a cable lost while the other is up is a warning.
  Each alert names the cable, the address it had and what is still up, and each return says when that cable was lost
  and when it was back. Do the cable-pull drill of section 4, step 10 on each cable.
- Before go-live, from each workstation: `Test-NetConnection HC360-SRV -Port 7360` must end with
  `TcpTestSucceeded : True`.

On **WiFi**:
- **Client isolation must be off** on the clinical SSID. Many access points turn it on by default, and it blocks
  workstations from reaching the server.

**Microsoft Edge on the server.** Reports, prescriptions, discharge summaries, bills and MIS reports are made into PDFs
on the server by headless Microsoft Edge, so that the copy downloaded or sent to the patient is the same as the printed
one. Windows 10/11 and Windows Server 2022 come with Edge; keep it installed and updating. On Windows Server Core, or if
Edge was removed, install Edge (or Google Chrome) for all users, or set its path in the configuration
(`HealthCare360:Pdf:BrowserPath`). Without it, everything else works and Administration › System health says that PDFs
can't be made.

## 2. Install PostgreSQL (once)

Open **PowerShell as Administrator** in the extracted `deploy` folder:

```powershell
.\Install-PostgreSQL.ps1 -SuperuserPassword (Read-Host -AsSecureString 'New postgres password')
```

This does seven things:
- Installs PostgreSQL through winget.
- Restricts it to localhost, with scram-sha-256 authentication only. Only a warm standby is ever let in from the
  network, and only once it is set up (section 9).
- Sizes its memory settings from the machine's RAM.
- Logs statements slower than two seconds, without their parameter values, which can be patient details.
- Sets the time zone to IST.
- Sets `wal_sync_method = fsync`. On Windows PostgreSQL's default (`open_datasync`) is safe only while the disk's write
  cache is off or survives a power cut, and a server without a UPS can lose committed work when the power goes.
  PostgreSQL's documentation (Reliability) says to turn the cache off or use `fsync`: it flushes the cache at every
  commit. The setting is written with `ALTER SYSTEM`, so running the script again changes nothing. A server installed
  earlier needs `ALTER SYSTEM SET wal_sync_method = 'fsync';` as the postgres user, then a restart of its service. The
  preflight (`Test-HC360ServerNode.ps1`, section 1) reports the system disk's write cache and, once PostgreSQL is
  installed, the `wal_sync_method` it runs with, and warns on a cache that is on without power protection.
- Has Windows restart its service if the service's process fails. The HC360 installer (step 3) also starts PostgreSQL
  again when the database itself crashes (see "When PostgreSQL stops" below).

A server installed with an earlier version of this script logged the parameter values of slow statements. Turn that
off once, as the postgres user, then delete the old PostgreSQL log files in its `data\log` folder:

```sql
ALTER SYSTEM SET log_parameter_max_length = 0;
ALTER SYSTEM SET log_parameter_max_length_on_error = 0;
SELECT pg_reload_conf();
```

Keep the postgres password in the hospital's password safe.

## 3. Install the HC360 server

```powershell
.\Install-HC360Server.ps1 -CreateDatabase -FacilityCode NH -FacilityName 'Nana Hospitals' `
    -SecondaryBackupDir '\\NAS\HC360-Backups'
```

The installer:
- Creates a least-privilege database account. Once HC360 has started, the service signs in to it with its Windows
  account instead of a password (see "How the server signs in to the database" below).
- Writes the configuration, readable only by Administrators and the service. Secrets in it are protected with Windows
  DPAPI for this machine, so a copy of the file is no use on another computer (see "Changing a protected setting"
  below).
- Checks that BitLocker protects every fixed disk, and warns if one is not. It registers the scheduled task
  *HC360 disk encryption check* (in Task Scheduler › HyFib), which checks again as SYSTEM every day at 06:15 and at
  start-up. Administration › System health shows what it found, and warns about a disk that is not protected or a
  check that has stopped running.
- Registers the `HC360Server` Windows service, which restarts itself if it stops.
- Has PostgreSQL started again if it stops without being shut down, within a minute, through the scheduled task
  *HC360 database watchdog* (see "When PostgreSQL stops" below).
- Opens the firewall, for Private networks only: HTTPS for the staff app (7360) and the patient portal (7361), and
  mDNS.
- Keeps the machine from sleeping or hibernating, its disk from stopping and its WiFi adapter from saving power, on
  mains and on the UPS's battery: in a power cut Windows switches to its battery settings, just when the UPS is keeping
  the hospital running. What Windows does when the battery is nearly empty is left as it is, so the server can still
  shut down cleanly before the UPS runs out, which protects the database.
- Has Windows never turn off a network card, the cable's or the WiFi's, to save power. It does not restart the cards,
  so a remote session over them stays up: the setting applies from the next restart. Restart the server once after the
  first installation, which also shows HC360 starting with Windows. `Get-NetAdapterPowerManagement` then shows
  `AllowComputerToTurnOffDevice : Disabled` for each card.
- Sets Windows Update's active hours to 06:00–23:00, so updates restart the server only overnight. Choose other hours
  with `-UpdateActiveHoursStart` and `-UpdateActiveHoursEnd` (at most 18 apart, as Windows allows); an upgrade keeps
  the hours set before.
- Starts HC360 and checks that it is ready: up, and reaching the database.
- Registers the scheduled task *HC360 certificate probe* (Task Scheduler › HyFib), which checks every day, as SYSTEM, the
  certificate the staff address serves, as HC360 Desktop and the lab agents check it, against the hospital CA
  fingerprint printed below; on a day the server was off at that time, once it starts. It runs it once and shows what
  it found. What needs attention goes to the Application event log, where monitoring tools read it, even while HC360 is
  down ([CA-ROLLOVER.md](CA-ROLLOVER.md#the-certificate-probe)).

At the end it prints two things. Write them down:
- the **hospital CA fingerprint**, which staff compare when setting up HC360 Desktop;
- the **first administrator password** (user `admin`).

Give the fingerprint to a certificate probe on a machine in IT too ([CA-ROLLOVER.md](CA-ROLLOVER.md#the-certificate-probe)):
the server's own cannot see the network or the server's name as the devices do, nor run while the server is off.

### When PostgreSQL stops

HC360 cannot work without its database, so PostgreSQL must come back by itself after a crash (SRS DED-01, REL-01). Two
things see to that, and both are set up again on every install and upgrade:
- **Windows' recovery settings** on the PostgreSQL service (Services › its Properties › Recovery): restart after 5
  seconds, 15 seconds, then every minute. These help only when the service's own process (`pg_ctl.exe`) ends. When
  the database server itself (`postgres.exe`) crashes or is killed, PostgreSQL's service tells Windows it stopped
  normally, and Windows leaves it stopped.
- The scheduled task ***HC360 database watchdog*** (Task Scheduler › HyFib), which runs as SYSTEM every minute. When
  the PostgreSQL service is set to start automatically but is stopped, and PostgreSQL's data folder says it was not
  shut down cleanly, the watchdog starts it and writes a warning in Event Viewer › Application (source *HyFib
  HealthCare360*). After three failed starts in a row it tries every quarter of an hour.

The watchdog leaves PostgreSQL stopped when someone stopped it: stopping the service (Services, `Stop-Service`, or
the installers' own restarts) shuts PostgreSQL down cleanly, which the watchdog can tell. It also leaves alone a
service set to start manually, and a PostgreSQL that runs without its service, started by hand with `pg_ctl`.

System health warns when PostgreSQL had to be started again in the last 24 hours, critically when it could not be, and
when the watchdog has not run for ten minutes. PostgreSQL's own log (`log\` in its data folder) says why it stopped.
The watchdog needs PostgreSQL on the same machine as HC360; when the database is elsewhere, look after its restarts
there.

### Changing a protected setting

Secrets in `%ProgramData%\HyFib\HealthCare360\config\hc360.settings.json` are stored as `dpapi:` followed by
protected text: every connection string, and every setting whose name ends in `Password`, `Secret` or `ApiKey` (for
example `HealthCare360:Network:CertificatePassword`). To change one, from an elevated prompt on the server:

1. Replace the `dpapi:…` value with the new plain value, for example the whole connection string.
2. Run `& "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe" settings protect`. It
   protects every secret written in plain text, and says which.
3. Restart the `HC360Server` service.

A protected value opens only on the machine that protected it. When the server moves to new hardware, write the
values in plain text on the new machine and protect them there (Install-HC360Server.ps1 does this for the database).
Administration › System health warns while a secret in the file is in plain text.

### Request limits

The server refuses requests over these limits with 429 Too Many Requests (SRS SEC-06). They are settings, in
`HealthCare360:RateLimits` in `hc360.settings.json`; the server starts with these defaults, and refuses to start with a
limit of zero or a window of zero. Change one and restart the `HC360Server` service.

| Setting | Default | Counts |
|---|---|---|
| `SignIn:PermitLimit`, `SignIn:Window` | 10 in `00:01:00` | Signing in, second factors, one-time codes, token refreshes, unlocking (PIN or passkey), setting a PIN, adding a passkey, setting up or turning off two-step sign-in, a password given again: per address, for the hospital when there are several servers. Every signed-in staff app and patient-portal page refreshes its sign-in a minute before its access token ends (`Security:AccessTokenMinutes`, at most 15), and pages signed in together, as at a change of shift, refresh together. Everyone who reaches the server from one public address shares the limit with those refreshes: the hospital's computers behind its NAT, and patients on a mobile network's carrier-grade NAT or behind an office proxy. Raise it for such networks |
| `OneTimeCode:PermitLimit`, `OneTimeCode:Window` | 5 in `00:15:00` | Asking for a portal sign-in code: per address, as above |
| `Public:PermitLimit`, `Public:Window` | 60 in `00:01:00` | Links, QR codes, workstation enrolment: per address |
| `Partner:PermitLimit`, `Partner:Window` | 1200 in `00:01:00` | Partners' calls: SMS and WhatsApp delivery receipts, the payment gateway, the ABDM gateway and records pushed to it; per address, and not counted against `PerUser` |
| `Device:TokenLimit`, `Device:TokensPerPeriod`, `Device:ReplenishmentPeriod` | 120, 60 every `00:00:30` | RPM gateways and biometric terminals: per device |
| `DesktopUpdate:PermitLimit`, `DesktopUpdate:Window` | 30 in `00:15:00` | HC360 Desktop's self-update: per workstation |
| `PerUser:TokenLimit`, `PerUser:TokensPerPeriod`, `PerUser:ReplenishmentPeriod` | 400, 200 every `00:00:10` | Every request: per signed-in user, or per address before signing in |
| `Ai:PermitLimit`, `Ai:Window`, `Ai:SegmentsPerWindow` | 20 in `00:01:00`, 4 | The AI assistant, dictation and note structuring: per user |
| `RadiologyImages:PermitLimit`, `RadiologyImages:QueueLimit` | 6 at once, 32 waiting | PACS thumbnails and images: per user |

Raise the sign-in limit when many workstations reach the server through one address (a NAT router), the partner limit
when a campaign sends more than 1,200 messages a minute through one provider, and the per-user limits for a load test. Lowering them below the defaults is rarely needed: the per-account lockout and the portal's proof
of work already stop guessing.

### The server never runs as Development

`appsettings.Development.json` is a developer's file (backups and MFA off, demonstration data on). It is not in the server
package, and the server refuses to start as the Windows service if `ASPNETCORE_ENVIRONMENT` or `DOTNET_ENVIRONMENT` is set to
`Development`: the service stops with "HC360 cannot start: the HC360 service is set to the Development environment" in the
Windows Application log. Remove that variable (from the service's environment and from System Properties › Environment
Variables) and start the service again.

### Reading the server log

The server writes its log to `%ProgramData%\HyFib\HealthCare360\logs\hc360-server-<date>.json`, one event per line as a
JSON object (SRS OBS-01), and keeps a year: the DPDP Rules 2025 ask for that from 13 May 2027 (r.6), beyond the 180
days of CERT-In's Directions of 28 April 2022. Each line has the time (`@t`), the level (`@l`), the message (`@m`), the
error (`@x`), the part of the server that wrote it (`SourceContext`) and the request's correlation id (`CorrelationId`),
the "Reference" an error message in HC360 shows. Phone, Aadhaar and ABHA numbers, e-mail addresses and secrets
are masked. Errors also go to Windows Event Viewer › Application (source *HyFib HealthCare360*). To list today's errors in
PowerShell:

```powershell
Get-Content "$env:ProgramData\HyFib\HealthCare360\logs\hc360-server-$(Get-Date -Format yyyyMMdd).json" |
  ConvertFrom-Json | Where-Object { $_.'@l' -in 'Error', 'Fatal' } | Format-List '@t', '@m', '@x', CorrelationId
```

### Sending the log and the audit anchors off the server

The log file and the audit chain's anchor file are on the server's own disk, where the server's administrators, and
anyone who takes the server over, can change them. To keep a copy they cannot, send both to another machine. Each is
off until it is set in `config\hc360.settings.json` (then restart the `HC360Server` service):

```json
"HealthCare360": {
  "Syslog": {
    "Enabled": true,
    "Host": "siem.hospital.lan",
    "Port": 6514,
    "CaCertificatePath": "C:\\ProgramData\\HyFib\\HealthCare360\\certs\\siem-ca.cer",
    "MinimumLevel": "Information"
  },
  "Audit": {
    "AnchorMirrorFile": "\\\\audit-nas\\hc360\\anchors.log",
    "AnchorsToSyslog": true
  }
}
```

- **Syslog** sends every log event, as it happens, to the hospital's syslog collector or SIEM (rsyslog, syslog-ng, a
  SIEM's syslog input) in RFC 5424's format over TLS (RFC 5425, TCP 6514). The collector's certificate must carry the
  name in `Host` and chain to a root this machine trusts, or to the CA in `CaCertificatePath` (the hospital's own CA,
  say). A collector that asks for a client certificate gets the PFX in `ClientCertificatePath` (with
  `ClientCertificatePassword`, protected in the file like the other passwords). Phone, Aadhaar and ABHA numbers,
  e-mail addresses and secrets are masked in every message, as in the log file. `Facility` (default 16, local0),
  `AppName` (default `hc360`) and `QueueLimit` (default 10,000 events waiting while the collector is away) can be
  changed. The logger, the request's correlation id and the level are structured data (`[hc360@32473 …]`).
- **AnchorMirrorFile** appends each audit anchor, as it is written, to a second anchor file, normally on a share on
  another machine. Give the service's account (`NT SERVICE\HC360Server`, or the computer account `DOMAIN\HC360SRV$` on
  a share) the right to read the folder and to create and append to files, and not to change or delete them (in the
  share's advanced permissions: *List folder / read data*, *Create files / write data* on the folder only, *Create
  folders / append data*; no *Delete*, no *Write attributes*). The file is brought level with the anchor file at every
  anchor and every five minutes, so anchors written while the share was away follow once it is back.
- **AnchorsToSyslog** sends each anchor to the collector too (facility 13, "log audit", MSGID `AUDIT_ANCHOR`), which
  needs Syslog on.

System health says when the collector cannot be reached (`logs.syslog_unreachable`), when log events were dropped
because too many were waiting (`logs.syslog_dropped`: the log file still has them), when an anchor cannot be copied
(`audit.anchor_mirror_failed`, critical after a day), and, critical, when the second anchor file holds an anchor the
anchor file here no longer has (`audit.anchor_mirror_mismatch`): keep both files as they are, and verify the chain
under Administration › Audit log. A live server that copies its anchors nowhere shows `audit.anchor_mirror_off` (info).
With several servers each sends its own log events; every one copies the anchors it writes.

### How the server signs in to the database

The `HC360Server` service signs in to PostgreSQL with its own Windows account, `NT SERVICE\HC360Server`, through
Windows' SSPI. There is no database password: none in the configuration, none to change, and none to leak with a copy
of the file. Backups (pg_dump) sign in the same way. The installer sets this up on every install and upgrade, once HC360
has started as it was:

1. In PostgreSQL's `pg_hba.conf` it lets the database account (`hc360_app`) sign in only with that Windows account and
   only from this machine, and in `pg_ident.conf` it maps the Windows account to the database account. Each file gets
   one block, between a `# BEGIN HyFib HealthCare360` and a `# END HyFib HealthCare360` line, which the installer
   writes again on every run: change the rest of the files as you need, but not the blocks.
2. It restarts PostgreSQL, takes the password out of the configuration, and starts HC360 again.
3. If HC360 does not reach the database within 3 minutes, it puts the configuration, `pg_hba.conf` and `pg_ident.conf`
   back as they were, and HC360 signs in with its password as before. The warning, and the log in `logs\`, say why.
4. Given `-PostgresSuperuserPassword` (as with `-CreateDatabase`), it removes the old password from the database
   account. Without it, PostgreSQL refuses that password anyway: it takes the account only from the service's Windows
   account.

The installer changes the PostgreSQL service that is running on the port in the configuration. A stopped one set to the
same port, such as an older version's after an upgrade, is left alone.

The server signs in with a password when PostgreSQL is on another machine, which the Windows sign-in is not set up for,
or after `.\Install-HC360Server.ps1 -DatabaseSignIn Password`. That gives the account a new random password (it asks
for the `postgres` password) and removes the blocks; if HC360 then does not reach the database, it puts everything
back, as above. Run the installer again without it to go back to the Windows sign-in. After PostgreSQL is reinstalled, or upgraded to a new version with a new data folder, run the HC360 installer
again: it writes the blocks into the new files.

### Two database accounts

`-CreateDatabase` makes two PostgreSQL accounts:

- `hc360_owner` owns the database and every table in it. HC360 signs in as it only to change the schema: as it starts
  (database updates, and the audit trail's triggers, which start-up checks and puts back), and in the jobs that add each
  month's partitions and their indexes. Its connection string is `ConnectionStrings:HospitalOwner`, with a random
  password, protected in the settings file like the other.
- `hc360_app` is the account the server works as (`ConnectionStrings:Hospital`): it reads and writes rows, and nothing
  else. It cannot change a table, nor drop or turn off the audit trail's triggers, nor empty the audit trail, so
  neither a flaw in a request nor someone who has its password can make the audit trail editable. It is the account
  the Windows sign-in (above) and the password change (below) are about.

The installer sets their rights with `hc360-database-roles.sql`, in the `deploy` folder, which it runs as `postgres`.
On a server installed before the split, where `hc360_app` owns everything, run the installer again with
`-CreateDatabase -PostgresSuperuserPassword (Read-Host -AsSecureString)`: the database is kept, and handed to
`hc360_owner` as it is (`REASSIGN OWNED`); nothing in it is copied or changed. Like any `-CreateDatabase`, it gives
`hc360_app` a new password, and the installer then switches it to the Windows sign-in as before. `Restore-HC360.ps1`
restores the tables as `hc360_owner` where it exists, and gives `hc360_app` its rights again.

A server whose tables belong to another account than the one it works as, with no `ConnectionStrings:HospitalOwner`,
does not start: the log says which account owns them. Set the owner's connection string (on a promoted standby, copy
it from the primary's settings), and start HC360 again.

### The database over TLS

PostgreSQL on the Server Node itself, reached through localhost, needs nothing more. When PostgreSQL runs on another
machine, every PostgreSQL connection string (`Hospital`, `HospitalOwner`, a reporting replica) must check the
database's certificate, or HC360 does not start, and the log names the connection string:

```
Host=db.hospital.lan;Port=5432;Database=hc360;Username=hc360_app;SSL Mode=VerifyFull;Root Certificate=C:\ProgramData\HyFib\HealthCare360\certs\db-ca.crt
```

`SSL Mode=VerifyFull` encrypts the connection and checks that the certificate names `Host` and chains to a trusted
root: the CA in `Root Certificate` (the hospital's own CA), or the machine's. `SSL Mode=Require` or `VerifyCA` is taken
only with a `Root Certificate`. On the database machine, turn TLS on in `postgresql.conf` (`ssl = on`, `ssl_cert_file`,
`ssl_key_file`, the certificate naming the host the server uses), and use `hostssl` lines, not `host`, for the server's
address in `pg_hba.conf`. For a lab only, `"HealthCare360": { "Database": { "AllowUnverifiedRemoteConnection": true } }`
lets HC360 start without it; System health warns while it is on (`database.tls_unverified`).

### Changing the database password

When the server signs in with a password (above), the password of HC360's database account is changed from
Administration › System health, while HC360 runs (SRS SEC-07). It needs the permission *Change the server's database
password and keys, make backup recovery keys, sign everybody out, and set how long the audit trail is kept and purge it
(with a second person)* (`admin.system.manage`), which only SuperAdmin has until it is given to another role in
Administration › Roles and permissions. Then:

1. Open Administration › System health. Under Database it shows when the password was last changed, and by whom.
2. Select **Change password** and confirm.

HC360 makes a new random password that nobody sees, sets it on the database and writes it protected in the settings
file together, and uses it for every new connection. Screens keep working, and no restart is needed. When either step
fails, both keep the old password. A reporting replica in the settings file that signs in as the same account gets the
new password too. Each change is recorded in the audit log. Change the password when someone who knew the old one
leaves, and at least once a year.

It works on the Windows Server Node when the connection string is in the settings file. When the connection is set
somewhere else, such as in environment variables, System health says so, and the password is changed there. With the
Windows sign-in there is no password, and System health says so.

### Rotating the encryption keys

Two keys are rotated from Administration › System health › Encryption keys, with the same permission
(`admin.system.manage`), while HC360 runs (SRS SEC-07). Each rotation is recorded in the audit log, and System health
says when one is due: the sign-in token key every year, the data encryption key every two years. Rotate both, and
change the database password if the server signs in with one, at once if the Server Node, its disks or a backup may
have been exposed.

- **Rotate data key.** New identifiers, secrets and documents are encrypted with a new key from then on. A background
  job moves everything encrypted before to the new key, a little at a time, and computes the search indexes of ABHA,
  policy, PAN and bank account numbers again with it; the section shows how much is left, and screens and searches keep
  working meanwhile. Documents move only while no backup is copying them. Earlier keys stay in
  `keys\field-keys` beside `keys\field-master.key` until they are retired, and every backup carries every key the
  server has when the backup is made, so older backups restore as before. Rotating it does not change the backup
  recovery key, which has a key of its own (below).
- **Retire.** *Earlier keys* lists each earlier data key with what is still encrypted with it. Once nothing is, and
  the current key has been current for a day, **Retire** deletes the key from the server, after you give your password
  again. Backups made before keep their own copy of it, so they still restore, and restoring one puts it back (retire
  it again once its data has moved; if the restore made it the current key, rotate first). The original key is never
  retired: searching by Aadhaar number depends on it.
- **Rotate sign-in key.** New sign-in tokens are signed with a new key. Tokens already issued stay valid until they
  expire, within minutes, so nobody is signed out. It can be rotated again once those have expired.

The keys are on the Server Node. With several servers (section 10) they are in the folder the servers share, and any
of them can rotate them.

### A new backup recovery key

Backups are encrypted with the recovery key, which the hospital keeps offline. Make a new one when someone who has
seen the current one leaves, when a copy of it may have been exposed, and once after upgrading from a version that
showed the original key on screen; System health says when backups still use that one. It needs the same permission
(`admin.system.manage`):

1. Open Administration › Backups and select **Recovery key**. It names the key backups use, since when, and who made
   it.
2. Select **Make a new key** and give your password.
3. Write down the key, 14 groups of 4 letters and digits, and its ID. It is shown this once.
4. Select **I have written it down** and type the key from your copy, within 30 minutes. Only then do backups use it,
   so a key nobody wrote down never becomes the one they need. A mistake in the copy is caught here.

The Backups list names the key that opens each backup. Keep every key until the backups made with it, and their copies
on a NAS or USB drive and off site, are deleted: 30 days by default, 35 in the off-site bucket. Both steps are recorded
in the audit log, without the key.

**Two people hold the key.** A key one person holds is lost with them. Give a copy to a second administrator (the
medical director, or the hospital administrator), who keeps it outside the building, at home or in a bank locker, so a
fire takes at most one copy. The administrator who typed the new key back is its first holder. The second opens
Administration › Backups, **Recovery key**, selects **I hold this key**, and types it in from their own copy; HC360
records who and when, and never shows the key again. The dialog lists who has shown they hold the key. It needs the
backups permission (`admin.backups.manage`), not `admin.system.manage`. System health warns
(`backup.recovery_key_custodians`) until two different people have typed in the current key.

**Check the copies every quarter.** Each holder finds their copy and types it in the same way. System health warns
(`backup.recovery_key_unchecked`) once nobody has for 90 days. A copy that can no longer be read, or found, is a reason
to make a new key now, while the current one still opens the backups.
HC360 keeps the key protected on the Server Node, to encrypt new backups, and never shows it again. Until it is typed
back, the new key waits, protected the same way, beside it in the keys folder; with several servers (section 10) that
is the folder they share, so whichever server the reverse proxy sends the confirmation to finds it.

## 4. First sign-in and security set-up

On the server open `https://localhost:7360/`. The browser warns once, because the hospital CA is private. Then:

1. Sign in as `admin` and choose a new password. Set up two-step verification. Administrators, accountants, HR
   managers and anyone with an administrative permission must use it.
2. Check that `%ProgramData%\HyFib\HealthCare360\initial-admin-password.txt` is gone: HC360 deletes it once `admin`
   has a new password. If it is still there, delete it.
3. **Enrol the first HC360 Desktop.** Administration works only in HC360 Desktop on a computer IT enrolled (SRS
   SEC-08); until one workstation is, this browser can open only Administration › Devices. There, **Enrol a device**
   (a workstation), install HC360 Desktop on IT's computer, or on this one, with the code (section 5), and sign in there
   as `admin`. Do the steps below in HC360 Desktop.
4. **Administration › Backups › Recovery key › Make a new key** (see "A new backup recovery key" above): write down
   the key and its ID, type the key back from your copy, and store the copy in the hospital safe. Until then System
   health warns that nobody holds the key, and backups cannot be restored on new hardware.
5. **Audit anchors and the clock** (CERT-In's Directions of 28 April 2022):
   - The audit trail's anchors go to `%ProgramData%\HyFib\HealthCare360\audit\anchors.log`. To keep them where the
     database's administrators cannot change them, set `HealthCare360:Audit:AnchorFile` to a write-once share or another
     machine, and give that file to auditors with an audit export (Administration › Audit log › Export for auditors).
   - The audit trail is kept permanently unless two super administrators set a period (8 years at the least, Privacy ›
     Retention › Audit trail). A purge they agree on writes the months it removes to
     `%ProgramData%\HyFib\HealthCare360\audit\archive` first (or `HealthCare360:Audit:ArchiveDirectory`): copy each
     export off the server, with the anchor file, and keep it as long as an auditor may ask for those months. The
     anchor file is not in the backups: after a purge, a server restored on new hardware verifies its audit trail only
     with the anchor file put back (the anchor of the purge's boundary is checked), so keep a copy off the server.
   - Set Windows Time to synchronise with NPL or NIC (`w32tm /config /manualpeerlist:"time.nplindia.org samay1.nic.in"
     /syncfromflags:manual /update`), and allow NTP (UDP 123) to them. System health shows the clock's offset and warns
     when it is more than a second off or no time server answers; a hospital with its own time server that follows
     them lists it under `HealthCare360:TimeSync:Servers`.
   - The server keeps its log files a year in `logs`; leave room for them on the data drive.
   - **Privacy › Settings › CERT-In point of contact**: name the person CERT-In writes to and who reports an incident
     within the 6 hours, with an e-mail address and a phone number (direction (iii)). System health warns until it is
     set.
6. **Administration › Users**: create staff accounts, giving each person only the roles their job needs, with their
   own mobile number (and e-mail address, if they have one). HC360 refuses passwords on public breach lists or holding a
   facility's name, for temporary passwords too. A temporary password works for 72 hours; after that, reset the
   password to give out a new one. A reset sends it to the person's own mobile number and e-mail address, and you are
   told only where it went; for an account with neither, the reset waits for another administrator who may approve
   grants, who is shown it once and gives it in person. Another user's mobile number or e-mail address, two-step
   verification resets and linking an account to a doctor wait for such an administrator too.
   Access is given by two people: create the hospital administrators first, while the Super Administrator is the only
   one who may approve grants, so their roles are given at once (and audited so). From then on, the roles and
   facilities you give, and the permissions you add to a role, wait for another administrator who may approve grants
   (Approvals, or the link on the user's page); taking access away is immediate. Keep at least two such people
   active: System health warns when fewer remain.
7. **Administration › Settings**: set the SMS/WhatsApp/payment gateway keys (stored encrypted) and
   `alerts.oncallPhone` (IT's mobile, which gets an SMS for each critical warning in System health and when a server of
   several that stopped reporting reports again, and if the server's network cable is pulled or its switch goes off
   and when it is back, its WiFi drops, its WiFi adapter disappears, it starts without the WiFi it had when it last ran,
   its signal stays weak for a minute or its network is not secure),
   and who a critical alert nobody acknowledges goes to next:
   `alerts.escalationPhone` (a second contact, such as the IT head) and `alerts.supportPhone` (HyFib support's number).
   Set up the heartbeat outside the hospital as "Alerts that reach someone" below says.
   System health warns while the adapter is missing; on a server moved to a network cable on purpose, the warning
   clears once the cable is connected.
   Set `alerts.oncallWhatsApp` to `true` to have those alerts on WhatsApp too, once the hospital's WhatsApp provider
   has approved the `system_alert` template. A failed backup is told to the administrators in the app, not by SMS.
   Set `privacy.noticeVersion` to the version of the privacy notice the desk hands out (default `1`), and change it
   when the notice changes. A lab with more than one pathologist can set `lab.separateVerifier` to `true` for its
   facility, so whoever entered a result does not also verify it, and a correction of a released report waits for a
   second pathologist's approval. A second doctor confirms each death certificate before it is reported, unless a
   facility with a single doctor sets `mrd.deathCertificateConfirmation` to `false`. Who may confirm is the
   `mrd.deaths.confirm` permission (every doctor by default); a hospital that wants only senior doctors to confirm takes
   it from the Doctor role and gives it to them. In the blood bank, a second officer verifies each donation's screening
   results before its units leave quarantine, and issues what one officer both grouped and cross-matched, unless a
   facility with one blood bank officer on duty sets `bloodbank.separateVerifier` to `false`: that officer then does both,
   and what they release so is listed on the Donations and Blood requests screens for the medical officer's review.
   Uncross-matched blood is released only on a doctor's own sign-in, so give doctors' accounts their practitioner record.
   A facility that does ultrasounds in pregnancy sets `pcpndt.registrationNumber` (for that facility) to its PCPNDT
   registration: until then Form F cannot be recorded, and those scans cannot start. Mark each obstetric ultrasound
   procedure "Ultrasound in pregnancy" under Radiology › Procedures and templates; the seeded obstetric and anomaly
   scans already are.
   A facility approved for terminations of pregnancy sets `mtp.approvalNumber` (for that facility) to its certificate of
   approval (MTP Rules, Form B): until then no MTP case can be opened. No clinical role may certify a termination out of
   the box: grant `mtp.certify` and `patients.sensitive.mtp` to the gynaecologists the approval names.
   **Some settings wait for a second administrator** who may change settings (Approvals), so that no one person switches
   off a control another person's check relies on, lets more money out, or sends money, patients' data or critical
   alerts somewhere else. The screen asks why, says the change was asked for, and the setting stays as it is until
   another administrator approves it. They are: switching off `lab.separateVerifier`,
   `mrd.deathCertificateConfirmation`, `bloodbank.separateVerifier`, `security.administrationRequiresDesktop`,
   `lab.requirePaymentBeforeCollection` or `billing.shift.required`; switching on `ui.biometricUnlock` or `ai.enabled`;
   raising `billing.discount.defaultMaxPercent`, `pharmacy.discount.max_percent`, `billing.chargeCancel.approvalAbove`,
   `billing.claimWriteOff.approvalAbove`, `billing.shift.shortageTolerance`, `supportservices.canteen.staffCreditLimit`, `security.maxSessionsPerUser`, `ui.idleLockMinutes`,
   `inpatient.marGraceMinutes`, `inpatient.verbalOrderCountersignHours` or `lab.deltaCheckPercent` (0, for no limit or
   no check, counts as the highest), or
   lowering `lab.deltaCheckWindowDays`; clearing any of these, so the default decides; and any change at all to
   `portal.baseUrl`, `doctorfees.tds.percent`, every `alerts.*` setting, every integration (`integrations.*`),
   `abdm.gateway`, `ai.provider`, `ai.openai`, `ai.speech` and every credential (a `*.secret` setting, or one saved as
   secret). The other direction (a control switched on, a limit lowered) is saved at once, as is every other setting. So
   set the gateway keys, the alert numbers and the portal's address with a hospital administrator from step 6 at hand to
   approve them.
   **Verbal orders on the wards** are countersigned by the doctor within `inpatient.verbalOrderCountersignHours` (24 by
   default; set fewer at once, more waits for a second administrator). Past it, the doctor and everyone holding
   `nursing.in_charge` in the facility are told: the hospital administrator holds it out of the box, so give it to the
   ward sisters' role (Administration › Roles). The drug-safety tables are loosened only with the pharmacy and
   therapeutics committee's approval (`clinical.drug_safety.approve`, pharmacists and hospital administrators by
   default): grant it to the committee's members, and take it from anyone who should not approve.
   **The billing desk's money decisions wait for a second person** (SECURITY.md, Maker-checker): a discount above the
   giver's own limit (an approver's included), a discount limit raised, a charge cancelled at the desk (every OT,
   procedure, implant or bed-day charge, and others above `billing.chargeCancel.approvalAbove`, ₹1,000 by default; set
   0 to make every cancellation wait), a cash shortage at a shift's close beyond `billing.shift.shortageTolerance` (₹0 by
   default: every shortage waits for a supervisor who is not the cashier) and a doctor's consultation fee. So the hospital
   needs at least two people holding `billing.discounts.approve`, `billing.refunds.approve` and `masters.prices.approve`
   (the accountant and the hospital administrator out of the box), and the hospital administrator who bills at a counter
   has the default discount limit (0%) like everyone else: give them a limit in Billing › Discount approvals › Discount
   limits, which another approver approves. Doctors added in **Hospital setup › Doctors and clinical staff** charge the
   consultation service's price until someone who approves prices approves their own fee.
   **Insurance money waits for a second person too** (MAKER-CHECKER.md slice 12): what an insurer deducts or rejects is
   written off only when someone who holds `insurance.claims.writeoff.approve` (the accountant and the hospital
   administrator out of the box) approves it, above `billing.claimWriteOff.approvalAbove` (₹0 by default: every
   write-off), and an amount an insurer approves on a pre-authorisation moves the bill to the insurer only once a second
   insurance desk user checks it. So the insurance desk needs at least two users holding `insurance.preauth.manage` (or
   the hospital administrator as the second), or pre-authorised amounts wait.
8. **Documents and the messages that carry them** (reports, prescriptions, bills):
   - **Administration › Hospital setup › Facilities and letterhead** holds what the letterhead prints: legal name,
     address, registration, NABH, GSTIN, PAN, a letterhead line, and the logo (shrunk on upload to fit the 300 KB a
     document embeds). *Preview letterhead* shows the result before saving. Doctors' signatures are uploaded in
     **Hospital setup › Doctors and clinical staff**. Documents print without them until they are set.
   - **Hospital setup › Print templates**: for documents printed on the hospital's own pre-printed stationery, choose
     *Pre-printed stationery* and measure the space its letterhead (and footer) takes; *Preview* shows the page. Add
     closing notes, such as the terms on bills, and choose A5 or A4 for prescriptions. Without templates, documents
     print the letterhead with the logo, as before.
   - Set `portal.baseUrl` to the patient portal's address: `https://HC360-SRV:7361`, or the public https address that
     forwards to port 7361. The portal has an address of its own (SECURITY.md, "Separate addresses"). Documents then
     carry a QR code that anyone can scan to check the report is genuine. Without it they print only the verification
     code.
   - E-mail (`integrations.email`): the "report ready" e-mail carries the report's PDF.
   - WhatsApp: to send the PDF on WhatsApp, register the `lab_report_ready` template in WhatsApp Manager **with a
     document header**. Then, in Administration › Notification templates, tick "Document header" on that template.
     Without the tick, the WhatsApp message is text only and the patient finds the report on the portal.
   - To stop sending report PDFs by e-mail and WhatsApp, set `diagnostics.reports.sendPdf` to `false`, and use a
     WhatsApp template without a document header (untick "Document header"). A template with a header can't go
     without its PDF, so those WhatsApp messages would be skipped; the SMS and e-mail still go.
   - WhatsApp templates seeded **Not in use** in Administration › Notification templates wait for Meta: the queue token
     (`visit_token`), the payment receipt (`bill_receipt`), the admission (`admission_admitted`), the discharge
     (`discharge_completed`, without a document header), the discharge summary (`discharge_summary`, with a document
     header: see below), the feedback request (`feedback_request`), the follow-up reminder (`followup_reminder`) and the
     complaint acknowledgement (`complaint_acknowledged`). Register each in WhatsApp Manager as a utility template (the feedback
     request and the follow-up reminder go only to numbers that opted in), then switch it on; until then those messages
     go by SMS only. Any template can be switched off the same way: when no version of a message is in use, it is not
     sent on that channel (sign-in codes always go).
   - The discharge has two WhatsApp templates. Register `discharge_completed` without a document header: every
     discharge gets it. Register `discharge_summary` **with a document header**, and Administration › Notification
     templates already has "Document header" ticked on it. Switch on either or both. With `discharge_summary` on, a
     discharge with a signed summary gets it, with the summary's PDF, and the other discharges (against advice, or a
     planned one while the summary hold is off) get `discharge_completed`, so none loses its WhatsApp message. With it
     off, the summary is on the patient portal.
   - Patients get their messages in the language their record prefers when the hospital adds that language's version of
     the template (Hindi `hi`, Tamil `ta`), English otherwise. Register each language's WhatsApp template under the same
     name in WhatsApp Manager, and map it in `integrations.whatsapp` `languageCodes` (for example `{"en":"en_US","ta":"ta"}`).
   - A WhatsApp template takes the message's text as `integrations.whatsapp` `parameters` says (Administration ›
     Integrations › WhatsApp, *Template parameters*). By default (*One parameter*, `text`) the whole text is the
     template's one parameter, so each template is registered as the single variable `{{1}}`, which Meta no longer
     approves for a new template. For new registrations choose *By name* (`named`): register each template with its
     HC360 text (Administration › Notification templates; each language's with its own), each placeholder in lower case
     with underscores (`{{billNumber}}` as `{{bill_number}}`), as a template with named parameters. *Numbered*
     (`positional`) takes the HC360 text with each placeholder replaced by its number, counting from 1 (a placeholder
     used twice takes two numbers). The setting applies to every WhatsApp template, so register all those in use the new
     way before changing it (docs/modules/integrations.md, "Template parameters").
9. **Doctors' DSC signatures on prescriptions** (ERX-04; only where the hospital sets
   `clinical.prescriptions.signature` to `"optional"` or `"required"`, docs/modules/clinical.md): HC360 accepts a
   doctor's DSC only from a certificate that chains to a CCA India root in the server's **DSC trust folder**, and HC360
   does not ship those roots. Download the CCA India root certificates from cca.gov.in, check their thumbprints against
   the ones the CCA publishes, and copy them there:

   ```powershell
   New-Item -ItemType Directory -Force "$env:ProgramData\HyFib\HealthCare360\certs\dsc-trust"
   Copy-Item E:\cca-roots\*.cer "$env:ProgramData\HyFib\HealthCare360\certs\dsc-trust\"
   ```

   No restart is needed. Keep a copy of these files with the backups (on the NAS) and with the standby's set-up: the
   folder is **not** in the backups and is not streamed to the standby, so a restore on new hardware or a failover
   needs them again (sections 8 and 9). Until they are there, DSC signing is refused, signatures already made are not
   printed as DSC signatures, and, where the DSC is required, those prescriptions cannot be printed or shared: System
   health says so (`prescriptions.dsc_trust_empty`, critical where the DSC is required). The HC360 service's account
   must be able to read the folder and its files (copied files keep the folder's permissions; a folder locked down by
   hand may not): a folder it cannot read counts as an empty one, and System health says why
   (`prescriptions.dsc_trust_unreadable`). In **Hospital setup › Doctors and clinical staff**, write each doctor's name
   in full as it is on their DSC: a DSC is accepted only from the doctor it is issued to.
10. **Administration › System health**: everything should be green: the server on its cable at 1 Gbps, or on WiFi with
   its signal above 60%. On a cabled LAN, pull the server's cable for a minute: System health must say the cable is
   lost, and that it is back once it is in again. Within a few minutes of it going back in, the on-call phone must get
   an SMS that it is connected again, saying when it was lost and when it was back. Where the server reaches the SMS
   gateway over that cable alone, that is the only SMS: the alert of the loss could not go while the cable was out, and
   **Administration › Notifications** shows it suppressed, withdrawn. Where it has another way out, the phone gets the
   loss at once and the return after it. On a server with two cabled cards, do it on each cable in turn while HC360
   Desktop is open on a workstation: its requests tell the server which cable the workstations use. System health must
   show the hospital LAN's cable red, and its return's SMS must say the workstations can reach HC360 again. System
   health must show the other cable amber; its SMS, sent at once over the hospital LAN, must name it and say the
   workstations reach the server over the hospital LAN's cable, which is up, and its return's SMS must follow. System
   health also shows each integration set up in step 7 as Administration › Integrations does, and when the hospital CA
   and the server certificate expire. Run an alert drill there, and keep its timings with the go-live record ("Alerts
   that reach someone").

### Alerts that reach someone

A critical warning is told within five minutes: in the app to everyone who may see System health, and to the IT
on-call number by SMS (and WhatsApp, where the hospital chose it), again a day later while it lasts. Four things make
sure it reaches someone. On a live server, System health warns while one is missing.

1. **The on-call number, and an SMS that goes** (`alerts.not_ready`). Set `alerts.oncallPhone` (step 7) to IT's mobile
   number: one that is not a phone number gets no alert, which System health says as critical. The SMS is the
   `system.alert` template (Administration › Message templates):
   - with MSG91, give it the DLT template id its text was registered with on the DLT portal. MSG91 refuses an SMS
     without one, so every alert SMS would fail unseen: System health says so as critical;
   - other Indian gateways need the text registered too: operators drop SMS whose template is not registered on DLT.
2. **A heartbeat outside the hospital** (`alerts.heartbeat_off`, `alerts.heartbeat_failing`). HC360's alerts run in
   the server, so a power cut, a stopped `HC360Server` service, a server that cannot reach its database, or a site cut
   off from the internet tells nobody. A heartbeat monitor outside the hospital (a "dead-man's switch") fills the gap:
   it gives an address to fetch, and raises its own alarm by SMS, call or e-mail when the fetches stop. Healthchecks.io,
   Better Stack, UptimeRobot and Cronitor host one; Uptime Kuma's push monitor runs on a machine of the hospital's own,
   never the HC360 server.
   - Create a heartbeat check with a period of 5 minutes and a grace time of 5 to 10, alerting IT and HyFib support.
   - Set `alerts.heartbeatUrl.secret` to its address, and `alerts.heartbeatMinutes` if the period is not 5. The server
     that runs the jobs fetches it only while the database answers **and** the server's own web host answers: before
     each fetch it asks `/health/ready` on its own staff address over HTTPS on this machine (a 5-second limit, trusting
     only the server's own LAN certificate). A server whose web host is wedged, with staff unable to work, so stops the
     heartbeat, and counts as down like one that cannot reach its database. System health does not warn of the pause
     itself (the web host may be what is stuck): the monitor's alarm is how it is noticed. The address is https, or http
     to a private address on the hospital's network.
   - Let the server through the firewall to the monitor's host: one https request every few minutes, carrying nothing
     but the address.
   - Keep the address to those who need it: whoever has it could fetch it and hide an outage. HC360 keeps it
     encrypted, as a secret setting, and never shows it again or writes it in the audit trail.

   System health warns after three failed fetches in a row, naming the monitor's host.
3. **Someone acknowledges each critical alert** (Administration › System health › Critical alerts). Each critical
   warning stays an alert until a person acknowledges it there, which says they are on it: the warning itself stays
   until its condition clears. While nobody acknowledges it:

   | When | Who is paged, by SMS |
   |---|---|
   | At once | IT on-call (`alerts.oncallPhone`), and everyone who may see System health, in the app |
   | `alerts.escalateMinutes` later (15 if not set), or as soon as nothing reached IT on-call | The second contact (`alerts.escalationPhone`) |
   | `alerts.supportMinutes` after that (30 if not set), or as soon as nothing reached the second contact | HyFib support (`alerts.supportPhone`), with the hospital's name |

   - A message that failed or was never sent (no number set, or the number opted out of SMS) counts as nothing reached:
     the next contact is paged at the next check, within five minutes, without waiting on the SMS provider's retries.
   - A level nobody is set for is skipped. With neither set, System health warns (`alerts.no_escalation`) on a live
     server.
   - Once acknowledged, nobody further is paged, though the alert is told again a day later while it lasts.
   - The escalations use the `system.alert` template, so they need no DLT registration of their own.
   - A warning that clears and comes back is a new alert, told again at once, as before.
   - A server of several that stopped reporting, and reports again, ends its alert with one more message to everyone it
     reached: IT on-call, and the second contact and HyFib support if it went that far, by SMS, and everyone who may see
     System health, in the app. It says when it was not reporting and when it was again: "HC360-API1 is reporting again:
     it is on, its HC360Server service runs and it reaches the database. Not reporting at 09:33, reporting again at
     09:45." A message of the alert not sent by then, such as one waiting to be tried again because the SMS gateway
     could not be reached, is withdrawn, so that the return never comes before it; and a return not sent by the time
     the server stops reporting again is withdrawn for the new alert. **Administration › Notifications** shows each
     withdrawn message suppressed, and why. A server stopped on purpose, or removed from System health, is gone rather
     than back: its alert clears without a word.
   - A server of several that stays silent stays on System health, and its alert is told again every day, saying since
     when it has not reported, and after a day how to stop it if the server was retired: whoever manages the server
     (`admin.system.manage`) removes it with **Remove** on its card in System health, once it was retired, renamed, or
     set up again elsewhere. Removing it acknowledges its alert in their name, so nobody further is paged, and is in the
     audit trail (`system.server_removed`). A server that reports cannot be removed: stopped on purpose, it removes
     itself. One removed that reports again is shown again.
   - The alerts are kept in the database: a restart pages nobody again. Each escalation and acknowledgement is in the
     audit trail (`alerts.escalated`, `alerts.acknowledged`).

   The same section gives the last 30 days in figures: how many alerts, how long they took to be acknowledged (median and
   90th percentile), how many reached the second contact and HyFib support, and how many nobody acknowledged. The
   figures count every alert of the period; the list below them shows every open one and the latest 500 that cleared.
4. **A drill** (Administration › System health › Alert drills; starting one needs `admin.system.manage`). It raises a
   test critical warning, `alerts.drill:<code>`, which the alerts tell as any other: "Nothing is wrong. Acknowledge
   it". Whoever it reaches acknowledges it there. The drill keeps when it was told in the app, when its SMS was sent and
   delivered, when it was first read, and when it was acknowledged and by whom. One nobody acknowledges within an hour
   ends as not acknowledged.
   - Run one at go-live, every quarter, and whenever the on-call number, the SMS provider or its templates change.
   - A drill is an alert like any other. Leave one unacknowledged once a year, to see the second contact and HyFib
     support paged at the times set: check the times against the SMS provider's delivery reports, and against HyFib
     support's record of the call.
   - Keep the timings with the go-live and quarterly records. A step that never came shows where the chain breaks.

### Going live on a database used for training

Start the live server on a database of its own. Where a hospital trained on the database it goes live on, or set it up
before HC360 refused the simulators on a live server, the integrations' simulators left records there that look real:
payments marked received and refunds marked paid back, card sales approved, Aadhaar numbers linked with nobody's
fingerprint, images and lab results from nowhere, messages marked sent, day books posted to a simulated Tally, and
attendance punches.

A live server sweeps its database for them ten minutes after it starts and every day after. **Administration ›
Simulator records** lists each kind it found:
- how many, and the money they say moved;
- when they were made, and the references of the newest;
- for the six kinds below, how many are still open;
- what to do about them.

A payment, refund, card sale, Aadhaar link, image or lab result a simulator made stays open until its record shows it
corrected. **To correct** on the same page lists the open ones of a kind (up to 500, newest first), each with a link to
the record it is corrected on:

| Kind | Corrected when | Where |
|---|---|---|
| Online payments the payment simulator took (bill payments, IP deposits, appointment prepayments, payments kept as an advance) | All of the receipt line's money is recorded as not received | Billing › Receipts › Money not received; a second person approves |
| Refunds the payment simulator said it paid back | The refund is recorded as not paid out | The refund › Not paid out; a second person approves |
| Card and UPI sales the terminal simulator approved | Billing's: all of the money of the receipt line the sale made is recorded as not received. The pharmacy's: the invoice is cancelled the same day or taken back in a return, so its card or UPI money is paid back. The canteen's: the bill is cancelled, which only its own day allows | As a payment; the pharmacy's invoice or the canteen's bill › Cancel (the pharmacy's return: Returns). A canteen bill of an earlier day is corrected in that day's canteen accounts in Tally, and the sign-off note says so |
| Aadhaar numbers the Aadhaar simulator linked | The number is removed from the patient's record or replaced, or a real fingerprint check of it, not with a virtual ID, matches | The patient › Edit |
| Imaging studies linked to the PACS simulator | The study is linked to its real images, cancelled as not done, or its report has an addendum written after the images were linked | Radiology › the study |
| Results typed into the training analyser | Each result it gave on the sample is entered again from a real measurement (a verified one is reopened first) or amended on the released report; one still waiting unmatched is discarded | The lab order; Lab › Unmatched analyser results |

System health warns until an IT officer (`admin.system.manage`) has dealt with each kind and signed the sweep off there,
with a note of what was done:
- While any of the six is still open, the warning is critical and is sent to IT on-call, and the sign-off is refused
  (`simulators.critical_open`, naming the kinds and how many of each). Correct them, **Sweep now**, and sign off that
  sweep: one that still counted open records is refused too, even when they have been corrected since.
- The other kinds (settlements, messages, day-book vouchers, attendance punches, cardiology files and labels) leave no
  trace on their records to tell when they are dealt with. The note says what was done about them.
- Once none is open, the warning stays (not critical) until the sign-off.

A record a simulator makes after the sign-off brings the warning back.

## 5. Install HC360 Desktop on workstations

Administration works only in HC360 Desktop on an enrolled workstation or tablet (SRS SEC-08), not on a kiosk or queue display. Until the first workstation or tablet
is enrolled, a browser signed in as an administrator can open Administration › Devices and make its code, and nothing
else of administration: enrol IT's own workstation first, and set up the rest of the hospital from it. Once one is
enrolled, browsers keep everything but administration.

Once a workstation or tablet is enrolled, the code for another waits for a second administrator who may approve grants
(Approvals): once approved, Administration › Devices shows it to the administrator who asked, once, and it works for the
minutes chosen from the approval. Codes for kiosks, queue displays and lab agents are made at once.

Create one-time enrolment codes in **Administration › Devices › Enrol a device**, then on each workstation run
(Administrator PowerShell, from the extracted desktop package). Copy the checked and unblocked zip there, or the
extracted folder, and let the window run scripts first, as in section 1 (*The release files*, steps 2 and 4); the same
holds for the lab agent's package (section 6):

```powershell
# Reception, OPD, wards, billing counters
.\Install-HC360Desktop.ps1 -ServerUrl https://HC360-SRV:7360/ -CaFingerprint '<fingerprint from step 3>' -DesktopShortcut

# Waiting-area TV (queue board) or registration kiosk: full screen, starts with Windows
.\Install-HC360Desktop.ps1 -ServerUrl https://HC360-SRV:7360/ -CaFingerprint '<fingerprint>' -Kiosk `
    -StartPath '/display/queue' -StartWithWindows
```

On first start HC360 Desktop:
- confirms the server's certificate against the pinned fingerprint;
- asks for the enrolment code;
- names the computer (e.g. "OPD Reception 2").

Without `-ServerUrl` / `-CaFingerprint`, it finds the server on the hospital network and shows the fingerprint for staff
to compare.
On an all-in-one machine (server and Desktop on one computer), setup also lists "This computer" when the server
listens on port 7360 there, because the network search does not always find the computer it runs on.

HC360 Desktop remembers the addresses the server answered on, and the others the network search found for it at
setup (its name, `name.local` and its IP addresses). When the staff app cannot load and the server has not answered
three tries in a row, HC360 Desktop looks for it, once a minute, at those addresses, on this computer, and on the
network (a server announcing the same certificate authority). It opens again where the server answers, says so from
the tray, and staff sign in again. Only the hospital's server can answer: every address is checked against the pinned
certificate authority first. A workstation whose server IT fixed with `-ServerUrl` (the machine policy) only ever uses
that address (SRS DED-03).

**Updates come from the server** (SRS DED-18), so workstations need no internet access. Every 4 hours HC360 Desktop
asks the server for the version it offers (Administration › Devices shows it, and marks each device that is behind),
downloads a newer one and checks HyFib's signature. Staff then see "HC360 Desktop … is ready" from the tray: "Restart to
update" (in the tray icon's menu, or by clicking the notification) closes HC360 and opens the new version a minute or so
later, so save your work first. Otherwise it installs the next time HC360 Desktop is closed, or at the next start-up or
sign-in; kiosks and TV boards are not asked and update at their next start-up. The installer sets this up: the updater
in `HC360 Desktop Updater` beside the program, and the "HC360 Desktop update" task in Task Scheduler (HyFib folder),
which installs as SYSTEM, keeps the version before in `HC360 Desktop.previous`, and puts it back if the installation
fails. Its log and last result are `update.log` and `status.json` in the updater's folder. To leave a computer's version
to your own software distribution, install with `-NoAutoUpdate` (`"autoUpdate": false` in `policy.json`).

**DevTools** (the browser's developer tools) are off in HC360 Desktop: they show the signed-in user's token and every
response. On a support computer IT can allow them with `"allowDevTools": true` in `policy.json`, which only an
administrator can write; a user's own settings cannot turn them on, and `"disableDevTools": true` (kiosks) always wins.

**Tablets and browsers**:
1. Download `https://HC360-SRV:7360/api/v1/system/ca-certificate`.
2. Install it as a trusted root certificate on the device (MDM or device settings).
3. Bookmark `https://HC360-SRV:7360/`.

**Unlocking with Windows Hello** (SRS UX-11). Once it is turned on (below), staff can set up a workstation's Windows
Hello (face camera, fingerprint reader or the Windows Hello PIN) under My account › Lock screen, and then unlock their
locked screen with it. Nothing needs installing on the server, and no outside service is used. For it to work:
- every staff member who uses it signs in to Windows with their **own account** on that computer (see below);
- open HealthCare360 by the server's **name**, as in the addresses above, not by its IP address: WebAuthn ties each
  key to the name, and refuses IP addresses. Use one name everywhere; a key set up under `HC360-SRV` does not work
  for `HC360-SRV.local`. The name must be one of the server certificate's (the computer's name, `name.local`, or one
  added with `HealthCare360:Network:AdditionalHostNames`);
- set up Windows Hello in Windows (Settings › Accounts › Sign-in options) first;
- the browser must trust the hospital CA (step 2 above): Edge and Chrome turn WebAuthn off on a page whose certificate
  they do not trust. HC360 Desktop checks the server's certificate against the hospital CA itself rather than through
  Windows, and the WebView2 runtime it uses may treat the page the same way. If "Set up this device" in HC360 Desktop
  says Windows Hello did not answer, or the address cannot use it, install the hospital CA as a trusted root on that
  workstation too (as in step 2; for many PCs, with Group Policy). This depends on the WebView2 runtime's version, so
  try it on one workstation before go-live.

It is **off** until you turn it on: set `ui.biometricUnlock` to `true` in Administration › Settings, for the hospital
or for one facility (`false` for a facility keeps it off there). The PIN and the password always unlock the screen.

Turn it on only where every staff member signs in to Windows with their own account. Windows Hello answers for
whoever owns the Windows login, not for the HC360 user: on a computer where staff share one Windows login, as at many
nursing stations and counters, anyone who knows that login's Windows Hello PIN, or whose finger or face is set up on
it, could unlock a colleague's locked HC360 screen. HC360 cannot tell a shared Windows login from a personal one, so
where any computer in a facility has a shared login, leave it off for that facility and use the PIN. Computers
enrolled as kiosks or display boards never use it, and HC360 Desktop in kiosk mode warns against setting it up.

## 6. Printers and devices

- Receipt and token printers (80 mm), label printers, wristband printers and A4/A5 printers are ordinary Windows
  printers on each workstation. In HC360 Desktop, open **Printers on this computer** (user menu) to choose the printer
  for each kind of print and print a test. HC360 then prints silently to that printer; a kind with no printer shows
  the print dialog, never the Windows default. Changing the printers needs the "set up printers" permission
  (`integrations.printers.setup`, Hospital Admin by default), and each change is in the audit trail.
- For Zebra (ZPL) and TSC (TSPL) label and wristband printers, choose the language, resolution (203 or 300 dpi) and
  label or band size; the server then writes the printer commands so barcodes scan. Other label printers print
  through their Windows driver.
- The printers are kept for the computer in `%ProgramData%\HyFib\HC360Desktop\Workstation\printers.json`, which the
  installer lets every Windows user change. To fix them so that users and scripts on the computer cannot, add a
  `printers` entry to the HC360 Desktop policy, `%ProgramData%\HyFib\HC360Desktop\policy.json` (make the file
  read-only to users). HC360 then shows them and refuses changes:

  ```json
  {
    "serverUrl": "https://hc360-srv:7360/",
    "printers": {
      "labels": { "printer": "ZDesigner GK420t", "language": "zpl", "dpi": 203, "widthMm": 50, "heightMm": 25 },
      "wristbands": { "printer": "ZDesigner HC100", "language": "zpl", "dpi": 300, "widthMm": 25, "heightMm": 279.4, "code": "code128" },
      "receipts": { "printer": "EPSON TM-T82 Receipt" },
      "documents": { "printer": "HP LaserJet M404" }
    }
  }
  ```

  Printer names must match Windows exactly. A printer that is not installed, or an entry that is not valid, is not
  used, and Printers on this computer says why.
- Barcode scanners that work as keyboards (USB HID) need no driver or setup. A scanner on a COM port (RS-232, or USB
  in serial mode) is read by HC360 Desktop: in **Printers on this computer › Barcode scanner** choose the port and set
  the speed, data bits, parity and stop bits as they are set on the scanner (most use 9600, 8, none, 1), then try a
  scan there. It is kept in `%ProgramData%\HyFib\HC360Desktop\Workstation\scanner.json`; IT can fix it with a
  `scanner` entry in `policy.json`, for example `"scanner": { "port": "COM3", "baudRate": 9600 }`.
- **Fingerprint readers for Aadhaar identity checks** (optional, INT-BIO). Install only a UIDAI-certified reader (a
  registered device: Mantra, Morpho/IDEMIA, Startek, SecuGen…) and its maker's **RD service** for Windows, and keep the
  RD service's yearly registration current: an expired one stops capturing. The RD service listens on `127.0.0.1`,
  on a port from 11100 to 11120; HC360 Desktop finds it there by itself, so no setting is needed on the workstation
  and no firewall port is opened. Check it on the patient profile's **Identity** tab with the training simulator
  first. Checking with UIDAI needs, in **Administration › Integrations › Aadhaar authentication**, the hospital's AUA
  licence details and signing certificate and its ASA's address, all from the ASA, and the server must reach the
  ASA over https (allow its address in the hospital's firewall). Only enrolled HC360 Desktops can check: a browser
  cannot reach the reader.
- PACS, the modality worklist, biometric attendance devices, Tally and payment terminals are configured in
  **Administration › Integrations**. See `docs/modules/integrations.md`.
- Lab analysers, radiology and BI tools such as Power BI: see below.

### Lab analysers

Switch the gateway on in **Administration › Integrations › Laboratory equipment**. Register each analyser in **Lab
setup › Analysers** (code, bench, protocol HL7 or ASTM; tick **Bidirectional** if it should ask HC360 for tests and be
sent orders), then set up its **Connection** there. An analyser connects in one of two ways.

**Network analysers** (Ethernet). The analyser connects to a port on the server, one port per analyser, for example
5100. In the Connection drawer choose Network, enter the port and the analyser's fixed IP address. The address is
required: an analyser does not sign in, so HC360 takes its results only from that address. Open those ports in the
server's firewall, limited to the analysers' addresses, by running the server installer again:

```powershell
.\Install-HC360Server.ps1 -LabPorts 5100,5200 -LabAnalyserAddresses 10.0.5.21,10.0.5.22
```

**Serial analysers** (RS-232). The analyser is cabled to a Windows 10/11 PC in the lab, which runs the **HC360 lab
agent**. The agent is a Windows service: it keeps relaying when nobody is signed in to the PC, and it starts with
Windows. It relays the COM port to the server over HTTPS (port 7360), so the lab PC needs no inbound firewall rule and
the server needs none beyond 7360. One agent can relay several analysers on different COM ports.

While it relays an analyser, the agent keeps the PC from sleeping when nobody uses it: a PC asleep relays nothing, and an
analyser may give up on a result nobody takes. As Administrator, `powercfg /requests` then lists under SYSTEM
`HC360 lab agent: relaying analyser results from COM3` (its COM ports). It does not stop someone putting the PC to sleep
(Start › Power › Sleep, or a power button set to sleep), and the screen still turns off as set. If Windows refuses the
request, the agent says so in the Application event log (source *HyFib HC360 Lab Agent*): set the PC never to sleep then,
`powercfg /change standby-timeout-ac 0`.

1. Signed in to the facility the lab belongs to, open **Administration › Devices › Enrol a device** and choose the
   device type **Lab analyser agent**. Note the one-time code (valid 30 minutes by default). The agent belongs to that
   facility, and an analyser can only be relayed by an agent of its own facility.
2. On the lab PC, extract `HC360-LabAgent-<version>-win-x64.zip` and run, in an Administrator PowerShell:

   ```powershell
   .\Install-HC360LabAgent.ps1 -ServerUrl https://HC360-SRV:7360/ -CaFingerprint '<fingerprint from step 3>' `
       -EnrolmentCode ABCD-EFGH -AgentName 'Haematology PC'
   ```

   The script copies the agent to `C:\Program Files\HyFib\HC360 Lab Agent`, registers the `HC360LabAgent` service
   (its own virtual account `NT SERVICE\HC360LabAgent`, automatic start, restart on failure), enrols the PC and starts
   the service. The CA fingerprint is required: the agent refuses a server whose CA does not match it, and from then
   on trusts only that CA. The device secret is protected for this PC (machine DPAPI) in
   `%ProgramData%\HyFib\HC360LabAgentgent.json`, which only SYSTEM, Administrators and the service account can read.
3. In **Lab setup › Analysers › Connection**, choose "Serial cable to a lab PC", this agent, the COM port and the line
   settings from the analyser's interface manual (most use 9600 baud, 8 data bits, no parity, one stop bit, no flow
   control). Windows names the port in Device Manager › Ports (COM & LPT); a USB-to-serial adapter keeps its COM number
   only on the same USB socket. The agent opens the port within a minute and shows as online.

Upgrade an agent by running `.\Install-HC360LabAgent.ps1` from the new package without parameters; the enrolment is
kept. `-Uninstall` removes it; revoke the device in Administration › Devices as well. A lab agent's credential is only
good for relaying analysers: it cannot be used to sign in.

**Before go-live, test each analyser on site.** The automated tests run the protocols, the relay and the agent against
simulated analysers and COM ports, but they cannot check a real analyser's dialect, the cable and its wiring (straight
or null-modem), a USB-to-serial adapter's driver, the Windows service account's access to the COM port, or the
firewall. For each analyser: send a result for a test tube and check it reaches the worklist; if it is bidirectional,
scan a received tube on the analyser (host query) and check that a newly received tube appears on the analyser's
worklist (order download). Then run a tube whose barcode HC360 does not know, or one without a barcode: the analyser
is answered and moves on, and the result waits in **Lab › Unmatched analyser results**. Register each analyser's
control lots under Quality control with the analyser named on the lot: an analyser's results are auto-validated only
on the QC that analyser ran that day.

### Radiology: PACS and the modality worklist

HC360 reads studies and images from the PACS over DICOMweb, and serves the day's patients to the modalities (CT, MR,
X-ray, ultrasound…) as a DICOM modality worklist, so radiographers pick the patient at the console instead of typing.

**PACS** (Administration › Integrations › PACS, key `integrations.pacs`):
1. Set the provider to `dicomweb` and the DICOMweb address of the PACS, for example
   `http://orthanc.hospital.local:8042/dicom-web`. Add a separate WADO address only if the PACS serves images elsewhere.
2. Set the sign-in (none, basic or bearer). The password or token is stored encrypted and never shown again.
3. Set the viewer link, for example `https://pacs.local/ohif/viewer?StudyInstanceUIDs={studyUid}`.
4. Press **Test**. HC360 then links new studies with their images every five minutes. A study that PACS holds under
   another patient ID, without one, or twice is not linked, and the radiographers are told: correct it in PACS.

**Modality worklist** (Administration › Integrations › Modality worklist, key `integrations.mwl`, one setting for
all facilities):
1. Turn it on. Keep port **11112** (the DICOM port) and AE title `HC360MWL` unless the PACS vendor asks otherwise.
   To keep the port off the clinical WiFi, set the listen address to the server's address on the imaging network.
2. Add each modality: its own AE title as set on its console (for example `CT1`), its fixed IP address (give it a DHCP
   reservation), the facility code whose patients it sees, and the kinds of study it does (`CR` or `DX` for X-ray,
   `CT`, `MR`, `US`, `MG`, `RF`). Only listed modalities, calling from their own address, get an answer.
3. Keep facility codes to five letters: radiology accession numbers are then within DICOM's 16 characters. Longer
   ones are still matched with PACS, by study UID, but cannot be typed or searched at the console.
4. Open the port in Windows Firewall for the modalities only. The installer opens only HTTPS and mDNS, and it removes
   its own rules when it runs again, so this rule has a group of its own:

   ```powershell
   New-NetFirewallRule -DisplayName 'HC360 Modality worklist (DICOM)' -Group 'HyFib HealthCare360 DICOM' `
       -Direction Inbound -Protocol TCP -LocalPort 11112 -RemoteAddress 10.0.5.31,10.0.5.32 `
       -Profile Private, Domain -Action Allow
   ```

   Put the modalities' addresses in `-RemoteAddress`. The uninstaller does not remove this rule; remove it with
   `Get-NetFirewallRule -Group 'HyFib HealthCare360 DICOM' | Remove-NetFirewallRule`.
5. Press **Test** on the dashboard: the server sends itself a DICOM echo, so the port is open and the AE title answers.
   The dashboard then shows the listener, its open associations and the queries answered and refused.

**At each modality console** (the vendor's service engineer usually does this), add a worklist provider (it may be
called RIS, MWL or "worklist server"): AE title `HC360MWL`, the server's IP address, port 11112. Set the modality's own
AE title to the one entered in HC360. Keep sending images to the PACS as before; the modality copies the accession
number and Study Instance UID from the worklist into the images, which is how HC360 links them. Then run the console's
"verify" or "echo" and query today's worklist.

**Testing without a modality.** From a PC listed as a modality (its IP address and an AE title such as `TESTSCU`), use
the free DCMTK tools:

```
echoscu -v -aet TESTSCU -aec HC360MWL HC360-SRV 11112
findscu -v -W -aet TESTSCU -aec HC360MWL HC360-SRV 11112 ^
  -k "ScheduledProcedureStepSequence[0].Modality=CT" ^
  -k "ScheduledProcedureStepSequence[0].ScheduledProcedureStepStartDate=20261003" ^
  -k PatientName -k PatientID -k AccessionNumber -k StudyInstanceUID -k RequestedProcedureDescription
```

Each answer is one patient. Wildcards work, for example `-k PatientName=SHARMA*`. Administration › Integrations ›
Logs shows each query (patient keys masked), and `GET /api/v1/radiology/worklist/mwl?date=…` returns the same list as
JSON for a radiographer or radiologist. For a test PACS, Orthanc with its DICOMweb plugin works:
`deploy/docker/compose.dev.yml --profile pacs` starts one at `http://localhost:58042/dicom-web`. Upload a DICOM file
whose Accession Number and Patient ID are a study's accession number and the patient's UHID, then link the study.

### Card and UPI terminals (Pine Labs)

HC360 pushes the amount to the counter's Pine Labs terminal (Plutus Smart, cloud integration), the patient pays on
the terminal, and the receipt is recorded with the terminal's RRN, approval code and the card's last four digits
(`docs/modules/integrations.md`, "Card/UPI POS"). Billing's counters push bills and IP advances to the terminal, and the
pharmacy and canteen counters push their invoices and bills the same way (**Take on terminal**); the payment then
carries the terminal's RRN, approval code and card digits, and nothing is typed. A counter can still type the RRN or UPI
reference from the slip instead.

1. **From Pine Labs** (the hospital's relationship manager, or Pine Labs' integration team): the UAT and production
   addresses of the cloud integration service, the merchant id, the security token, the user id, and for each terminal
   its store/POS code and its IMEI or serial number. Ask also for their Cloud Integration document. Its version 1.36
   matches HC360's payment-mode codes (card `1`, UPI `10`) but lists no status code for a sale still waiting on the
   terminal: ask Pine Labs for it, and confirm it in step 3.
2. **Set it up in UAT first**: Administration › Integrations › Card/UPI POS: provider Pine Labs, the UAT address,
   merchant id, user id, security token and the terminals, one entry per counter with an id such as `OPD-1`. When Pine
   Labs' codes differ from HC360's defaults, set *Payment mode codes* (card `1`, upi `10`) and *Codes that mean "still
   waiting"* (`1001`). At each billing, pharmacy and canteen counter, the first card or UPI payment asks for the
   terminal id (on the terminal's label), and the computer remembers it for that counter.
3. **Run the UAT test pack** with a UAT terminal and Pine Labs' test cards, and keep each result for Pine Labs' sign-off:

   | # | At the counter | On the terminal | HC360 shows |
   |---|---|---|---|
   | 1 | A bill by card | pay with a test card | Approved, and a receipt with the card's last four digits, the approval code and the RRN |
   | 2 | A bill by UPI | pay by UPI | Approved, and a receipt with the UPI reference |
   | 3 | A card sale of ₹1,234.56 | the same amount, paise included | Approved for ₹1,234.56 |
   | 4 | A card sale | press Cancel | Cancelled, no receipt |
   | 5 | Cancel in HC360 before paying | the sale disappears | Cancelled |
   | 6 | Cancel in HC360 while the customer pays | pay | Approved, and the receipt is recorded: an approval wins |
   | 7 | A card sale, then wait past the auto-cancel time | nothing | Timed out or cancelled |
   | 8 | Close the payment screen, then pay | pay | The receipt is recorded within a minute by the re-check |
   | 9 | An IP advance by card | pay | Approved, and an advance receipt |
   | 10 | Part of a bill on the terminal, the rest in cash | pay | Two payments on the bill |
   | 11 | A card Pine Labs declines (their decline test card) | try again or cancel | The sale stays waiting while the terminal lets the customer retry, or ends declined (note which) |
   | 12 | A sale while the terminal is offline | nothing | Waiting; Cancel in HC360 ends it |
   | 13 | A void on the terminal, then a refund in HC360 with the void's RRN | void | The refund is recorded |
   | 14 | A card sale; pull the terminal's network cable just as **Send to terminal** is pressed, and put it back after the screen has answered | the sale may or may not appear | Not confirmed (never Declined), with what to check on the terminal, the time a new sale may be sent, and **Try again** off. Note whether the sale appeared on the terminal |
   | 15 | The same, then press **Send to terminal** again at once from another counter | the first sale, if it appeared | Refused with "The last sale of ₹… at … may still be on the terminal"; the customer is charged once |
   | 16 | The same, with the sale on the terminal: pay it | pay | Record the payment from the slip; the exception raised later is resolved with a note |
   | 17 | The same, with no sale on the terminal | idle menu | **The terminal shows no sale** with a note frees the terminal; the note is in the audit trail |
   | 18 | The same, and leave it past the auto-cancel time plus two minutes | nothing | Lapsed, outcome unknown, and a Gateway reconciliation exception the accountants clear against the settlement |
   | 14 | A pharmacy walk-in counter sale by card, **Take on terminal** | pay with a test card | "Paid on the terminal · Card ending …"; the invoice's payment has the RRN, approval code and card digits |
   | 15 | A pharmacy dispense paid part by UPI on the terminal, the rest in cash | pay by UPI | Two payments on the invoice; the UPI one has the terminal's reference |
   | 16 | A canteen bill by UPI, **Take on terminal**, then **Finish the bill** | pay by UPI | The bill is saved with the terminal's reference |
   | 17 | A pharmacy payment taken, the screen closed, then the invoice made from **Already paid on the terminal** | pay, then nothing | The payment is listed, **Use this payment** records it, and the same payment cannot be used on a second invoice |
   | 18 | A pharmacy payment approved and never put on an invoice, then wait past the re-check time (15 minutes) | pay | Billing › Gateway reconciliation shows "Paid on the terminal, but no invoice records it yet", and the accountants get one alert; recording it on an invoice clears it |

   Administration › Integrations › Integration log shows each call's `code=… message=…`. Note the code and message
   for a waiting, approved, cancelled, declined and timed-out sale: HC360 relies on them. If a sale that is still
   waiting shows any code other than `1001`, add it to *Codes that mean "still waiting"* before going live, or HC360
   takes the sale as declined while the customer may still pay.
4. **Settlement**: import the UAT day's settlement report on Billing › Card and UPI settlements. Every approved test
   sale should match its receipt. The pharmacy's and the canteen's terminal payments match their invoices and bills,
   not Billing's receipts, even when one terminal serves Billing and a counter.
5. **Go live** once Pine Labs signs off UAT: switch the address, merchant id, user id, security token and terminals to
   the production ones, then run one small live sale and void it.

Money a terminal took that HC360 could not record (an advance for a stay whose account was closed meanwhile, say) is
shown on the counter screen ("Paid, but no receipt yet"), raised in Billing › Gateway reconciliation as a payment
without a receipt, and the accountants are alerted. Record it or refund it on the terminal there. A pharmacy or canteen
payment that no invoice or bill records within the re-check time (15 minutes by default) is raised there the same way,
as "Paid on the terminal, but no invoice records it yet", with one alert "Card/UPI payment not recorded at the
pharmacy/canteen"; recording it on an invoice later clears it. When the customer has gone, refund it on the terminal
and resolve the exception with a note: the counter is then no longer offered the payment, and cannot record it.

### BI tools: Power BI and the extract folder

HC360 writes BI extracts (Analytics › BI extracts) as CSV files to one folder, and the BI tool reads them from there.
Each facility's files are in a sub-folder named by its code (`NH\daily-revenue.csv`; a facility coded `CON`, `PRN`,
`AUX` or `NUL`, names Windows keeps for devices, gets `CON_`), and `refresh-log.csv` at the top lists every extract with
when it was last refreshed.

1. Make a folder for the extracts outside HC360's data folder, for example `D:\HC360-BI`. Give the HC360 service
   *Modify* and the BI tool's account *Read*, and nobody else access:

   ```powershell
   New-Item -ItemType Directory D:\HC360-BI
   icacls D:\HC360-BI /inheritance:r /grant 'NT AUTHORITY\SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' `
       'NT SERVICE\HC360Server:(OI)(CI)M' 'HOSPITAL\svc-pbi-gateway:(OI)(CI)RX'
   ```

   Use the account the Power BI on-premises data gateway runs as (or the account of whoever opens the files).
2. Tell HC360 the folder in its settings file, `%ProgramData%\HyFib\HealthCare360\config\hc360.settings.json`, which
   upgrades keep. Add `Analytics` at the top of its `HealthCare360` section, writing each `\` twice as JSON needs:

   ```json
   "HealthCare360": {
     "Analytics": { "ExtractDirectory": "D:\\HC360-BI" },
     "HttpsPort": 7360,
   ```

   Then restart the service: `Restart-Service HC360Server`. Without the setting, HC360 writes to
   `%ProgramData%\HyFib\HealthCare360\bi-extracts`, which only Administrators and HC360 can read.
3. If the gateway runs on another computer, share the folder read-only:
   `New-SmbShare -Name HC360-BI -Path D:\HC360-BI -ReadAccess 'HOSPITAL\svc-pbi-gateway'`.
4. Open Analytics › BI extracts. The *Extract folder* panel shows the facility's folder and the refresh log, or why
   HC360 cannot write there.
5. In Power BI Desktop, choose Get data › Folder and enter the folder (or the share), or Get data › Text/CSV for one
   file. After publishing, add the folder to the gateway as a data source and schedule the refresh after the extracts'
   refresh time.

Each refresh replaces the file whole, so the BI tool never reads half a file. If a refresh fails, the last file stays
and the people who manage schedules are told in HC360. With more than one HC360 server (`HealthCare360:Role`), set the
same shared folder, as a UNC path such as `\\NAS\HC360-BI`, on every server, and give each server's computer account
(`HOSPITAL\HC360-SRV$`) *Modify* on it.

## 7. Upgrades

1. Tell users: HC360 is unavailable for a few minutes.
2. Extract the new server package.
3. Run `.\Install-HC360Server.ps1` again, **without** `-CreateDatabase`.

With several servers behind a reverse proxy, stop them all and upgrade the node that runs the jobs first (section 10,
"Running several nodes").

The installer stops the service, replaces the program files, keeps configuration and data, and starts the new version.
Every setting in `hc360.settings.json` stays as it is, including those added by hand (such as
`HealthCare360:Pdf:BrowserPath`, the BI extract folder or a reporting replica) and protected values. Of its own
settings, the installer changes only those given to it again as parameters (`-HttpsPort`, `-PortalHttpsPort`,
`-FacilityCode`, `-FacilityName`, `-SecondaryBackupDir`), and `HealthCare360:Backup:PgDumpPath`, which follows the newest
PostgreSQL under Program Files. One the file lacks is added, such as the portal's port. The same holds when it runs
again for `-LabPorts`. Leave comments out of the file: Windows PowerShell cannot read them (the installer then stops
before it changes anything), and they are lost when the file is rewritten.
It also protects any secret still written in plain text in the configuration (an installation from before this was
done), and checks BitLocker again.
Staff whose password is on the breach list built into the new version are asked to choose a new one at their next
sign-in, before anything else (SECURITY.md, "Identity and sessions"). Warn them if many may be affected. In the same
way, staff who must now use two-step verification and have not set it up are asked to at their next sign-in:
accountants, HR managers, and anyone whose roles carry an administrative permission. Check that each has an
authenticator app on their phone. Temporary passwords given out before HC360 made them expire work for 72 hours after
the upgrade.
Network analysers must now have their IP address in their connection. One set up without it keeps working, but takes
results from any device on the network until the address is added: Lab setup › Analysers shows it as *Open to any
device*. Enter each such analyser's address in its connection.
Once the new version has started, the installer switches the service to sign in to PostgreSQL with its Windows account,
so the configuration no longer holds the database password (section 3, "How the server signs in to the database").
HC360 stops for another minute or two while PostgreSQL restarts. If HC360 then does not reach the database, the
installer puts everything back, and HC360 signs in with its password as before; the warning says so. Run the upgrade
with `-PostgresSuperuserPassword` to remove the old password from the database account as well.
The patient portal moves to an address of its own, port 7361 (SECURITY.md, "Separate addresses"), and the installer
opens that port in the firewall. Links to the old address (`https://HC360-SRV:7360/portal/...`), in messages already
sent and on QR codes already printed, keep working: the staff app's address sends them on to the portal's. Then:
- Set `portal.baseUrl` to the portal's new address, for example `https://HC360-SRV:7361`, so new links go there directly.
- If the portal was reachable from the internet through port 7360, forward port 7361 instead and close 7360 to the
  internet: staff never need the staff app's address from outside.
- Webhooks of the payment gateway, SMS and WhatsApp answer on both addresses, so their settings at the provider can
  stay as they are.
- The API reference, if IT turned it on, moves to `https://localhost:7362/docs` on the server machine.
- Before upgrading, check the ports of the network analysers (Lab setup › Analysers, each one's Connection) and of the
  modality worklist (Administration › Integrations › Modality worklist). After the upgrade the server keeps port 7361
  for the portal: an analyser or worklist set to it is not listened for, and shows as failing with the reason.
  Move it to another port, in HC360 and on the analyser or the modalities, or run the installer with
  `-PortalHttpsPort` and a free port, and use that port wherever this section says 7361.
Before the new version changes the database schema, it backs up the database and checks the backup (Administration ›
Backups lists it as "before an update"). If that backup fails, for example because the disk is full, HC360 does not
start and the database is not changed. The reason is in the log and in Windows Event Viewer.

A server first installed from a pre-release build, before database changes were versioned, is brought up to date
automatically on the first start of this version. The log lists what was added. If its database differs in a way that
cannot be fixed safely (for example a column of another type), HC360 stops with the list of differences and changes
nothing. Keep the previous version running and contact HyFib support.

HC360 Desktop updates itself from the server (section 5). Download `HC360-Desktop-<version>-win-x64.zip` with its
`.manifest.json` and `.manifest.sig` from the release into the same folder as the server package: the server installer
offers it to the workstations, once the server has checked HyFib's signature. To offer one later, run
`HyFib.HealthCare360.Server.exe desktop publish --package <zip>` on the Server Node from an elevated prompt (`desktop show`
says what is offered). A release without a signature (`.manifest.sig`) is not offered; upgrade those workstations, and
any installed before self-update or with `-NoAutoUpdate`, with `Install-HC360Desktop.ps1` (or through your software
distribution tool). Self-update replaces the program files only: run the installer again when a release's notes ask for
it.

### Desktops and lab agents left on an old version

An update is offered to workstations and lab agents, not forced, so some stay behind. System health lists, as a warning
(`clients.outdated`), every HC360 Desktop and lab agent seen in the last 30 days that runs a version below the minimum, or
reports none, and the answer to each one's heartbeat carries the header `X-HC360-Update-Required` with the version to update
to. They keep working: nothing is refused, so no ward is locked out mid-shift. By default the minimum is the server's release
(major and minor: a 1.1.x server asks for 1.1.0); to set one, put `HealthCare360:Clients:MinimumDesktopVersion` and
`HealthCare360:Clients:MinimumLabAgentVersion` (such as `1.0.5`) in `hc360.settings.json`.

### Going back

The way back from an upgrade is the previous version **and** the backup HC360 took just before the update changed the
database. Never the previous version alone: on a database a newer version changed, it stops, says so in its log and in
Windows Event Viewer, and names the database changes it does not know. It changes nothing.

Restoring that backup loses everything entered since the upgrade. So upgrade at the quietest hour with the downtime kit
out (docs/modules/downtime.md), decide within about half an hour whether the upgrade stands, and keep on paper what is
done after that, to enter again once the previous version is back. With several servers, stop them all and go back on
each.

1. **Find the backup.** It is the newest file in the backup folder (`%ProgramData%\HyFib\HealthCare360\backups`, or the
   folder in `HealthCare360:Backup:Directory`) from just before the upgrade. Check it:

   ```powershell
   & 'C:\Program Files\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe' backup verify --in '<file>'
   ```

   It says `PreMigration backup ... taken <the time of the upgrade> ... by HC360 <the version upgraded to>`: the new
   version takes it as it starts, just before it changes the database, so the backup names the new version while its
   database is still the previous version's, which the previous version restores. An upgrade that changed no table made
   no such backup, and the previous version starts on the database as it is: skip step 3.
2. **Put the previous version back, stopped.** From the previous release's `deploy` folder, checked and unblocked as in
   section 1, run `.\Install-HC360Server.ps1 -NoStart`. It replaces the program files and keeps the configuration and
   data.
3. **Restore the backup:** `.\Restore-HC360.ps1 -BackupFile '<file>'`. It keeps the upgraded database under another
   name (`hc360_before_restore_<time>`), restores the one from before the update, and starts HC360. The backup holds
   the database and its keys, not the documents, which stay as they are. As HC360 starts on it, it moves every number
   series past the numbers issued since the upgrade, so a bill or UHID printed meanwhile is not issued again, and holds
   outgoing messages until an administrator releases them (section 8, "After a restore"; a previous version does so
   only if it is this version or later).
4. **Run the previous version's `.\Install-HC360Server.ps1` again**, without `-NoStart`, so that HC360 starts with
   Windows again. *Check:* Administration › System health shows the previous version and nothing red.
5. **Workstations.** HC360 Desktop never goes back a version by itself. One that took the newer version keeps it; where
   it misbehaves, install the previous one there with that release's `Install-HC360Desktop.ps1`.

Rehearse these steps once, on a copy restored on a spare PC, before the first upgrade a hospital depends on.

## 8. Backups and disaster recovery

- An **encrypted** backup (AES-256-GCM) runs every night at 01:30 IST and is kept for 30 days. With
  `-SecondaryBackupDir`, each backup is also copied to the second disk or NAS, and once the off-site copy is set up
  (below), it is uploaded to a bucket outside the building within 15 minutes.
- Check **Administration › Backups** weekly for green "Succeeded" rows. A failure is also told to the administrators in
  the app, and System health warns once no backup has succeeded for 36 hours.
- **The copy to the second disk or NAS** is what survives if the server's own disk fails. The number series'
  high-water mark is copied there too, every minute while numbers are issued ("After a restore" below). When a copy
  fails (the USB disk is unplugged or has another drive letter, the NAS is down), the backup is kept on the server and:
  - the Backups screen shows "No" in its **Second copy** column, and the reason in its **File / error** column;
  - the administrators are told in the app ("Backup not copied");
  - System health warns (`backup.copy_failed`), naming the folder and the reason;
  - once no backup has been copied for three and a half days, the warning turns critical and is told to IT on-call
    and escalated like every critical warning (section 4, "Alerts that reach someone"). A disk taken out on a Friday
    and not put back is told on the Monday afternoon.

  Plug the disk back in, or bring the NAS back, then **Back up now**: the warning clears once a backup is copied.
  Only full backups count, so a failed copy of the backup taken before an update raises nothing. With several servers
  (section 10), give every node the same `Backup:SecondaryDirectory`, since **Back up now** runs on the node that
  answers it.
- **At go-live, test the copy warning.** Unplug the backup disk (or point a NAS path at a share that is off) and press
  **Back up now**: check that the Second copy column says "No", the in-app notice arrives, and System health shows
  `backup.copy_failed`. Plug the disk back in and back up again: the warning clears. Keep the result with the go-live
  records.
- Each backup holds the database, the stored documents, the field encryption keys and the audit seal key, all encrypted.
  Every backup is read back and checked when it is made. The **Contents** and **Checked** columns show this.
- **Restore test every quarter** on a spare machine. The restored copy holds its outgoing messages ("After a restore"
  below): leave them held there, or patients get the test copy's messages. To check a copy on the NAS or a USB drive
  without restoring it:

  ```powershell
  & 'C:\Program Files\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe' backup verify --in '\\NAS\HC360-Backups\hc360-<date>.hc360bak'
  ```

- To restore after a disk failure on new hardware:
  1. Install PostgreSQL and HC360 as above, with the old server's `-SecondaryBackupDir`: HC360 finds the copy of the
     number series' high-water mark there ("After a restore" below).
  2. Run:

     ```powershell
     .\Restore-HC360.ps1 -BackupFile '\\NAS\HC360-Backups\hc360-<date>.hc360bak'
     ```

     It asks for the recovery key that opens that backup: the one in use when it was made, which the Backups list names.
     It checks the whole backup before it changes anything. It restores the database first, keeping the current one
     under a new name (nothing is dropped). Only then does it put the documents back and install the backup's
     encryption key. If a step fails, the previous database is put back and HC360 starts again on it. On new hardware,
     later backups open with the key entered here; make a new key if that one should be retired.

     A backup holds no server's part of System health, the heartbeat of the machines that made it: each server here
     writes its own as it starts, so System health shows the servers that run here, not the old ones. A backup made
     before HC360 left those out holds them: the script clears them after restoring the database, and names them
     ("Cleared the rows of the servers the backup knew …"). If it cannot, it says so and goes on; remove any old
     server that shows as not reporting with **Remove** on its card in System health.

     Last, before it starts HC360, it leaves `restored.json` in the data folder. What HC360 then does, and what to check
     before the hospital works on the restored server, is under "After a restore" below.
  3. Workstations see a new server certificate. If the hospital CA was exported from the old server and kept (section
     9, step 3), install it with step 1's installer run: `.\Install-HC360Server.ps1 -HospitalCaFile <the .pfx>`, before
     the restore. Workstations then trust the new server as they trusted the old one, and find it on the network.
     Otherwise re-run HC360 Desktop setup on each (Change server…) and compare the new fingerprint.
  4. Where doctors sign prescriptions with their DSC, copy the CCA India root certificates back into
     `%ProgramData%\HyFib\HealthCare360\certs\dsc-trust` (section 4, step 9): they are not in the backup. Until then
     DSC signing is refused and DSC-signed prescriptions are not printed as such (where the DSC is required, not at all);
     System health warns.

  Backups listed as "Database only (older backup)" were made before HC360 archive backups. To restore one of those on
  new hardware, first copy the old `%ProgramData%\HyFib\HealthCare360\keys` folder back, if you have it.

  Backups whose recovery key is "Original key" open with the original key, which no screen shows any more. On the
  Server Node that made them, enter `original` when the script asks for the key; elsewhere, use the copy made when
  administrators could still display it.

### After a restore

A restore puts the database back to the moment its backup was made, and with it every number series. The UHIDs, visit
numbers, bills, receipts and GST invoices issued since would be issued again, though they are on cards, wristbands and
invoices already, and messages queued then would go again. So as HC360 starts on a restored database, before anyone
can sign in, it:

1. **Moves every number series past the numbers issued before the restore.** The server that runs the jobs keeps a
   high-water mark of every series outside the database: `numbering\number-series-high-water-mark.json` in the data
   folder, written every minute while numbers are issued and as the service stops, and copied beside the backups'
   second copy (`-SecondaryBackupDir`), which is the copy that survives a disk failure. Each series behind the mark goes
   on from the last number the mark records **plus 100**, for the numbers issued in the minute before a power cut. So
   after last night's backup is restored, the next UHID is about 100 above the last one printed. Each series moved is in
   the audit trail (`numbering.raised`, Administration › Audit), which explains its gap to the auditors of bills and
   receipts. A yearly series moves into the new year when the mark has numbers of it. This happens at every start, so a
   restore made by hand, without the script, is caught too; only the next two steps need the script's `restored.json`.
2. **Holds the outgoing messages queued before the restore** (SMS, WhatsApp, e-mail, ABDM, e-invoicing). Some were sent
   after the backup was made and would go again. Messages queued after the restore go as usual, among them the SMS that
   tells IT on-call of the restore.
3. **Says so in System health**, as a critical warning (`system.restored`): the backup it was restored from, the series
   it moved with their next numbers, and that the messages are held. It lasts until the messages are released.

Before the hospital works on the restored server:
- Sign in, check the records entered last before the failure, and enter again from paper what is missing.
- Check the next numbers System health names against the last ones printed (the last bill, the last wristband).
- Check the held messages, then release them (below). None of them goes out until then.

Without the mark's copy (no `-SecondaryBackupDir`, or a different one on the new hardware), a restore on new hardware
finds no mark, and System health says so: set each series' next number past the last one printed, in Hospital setup ›
Number formats, before the hospital works on it. The mark is sealed with the audit seal key that every backup carries,
so one written elsewhere, or changed by hand, is not used. If start-up cannot do all of this after a restore, it stops
rather than issue numbers or send messages twice; the reason is in the log and Windows Event Viewer, and the marker
stays until HC360 starts.

**The held messages.** A restored database holds, as waiting, the messages that were waiting when the
backup was made, and some of them may have gone out since: a payment link, a bill, a result. So as the restored server
starts, it holds the messages queued before that moment that go to the internet (the hospital-wide setting
`messaging.outbox.hold`): none of them goes by SMS, WhatsApp, e-mail, ABDM or e-invoicing until an administrator
releases them. Messages queued after the restore are new and go as usual, the SMS that tells IT on-call of the restore
among them, and so do alerts in the app, the analysers' orders and Tally. A held message still expires when its time
is up (a sign-in code after ten minutes), and System health counts it as expired. System health shows the restore as
a critical warning (`system.restored`), which the alerts tell IT on-call, and counts the held messages in a warning of
its own, which does not page anyone again: "12 outgoing messages queued before the restore at … are held: check them,
then release". To release them:
1. Open **Administration › System health**. The **Messages waiting to send** card says how many messages are held and
   when the backup was made.
2. Check what is held: **Check the queued messages** opens **Administration › Notifications › Delivery log** at the
   Queued messages; those queued before the restore are the held ones. Compare them with what the providers' own
   portals show went out after the backup was made (MSG91's report, Meta's WhatsApp Manager, the mail server's log), and
   tell anyone who should know of a message that will go twice.
3. Press **Release** on the card (`admin.system.manage`) and confirm. The release clears the hold and the restore's
   warning, is audited (`messaging.outbox_released`, with what the hold said and how many messages it held), and the
   messages go within seconds, on every server, as each provider answers.

A hold set for any other reason works the same way, and its warning is critical: nothing else tells of it.

### The off-site copy

The second disk or NAS is in the same building as the server, so a fire, a flood, a theft, or ransomware on the server
can take both. HC360 therefore also uploads every verified full backup to a bucket outside the building, in an
S3-compatible object store: AWS in Mumbai (`ap-south-1`) or Hyderabad (`ap-south-2`), OCI in Mumbai or Hyderabad, or
any S3-compatible store hosted in India. The backups are encrypted with the recovery key before they leave; the store
never has that key.

- **When.** The server that runs the background jobs looks every 15 minutes. It uploads the newest verified full backup
  first, then older ones not yet off site that were made within the last 7 days (`backfillDays`), one at a time. The
  backup taken before an update holds no documents and is not uploaded.
- **Large backups on a slow line.** A backup over 16 MB goes up in parts of 8 MB or more. Each part the store takes is
  recorded with the backup, so an upload cut off by a lost connection or a restart goes on from where it stopped. Size
  the uplink so that a night's backup goes up within the day: a 6 GB backup takes about 80 minutes at 10 Mbit/s.
- **Checked on the way and at the end.** Every request goes over https only, is signed with the access key, and carries
  the SHA-256 and MD5 of what it sends, which the store checks: a part damaged on the way is refused, not stored. Before
  the parts are joined, HC360 reads the backup file again and checks it against the SHA-256 it was verified with when
  it was made. Afterwards it reads back the copy's size and checksum from the store. Only a copy that matches counts.
- **Locked.** Each copy is locked for 35 days (`retainDays`) with S3 Object Lock in compliance mode: nobody can delete
  or overwrite it before then, not HC360, not an administrator, not the owner of the store account. HC360's access key
  cannot delete anything in any case.
- **Shown.** The Backups screen's **Off site** column says Yes, with the time, or the store's answer to the last try.

**Setting it up.**

1. With the hospital's own administrator account for the store (never HC360's key), create the bucket with Object Lock
   on. Versioning comes with it. With the AWS CLI:

   ```powershell
   aws s3api create-bucket --bucket nana-hc360-backups --region ap-south-1 `
       --create-bucket-configuration LocationConstraint=ap-south-1 --object-lock-enabled-for-bucket
   aws s3api put-public-access-block --bucket nana-hc360-backups `
       --public-access-block-configuration BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true
   aws s3api put-bucket-lifecycle-configuration --bucket nana-hc360-backups --lifecycle-configuration file://hc360-lifecycle.json
   ```

   `hc360-lifecycle.json` removes each copy as its lock passes, and the parts of uploads that never finished. Keep its
   `Days` equal to `retainDays`: the privacy notice tells patients that erased data leaves the backups within
   `retainDays` + 2 days (37) while the off-site copy is on, and counts on it (docs/modules/privacy.md).

   ```json
   {
     "Rules": [
       {
         "ID": "hc360-backups",
         "Status": "Enabled",
         "Filter": { "Prefix": "hc360/" },
         "Expiration": { "Days": 35 },
         "NoncurrentVersionExpiration": { "NoncurrentDays": 1 },
         "AbortIncompleteMultipartUpload": { "DaysAfterInitiation": 7 }
       }
     ]
   }
   ```

   Keep the bucket's default encryption with keys the store manages (SSE-S3), not KMS keys: HC360 checks each copy
   against its ETag, which is a checksum only with SSE-S3.
2. Make an access key for HC360 alone: an IAM user with this policy and nothing else. It may upload, finish or give up
   an upload, and read back what it uploaded. It may not delete anything, list the bucket, or change the bucket, its
   lock or its lifecycle.

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       {
         "Sid": "HC360UploadsBackups",
         "Effect": "Allow",
         "Action": ["s3:PutObject", "s3:PutObjectRetention", "s3:GetObject", "s3:AbortMultipartUpload"],
         "Resource": "arn:aws:s3:::nana-hc360-backups/hc360/*"
       },
       {
         "Sid": "HC360NeverDeletes",
         "Effect": "Deny",
         "Action": ["s3:DeleteObject", "s3:DeleteObjectVersion", "s3:BypassGovernanceRetention", "s3:PutBucketObjectLockConfiguration", "s3:PutLifecycleConfiguration", "s3:PutBucketPolicy"],
         "Resource": ["arn:aws:s3:::nana-hc360-backups", "arn:aws:s3:::nana-hc360-backups/*"]
       }
     ]
   }
   ```

   `s3:PutObject` covers a multipart upload's start, parts and completion; `s3:AbortMultipartUpload` gives up one whose
   backup file failed its check (it removes parts, never a finished copy). `s3:PutObjectRetention` lets each upload set
   its copy's lock (in compliance mode a lock can be lengthened, never shortened), and `s3:GetObject` lets HC360 read back
   what arrived. Keep the account's administrator credentials off the Server Node.
3. In HC360, under Administration › Backups, **Off-site copy**: switch it on, enter the store address, region, bucket and
   folder (`hc360/`), and the access key ID and secret access key, and save. The change waits for a second
   administrator (MAKER-CHECKER.md), who approves it under Approvals; nothing is uploaded until then. The settings are
   `backup.offsite` and, encrypted, `backup.offsite.secret`, for the whole hospital.
4. Select **Back up now**. Within 15 minutes the backup's **Off site** column says Yes. Check that the bucket shows the
   file under `hc360/`, with its lock date.

| Store | Store address | Region | Path-style | Lock |
|---|---|---|---|---|
| AWS Mumbai | `https://s3.ap-south-1.amazonaws.com` | `ap-south-1` | Off | Object Lock, `retainDays` 35 |
| AWS Hyderabad | `https://s3.ap-south-2.amazonaws.com` | `ap-south-2` | Off | Object Lock, `retainDays` 35 |
| OCI Mumbai | `https://<namespace>.compat.objectstorage.ap-mumbai-1.oraclecloud.com` | `ap-mumbai-1` | On | A retention rule of 35 days on the bucket, and `retainDays` 0 |
| OCI Hyderabad | `https://<namespace>.compat.objectstorage.ap-hyderabad-1.oraclecloud.com` | `ap-hyderabad-1` | On | As for OCI Mumbai |
| Another S3-compatible store in India | Its S3 address, from its documentation | As it names it | As it says (On for MinIO-based stores) | Object Lock where it offers it; otherwise its own lock on the bucket, and `retainDays` 0 |

On OCI, the access key is a customer secret key of a user whose group's policy allows only `OBJECT_CREATE`,
`OBJECT_INSPECT` and `OBJECT_READ` in that bucket.

**What System health says** (`backup.offsite_stale`):

- A warning while the newest backup's upload fails, with the store's answer. HC360 tries again every 15 minutes.
- Critical once no backup has reached the bucket for three and a half days (where none ever has, once uploads have
  failed that long), or once the newest verified backup is still not off site a day after it was made, as on an uplink
  too slow to carry a night's backup in a day, or a server that stopped trying. Critical warnings are told to IT on-call
  by SMS and escalated (section 4, "Alerts that reach someone").
- A warning, on a live server with nightly backups, while no off-site copy is set up: "No copy of the backups leaves the
  building".

| The store's answer | What it means |
|---|---|
| "The store could not be reached" | No internet, or a firewall or proxy in the way: the server must reach the store address on port 443. |
| 403 `AccessDenied` | The access key, or its policy, does not allow uploads to that bucket and folder. |
| 403 `SignatureDoesNotMatch` | The secret access key is wrong, or the server's clock is out (System health says so too). |
| 301 `PermanentRedirect`, or "the bucket is in …" | The region is not the bucket's. |
| 404 `NoSuchBucket` | The bucket's name, or path-style, is wrong. |
| 400 `InvalidRequest` about Object Lock | The bucket was made without Object Lock: make a new one with it, or lock the bucket another way and set `retainDays` to 0. |
| "does not match the backup" | The copy read back from the store differs from what was sent. HC360 uploads it again, and after three such copies stops and says so: check that the bucket encrypts with SSE-S3, not KMS keys. |
| "no longer matches the SHA-256" | The backup file on the server's disk changed after it was checked: the disk may be failing. It is not uploaded; check the disk, and take a backup. |

**Restoring from the off-site copy.** When the server and the second disk are both gone:

1. On the new machine, with the hospital's own credentials for the store (not HC360's key), download the backup the
   Backups list named, or the newest one, from the store's console or with the AWS CLI:

   ```powershell
   aws s3 cp s3://nana-hc360-backups/hc360/hc360-<date>-scheduled.hc360bak D:\Restore\
   ```

   The object's metadata names the backup's SHA-256 (`x-amz-meta-hc360-sha256`) and the recovery key that opens it
   (`x-amz-meta-hc360-recovery-key`).
2. Check it without restoring it: `backup verify --in D:\Restore\hc360-<date>-scheduled.hc360bak`, as above.
3. Restore it with `.\Restore-HC360.ps1 -BackupFile D:\Restore\hc360-<date>-scheduled.hc360bak`, as above. No other tool
   is needed.

### A second copy the server cannot delete

The off-site bucket's copies cannot be deleted by the server. The copy on the NAS can be made the same way, so the
server, and ransomware on it, can add copies there but not remove or change them:

- Give the server's computer account (`HOSPITAL\HC360-SRV$`) permission to create and write files in the share, but not
  *Delete*, *Delete subfolders and files*, *Change permissions* or *Take ownership*. HC360 writes each copy once, under a
  new name, and never changes it.
- Better still, make the share a WORM folder (write once, read many: Synology's WriteOnce, QNAP's WORM), which keeps
  each file unchangeable for the days set, or take immutable snapshots of it every day. Then a copy cannot be
  overwritten either.
- Let the NAS's own retention remove the copies after 30 days or more. HC360 cannot: each backup's note then says it was
  copied and that the older copies there were not removed. That note is not a failed copy, and raises no warning.
- Keep the NAS's administrator account off the Server Node.

## 9. Warm standby and failover (optional)

A second Server Node can keep a copy of the database that follows every change within a second, through PostgreSQL
streaming replication: a **warm standby** (SRS DED-20). When the primary Server Node fails for good, an administrator
**fails over**: promotes the standby, puts the keys back and starts HC360 there, in about half an hour. Only the changes
the standby had not received yet are lost, normally under a second's worth. Without a standby, recovery is a restore of
the last nightly backup on new hardware (section 8): up to a day of data lost, and hours of work (SRS REL-02). WAL
archiving (below), which needs no standby, brings the data lost down to minutes: HC360 copies the database's changes,
encrypted, to the second disk or NAS every minute.

What the standby has, and what comes from elsewhere on a failover:

| | On the standby | On a failover |
|---|---|---|
| The database | Streamed as it changes, over TLS | Promoted, as it is |
| The hospital CA | Imported once, at set-up | Workstations trust the standby, and find it on the network |
| The data encryption keys, stored documents (`files\`) | Not streamed: they are not in the database | Put back from the latest backup |
| Settings added to `hc360.settings.json` by hand | Not copied | Add them on the standby too, at set-up |
| The DSC trust folder (`certs\dsc-trust`: the CCA India roots, section 4 step 8) | Not copied: it is not in the database or the backups | Copy the roots there at set-up (step 6), so they are already there |

Documents uploaded after the latest backup are only on the old primary: copy them from its disk if it can still be read
(below). After rotating the data encryption key, make a backup at once (Administration › Backups › **Back up now**): the
standby can read data encrypted with a key only once a backup has carried it there.

**You need** a second machine like the first (section 1: Windows with BitLocker, a UPS, the same PostgreSQL version) on
the same network, with a DHCP reservation; a password for the replication account, 16 characters or more, kept in the
hospital's password safe with the `postgres` one; and a USB drive to carry two files between the machines.

**What is opened.** PostgreSQL on the primary takes one more sign-in from the network: the account `hc360_replicator`,
from the standby's address only, over TLS, with its password. The account can only stream the database; it cannot open
it. Windows Firewall opens PostgreSQL's port to the standby's address only. The standby's PostgreSQL listens on its own
machine only. The standby holds every patient record: protect it as the primary is protected.

### Setting up the standby

1. On the standby, install PostgreSQL with the primary's version (the `-Version` it was installed with):

   ```powershell
   .\Install-PostgreSQL.ps1 -Version 17 -SuperuserPassword (Read-Host -AsSecureString 'New postgres password')
   ```

2. On the primary, let the standby in, with the standby's address:

   ```powershell
   .\Enable-HC360Replication.ps1 -StandbyAddress 10.0.5.12
   ```

   It asks for the `postgres` password and, twice, a new password for the replication account. It creates the
   account; a replication slot through which the standby streams, so the primary keeps the changes the standby has
   not received while it is away (up to 20 GB, `-StandbyWalLimitGB`); TLS for PostgreSQL with a certificate of its own
   (`hc360-server.crt` in PostgreSQL's data folder; its key is readable only by PostgreSQL's service account and
   Administrators); the rules in `pg_hba.conf`, between `# BEGIN HC360 replication` and `# END HC360 replication`, which
   take the account from the standby's address over TLS for replication and refuse it every database; and the firewall
   rule. PostgreSQL restarts once, and HC360 is unavailable for those seconds. It prints the
   certificate's fingerprint. Run it again whenever you like: it writes the same settings.

3. On the primary, export the hospital CA, which HC360 Desktop and the lab agents trust:

   ```powershell
   & "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe" certificates export-ca --out E:\hc360-hospital-ca.pfx
   ```

   It asks, twice, for a password of at least 16 characters, which encrypts the file. With the file and its password,
   anyone can make certificates the workstations trust: keep both as safely as the backup recovery key, and apart.

4. Copy `hc360-server.crt` from PostgreSQL's data folder on the primary (for example
   `C:\Program Files\PostgreSQL\17\data`) and the `.pfx` file to the USB drive. The certificate holds no secret.

5. On the standby, make its PostgreSQL a copy of the primary's:

   ```powershell
   .\New-HC360Standby.ps1 -PrimaryAddress 10.0.5.11 -PrimaryCertificate E:\hc360-server.crt
   ```

   It asks for the replication password and the primary's `postgres` password (the copy has the primary's accounts).
   Compare the certificate fingerprint it shows with the one the primary printed. It checks the password, the
   certificate, the PostgreSQL version and the slot before it changes anything. Then it moves this machine's (empty)
   database aside, to `data.before-standby-<time>` beside it, copies the primary's with `pg_basebackup` over TLS,
   trusting the primary's certificate and no other, and starts it as a standby. The standby keeps the replication
   password in `hc360-standby\replication.pgpass` beside its data folder, readable only by PostgreSQL's service account
   and Administrators: it needs it to reconnect after a restart. If a step fails before the copy runs as a standby, the
   old data folder is put back and PostgreSQL started on it again.

6. On the standby, install HC360 with its service disabled, and the hospital CA:

   ```powershell
   .\Install-HC360Server.ps1 -Standby -HospitalCaFile E:\hc360-hospital-ca.pfx -FacilityCode NH -FacilityName 'Nana Hospitals' `
       -SecondaryBackupDir '\\NAS\HC360-Backups'
   ```

   It asks for the CA file's password. It does everything section 3 lists except start HC360: the service is disabled,
   because HC360 must never run against the standby's read-only database (HC360 refuses to, too). It prints the
   hospital CA fingerprint: the primary's. Add any setting added by hand to the primary's `hc360.settings.json`. Where
   doctors sign prescriptions with their DSC, copy the primary's DSC trust folder
   (`%ProgramData%\HyFib\HealthCare360\certs\dsc-trust`, the CCA India roots) to the same place on the standby, and
   again whenever IT adds a root on the primary.

7. Delete the `.pfx` file from the USB drive, or keep it in the safe with the recovery key: replacement hardware that
   imports it keeps the workstations too (section 8).

8. Check: Administration › System health on the primary shows no warning about the standby. On the primary, as
   `postgres`, `SELECT client_addr, state, replay_lag FROM pg_stat_replication;` shows the standby `streaming`.

**Upgrades.** Upgrade the standby whenever the primary is upgraded (section 7), with the same package:
`.\Install-HC360Server.ps1 -Standby`. The database's own changes reach the standby through replication. After upgrading
PostgreSQL itself to a new version, set the standby up again (steps 1, 2, 5).

**System health on the primary** says, in its warnings:
- *The warm standby … is not connected*: it is off, its PostgreSQL is stopped, or the network between them is down. The
  primary keeps the changes it needs, up to `-StandbyWalLimitGB`, and it catches up once it is back.
- *… is … MB behind*: connected, but more than 1 GB behind. Check the network between the two.
- *… is about to remove WAL it still needs* or *… cannot catch up* (critical): it has been away too long. Set it up
  again with `New-HC360Standby.ps1` (step 5); it replaces the copy.
- *WAL archiving is failing* (critical), with WAL archiving on (below).

HC360 reads the slot with its own database account, which every account may read. How far behind the standby is in
time (`replay_lag` above) is visible only to `postgres` and the `pg_read_all_stats` role, which HC360's account is not
given.

To stop streaming to the standby: `.\Enable-HC360Replication.ps1 -StandbyAddress 10.0.5.12 -Remove` on the primary. It
removes the slot, the account, the `pg_hba.conf` rule, the firewall rule, the address it listened on, and TLS. WAL
archiving (below) stays, as it needs no standby: add `-NoWalArchive` to end it too.

### WAL archiving: a restore to any moment

Optional, and off by default, and it needs no standby. With it, PostgreSQL copies each finished WAL file (its record of
every change) to a folder on the server's own disk, at least every 15 minutes; HC360 encrypts each one within a minute
and copies it, encrypted, to the second disk or NAS (`-SecondaryBackupDir`); and HC360 takes a **base backup**, a copy of
PostgreSQL's whole data folder, at once and then every Sunday at 03:30. From the newest base backup and the WAL archived
since, the database can be restored to any moment: a mistake undone to the minute before it, not to last night's backup,
and after a disk failure, to within minutes of it (SRS REL-02). On the Server Node, or on the primary:

```powershell
.\Enable-HC360Replication.ps1 -WalArchive
```

It asks for the `postgres` password, and PostgreSQL restarts once (HC360 is unavailable for those seconds). It:
- archives to `%ProgramData%\HyFib\HealthCare360\wal-archive` (`-WalArchiveDirectory` names another folder on the
  server's own disk; a share is refused), which only PostgreSQL's and HC360's service accounts, Administrators and SYSTEM
  can open: the WAL holds patient data. It archives one file at once to check;
- creates the account `hc360_basebackup`, which may only stream the database and opens none of it, and which signs in
  only as the HC360 service's Windows account, from this machine, with no password (SSPI): one more block in
  `pg_hba.conf` and one in `pg_ident.conf`, each between `# BEGIN HC360 replication` and `# END HC360 replication`.

With a standby too, give both: `.\Enable-HC360Replication.ps1 -StandbyAddress 10.0.5.12 -WalArchive`. Run it again
whenever you like: it leaves archiving on. `-NoWalArchive` turns it off and removes the account; the base backups made
stay. System health warns while archiving fails (*WAL archiving is failing*, critical): PostgreSQL then keeps every WAL
file until it is archived, and its disk fills. Each file never replaces one already archived; a file with the same name
and other content fails, and PostgreSQL's log says so.

**The WAL is encrypted before it leaves the server.** Every minute HC360 encrypts each file PostgreSQL archived, with the
backup recovery key current at the time, as the nightly backups are, into
`%ProgramData%\HyFib\HealthCare360\backups\wal\<system identifier>\`, checks that the encrypted file opens to the
same bytes, and only then removes the plain one: the archive folder holds a minute's WAL at most. It copies the
encrypted WAL, and the base backups, to the second disk or NAS, in `wal\<system identifier>` and
`base\<system identifier>` beside the nightly backups' copies, so the disk taken off site holds nothing unencrypted. The
standby does not read the archive (`New-HC360Standby.ps1` sets no `restore_command`): its slot keeps what it needs.
System health says, critically, when archived WAL has waited over 15 minutes unencrypted on the server's own disk
(`pitr.wal_not_encrypted`, with the reason), and when PostgreSQL archived a file again with other content than the copy
already encrypted (`pitr.wal_conflict`: both are kept, the new one unencrypted in the archive folder, for HyFib support
to look at); and it warns while the copies to the second disk or NAS fail (`pitr.wal_not_copied`), critically after three
and a half days, as for the nightly backups' copies.

**An archive set up before HC360 encrypted it** (`-WalArchiveDirectory D:\HC360-WAL` on the second disk, or a share)
holds plain WAL. Run `.\Enable-HC360Replication.ps1 -WalArchive` again on the primary: archiving moves to the server's
own disk. Once System health shows no `pitr.no_base_backup`, delete the old archive folder and any base backup made by
hand (such as `D:\HC360-Base`): they are not encrypted.

**Base backups.** HC360 makes each one with `pg_basebackup`, signed in as `hc360_basebackup`, encrypts it with the
backup recovery key current at the time, as the nightly backups are, reads it back to check it, and keeps it in
`%ProgramData%\HyFib\HealthCare360\backups\base\<system identifier>\` for as long as nightly backups are kept (30
days), and the newest always. The plain copy `pg_basebackup` writes stays on the server's own disk, in HC360's `temp`
folder, only until it is encrypted. A file's name gives its time and the WAL file it starts from:
`hc360-base-20261115-220000-000000010000000000000042.hc360base`. System health warns, and the administrators are told in
the app, when one fails (`pitr.base_backup_failed`; HC360 tries again after 6 hours); while there is none of the
database's current timeline (`pitr.no_base_backup`: just after archiving is turned on, and after a failover), until
HC360 has taken one; and when the weekly one has not been made for over a week (`pitr.base_backup_stale`).

**Space.** PostgreSQL closes a 16 MB WAL file at least every 15 minutes while HC360 runs; HC360 compresses it before it
encrypts it, so a quiet quarter of an hour takes well under 1 MB, and a busy one about what the hospital changed. A base
backup is about the size of the database. HC360 removes the encrypted WAL older than the WAL the oldest base backup kept
starts from, on the server and on the second disk or NAS (a share that does not let the server delete keeps them for its
own retention). Watch the folders for a week.

**A restore to a point in time** starts from a base backup, not from the nightly `.hc360bak` backup (which holds a dump
of the database), and needs the same PostgreSQL version. To restore to 10:45 today, for example:

1. Stop `HC360Server` and PostgreSQL. Move PostgreSQL's data folder aside.
2. Decrypt the newest base backup made before 10:45 into a new folder on the server's own disk, never the disk taken
   off site. The files are in `%ProgramData%\HyFib\HealthCare360\backups`; on new hardware, after a disk failure, use
   the copies on the second disk or NAS, in the same folders:

   ```powershell
   $hc360 = "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe"
   & $hc360 backup decrypt-base --in "<backups>\base\<system identifier>\hc360-base-<time>-<WAL file>.hc360base" --out C:\HC360-Restore\base
   ```

   It asks for the recovery key that was current when the base backup was made, as a nightly backup of that day needs,
   checks the whole file, and writes `base.tar.gz`, `pg_wal.tar.gz` and `backup_manifest`.
3. Decrypt the WAL archived since, from the folder of the same system identifier:

   ```powershell
   & $hc360 backup decrypt-wal --in "<backups>\wal\<system identifier>" --out C:\HC360-Restore\wal
   ```

   It checks each file as it writes it. Files encrypted with another recovery key (the key was changed since the base
   backup) are named by that key's ID and left: run it again with that key, into the same folder.
4. Make a new data folder, and extract the base backup into it: `tar -xzf C:\HC360-Restore\base\base.tar.gz -C <data
   folder>`, then `tar -xzf C:\HC360-Restore\base\pg_wal.tar.gz -C <data folder>\pg_wal`. Give it the permissions the
   old one had (PostgreSQL's service account, Administrators and SYSTEM).
5. Add to its `postgresql.auto.conf`:

   ```
   restore_command = 'copy "C:\\HC360-Restore\\wal\\%f" "%p"'
   recovery_target_time = '2026-10-12 10:45:00+05:30'
   recovery_target_action = 'promote'
   ```

   and create an empty file `recovery.signal` in it.
6. Start PostgreSQL. It replays the archived changes up to 10:45, then opens, on a new timeline. Its log says when it
   got there.
7. Take the three lines out of `postgresql.auto.conf` again, delete `C:\HC360-Restore` (it holds the database in plain),
   and start HC360. It takes a base backup of the new timeline within a quarter of an hour. On other hardware, put the
   keys and documents back first, as for a failover (step 3 below), and set the archive up there with
   `.\Enable-HC360Replication.ps1 -WalArchive`.

### Failing over to the standby

Fail over when the primary cannot be back soon enough: a failed disk or board, a machine that does not start, or
anything that keeps it down for more than about an hour. Not for a restart or a short outage: HC360 Desktop waits and
reconnects by itself. It needs an administrator, the recovery key of the latest backup, and the primary's `postgres`
password. On the standby, in an elevated PowerShell in the `deploy` folder:

1. **Fence the old primary**, so that it cannot come back as a second server: switch it off, or unplug its network.
   Workstations trust any server with the hospital CA, so an old primary that came back with HC360 running would take
   patient data the new one never sees. Before it is switched on again, unplug its network, and disable both its
   services first (`Set-Service HC360Server -StartupType Disabled`, and the same for its PostgreSQL service, for
   example `postgresql-x64-17`).
2. **Promote the standby's PostgreSQL**:

   ```powershell
   & 'C:\Program Files\PostgreSQL\17\bin\psql.exe' -h localhost -U postgres -d postgres -c 'SELECT pg_promote()'
   ```

   With the primary's `postgres` password. It answers `t`: PostgreSQL takes writes now, and `SELECT
   pg_is_in_recovery()` answers `f`. (`pg_ctl promote -D <data folder>` does the same.)
3. **Put the keys and documents back** from the latest backup, on the NAS or wherever it is kept:

   ```powershell
   & "$env:ProgramFiles\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe" backup restore --in '\\NAS\HC360-Backups\hc360-<latest>.hc360bak'
   ```

   It asks for the recovery key that opens that backup. It installs the data encryption keys, protected for this
   machine, and the stored documents, and leaves the database as it is: the standby's is newer than the backup's. Do not
   run `Restore-HC360.ps1`, which would replace it with the backup's. If the old primary's disk can still be read, copy
   its `%ProgramData%\HyFib\HealthCare360\files` folder over the standby's too, keeping files already there: it has the
   documents uploaded since the backup.
4. **Start HC360**: `.\Install-HC360Server.ps1`, without `-Standby`, from the same package. It enables and starts the
   service, checks that HC360 is ready, and prints the hospital CA fingerprint, the same as before. It refuses until
   step 3 is done.
5. **Workstations** follow by themselves: when HC360 Desktop cannot reach the old primary, it looks on the network for
   a server announcing the same CA, connects to the standby and says so from the tray. Staff sign in again. Where
   multicast does not pass between access points, or IT fixed a workstation's server (`-ServerUrl`, the machine policy),
   point the old primary's name at the standby (a DNS record, or the old primary's DHCP reservation moved to the
   standby), and add the old name to the standby's `HealthCare360:Network:AdditionalHostNames`, so that its
   certificate names it. Lab agents, network analysers and modalities are set up with the server's name or address:
   point it at the standby the same way, or set them up again with the standby's. Without the CA imported at set-up
   (step 6), every workstation must be set up again: Change server…, and compare the new fingerprint.
6. **Check**: System health; and that the records made just before the failure are there. Where the database has two
   accounts (section 3, "Two database accounts"), the standby's settings need the primary's
   `ConnectionStrings:HospitalOwner`: HC360 does not start without it, and its log says so. Where doctors sign
   prescriptions with their DSC, System health warns (`prescriptions.dsc_trust_empty`) if the standby's DSC trust
   folder is empty: copy the CCA India roots there (section 4, step 9).

### Making the old primary the new standby

Once the old primary is repaired, and still fenced:

1. On the new primary (the promoted standby), let the old one in: `.\Enable-HC360Replication.ps1 -StandbyAddress
   10.0.5.11`, with the replication password (or a new one). Copy its `hc360-server.crt`.
2. On the old primary, with its network back, start its PostgreSQL again (HC360 stays disabled):
   `Set-Service postgresql-x64-17 -StartupType Automatic; Start-Service postgresql-x64-17`. Then
   `.\New-HC360Standby.ps1 -PrimaryAddress 10.0.5.12 -PrimaryCertificate E:\hc360-server.crt -ReplaceExistingData`. Its
   own database, which holds data, is kept aside, not deleted.
3. `.\Install-HC360Server.ps1 -Standby` on it, which keeps its service disabled. It has the CA already.

To make it the primary again, fail over once more, at a quiet hour.

### A failover drill every quarter

A planned failover, at a quiet hour, proves the steps and that the people on call can do them:

1. Tell users: HC360 is unavailable for about half an hour.
2. Check that System health on the primary has no warning about the standby. Make a backup (Back up now).
3. Stop `HC360Server` on the primary. On the primary, `SELECT pg_current_wal_lsn()`; on the standby,
   `SELECT pg_last_wal_replay_lsn()`: when they match, the standby has everything.
4. Fail over (steps 1 to 6 above), and check that a workstation reconnects and that a record made just before is
   there.
5. Make the old primary the new standby (above).
6. Write down how long each step took and what was unclear, and fix this section.

## 10. Several servers behind a reverse proxy (optional)

One Server Node serves about 250 concurrent users (section 1). Beyond that, or to keep HC360 answering while one machine
is serviced, HC360 runs as **several API nodes behind a reverse proxy and a node that runs the background jobs**, all
on one PostgreSQL database (SRS SCAL-01, SCAL-02, SCAL-05). It is the same program on every machine; the setting
`HealthCare360:Role` says what each one does. More than one node may be able to run the jobs: they take turns through
the **job lease**, so exactly one runs them at a time and another takes over if it goes ("The job lease", below).

```
  Workstations, tablets, the portal ── HTTPS 7360 / 7361 ──► Reverse proxy (IIS ARR, YARP or nginx)
                                                                 │ sticky for /hubs/, health probe /health/ready
                                     ┌───────────────────────────┼───────────────────────────┐
                                     ▼                           ▼                           ▼
                              API node 1 (api)            API node 2 (api)      …     Job node (worker)
                                     │  every node: the database, the shared folder, Redis  │ analysers, modalities,
                                     ▼                                                       │ backups, the outbox
        PostgreSQL (one primary) ── optional reporting replica (ConnectionStrings:Analytics, SCAL-03)
        File share \\hc360-files\hc360: the keys and the stored documents
        Redis: real-time updates between the nodes
```

**What the nodes share, and what each keeps.**

| | Where |
|---|---|
| The database | One PostgreSQL primary, in every node's `ConnectionStrings:Hospital`. Nothing about a user, a sign-in or a request is kept in one node's memory only: sessions, sign-in challenges, one-time codes, emergency access to a restricted record, the attempts counted against the sign-in limits, and the portal's proof of work (challenges spent, failures counted) are all in the database |
| The keys and the stored documents | The shared folder (`HealthCare360:Cluster:SharedDirectory`). The keys there are protected with a certificate every node holds (`KeyCertificateThumbprint`), because machine DPAPI, which protects them on a single Server Node, opens only on the machine that wrote them. The keys are never in the database: a copy of the database (a backup, the reporting replica) opens no encrypted value without the shared folder too |
| Real-time updates (queue boards, bed board, critical alerts) | Redis (`ConnectionStrings:Redis`), through which an update raised on any node reaches the screens connected to every node, its own included. While Redis is away no screen gets updates; once it is back, every screen is told to catch up, and does: it subscribes again to what it opened meanwhile, and refetches what it shows |
| The request limits (public pages, partners, devices, HC360 Desktop's update, requests per user) | Redis as well, so a client's budget is the hospital's whichever node answers. While Redis is away each node counts them on its own (below). The limits on sign-in and one-time codes are in the database |
| Which node runs the background jobs | The database: the node holding the job lease, a PostgreSQL advisory lock on a connection of its own (below) |
| Permissions, sessions and settings held in memory for speed | Each node, for a moment. A change on one node expires them on every node at once, through PostgreSQL's LISTEN/NOTIFY on the database they share. A node that stops hearing the others reads them from the database every time until it hears them again |
| Analyser and modality worklist status (Lab setup › Analysers, Administration › Integrations and its tests) | The database: each node writes its part, the job node's network analysers and worklist server and the relays and heartbeats of the lab agents on the API nodes they reach, within seconds of a change and every minute, and every node shows them together. The worklist's test on an API node (or a node standing by for the job lease) reports what the job node says of its worklist server, or that the job node is not reporting: only the job node can send the worklist server its test echo |
| System health (Administration › System health, and its summary on the home screen) | The database: each node writes what it reports of its own machine (the computer, its network, data drive, certificate and power, and the warnings about them) every minute, and System health on any node shows every node, the job node first, with when each last reported. A node that has not reported for three minutes is shown as such, and a warning says so, critically, so that IT's phone is told: for the job node, since nothing makes the backups, sends messages or listens for analysers and modalities while it is away, and for a node that takes requests, whose warning says which servers take them meanwhile, or that none does and workstations cannot reach HC360. A node that only stands by for the jobs is a plain warning. A node stopped on purpose removes itself, and pages nobody. A node that stopped without saying so stays, and its alert is told every day, until it reports again or someone who manages the server removes it (**Remove** on its card), once it was retired, renamed, or set up again elsewhere; it no longer drops off after a day. Once a node that paged IT reports again, everyone the alert reached is told so, with when it was not reporting and when it was again (section 4, "Alerts that reach someone"). A node is known by an id kept in its own data folder (`config\server-node-id.txt`) for its machine, so one that starts again, after a power cut too, takes its own place again; one set up again on the same machine in the same data folder, which makes a new id, removes its old place as it starts; and a machine cloned from a node's is a node of its own; another warning says when more than one node runs the background jobs. With the job lease, each node that may run the jobs is shown as holding it or standing by, read from the database's lock at the moment the page is built. The database, backups, messages, integrations and keys are the hospital's, and shown once |
| The network links (cables and WiFi) | Each node watches its own (DED-06), whatever its role and whether or not it holds the job lease: its card in System health shows them, and its alerts name it, in the SMS ("HC360 server alert (HC360-API2): …"), the in-app notification's title and the audit entry. The cable its requests come in on, lost, is critical, and the alert says the reverse proxy sends requests to the other servers until it is back. A node whose only cable is out cannot reach the database to say so: System health shows it as not reporting, critically, so IT's phone is told within about eight minutes (three to count as silent, and System health's alerts run every five). Once it is back, its own SMS says when the cable was lost and when it was back, and within five minutes System health's alerts tell everyone they paged that it reports again. Each node's WiFi history in System health is its own |
| Backups | One folder every node names in `HealthCare360:Backup:Directory` (a second share, such as `\\hc360-files\hc360-backups`). The job node makes the scheduled ones; **Back up now** runs on the API node that answers it, so it must write to the same folder, and every API node needs PostgreSQL's client tools (`pg_dump`, `Backup:PgDumpPath`) as the job node does |
| The settings file, the TLS certificate, logs, temporary files | Each node's own data folder |

**You need:**
- Two or more API nodes and one job node, each as section 1 says, with HC360 installed by
  `.\Install-HC360Server.ps1 -PostgresHost <the database server> -DatabaseSignIn Password -NoStart` (`-CreateDatabase`
  on the first only) and, on all but the first, `-HospitalCaFile` with the CA exported from the first (`certificates
  export-ca`, section 9), so that every node's certificate comes from the one hospital CA. `-NoStart` leaves the service
  stopped, and not starting with Windows, until step 5: a node started before its settings name its role and the shared
  folder would run as a whole Server Node (`all`) against the hospital's database, running the background jobs beside
  the real job node and making data keys of its own that cannot read the hospital's data. The job node also listens for
  network analysers and modalities: give those its address.
- PostgreSQL on a machine of its own, reachable from every node, which then signs in with a password (section 3, "How
  the server signs in to the database"). If the nodes reach it through PgBouncer in transaction pooling, also set
  `ConnectionStrings:Signals` to PostgreSQL itself: PgBouncer in that mode does not pass on the notifications that keep
  the nodes' caches in step, and a node that cannot hear them reads everything from the database (it says so in its
  log).
- A **file share** for the keys and documents, on a server that is backed up and kept as secure as the Server Node.
  Give each node's computer account (`HOSPITAL\HC360-API1$`, …) *Modify* on it, and nobody else but Administrators: the
  service account, `NT SERVICE\HC360Server`, reaches the network as its computer.
- A **certificate with an RSA private key** for the keys in the shared folder, the same one in the Local Computer's
  Personal store of every node. For example, on the first node:

  ```powershell
  $cert = New-SelfSignedCertificate -Subject 'CN=HC360 cluster keys' -KeyAlgorithm RSA -KeyLength 3072 `
      -KeyExportPolicy Exportable -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(20)
  Export-PfxCertificate -Cert $cert -FilePath E:\hc360-cluster-keys.pfx -Password (Read-Host -AsSecureString 'PFX password')
  $cert.Thumbprint
  ```

  Import the PFX on every other node (`Import-PfxCertificate -CertStoreLocation Cert:\LocalMachine\My`), and on each
  node give `NT SERVICE\HC360Server` *Read* on its private key (certlm.msc › the certificate › All Tasks › Manage Private
  Keys). Keep the PFX and its password in the hospital's password safe, offline, with the backup recovery key: without
  the certificate no node can open the keys, so give it a long life, as above.
- **Redis** 6 or later (or Memurai on Windows), reachable only from the nodes, with a password. Without it every node
  still works, but a screen sees only the updates raised on the node it is connected to, and each node counts the
  request limits other than sign-in's on its own. Once it is set, real-time
  updates go through it alone: while it is away, no screen gets any, whichever node it is connected to, until it is back.
  Then every node tells the screens connected to it to catch up, and tells the other nodes to tell theirs: each screen
  subscribes again to what it opened meanwhile and refetches what it shows, so the updates missed meanwhile appear, and
  the later ones arrive, without anyone pressing anything.
- A **reverse proxy** in front of the API nodes, which:
  - listens on 7360 and 7361 and forwards each to the **same port** on the API nodes, over HTTPS (it trusts the hospital
    CA). HC360 tells the staff app from the patient portal by the port a request arrives on, so the two must not be
    mixed;
  - presents a certificate for its own name issued by the hospital CA, which HC360 Desktop pins (export the CA from the
    first node with `certificates export-ca`, as in section 9, and issue the proxy's certificate with it, for example
    with `New-SelfSignedCertificate -Signer`);
  - adds the client's address to `X-Forwarded-For` and sets `X-Forwarded-Proto`, and does not pass `X-Forwarded-Host`;
  - passes WebSockets, with an idle timeout of at least two minutes, and keeps a client on one node for `/hubs/`
    (session affinity, such as ARR's client affinity or YARP's session affinity): a real-time connection is negotiated
    by one request and opened by the next, which must reach the same node;
  - accepts request bodies of 30 MB (documents and images);
  - asks every node for `/health/ready` every 10 seconds, and takes a node out after three failures. A node answers 503
    when it cannot reach the database or the shared folder. It answers 200, *Degraded*, when only Redis is away: it
    still serves the API, though no screen gets real-time updates until Redis is back.

**Setting it up**, from a single Server Node (the steps are the same for a new hospital, without the move):

1. Make a backup (Administration › Backups › **Back up now**), then stop HC360 on the Server Node:
   `Stop-Service HC360Server`.
2. Copy the stored documents to the share, which moves nothing yet:
   `robocopy "$env:ProgramData\HyFib\HealthCare360\files" \\hc360-files\hc360\files /E /COPY:DAT`.
3. On every node, add to `config\hc360.settings.json` (section 3, "Changing a protected setting", for the connection
   strings):

   ```json
   "ConnectionStrings": {
     "Hospital": "Host=hc360-db;Database=hc360;Username=hc360_app;Password=…",
     "Redis": "hc360-redis:6379,password=…"
   },
   "HealthCare360": {
     "Role": "api",
     "Cluster": {
       "SharedDirectory": "\\\\hc360-files\\hc360",
       "KeyCertificateThumbprint": "3F9A…"
     },
     "Backup": { "Directory": "\\\\hc360-files\\hc360-backups" },
     "Network": { "KnownProxies": [ "10.0.0.5" ], "EnableMdns": false }
   }
   ```

   On the job node, `"Role": "worker"`, and no `KnownProxies` (nothing reaches it through the proxy). `KnownProxies` (or
   `KnownNetworks`, such as `10.0.0.0/29`) names the proxy's own addresses as the nodes see them: a request from there
   is taken to come from the client the proxy names, so sign-in limits, the audit trail and webhook and device address
   checks see the real client. A request from anywhere else that carries the headers is taken as it is: nobody can
   claim another address by sending them. mDNS is off because workstations connect to the proxy, not to a node.
   If workstations reach the proxy over internet (global) IPv6 addresses, also list the hospital's IPv6 networks in
   `LocalNetworks`, such as `"LocalNetworks": [ "2001:db8:12::/48" ]`. Sign-in and code limits count an IPv6 address
   from outside the hospital by its /64, which all the workstations on one subnet share; only addresses in a
   `LocalNetworks` network, the /64s of the node's own addresses, and link-local and unique local (`fd…`) addresses
   count one by one (SECURITY.md, "What counts as one address"). A single Server Node needs it only for workstations
   on other subnets than its own. An IPv4 client that reaches the nodes through NAT64 (`64:ff9b::` and its IPv4
   address) counts by that IPv4 address. Counting by the /64 keeps one machine from getting round the limits with a
   new address for each try; a client holding a /56 or /48 still gets a budget per /64, which is why the limits per
   account and per number and the portal's proof of work stay on as well.
4. Put the keys in the shared folder. On the first node, with the settings above in place:

   ```powershell
   & 'C:\Program Files\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe' backup restore --in '<the backup from step 1>'
   ```

   It asks for the backup's recovery key, puts back any document the share does not have, and installs the backup's
   data keys in the shared folder, protected with the certificate. It does not touch the database. A node that has keys
   of its own and finds none in the shared folder refuses to start and says to do this: it would otherwise make new
   keys there, and nothing encrypted before could be read.
5. Start the job node first, then the API nodes: on each, `.\Install-HC360Server.ps1` again from the same package,
   without `-NoStart` or `-CreateDatabase`. It has the service start with Windows, starts it and checks that it is
   ready. Each node refuses to start, saying why, if the certificate is missing or its private key cannot be read, or if
   a proxy address is not an address.
6. Point the workstations at the proxy: HC360 Desktop › Change server…, with the proxy's name, and compare the hospital
   CA fingerprint as at first set-up (section 5). Point `portal.baseUrl` at the proxy's portal address.
7. Check: `/health/ready` on each node answers 200; Administration › System health shows every node, the job node first, and no warning; a bed changed on a
   workstation appears on the bed board of another (connected, through the proxy, to another node). Then pull one API
   node's network cable for five minutes: within about eight minutes System health shows it as not reporting,
   critically, and the on-call phone gets the alert. Within five minutes of the cable going back in, the phone gets that
   it is reporting again, saying when it was not reporting and when it was again (and the node's own SMS of its cable).
   Stopping an API node's `HC360Server` service instead removes it from System health, and pages nobody.

**Running several nodes.**
- Exactly one node runs the jobs at a time. Either give one node `"Role": "worker"` (or `all`) and every other
  `"Role": "api"`, or give several `"Role": "worker"` or `all` and let them take turns through the job lease (below),
  which is on whenever the nodes share a folder. An API node runs no jobs, so it can be stopped or added at any time.
  System health warns when more than one node runs them without the lease: a node whose settings miss `"Role": "api"`
  runs as `all`, and with the lease off (`"JobLeadership": { "Enabled": false }`) it runs every job beside the others.
- A node that starts while the others work holds up none of their audit writes. It changes the audit trail's database
  triggers only if it finds one dropped, turned off or changed, and then waits at most two seconds for the audit trail.
  If something holds it longer (a long transaction writing to it, or, to put back the chain-link trigger, a report or
  backup reading it), the node changes nothing and stops, its log saying that an audit trail trigger could not be put
  back because the audit trail is in use. Let that finish or stop it; Windows starts the service again within a minute.
- **Upgrades:** stop every node. Upgrade and start one node that may run the jobs first: it backs up the database,
  applies the database changes and starts. Then upgrade and start the API nodes. An upgrade that partitions large tables (below) may take
  some minutes on the job node before it is ready. If it stops, its log saying that a table the update changes is in use
  ("canceling statement due to lock timeout"), something still uses those tables (a node left running, or a reporting
  tool with a transaction open): nothing has changed. Stop what uses them, then start the job node again. Windows starts
  the stopped service again within a minute, and each start backs the database up before it tries, so if that will take
  longer, stop the service meanwhile (`Stop-Service HC360Server`).
- **Rotating keys** (section 3) works on any node: the others use the new key within ten seconds, and accept tokens
  signed with it at once.
- **Restoring** a backup (section 8): stop every node, restore on the job node from the shared backup folder (its
  settings name the shared folder, so the documents and keys go there), then start the job node and the API nodes.
  System health shows each node as it starts: the backup holds none of the nodes it was made with (on new hardware,
  the old ones would show as nodes that stopped reporting), and `Restore-HC360.ps1` clears, and names, those an older
  backup holds. Check that every node shows: one that does not start is not shown.
- **Request limits.** The limits on sign-in and one-time codes per address (10 sign-in attempts a minute, 5 code requests
  in 15 minutes) are counted for the whole hospital, in the database, whichever node answers. The others (public pages
  and QR codes, partners' calls, devices, HC360 Desktop's update, and every request per user) are counted for the whole
  hospital too, on Redis: a sliding window for the per-minute limits, a token bucket for devices and users, each request
  one atomic script timed by Redis's own clock, so the nodes' clocks need not agree. Redis holds a keyed hash of each
  address, device or user, never the value. If Redis cannot be reached, or does not answer within half a second, each
  node counts those limits on its own, as a single Server Node does, and asks Redis again every 5 seconds: the limits
  still hold, but with *n* API nodes a client may make up to *n* times as many requests until Redis is back. System
  health says so on each node meanwhile ("Request limits have been counted on this server alone…").

#### The job lease

Any node with `"Role": "worker"` or `all` may run the background jobs: the backups, the outbox (SMS, WhatsApp, e-mail),
the analysers' and modalities' listeners, and every module's scheduled work. With several such nodes on one database,
they take turns through the **job lease**, a PostgreSQL advisory lock that the node holding it keeps on a connection of
its own:

- The node holding it says so to the database every 5 seconds. PostgreSQL ends that connection, and with it the lease,
  once it has been quiet for 20 seconds (`idle_session_timeout`): the node lost its power or its network, hangs, or
  stopped. The node itself stops its jobs as soon as a heartbeat fails, and at the latest 10 seconds after the last
  that succeeded, and then stops its service; Windows starts it again, and it stands by.
- Every other node asks for the lease every 5 seconds. The one that gets it waits 20 seconds more, so that the last
  holder has stopped its jobs even if the database let the lease go before it knew (PostgreSQL restarted, or its
  connection was ended by hand), and then starts them. Nothing else is needed: no table, no clock but each machine's own
  stopwatch, and the database alone says who holds the lease.
- So **two nodes never run the jobs at once**, and once the node running them has gone, another runs them within
  45 seconds (a node whose process ended, rather than its machine, within about 25). A node that takes over finds in the
  database what the last one did, as after a restart of the job node: each job keeps there what it has done (the
  ledger of charges posted, the messages sent, each report run), and the nightly backup claims its time once for the
  hospital (02:00 on the 9th runs once, whichever node fires it), so no job runs twice for the same time.
- The connection names the node (`HC360 jobs <node id>`): System health reads from PostgreSQL which node holds the lease,
  and shows it first, "holds the job lease", and the others "standing by". Between one holder and the next it warns that
  another is taking over; if that lasts more than a minute, the nodes standing by cannot reach the database, and their
  log says why.
- Point network analysers and modalities at a name or address that moves with the jobs (a DNS name with a short time
  to live, or a cluster address), or they keep calling the node that ran the jobs before. HC360 does not move an
  address itself.
- Like the cache signals, the lease needs PostgreSQL itself, not PgBouncer in transaction pooling: set
  `ConnectionStrings:Signals` to PostgreSQL if the nodes go through PgBouncer.
- The timings are settings (`HealthCare360:JobLeadership`: `Heartbeat`, `LeaseTimeout`, `CandidatePoll`), checked at
  start-up: the lease must last at least three heartbeats. `"Enabled": false` turns the lease off; each node that may run
  the jobs then runs them all the time, as before.

### Monthly partitions of the busiest tables

The audit trail, remote-monitoring readings, the message log (SMS, WhatsApp, e-mail) and the integration log grow
fastest. Each is split into one partition per calendar month (UTC) of the time a row is about, and a default partition
for times outside every month that has one (SRS SCAL-04). Queries for a period read only its months. The upgrade that
brings this in copies each table into its new form in one transaction, after the backup taken before every upgrade: it
takes a few minutes for a large audit trail, and if anything fails, the tables stay as they were.

The node that runs the jobs adds each table's partitions three months ahead, a minute after it starts and every six
hours. Adding one needs its table to itself for a moment: when something holds the table (a backup's pg_dump, a long
report), the job waits two seconds at most, so writes to the table are not held up, and leaves that table to its next
run, which is still months ahead. Rows that ever went to the default partition (a device whose clock was months out, or the job not running for
months) move into their month's partition when it is made: at the job's next run for a month gone by (as far back as
15 years; older rows stay where they are), and when it comes within the months kept ahead for one still to come. Change how far ahead with
`HealthCare360:Partitions:MonthsAhead` (1 to 24).

Moving a month's rows holds its table until they have moved, and writes to the table wait that long: some seconds for a
month of a million rows. The job moves one month at a time, oldest first, so writes wait for one month's rows at most,
and spends at most a minute on each table's months gone by each run; the months it has not reached wait for its next
run. It adds this month and the months ahead at every run all the same, so new rows do not join the backlog. A month
that cannot be made for any other reason (an error, or a statement that runs out of time) is logged and tried again at
the next run, and the job goes on with the other months and tables. The exception is a month gone by whose rows take
longer than a minute to move (the server's limit for one database statement): writes to its table wait that minute,
then the move is undone, and trying again at every run would hold them as long each time. So the job logs an error and
leaves that month's rows in the default partition, where they are kept and found as before; restart the `HC360Server`
service at a quiet hour to have it try once more.

**Partition upkeep never drops a partition.** How long each kind of record is kept is set in one place, Privacy ›
Retention, whose clean-up keeps every record for at least what the law asks (a year for the message and integration
logs), leaves medical records, remote-monitoring readings among them, to the medical records department's review instead
of deleting them (SRS PRV-02), and records every run in the audit trail. Because the logs are partitioned by the time it
deletes by, the clean-up reads only the months concerned. The audit trail's partitions refuse TRUNCATE and DELETE like the
table itself; the only ones ever dropped are whole months a purge removes, which two super administrators agree on once
they keep the audit trail for a period (8 years at the least), after it exports them to the audit archive (ADR-015).

## 11. Troubleshooting

| Symptom | Check |
|---|---|
| "Cannot reach the HealthCare360 server" on workstations | Server switched on? Its network cable in and its switch on, or its WiFi connected? System health on the server; client isolation on the AP. A workstation on a cable that is in says the server or the hospital network may be down: from it, `Test-NetConnection HC360-SRV -Port 7360`. After three tries the details line says HC360 is looking for the server at its other addresses; if the server's address changed and HC360 does not find it, use Change server… |
| "HC360 now connects to the server at …" (tray) | The server stopped answering at its usual address, and answered at this one. Usually its IP address changed: give the server a fixed address or a DHCP reservation. The desktop log (`%LocalAppData%\HyFib\HC360Desktop\logs`) has both addresses |
| Slow screens on one ward | Administration › Devices shows each workstation's WiFi signal (a workstation on a cable shows —); Administration › System health shows the server's link: on a cable, its speed (1 Gbps), else its WiFi signal. On a cabled ward, the ward's switch and its uplink |
| "Cable disconnected" (System health), or the on-call phone says the server lost its network cable | Which cable: the alert and System health name it, with the address it had. Is it in at both ends, and its switch on (on the UPS)? The port's lights show a link. If it comes back and drops again (**Last cable drop**), change the cable, then the switch port, then turn off Energy-Efficient Ethernet in the card's Advanced settings (section 1, Network checklist) |
| The on-call phone says the server's network cable is connected again, with no SMS of its loss before | The server reaches the SMS gateway over that cable alone, so the loss could not go while it was out: it was withdrawn (**Administration › Notifications** shows it suppressed), and the return says when the cable was lost and when it was back. If it happens again, see the row above |
| "Outbound messages are queued for more than 30 minutes (internet down?)" (System health), or SMS and WhatsApp arrive late | Is the internet up? Alerts in the app (a fridge out of range, a critical result) do not wait for it. A provider that could not be reached three times in a row waits 30 s, doubling to 5 minutes, before its next try, and the server log says so ("… could not be reached 3 or more times in a row"); once the internet is back, its messages go within 5 minutes. System health counts the messages that failed or expired unsent in the last day |
| "… outgoing messages queued before the restore at … are held: check them, then release" (System health) | The messages queued before the restore wait; those queued since go as usual. Check the queued messages, then press **Release** on the Messages waiting to send card (section 8, "Messages held after a restore"), which also clears the restore's warning |
| Desktop setup finds no server | Multicast blocked. Type the server name (`HC360-SRV`) or use `-ServerUrl` |
| At busy times staff on some workstations are told to wait before signing in again | Do those workstations reach the server over internet (global) IPv6 addresses from another subnet than the server's? Sign-in limits count such an address by its /64, which the whole subnet shares. List the hospital's IPv6 networks in `HealthCare360:Network:LocalNetworks` in `hc360.settings.json` (for example `[ "2001:db8:12::/48" ]`) and restart the `HC360Server` service: addresses there count one by one, as on the server's own subnet |
| "The server could not be verified" | The server was reinstalled or replaced. Re-run setup (Change server…) and compare the fingerprint |
| "HC360 stopped: the server could not be verified" | Something answered for the server with a certificate that is not from the hospital CA; the screen shows its issuer. A proxy or security product that inspects HTTPS: exempt the HC360 server from it. The server was reinstalled with a new CA: re-run setup (Change server…) and compare the fingerprint. Also check the workstation's date and time |
| Service will not start | The newest `hc360-server-<date>.json` in `%ProgramData%\HyFib\HealthCare360\logs\` (see "Reading the server log"), then Windows Event Viewer › Application (source *HyFib HealthCare360*) |
| A restore says "This backup opens with recovery key …, not with the key entered" | Each backup opens with the key in use when it was made. Use the copy whose ID the message names; the Backups list names every backup's key |
| "The backup of … was not copied to …" or "No backup has been copied to … since …" (System health), or "Backup not copied" in the app | The message gives Windows' reason. "Could not find a part of the path": the USB disk is unplugged, or came back with another drive letter (give both disks the same one in Disk Management). "The network path was not found": the NAS is off or unreachable. "Access … denied": HC360 may not write there: on the server that is `NT SERVICE\HC360Server`, on a NAS share the server's computer account (`HOSPITAL\HC360-SRV$`). Fix it, then **Back up now** (section 8) |
| "Copied to the secondary directory; older copies there were not removed" in a backup's note | The share lets the server add copies but not delete them, as an append-only share should (section 8, "A second copy the server cannot delete"). Nothing failed; let the NAS's own retention remove the old copies |
| "No copy of the backups leaves the building", or "has not reached the off-site copy" (System health, `backup.offsite_stale`) | Set up the off-site copy, or read the store's answer in the warning against the table in section 8, "The off-site copy" |
| "Only … has shown they hold recovery key …" or "Nobody has typed in recovery key … since …" (System health) | A second administrator, or each holder at the quarterly check, types the key in under Backups › Recovery key, **I hold this key** (section 3, "A new backup recovery key") |
| A restore says "The recovery key has a mistake in it" | A character was copied or typed wrong. The key has no letters I, L, O or U; an I or L is read as 1, and an O as 0 |
| Service will not start, and the log says `SSPI authentication failed for user "hc360_app"` or `No password has been provided but the backend requires one` | PostgreSQL no longer has HC360's sign-in rules, for example after it was reinstalled or upgraded to a new version with a new data folder. Run `Install-HC360Server.ps1` again: it writes them again. To sign in with a password instead, run it with `-DatabaseSignIn Password` |
| The installer warns "The server did not reach the database with its Windows account" | It put everything back, and HC360 signs in with its password as before. The HC360 log in `logs\` and PostgreSQL's log (`log\` in its data folder) say why. Send both to HyFib support; until then HC360 works as it did |
| "The database could not be reached to confirm the change" after Change password | HC360 kept the old password. If it cannot sign in to the database once the database is back, set a new password for its account as `postgres` (`\password hc360_app` in psql), write the connection string with it in plain text in `hc360.settings.json`, run `settings protect` and restart the `HC360Server` service |
| `Install-HC360Server.ps1` stops at the start: "… hc360.settings.json is not valid JSON" | Nothing was changed, and HC360 keeps running. Correct the file where the message says (Windows PowerShell does not accept comments in it), then run the installer again |
| Service will not start: "The setting … cannot be opened on this machine" | The configuration was copied from another server, or the value was damaged. Write that setting's plain value in `hc360.settings.json` and run `settings protect` (see "Changing a protected setting") |
| "BitLocker is off on D:" or "BitLocker is not installed" (System health) | Turn on BitLocker, with the TPM, for that disk (Control Panel › BitLocker Drive Encryption). The warning clears after the next daily check, or run the *HC360 disk encryption check* task in Task Scheduler › HyFib |
| "BitLocker was last checked on …" or "has not been checked" (System health) | Is the *HC360 disk encryption check* task in Task Scheduler › HyFib, enabled, and does its last run show success? Run `Install-HC360Server.ps1` again to recreate it |
| "PostgreSQL stopped without being shut down" (System health, or Event Viewer › Application) | The watchdog started it again (see "When PostgreSQL stops"). PostgreSQL's log, in `log\` in its data folder, says why it stopped: often a full disk, or antivirus software that ended or locked `postgres.exe`. Exclude the PostgreSQL data folder and programs from scanning |
| "… the database watchdog could not start it again" (System health) | PostgreSQL's log says why it does not start. Once the cause is fixed, start the PostgreSQL service in Services; the watchdog keeps trying every quarter of an hour |
| "The database watchdog has not run since …" or "… watches the PostgreSQL service …, which is no longer on this server" (System health) | Is the *HC360 database watchdog* task in Task Scheduler › HyFib, enabled, and does its last run show success? After PostgreSQL was upgraded to a new version, run `Install-HC360Server.ps1` again: it watches the new service |
| A serial analyser shows "Waiting for agent" | Is the lab PC on and the `HC360LabAgent` service running? Administration › Devices shows when the agent last called in. Event Viewer › Application on the lab PC (source *HyFib HC360 Lab Agent*) says why it cannot connect |
| A serial analyser shows "COM port problem" | The message gives the Windows error. "Access to the port is denied": another program has the port open. Otherwise check the COM number in Device Manager on the lab PC |
| An analyser sends results but none arrive | Integration log (Administration › Integrations) for refused messages. Network analysers: is the analyser's IP the one in its connection, and the port open in the firewall? Serial: compare baud rate, parity and stop bits with the analyser's settings |
| "PDFs can't be made on this server" (System health), or a PDF download fails | Is Microsoft Edge installed for all users? Set `HealthCare360:Pdf:BrowserPath` if it is somewhere unusual. The log says why the browser did not start. Printing from the screens still works |
| "… is failing: the latest call failed" (System health) | Administration › Integrations shows the last error for that integration, and its log says more. Correct the settings or the provider's side, then *Send a test*: the warning clears once a call succeeds. An integration no longer used can be switched off instead |
| "The hospital CA expires on …" or "… expired" (System health) | The CA is made for ten years when the server first starts and is never made again by itself, because every workstation, tablet, HC360 Desktop and lab agent trusts it. System health warns a year ahead: prepare the next CA with `certificates prepare-ca`, and switch to it with `certificates switch-ca` once every device holds it, as [CA-ROLLOVER.md](CA-ROLLOVER.md) says. No device stops connecting, and none is set up again. A server whose CA has expired does not start, and says so in the Application event log: with the next CA prepared it says to switch to it, and every device that holds it trusts the server again; without one, a new CA means setting every device up again. "The server certificate expires on …" instead means the automatic renewal 30 days before expiry failed: the log says why |
| "The next hospital CA is prepared: … devices seen in the last 30 days do not hold it yet …" (System health) | It names them, and `certificates switch-ca` waits for them: after the switch they would no longer trust the server. **Without it**: the device has not reached the server since the next CA was prepared, or cannot ask it: check that it connects, and its log ("HC360 Desktop" or the lab agent's service log). **Not saying which CAs they hold**: an HC360 Desktop or lab agent older than this server, which does not take the next CA by itself: update it. Revoke a device that is gone for good (Administration › Devices). Revoked devices and those not seen for 30 days are left out. Critical in the current CA's last 90 days ([CA-ROLLOVER.md](CA-ROLLOVER.md#rolling-the-ca-over)) |
| "HC360 certificate probe, …: No TLS connection" (Application event log, Error) | The server's own daily probe could not reach the staff address: HC360 was down when it ran, or does not listen on that port. Check the service and System health; the probe runs again the next day, or run the task now |
| "HC360 certificate probe, …: The hospital CA was switched …" (Application event log, Error) | The server's own daily probe still checks for the CA the switch replaced. Give it the new fingerprint as step 4 of the day you switch says ([CA-ROLLOVER.md](CA-ROLLOVER.md#the-day-you-switch)), or run `Install-HC360Server.ps1` again: each install or upgrade gives it the server's CA |
| "The audit trail failed its check on …" (System health, critical) | A check of the audit chain, asked for under Administration › Audit log or made by the server once a day, found an entry changed, moved or removed in the database. Keep the database and its backups as they are, note the position the message names, and contact HyFib support. The warning clears when a later check passes |
| "System health: critical" notifications, or an SMS from HC360 to the IT on-call number | A critical System health warning appeared; the notification says which. Open Administration › System health for what to do. Each is told again every day until it is fixed, and at once if it clears and comes back. "System health: server reporting again" ends the alert of a server of several that had stopped reporting |
| A report e-mail arrives without its PDF | The notification log notes "Sent without the document" and why. The PDF was tried three times first |
| WhatsApp refuses the report message (error 132012) | The WhatsApp template has no document header but "Document header" is ticked in HC360, or the other way round. Make them match |
| A modality cannot read the worklist | Administration › Integrations › Modality worklist: is it listening, and what is the last error? The modality's AE title and IP address must match its entry exactly, and the firewall rule must list its address. Refusals are logged once per ten minutes per network, so wait or look at the refused count |
| An analyser or the modality worklist shows "Port … is used by the HC360 server itself" | It was given a port the server listens on, such as the patient portal's 7361 (see 7, Upgrades). Choose another port, in HC360 and on the analyser or the modalities, and open it in the firewall as described in 6 |
| "HC360 cannot write the extract folder" (Analytics › BI extracts), or an extract is "Not refreshing" | The message gives the Windows error. Does the folder set as `HealthCare360:Analytics:ExtractDirectory` in `hc360.settings.json` exist (a machine environment variable `HC360_HealthCare360__Analytics__ExtractDirectory` overrides it), and does `NT SERVICE\HC360Server` (or the server's computer account, for a share) have *Modify* on it? Is the disk full? A file the BI tool keeps open can also stop a refresh: close Power BI Desktop and press *Refresh now* |
| Images are not linked with PACS | Press "Link images from PACS" on the study: the message says why. "Under another patient ID", "without a patient ID" or "several studies" must be corrected in PACS; "not reached PACS yet" usually means the modality has not sent the images, or sent them without the worklist's accession number |
| "The warm standby (replication slot hc360_standby) is not connected" (System health) | Is the standby on, and its PostgreSQL service running? Its log (`log\` in its data folder) says why it does not connect: the primary's address changed, its certificate was made again (copy the new `hc360-server.crt` and run `New-HC360Standby.ps1` again), or the network. The primary keeps what the standby needs up to `-StandbyWalLimitGB` |
| `New-HC360Standby.ps1` stops: "The primary at … refused the connection" | The message above it says why. The password is the one given to `Enable-HC360Replication.ps1`; the certificate is the primary's current `hc360-server.crt`; the standby must connect from the address given to `-StandbyAddress`, and the primary's firewall must not be Public for that network. PostgreSQL's log on the primary names the address it refused |
| `Install-HC360Server.ps1` stops: "This machine's PostgreSQL is a warm standby" | Run it with `-Standby` on the standby. To make the standby the primary, fail over first (section 9) |
| `Install-HC360Server.ps1` stops: "HC360 was installed here as a warm standby … and has no data encryption key yet" | Put the keys back from the latest backup with `backup restore` (section 9, "Failing over to the standby", step 3), then run the installer again |
| HC360 does not start, and its log says "The database is a read-only warm standby" | HC360 was started on the standby. Leave its service disabled; to fail over, promote the standby first (section 9) |
| "Critical warnings do not reach IT on-call by phone" or "…may not arrive by SMS" (System health) | The on-call number, the SMS provider, or the `system.alert` template's DLT template id: the warning names which (section 4, "Alerts that reach someone"). Then run an alert drill |
| "The outside heartbeat to … has failed" (System health) | The server's internet access, the firewall towards the monitor's host, and `alerts.heartbeatUrl.secret`. The monitoring service raises its own alarm meanwhile |
| "This live server's database holds records the integrations' simulators made" (System health) | The database was used for training, or set up before HC360 refused the simulators on a live server (section 4, "Going live on a database used for training"). Administration › Simulator records lists each kind and what to do about it, and under "To correct" the payments, refunds, card sales, Aadhaar links, images and lab results not corrected yet. Correct those, deal with the rest, sweep again, then sign them off there |
| "… record(s) the simulators made are not corrected yet" or "The sweep of … found … record(s) not corrected" when signing off (Administration › Simulator records) | The sign-off waits for the records: each kind named still has records open, or the sweep being signed off counted some. Correct each under "To correct" as the table in section 4 says, press **Sweep now**, and sign off the new sweep |
| HC360 does not start, and its log says "HealthCare360:SeedDemoData is on, but this database holds patients" | `SeedDemoData` was turned on for the hospital's database. Demonstration data, its accounts and the simulators would mix into real records, so HC360 refuses. Remove `"SeedDemoData": true` from `config\hc360.settings.json` and start the service. A training or test server needs a database of its own, with demonstration data on before anyone is registered (docs/testing/LOAD-TESTS.md); one made before this check that holds patients needs a new database |
| A node of several does not start: "this server has a data encryption key of its own …, and the shared folder … has none" | The keys are not in the shared folder yet. Put them there with `backup restore` (section 10, "Setting it up", step 4) |
| A node of several does not start: "… names certificate …, which is not in the Local Computer's Personal store", or "… has no RSA private key this server may use" | Import the cluster key certificate on this node, with its private key, and give `NT SERVICE\HC360Server` *Read* on the key (section 10) |
| `/health/ready` on a node answers 503 (*Unhealthy*) | The node cannot reach the database, or, with several nodes, the shared folder; its log names the check that failed. For the share: is the file server up, and does the node's computer account still have *Modify* on it? |
| "Request limits have been counted on this server alone since …" (System health, with several nodes) | That node cannot reach Redis, or Redis does not answer within half a second: is Redis up, and does `ConnectionStrings:Redis` name it with its password? The node counts the limits on its own meanwhile, and asks Redis again every 5 seconds; the warning clears once Redis answers |
| "No server holds the job lease at this moment: … stand by" (System health, with several nodes) | One of them takes the jobs over within a minute. If it lasts, the nodes standing by cannot reach PostgreSQL itself (through PgBouncer in transaction pooling, set `ConnectionStrings:Signals` to PostgreSQL), or PostgreSQL is older than 14 (it has no `idle_session_timeout`); their log says why |
| "… hold the job lease, which only one server can" (System health, critical) | Should never happen: restart the `HC360Server` service on each of them but one, and send their logs to HyFib support |
| "This server may no longer hold the job lease …" (a node's log, critical), and the service stopped | It lost the database for a moment, or hung: it stopped its jobs before another node could take them over, and stopped. Windows starts it again within a minute, and it stands by |
| "… has not reported since …" or "No server that runs the background jobs is reporting" (System health, with several nodes) | Is that node on, and its `HC360Server` service running, and its network cable in? Its log says why it cannot reach the database, if it cannot; a node whose cable was out says so itself once it is back, in an SMS with when the cable was lost and when it was back, and System health's alerts tell everyone they paged that it reports again. A node stopped on purpose removes itself from System health; one that lost its power is shown as not reporting, and told every day, until it is back. A node set up again on the same machine in the same data folder takes its old place; one renamed, retired, or set up in another folder or on another machine is a new node: remove the old one with **Remove** on its card in System health (`admin.system.manage`), which acknowledges its alert. A restore on new hardware shows none of the old nodes: the backup holds none, and `Restore-HC360.ps1` clears those an older backup holds |
| The on-call phone says a server is reporting again, with no SMS before that it had stopped (with several nodes) | The alert that it had stopped had not gone yet, waiting to be tried again because the SMS gateway could not be reached: it was withdrawn, so that the two could not arrive the wrong way round, and the return says when the server was not reporting and when it was again. **Administration › Notifications** shows it suppressed, and why. If SMS fail often, see "Critical warnings do not reach IT on-call by phone" above |
| "… both run the background jobs, and only one server should" (System health, with several nodes) | A node meant as an API node runs as `all`: its `hc360.settings.json` misses `"Role": "api"` (section 10, step 3). Add it and restart its `HC360Server` service. Until then each of them makes the nightly backup and listens for analysers and modalities |
| A node's log: "Not listening for cache signals from the other servers" | It cannot hear the other nodes' changes, so it reads permissions, sessions and settings from the database every time (slower, still correct). Is PostgreSQL reachable? Behind PgBouncer in transaction pooling, set `ConnectionStrings:Signals` to PostgreSQL itself (section 10) |
| A screen misses updates made on another workstation | With several nodes: is Redis up? `/health/ready` says *Degraded* when a node cannot reach it, and the node's log says that until it is back no screen gets real-time updates, whichever node it is connected to; everything else works. Once Redis is back, each node tells the screens connected to it, and the other nodes theirs, to catch up: within seconds each subscribes again to what it opened meanwhile and refetches what it shows, by itself (the node's log says how many it told); a screen that lost its own connection reconnects and refetches as always |

Log files hold no patient-identifiable data by design. Share them with HyFib support when asked.
