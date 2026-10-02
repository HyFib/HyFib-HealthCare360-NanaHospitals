# Deploying HyFib HealthCare360 at a hospital

HC360 runs on **one dedicated Windows machine (the Server Node)** connected to the hospital WiFi. Everything else is a
client: **HC360 Desktop** on workstations, kiosks and waiting-area TVs, and a browser on tablets. Nothing needs the
internet to work; SMS/WhatsApp, payment gateways, ABDM and the AI assistant use it when available.

```
  Hospital WiFi (WPA2/WPA3-Enterprise, one SSID for clinical devices)
  ┌─────────────────────────────────────────────────────────────────────────────┐
  │  Server Node (Windows 11 Pro / Server 2022+)                                │
  │   ├─ HC360Server service  :7360 HTTPS (API + staff app + patient portal)   │
  │   ├─ PostgreSQL           localhost only                                    │
  │   ├─ Data  %ProgramData%\HyFib\HealthCare360 (keys, certs, documents, logs) │
  │   └─ UPS (USB)  ·  second disk / NAS for backups                            │
  │                                                                             │
  │  Workstations / kiosks / TVs: HC360 Desktop   Tablets: Edge or Chrome       │
  └─────────────────────────────────────────────────────────────────────────────┘
```

## 1. Before you start

**Server hardware** (up to about 250 concurrent users):
- 8-core CPU, 32 GB RAM.
- 1 TB NVMe SSD, plus a second disk or NAS share for backup copies.
- WiFi 6 adapter; an Ethernet cable to the access point is better still.
- A UPS with a USB connection, so HC360 can see power cuts (Administration › System health).

**Network checklist**:
- The server gets a **DHCP reservation** (fixed IP) and keeps a fixed computer name, e.g. `HC360-SRV`.
- **Client isolation must be off** on the clinical SSID. Many access points turn it on by default, and it blocks
  workstations from reaching the server.
- Multicast DNS (UDP 5353) should pass between access points so HC360 Desktop can find the server automatically.
  If it can't, staff type the server name once, or IT pre-configures it (step 5).
- The network profile on the server must be **Private**; the installer warns if it is Public.
- Guest / patient WiFi must not be able to reach the server.

**Microsoft Edge on the server.** Reports, prescriptions, discharge summaries, bills and MIS reports are made into PDFs
on the server by headless Microsoft Edge, so that the copy downloaded or sent to the patient is the same as the printed
one. Windows 10/11 and Windows Server 2022 come with Edge; keep it installed and updating. On Windows Server Core, or if
Edge was removed, install Edge (or Google Chrome) for all users, or set its path in the configuration
(`HealthCare360:Pdf:BrowserPath`). Without it, everything else works and Administration › System health says that PDFs
can't be made.

**You need**: `HC360-Server-<version>-win-x64.zip` and `HC360-Desktop-<version>-win-x64.zip` from the release. Check them
against `SHA256SUMS.txt` (`Get-FileHash <file> -Algorithm SHA256`).

## 2. Install PostgreSQL (once)

Open **PowerShell as Administrator** in the extracted `deploy` folder:

```powershell
.\Install-PostgreSQL.ps1 -SuperuserPassword (Read-Host -AsSecureString 'New postgres password')
```

This does four things:
- Installs PostgreSQL through winget.
- Restricts it to localhost, with scram-sha-256 authentication only.
- Sizes its memory settings from the machine's RAM.
- Sets the time zone to IST.

Keep the postgres password in the hospital's password safe.

## 3. Install the HC360 server

```powershell
.\Install-HC360Server.ps1 -CreateDatabase -FacilityCode NH -FacilityName 'Nana Hospitals' `
    -SecondaryBackupDir '\\NAS\HC360-Backups'
```

The installer:
- Creates a least-privilege database account with a random password.
- Writes the configuration, readable only by Administrators and the service.
- Registers the `HC360Server` Windows service, which restarts itself if it stops.
- Opens the firewall for Private networks only.
- Keeps the machine and its WiFi adapter from sleeping.
- Starts HC360 and checks that it is healthy.

At the end it prints two things. Write them down:
- the **hospital CA fingerprint**, which staff compare when setting up HC360 Desktop;
- the **first administrator password** (user `admin`).

## 4. First sign-in and security set-up

On the server open `https://localhost:7360/`. The browser warns once, because the hospital CA is private. Then:

1. Sign in as `admin` and choose a new password. Set up two-step verification; administrators must use it.
2. Delete `%ProgramData%\HyFib\HealthCare360\initial-admin-password.txt`.
3. **Administration › Backups › Recovery key**: write the key down or print it, and store it in the hospital safe.
   Without it, backups cannot be restored on new hardware.
4. **Administration › Users**: create staff accounts, giving each person only the roles their job needs.
5. **Administration › Settings**: set the SMS/WhatsApp/payment gateway keys (stored encrypted) and
   `alerts.oncallPhone` (IT's mobile, which gets an SMS if the server WiFi drops or a backup fails).
6. **Documents and the messages that carry them** (reports, prescriptions, bills):
   - **Administration › Hospital setup › Facilities and letterhead** holds what the letterhead prints: legal name,
     address, registration, NABH, GSTIN, PAN, a letterhead line, and the logo (shrunk on upload to fit the 300 KB a
     document embeds). *Preview letterhead* shows the result before saving. Doctors' signatures are uploaded in
     **Hospital setup › Doctors and clinical staff**. Documents print without them until they are set.
   - Set `portal.baseUrl` (the patient portal's public https address). Documents then carry a QR code that anyone can
     scan to check the report is genuine. Without it they print only the verification code.
   - E-mail (`integrations.email`): the "report ready" e-mail carries the report's PDF.
   - WhatsApp: to send the PDF on WhatsApp, register the `lab_report_ready` template in WhatsApp Manager **with a
     document header**. Then, in Administration › Notification templates, tick "Document header" on that template.
     Without the tick, the WhatsApp message is text only and the patient finds the report on the portal.
   - To stop sending report PDFs by e-mail and WhatsApp, set `diagnostics.reports.sendPdf` to `false`, and use a
     WhatsApp template without a document header (untick "Document header"). A template with a header can't go
     without its PDF, so those WhatsApp messages would be skipped; the SMS and e-mail still go.
7. **Administration › System health**: everything should be green, and the WiFi signal above 60%.

## 5. Install HC360 Desktop on workstations

Create one-time enrolment codes in **Administration › Devices › Enrol a device**, then on each workstation run
(Administrator PowerShell, from the extracted desktop package):

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

Without `-ServerUrl` / `-CaFingerprint`, it finds the server on the WiFi and shows the fingerprint for staff to compare.

**Tablets and browsers**:
1. Download `https://HC360-SRV:7360/api/v1/system/ca-certificate`.
2. Install it as a trusted root certificate on the device (MDM or device settings).
3. Bookmark `https://HC360-SRV:7360/`.

## 6. Printers and devices

- Receipt/token printers (80 mm), label printers (50×25 mm) and A4/A5 printers are ordinary Windows printers on each
  workstation. HC360 prints silently to the default printer, or to one chosen per workstation.
- Barcode scanners work as keyboards (no driver).
- Lab analysers, PACS, biometric attendance devices, Tally and payment terminals are configured in
  **Administration › Integrations**. See `docs/modules/integrations.md`.

## 7. Upgrades

1. Tell users: HC360 is unavailable for a few minutes.
2. Extract the new server package.
3. Run `.\Install-HC360Server.ps1` again, **without** `-CreateDatabase`.

The installer stops the service, replaces the program files, keeps configuration and data, and starts the new version.
Before the new version changes the database schema, it backs up the database and checks the backup (Administration ›
Backups lists it as "before an update"). If that backup fails, for example because the disk is full, HC360 does not
start and the database is not changed. The reason is in the log and in Windows Event Viewer.

A server first installed from a pre-release build, before database changes were versioned, is brought up to date
automatically on the first start of this version. The log lists what was added. If its database differs in a way that
cannot be fixed safely (for example a column of another type), HC360 stops with the list of differences and changes
nothing. Keep the previous version running and contact HyFib support.

HC360 Desktop is upgraded the same way, with `Install-HC360Desktop.ps1` on each workstation (or through your software
distribution tool).

## 8. Backups and disaster recovery

- An **encrypted** backup (AES-256-GCM) runs every night at 01:30 IST and is kept for 30 days. With
  `-SecondaryBackupDir`, each backup is also copied to the second disk or NAS.
- Check **Administration › Backups** weekly for green "Succeeded" rows. A failure also raises a System health warning
  and an SMS to `alerts.oncallPhone`.
- Each backup holds the database, the stored documents and the field encryption key, all encrypted. Every backup is
  read back and checked when it is made. The **Contents** and **Checked** columns show this.
- **Restore test every quarter** on a spare machine. To check a copy on the NAS or a USB drive without restoring it:

  ```powershell
  & 'C:\Program Files\HyFib\HealthCare360\Server\HyFib.HealthCare360.Server.exe' backup verify --in '\\NAS\HC360-Backups\hc360-<date>.hc360bak'
  ```

- To restore after a disk failure on new hardware:
  1. Install PostgreSQL and HC360 as above.
  2. Run:

     ```powershell
     .\Restore-HC360.ps1 -BackupFile '\\NAS\HC360-Backups\hc360-<date>.hc360bak'
     ```

     It asks for the recovery key and checks the whole backup before it changes anything. It restores the database first,
     keeping the current one under a new name (nothing is dropped). Only then does it put the documents back and install
     the backup's encryption key. If a step fails, the previous database is put back and HC360 starts again on it.
  3. Workstations see a new server certificate. Re-run HC360 Desktop setup (Change server…) and compare the
     fingerprint.

  Backups listed as "Database only (older backup)" were made before HC360 archive backups. To restore one of those on
  new hardware, first copy the old `%ProgramData%\HyFib\HealthCare360\keys` folder back, if you have it.

## 9. Troubleshooting

| Symptom | Check |
|---|---|
| "Cannot reach the HealthCare360 server" on workstations | Server switched on? WiFi connected? System health on the server; client isolation on the AP |
| Slow screens on one ward | Administration › Devices shows each workstation's WiFi signal; Administration › System health shows the server's |
| Desktop setup finds no server | Multicast blocked. Type the server name (`HC360-SRV`) or use `-ServerUrl` |
| "The server could not be verified" | The server was reinstalled or replaced. Re-run setup (Change server…) and compare the fingerprint |
| Service will not start | `%ProgramData%\HyFib\HealthCare360\logs\`, then Windows Event Viewer › Application (source *HyFib HealthCare360*) |
| "PDFs can't be made on this server" (System health), or a PDF download fails | Is Microsoft Edge installed for all users? Set `HealthCare360:Pdf:BrowserPath` if it is somewhere unusual. The log says why the browser did not start. Printing from the screens still works |
| A report e-mail arrives without its PDF | The notification log notes "Sent without the document" and why. The PDF was tried three times first |
| WhatsApp refuses the report message (error 132012) | The WhatsApp template has no document header but "Document header" is ticked in HC360, or the other way round. Make them match |

Log files hold no patient-identifiable data by design. Share them with HyFib support when asked.
