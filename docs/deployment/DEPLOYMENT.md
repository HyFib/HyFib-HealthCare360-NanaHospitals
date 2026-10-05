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
  │  Lab PCs with serial analysers: HC360 lab agent (Windows service)           │
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

**You need**: `HC360-Server-<version>-win-x64.zip` and `HC360-Desktop-<version>-win-x64.zip` from the release, and
`HC360-LabAgent-<version>-win-x64.zip` if analysers are cabled to lab PCs by serial (RS-232) cable. Check them
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
- Barcode scanners work as keyboards (no driver).
- PACS, the modality worklist, biometric attendance devices, Tally and payment terminals are configured in
  **Administration › Integrations**. See `docs/modules/integrations.md`.
- Lab analysers and radiology: see below.

### Lab analysers

Switch the gateway on in **Administration › Integrations › Laboratory equipment**. Register each analyser in **Lab
setup › Analysers** (code, bench, protocol HL7 or ASTM; tick **Bidirectional** if it should ask HC360 for tests and be
sent orders), then set up its **Connection** there. An analyser connects in one of two ways.

**Network analysers** (Ethernet). The analyser connects to a port on the server, one port per analyser, for example
5100. In the Connection drawer choose Network, enter the port and the analyser's fixed IP address. Open those ports in
the server's firewall, limited to the analysers' addresses, by running the server installer again:

```powershell
.\Install-HC360Server.ps1 -LabPorts 5100,5200 -LabAnalyserAddresses 10.0.5.21,10.0.5.22
```

**Serial analysers** (RS-232). The analyser is cabled to a Windows 10/11 PC in the lab, which runs the **HC360 lab
agent**. The agent is a Windows service: it keeps relaying when nobody is signed in to the PC, and it starts with
Windows. It relays the COM port to the server over HTTPS (port 7360), so the lab PC needs no inbound firewall rule and
the server needs none beyond 7360. One agent can relay several analysers on different COM ports.

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
worklist (order download).

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
| A serial analyser shows "Waiting for agent" | Is the lab PC on and the `HC360LabAgent` service running? Administration › Devices shows when the agent last called in. Event Viewer › Application on the lab PC (source *HyFib HC360 Lab Agent*) says why it cannot connect |
| A serial analyser shows "COM port problem" | The message gives the Windows error. "Access to the port is denied": another program has the port open. Otherwise check the COM number in Device Manager on the lab PC |
| An analyser sends results but none arrive | Integration log (Administration › Integrations) for refused messages. Network analysers: is the analyser's IP the one in its connection, and the port open in the firewall? Serial: compare baud rate, parity and stop bits with the analyser's settings |
| "PDFs can't be made on this server" (System health), or a PDF download fails | Is Microsoft Edge installed for all users? Set `HealthCare360:Pdf:BrowserPath` if it is somewhere unusual. The log says why the browser did not start. Printing from the screens still works |
| A report e-mail arrives without its PDF | The notification log notes "Sent without the document" and why. The PDF was tried three times first |
| WhatsApp refuses the report message (error 132012) | The WhatsApp template has no document header but "Document header" is ticked in HC360, or the other way round. Make them match |
| A modality cannot read the worklist | Administration › Integrations › Modality worklist: is it listening, and what is the last error? The modality's AE title and IP address must match its entry exactly, and the firewall rule must list its address. Refusals are logged once per ten minutes per network, so wait or look at the refused count |
| Images are not linked with PACS | Press "Link images from PACS" on the study: the message says why. "Under another patient ID", "without a patient ID" or "several studies" must be corrected in PACS; "not reached PACS yet" usually means the modality has not sent the images, or sent them without the worklist's accession number |

Log files hold no patient-identifiable data by design. Share them with HyFib support when asked.
