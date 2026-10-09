# Deploying HyFib HealthCare360 at a hospital

HC360 runs on **one dedicated Windows machine (the Server Node)** connected to the hospital WiFi. Everything else is a
client: **HC360 Desktop** on workstations, kiosks and waiting-area TVs, and a browser on tablets. Nothing needs the
internet to work; SMS/WhatsApp, payment gateways, ABDM and the AI assistant use it when available.

```
  Hospital WiFi (WPA2/WPA3-Enterprise, one SSID for clinical devices)
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

**Server hardware** (up to about 250 concurrent users):
- 8-core CPU, 32 GB RAM.
- 1 TB NVMe SSD, plus a second disk or NAS share for backup copies.
- WiFi 6 adapter; an Ethernet cable to the access point is better still.
- A UPS with a USB connection, so HC360 can see power cuts (Administration › System health).
- Windows with BitLocker (Pro, Enterprise or Server), turned on with the TPM for every fixed disk before HC360 holds
  patient data. The keys that open HC360's stored data are on the same machine (SRS SEC-05).

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

This does five things:
- Installs PostgreSQL through winget.
- Restricts it to localhost, with scram-sha-256 authentication only.
- Sizes its memory settings from the machine's RAM.
- Logs statements slower than two seconds, without their parameter values, which can be patient details.
- Sets the time zone to IST.

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
- Creates a least-privilege database account with a random password.
- Writes the configuration, readable only by Administrators and the service. The database password in it is protected
  with Windows DPAPI for this machine, so a copy of the file is no use on another computer (see "Changing a protected
  setting" below).
- Checks that BitLocker protects every fixed disk, and warns if one is not. It registers the scheduled task
  *HC360 disk encryption check* (in Task Scheduler › HyFib), which checks again as SYSTEM every day at 06:15 and at
  start-up. Administration › System health shows what it found, and warns about a disk that is not protected or a
  check that has stopped running.
- Registers the `HC360Server` Windows service, which restarts itself if it stops.
- Opens the firewall, for Private networks only: HTTPS for the staff app (7360) and the patient portal (7361), and
  mDNS.
- Keeps the machine and its WiFi adapter from sleeping.
- Starts HC360 and checks that it is healthy.

At the end it prints two things. Write them down:
- the **hospital CA fingerprint**, which staff compare when setting up HC360 Desktop;
- the **first administrator password** (user `admin`).

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

### Changing the database password

The password of HC360's database account is changed from Administration › System health, while HC360 runs (SRS
SEC-07). It needs the permission *Change the server's database password and keys, make backup recovery keys, and sign
everybody out* (`admin.system.manage`), which only SuperAdmin has until it is given to another role in Administration ›
Roles and permissions. Then:

1. Open Administration › System health. Under Database it shows when the password was last changed, and by whom.
2. Select **Change password** and confirm.

HC360 makes a new random password that nobody sees, sets it on the database and writes it protected in the settings
file together, and uses it for every new connection. Screens keep working, and no restart is needed. When either step
fails, both keep the old password. A reporting replica in the settings file that signs in as the same account gets the
new password too. Each change is recorded in the audit log. Change the password when someone who knew the old one
leaves, and at least once a year.

It works on the Windows Server Node when the connection string is in the settings file. When the connection is set
somewhere else, such as in environment variables, System health says so, and the password is changed there.

### Rotating the encryption keys

Two keys are rotated from Administration › System health › Encryption keys, with the same permission
(`admin.system.manage`), while HC360 runs (SRS SEC-07). Each rotation is recorded in the audit log, and System health
says when one is due: the sign-in token key every year, the data encryption key every two years. Rotate both, and
change the database password, at once if the Server Node, its disks or a backup may have been exposed.

- **Rotate data key.** New identifiers, secrets and documents are encrypted with a new key from then on. A background
  job moves everything encrypted before to the new key, a little at a time; the section shows how much is left, and
  screens keep working meanwhile. Documents move only while no backup is copying them. Earlier keys stay in
  `keys\field-keys` beside `keys\field-master.key`, and every backup carries them all, so older backups restore as
  before. Rotating it does not change the backup recovery key, which has a key of its own (below).
- **Rotate sign-in key.** New sign-in tokens are signed with a new key. Tokens already issued stay valid until they
  expire, within minutes, so nobody is signed out. It can be rotated again once those have expired.

The keys are on the Server Node: rotate them there, not on a second API server, which cannot share them yet.

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
on a NAS or USB drive, are deleted: 30 days by default. Both steps are recorded in the audit log, without the key.
HC360 keeps the key protected on the Server Node, to encrypt new backups, and never shows it again.

## 4. First sign-in and security set-up

On the server open `https://localhost:7360/`. The browser warns once, because the hospital CA is private. Then:

1. Sign in as `admin` and choose a new password. Set up two-step verification. Administrators, accountants, HR
   managers and anyone with an administrative permission must use it.
2. Check that `%ProgramData%\HyFib\HealthCare360\initial-admin-password.txt` is gone: HC360 deletes it once `admin`
   has a new password. If it is still there, delete it.
3. **Administration › Backups › Recovery key › Make a new key** (see "A new backup recovery key" above): write down
   the key and its ID, type the key back from your copy, and store the copy in the hospital safe. Until then System
   health warns that nobody holds the key, and backups cannot be restored on new hardware.
4. **Administration › Users**: create staff accounts, giving each person only the roles their job needs. HC360
   refuses passwords on public breach lists or holding a facility's name, for temporary passwords too. A temporary
   password works for 72 hours; after that, reset the password to give out a new one.
5. **Administration › Settings**: set the SMS/WhatsApp/payment gateway keys (stored encrypted) and
   `alerts.oncallPhone` (IT's mobile, which gets an SMS if the server WiFi drops or a backup fails).
6. **Documents and the messages that carry them** (reports, prescriptions, bills):
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
On an all-in-one machine (server and Desktop on one computer), setup also lists "This computer" when the server
listens on port 7360 there, because the network search does not always find the computer it runs on.

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
- Barcode scanners that work as keyboards (USB HID) need no driver or setup. A scanner on a COM port (RS-232, or USB
  in serial mode) is read by HC360 Desktop: in **Printers on this computer › Barcode scanner** choose the port and set
  the speed, data bits, parity and stop bits as they are set on the scanner (most use 9600, 8, none, 1), then try a
  scan there. It is kept in `%ProgramData%\HyFib\HC360Desktop\Workstation\scanner.json`; IT can fix it with a
  `scanner` entry in `policy.json`, for example `"scanner": { "port": "COM3", "baudRate": 9600 }`.
- PACS, the modality worklist, biometric attendance devices, Tally and payment terminals are configured in
  **Administration › Integrations**. See `docs/modules/integrations.md`.
- Lab analysers, radiology and BI tools such as Power BI: see below.

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

The installer stops the service, replaces the program files, keeps configuration and data, and starts the new version.
Every setting in `hc360.settings.json` stays as it is, including those added by hand (such as
`HealthCare360:Pdf:BrowserPath`, the BI extract folder or a reporting replica) and protected values. Of its own
settings, the installer changes only those given to it again as parameters (`-HttpsPort`, `-PortalHttpsPort`,
`-FacilityCode`, `-FacilityName`, `-SecondaryBackupDir`), and `HealthCare360:Backup:PgDumpPath`, which follows the newest
PostgreSQL under Program Files. One the file lacks is added, such as the portal's port. The same holds when it runs again for `-LabPorts`. Leave comments out of the file: Windows
PowerShell cannot read them (the installer then stops before it changes anything), and they are lost when the file is
rewritten.
It also protects any secret still written in plain text in the configuration (an installation from before this was
done), and checks BitLocker again.
Staff whose password is on the breach list built into the new version are asked to choose a new one at their next
sign-in, before anything else (SECURITY.md, "Identity and sessions"). Warn them if many may be affected. In the same
way, staff who must now use two-step verification and have not set it up are asked to at their next sign-in:
accountants, HR managers, and anyone whose roles carry an administrative permission. Check that each has an
authenticator app on their phone. Temporary passwords given out before HC360 made them expire work for 72 hours after
the upgrade.
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

HC360 Desktop is upgraded the same way, with `Install-HC360Desktop.ps1` on each workstation (or through your software
distribution tool).

## 8. Backups and disaster recovery

- An **encrypted** backup (AES-256-GCM) runs every night at 01:30 IST and is kept for 30 days. With
  `-SecondaryBackupDir`, each backup is also copied to the second disk or NAS.
- Check **Administration › Backups** weekly for green "Succeeded" rows. A failure also raises a System health warning
  and an SMS to `alerts.oncallPhone`.
- Each backup holds the database, the stored documents and the field encryption keys, all encrypted. Every backup is
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

     It asks for the recovery key that opens that backup: the one in use when it was made, which the Backups list names.
     It checks the whole backup before it changes anything. It restores the database first, keeping the current one
     under a new name (nothing is dropped). Only then does it put the documents back and install the backup's
     encryption key. If a step fails, the previous database is put back and HC360 starts again on it. On new hardware,
     later backups open with the key entered here; make a new key if that one should be retired.
  3. Workstations see a new server certificate. Re-run HC360 Desktop setup (Change server…) and compare the
     fingerprint.

  Backups listed as "Database only (older backup)" were made before HC360 archive backups. To restore one of those on
  new hardware, first copy the old `%ProgramData%\HyFib\HealthCare360\keys` folder back, if you have it.

  Backups whose recovery key is "Original key" open with the original key, which no screen shows any more. On the
  Server Node that made them, enter `original` when the script asks for the key; elsewhere, use the copy made when
  administrators could still display it.

## 9. Troubleshooting

| Symptom | Check |
|---|---|
| "Cannot reach the HealthCare360 server" on workstations | Server switched on? WiFi connected? System health on the server; client isolation on the AP |
| Slow screens on one ward | Administration › Devices shows each workstation's WiFi signal; Administration › System health shows the server's |
| Desktop setup finds no server | Multicast blocked. Type the server name (`HC360-SRV`) or use `-ServerUrl` |
| "The server could not be verified" | The server was reinstalled or replaced. Re-run setup (Change server…) and compare the fingerprint |
| "HC360 stopped: the server could not be verified" | Something answered for the server with a certificate that is not from the hospital CA; the screen shows its issuer. A proxy or security product that inspects HTTPS: exempt the HC360 server from it. The server was reinstalled with a new CA: re-run setup (Change server…) and compare the fingerprint. Also check the workstation's date and time |
| Service will not start | `%ProgramData%\HyFib\HealthCare360\logs\`, then Windows Event Viewer › Application (source *HyFib HealthCare360*) |
| A restore says "This backup opens with recovery key …, not with the key entered" | Each backup opens with the key in use when it was made. Use the copy whose ID the message names; the Backups list names every backup's key |
| A restore says "The recovery key has a mistake in it" | A character was copied or typed wrong. The key has no letters I, L, O or U; an I or L is read as 1, and an O as 0 |
| "The database could not be reached to confirm the change" after Change password | HC360 kept the old password. If it cannot sign in to the database once the database is back, set a new password for its account as `postgres` (`\password hc360_app` in psql), write the connection string with it in plain text in `hc360.settings.json`, run `settings protect` and restart the `HC360Server` service |
| `Install-HC360Server.ps1` stops at the start: "… hc360.settings.json is not valid JSON" | Nothing was changed, and HC360 keeps running. Correct the file where the message says (Windows PowerShell does not accept comments in it), then run the installer again |
| Service will not start: "The setting … cannot be opened on this machine" | The configuration was copied from another server, or the value was damaged. Write that setting's plain value in `hc360.settings.json` and run `settings protect` (see "Changing a protected setting") |
| "BitLocker is off on D:" or "BitLocker is not installed" (System health) | Turn on BitLocker, with the TPM, for that disk (Control Panel › BitLocker Drive Encryption). The warning clears after the next daily check, or run the *HC360 disk encryption check* task in Task Scheduler › HyFib |
| "BitLocker was last checked on …" or "has not been checked" (System health) | Is the *HC360 disk encryption check* task in Task Scheduler › HyFib, enabled, and does its last run show success? Run `Install-HC360Server.ps1` again to recreate it |
| A serial analyser shows "Waiting for agent" | Is the lab PC on and the `HC360LabAgent` service running? Administration › Devices shows when the agent last called in. Event Viewer › Application on the lab PC (source *HyFib HC360 Lab Agent*) says why it cannot connect |
| A serial analyser shows "COM port problem" | The message gives the Windows error. "Access to the port is denied": another program has the port open. Otherwise check the COM number in Device Manager on the lab PC |
| An analyser sends results but none arrive | Integration log (Administration › Integrations) for refused messages. Network analysers: is the analyser's IP the one in its connection, and the port open in the firewall? Serial: compare baud rate, parity and stop bits with the analyser's settings |
| "PDFs can't be made on this server" (System health), or a PDF download fails | Is Microsoft Edge installed for all users? Set `HealthCare360:Pdf:BrowserPath` if it is somewhere unusual. The log says why the browser did not start. Printing from the screens still works |
| A report e-mail arrives without its PDF | The notification log notes "Sent without the document" and why. The PDF was tried three times first |
| WhatsApp refuses the report message (error 132012) | The WhatsApp template has no document header but "Document header" is ticked in HC360, or the other way round. Make them match |
| A modality cannot read the worklist | Administration › Integrations › Modality worklist: is it listening, and what is the last error? The modality's AE title and IP address must match its entry exactly, and the firewall rule must list its address. Refusals are logged once per ten minutes per network, so wait or look at the refused count |
| An analyser or the modality worklist shows "Port … is used by the HC360 server itself" | It was given a port the server listens on, such as the patient portal's 7361 (see 7, Upgrades). Choose another port, in HC360 and on the analyser or the modalities, and open it in the firewall as described in 6 |
| "HC360 cannot write the extract folder" (Analytics › BI extracts), or an extract is "Not refreshing" | The message gives the Windows error. Does the folder set as `HealthCare360:Analytics:ExtractDirectory` in `hc360.settings.json` exist (a machine environment variable `HC360_HealthCare360__Analytics__ExtractDirectory` overrides it), and does `NT SERVICE\HC360Server` (or the server's computer account, for a share) have *Modify* on it? Is the disk full? A file the BI tool keeps open can also stop a refresh: close Power BI Desktop and press *Refresh now* |
| Images are not linked with PACS | Press "Link images from PACS" on the study: the message says why. "Under another patient ID", "without a patient ID" or "several studies" must be corrected in PACS; "not reached PACS yet" usually means the modality has not sent the images, or sent them without the worklist's accession number |

Log files hold no patient-identifiable data by design. Share them with HyFib support when asked.
