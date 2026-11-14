# Nana Hospitals, Theni: the drug registers, the server machine and the network

**Status: assessment, before installation.** Written on 10 November 2026 from four photographs sent from the site (two
registers printed by the pharmacy's current software, and two Settings › About screens of the machine meant to run
HC360) and from the code as it stands. A photograph does not show everything that decides go-live. What it cannot show
is marked **to confirm**, and [`Test-HC360ServerNode.ps1`](../../deploy/windows/Test-HC360ServerNode.ps1) reads it from the
machine (section 4, step 1).

The hospital answered the open questions on 10 November 2026 (section 6). Sections 3 and 4 follow those answers.

On 12 November 2026 the hospital set the release for **23 November 2026** and sent two more Settings › About
photographs: the server's again, and the lab PC's. One server serves every client (nurses, front office, doctors and
the rest), and every machine is on the hospital's **cabled LAN**. Section 7 covers the network and the lab PC, and what
changed in HC360 for a cabled LAN.

The printouts name patients and doctors. None of them is copied here.

## 1. Are the Schedule H and H1 registers in HC360, and where?

**Yes, in the Pharmacy module, and only there.** They are under Pharmacy › **Drug registers** (`/pharmacy/registers` in
the staff app), for holders of `pharmacy.registers` (the Pharmacist and HospitalAdmin roles). The page has two tabs:

| Tab | API | What it holds |
|---|---|---|
| Controlled drugs (H1, X, NDPS) | `GET /api/v1/pharmacy/registers/controlled?schedule=H1&from&to&storeId&itemId`, and `…/controlled/export` (CSV) | Every movement of a register drug: receipts, sales, ward issues, returns, transfers, adjustments, write-offs and OT use. Each line has the running balance, batch and expiry, the patient (name, UHID, address), the prescriber and their council registration number, the prescription and document numbers, and who recorded it |
| Schedule H supplies | `GET /api/v1/pharmacy/registers/schedule-h?from&to&storeId`, and `…/schedule-h/export` (CSV) | Schedule H medicines sold at the counters and issued to the wards: date and time, document, patient, prescriber, prescription, drug, batch and quantity. H1 is not here; it is in the controlled register |

Code: [RegisterEndpoints.cs](../../src/server/Modules/HyFib.HealthCare360.Modules.Pharmacy/Features/Registers/RegisterEndpoints.cs)
and [registers.tsx](../../src/web/apps/staff/src/routes/_app/pharmacy/registers.tsx), described in
[pharmacy.md](../modules/pharmacy.md) (*Registers*). It is SRS PH-06, marked done in
[TRACEABILITY.md](../requirements/TRACEABILITY.md). Every CSV download is written to the audit log.

**No other module shows them.** The other modules feed the registers:
- **Clinical** (e-prescription) flags an H1 or X drug and asks the doctor to confirm it (ERX-06).
- **Inpatient** (MAR) needs a witness when a controlled drug is given on the ward.
- **SupplyChain** keeps each item's schedule and manufacturer. It sends every movement of a register drug made outside
  the pharmacy screens (GRN, transfers, adjustments, OT consumption) to the Pharmacy register (`ControlledStockMoved`).
- **Analytics** (MIS) has pharmacy sales and margin, near-expiry stock and dead stock, but no Schedule H or H1 report.

## 2. The printed registers beside HC360's

The two printouts:
- **"Schedule-H1 Report"**: one day's supplies (21-09-2026), nine lines. The "Print Date and Time" field is empty and no
  period is printed. A pharmacist signed and dated it by hand in the margin.
- **"Schedule-H Report"**: one day (26-04-2026), fourteen lines. Its header has From and To dates, the branch and
  "Medicine Type: Schedule-H", and it has an empty "Pharmacist sign" column. Someone has marked it up by hand:
  - "Separate for Schedule-H1" at the top;
  - "Drug Report" beside the title;
  - "+ Manufacturer name" over the expiry column;
  - arrows between the Patient and Doctor columns, read here as "swap them" (**to confirm** with the
    pharmacist-in-charge).

Column by column:

| Printed column | H1 printout | H printout | HC360 controlled register, H1 | HC360 Schedule H tab |
|---|---|---|---|---|
| S.No. | yes | yes | no (row order) | no |
| Bill no. | yes | yes | Document | Document |
| Bill date | yes | yes | Date and time | Date and time |
| Patient | yes | yes | Patient, UHID, address | Patient |
| Doctor | yes | yes | Prescriber, registration no. | Prescriber |
| Drug | yes | yes | yes | yes |
| Qty | yes | yes | In, out, balance | Qty |
| Batch | yes | yes | yes | yes |
| Expiry | yes | yes | yes | **no** |
| Manufacturer | yes | asked for by hand | **no** | **no** |
| Pharmacist's signature | in the margin | empty column | **no** | **no** |
| Header: hospital, address, phone, period, time printed | partly | yes | on screen only; the CSV has none | as for H1 |

HC360 has both registers. In places it records more than the printouts: the UHID, the patient's address, the
prescriber's registration number, running balances, and an audit entry for every download. But four things the pharmacy
does today are not built:
1. **A printed register.** There is a screen and a CSV, but no page to print and sign. Nothing prints a header with the
   hospital, the period and the time printed, page numbers, or a line for the pharmacist's signature.
2. **Expiry and manufacturer.** The Schedule H tab has neither; the controlled register has no manufacturer. The item
   master has a manufacturer field (`Item.Manufacturer`), but it is optional.
3. **H1 supplies on their own.** The controlled register is a stock register. Filtered to H1, it still lists receipts,
   transfers and adjustments with the running balance, as Schedule X and NDPS need, while the H1 printout lists supplies
   only. There is no "sales and ward issues only" filter.
4. **Column order and title**, if the pharmacist wants the doctor before the patient and "Drug Report" as the title
   (to confirm).

**H1 in law, as read here** (to confirm with the pharmacist-in-charge). Rule 65 of the Drugs Rules, 1945, as amended by
G.S.R. 588(E) of 2013, asks for H1 supplies to be entered in a separate register at the time of supply, and kept for
three years. Each entry has the prescriber's name and address, the patient's name, the drug and the quantity. HC360
records the prescriber's registration number, not an address. For the hospital's own doctors the address is the
hospital's. For an outside prescription brought to the counter, ask whether the registration number is enough.

### What the printouts show about the current data

No data is moved from the current software (section 6), so HC360's item master is built afresh. The printouts still
show what to get right in it:
- **A drug in the wrong register.** A tramadol and paracetamol tablet (TAB ULTRACET) is on the *Schedule H* printout.
  Tramadol has been in Schedule H1 since the 2013 notification, and HC360's catalogue seeds it as H1 and controlled
  ([ItemCatalogSeed.cs](../../src/server/Modules/HyFib.HealthCare360.Modules.SupplyChain/Seed/ItemCatalogSeed.cs)). The
  current software classes it as H (that printout is filtered to "Medicine Type: Schedule-H"). In HC360's item master,
  set each drug's schedule from its molecule against the H1 list, not from the current software's class. The hospital
  leaves the entries already made in the current software as they are.
- **Manufacturer missing.** Two H1 items print "Manufacturer Not Mentioned". The item master needs a manufacturer for
  every scheduled drug before any register can print one.
- **One line per scan.** The H1 printout shows one bill's drug and batch on three lines of 1 each. HC360 makes one line
  per drug and batch on a bill. Compare total quantities, not line counts.

### How to know HC360's registers are right

Put them beside the registers the pharmacy signs today:
1. **Parallel run.** For three working days, one of them with ward issues, make every bill in both systems.
2. **Reconcile each day.** Take the current software's H and H1 printouts and HC360's two registers (CSV) for the same
   day. Match lines by bill, drug and batch, and compare total quantities. Write down the reason for every difference: a
   return, a cancelled bill, or a drug in a different schedule, as above.
3. **Sign off.** The pharmacist-in-charge signs that HC360's registers replace the printouts. Show them to the Drugs
   Inspector at the next inspection: the inspector decides what a register must show.

## 3. The machine in the photographs

| | The photographs show | HC360 needs | Verdict |
|---|---|---|---|
| Windows | 11 Pro 25H2, build 26200.9711, 64-bit, installed 28-09-2025 | Windows 10 2004 or later; Pro or above for BitLocker | Meets |
| Processor | Intel Core i5-12400: 6 cores, 12 threads | 40 staff at once is SRS 3.2 Medium (26 to 75 users): 8 cores | **Two cores short.** The load test decides (below) |
| Memory | 32 GB (31.7 GB usable), 3200 MT/s | 32 GB (Medium) | Meets |
| Storage | 1.39 TB in all, 218 GB used | A 1 TB NVMe SSD (mirrored for Medium), plus a second disk or NAS for backup copies | **To confirm.** 1.39 TB is not a usual size for one drive, so there are probably several disks. Which one holds Windows, and whether it is an SSD, is not shown. PostgreSQL and HC360's data go on the Windows disk; the backup folder belongs on another (below, "How much backup space") |
| Graphics | Intel UHD 730 | Not used | — |
| Name | DESKTOP-RMI9PCM, a name Windows chose | A fixed name, e.g. HC360-SRV | Rename before installing |
| BitLocker, TPM | Not shown (Windows 11 Pro has both) | On for every fixed disk, with the TPM, before HC360 holds patient data | **To confirm** |
| Network | On the hospital's cabled LAN (the hospital, 12 November); the cable's speed is not shown | A network cable to a gigabit port of the switch, running at 1 Gbps; the Private profile; a DHCP reservation | **Cabled. The speed is to confirm**: the preflight grades it |
| Activation | "Activate Windows" across the screen (12 November); product ID ending AAOEM | Windows activated with the hospital's licence | **Not activated: activate before go-live** (below) |
| UPS | None (the hospital's answer) | 30 minutes or more, connected by USB (SRS 3.2, every profile) | **Missing: buy before go-live** (below) |
| Backup copies | No NAS and no USB disk (the hospital's answer) | An external backup target, a USB drive or a NAS (SRS 3.2, every profile) | **Missing: buy before go-live** (below) |
| Use | In daily use at a desk (the hospital's answer); everyday programs on the taskbar (browsers, WhatsApp, Office and others) | "One dedicated Windows machine" | **Departs from DEPLOYMENT.md** (below) |

### A PC in daily use as the server

The hospital means to run HC360 on this PC while someone keeps working at it. [DEPLOYMENT.md](DEPLOYMENT.md) asks
for one dedicated machine. HC360 Desktop can also run on the server itself ("This computer", section 5), so one machine
can do both. But a PC someone works at all day brings risks a server must not have:
- **It is switched off at night**, and the backup runs at 01:30. The preflight counts the last 30 days' shutdowns and
  restarts.
- **Everyday programs run on it**: browsers, WhatsApp, downloads and remote-access tools, all on the machine that holds
  the keys to every patient record.
- **Power cuts.** Without a UPS on USB, a cut can damage the database. The preflight counts the stops Windows did not
  expect.

**The recommendation is a dedicated server, with this PC kept as a workstation.** The hospital is buying a UPS and
backup disks in any case (below), and for 40 staff at once the SRS asks for 8 cores, which this PC does not have. A
machine to SRS 3.2 Medium has 8 cores, 32 GB and two 1 TB NVMe SSDs, mirrored.

If HC360 runs on this PC all the same, these are conditions of go-live, not advice:
- It is never switched off, and nobody restarts it by hand. Windows Update restarts it only overnight (the installer
  sets the hours).
- Whoever works at it signs in to Windows with a standard account, not an administrator, and uses HC360 through HC360
  Desktop like any workstation.
- WhatsApp, browsing and downloads move to another PC if at all possible. What stays runs under that standard account.
- It is on the UPS, with the USB cable connected.
- The load test (section 4, step 6) runs with the desk user's everyday programs open, as on a working day.
- The decision is recorded in writing, because it departs from DEPLOYMENT.md.

### Windows is not activated

The 12 November photograph shows "Activate Windows" over the desktop, and a product ID ending AAOEM: a key of the kind
PC makers install, not activated. HC360 installs and runs on it, but a hospital's server is to run licensed Windows,
and the preflight warns until it does. Activate it in Settings › System › Activation before go-live. Activation needs
the internet once. A licence built into the PC is for the edition it was sold with: if that is Home, Windows 11 Pro
(which BitLocker needs) needs a Pro licence or the upgrade to Pro.
*Check:* Settings › System › Activation says Windows is activated, and the preflight's *Activation* passes.

The same photograph shows a remote-access tool's toolbar on the server's screen. The preflight's *Remote access* lists
every such tool: keep the one IT uses, protected by a strong password and its two-step sign-in, and remove the rest
(section 4, step 3).

### Is a 6-core i5 enough for 40 staff?

Forty staff at once is the Medium profile (26 to 75 users), which asks for 8 cores; the i5-12400 has 6. The only load
test run so far, on a 4-core, 16 GB development VM, met PERF-01 at 75 users (p95 171 ms,
[LOAD-TESTS.md](../testing/LOAD-TESTS.md)), so 6 cores may well be enough for 40. But nobody was working at that
machine. The load test on this PC decides, before go-live: at 75 users, PERF-01's own figure, which leaves room above
40.

The documents disagree on sizing. DEPLOYMENT.md section 1 gives 8 cores and 32 GB for "up to about 250 concurrent
users", but SRS 3.2 gives the same as Medium (up to 75 users) and asks 12 cores and 64 GB for Large (up to 150). The
preflight follows the SRS.

### To buy before go-live

SRS 3.2 asks every profile for a UPS and an external backup target, and the hospital has neither:
- **A UPS with a USB data cable**, which Windows then shows as a battery. It keeps the PC and its monitor running for 30
  minutes or more: measure what they draw, and read the maker's runtime chart at that load. Put the access point and
  switch the workstations use on a UPS too, or nobody reaches the server in a power cut.
- **Two USB disks for the backup copies.** Give both the same drive letter (Disk Management), keep one connected as the
  copy target, and swap them once a week in the daytime, taking the other off site. HC360 encrypts its backups
  (AES-256-GCM), so a disk taken home holds nothing readable without the recovery key. The copy is made with the
  nightly backup at 01:30, so a disk swapped in the day misses nothing. A night with no disk connected leaves that
  backup without its copy: the Backups screen shows "No" in its copy column, the administrators are told in the app the
  next morning, and System health warns. After three nights without a copy the warning turns critical and is told to
  IT on-call by SMS (DEPLOYMENT.md section 8). Buy **2 TB disks and format them NTFS** (sizing below). Each disk keeps
  one copy of every stored document and the backups made while it was connected, and catches up on the documents it
  missed when it comes back.
- **An off-site bucket.** The two disks are in the hospital most of the week, and a disk swapped by hand is easily
  forgotten. The owner asked that once online, the backups are backed up: HC360 uploads each night's backup, encrypted,
  to an S3-compatible bucket in India (AWS Mumbai or Hyderabad, OCI, or another store hosted in India) within 15
  minutes, locked there for 35 days so nobody can delete it. The hospital opens the account in its own name and keeps
  its administrator login. It holds about 35 backups and six weekly copies of the documents (sizing below). The internet
  line must carry a night's backup within the day, and once a week every document: 100 GB takes about 23 hours at 10
  Mbit/s, so measure the line before relying on the bucket.
- **A dedicated server**, if the hospital takes the recommendation above.

### How much backup space

Each nightly backup holds the database dump and a list of the stored documents. Each backup folder keeps one copy of
every document, so a folder grows by about one dump and the day's new documents each night (DEPLOYMENT.md section 8;
docs/design/incremental-backup.md, sections 2 and 5.7). Space needed, with *R* backups kept (about 30 once backups are
a year old: every backup of the last 14 days, and the last of each of 8 weeks and of 12 months), a dump of *D* and
documents of *Docs*:

- **The server's backup folder, and each USB disk:** about *R* × *D* + *Docs*. Each USB disk keeps as many backups as
  the server, since retention counts the backups each disk holds. After the data encryption key is rotated every
  document is copied once more, and the old copies stay, *Docs* more, until the last backup made before the rotation
  goes: up to 342 days, as the monthly backups are kept a year.
- **The off-site bucket:** about 35 × *D* + 6 × *Docs*.

The hospital's own figures are not measured yet. Run `deploy\windows\Measure-HC360BackupSizing.ps1 -MeasureDump` on
this PC before go-live and again after the first month (docs/design/incremental-backup.md, Appendix A). It reads only,
projects these figures from the hospital's own data, and writes a table to put here in place of the one below. Until then, the figures below assume **0.25 GB of documents a
day**, with dumps of about 5 GB in year 1 and 15 GB in year 3.

| | Year 1 | Year 3 | Before (every document in every backup), year 3 |
|---|---|---|---|
| Server's backup folder (30 backups) | 0.24 TB | 0.72 TB (1.0 TB after a key rotation) | 8.7 TB |
| Each USB disk (as many backups, on each of two disks swapped weekly) | 0.24 TB | 0.72 TB (1.0 TB after a key rotation) | 8.7 TB |
| Off-site bucket | 0.72 TB | 2.2 TB | 10 TB |

What this means for this PC: by year 3 the documents (about 0.27 TB), the database and the backup folder no longer fit
on one 1 TB disk together. Put the backup folder on a disk of its own (`HealthCare360:Backup:Directory`), not the
Windows disk. Most of the backup space is then the 30 dumps, not the documents.

## 4. Deploying to this machine, step by step

Each step ends with a check, and the next step starts only once it passes.

**Steps 12 to 16, the alerts that reach IT's phone, start today (14 November), not after step 11.** The SMS in sections
3, 4 and 7 does not exist until the hospital has an SMS gateway account and has registered its sender and its message
on the DLT portal, which takes days to weeks, and nobody outside the hospital notices a stopped server until a heartbeat
monitor is set up. The dates are against the release on 23 November.

1. **Read the machine.** First download the release and check it before anything in it runs: compare each file with
   `SHA256SUMS.txt` and with the checksums HyFib sends separately, unblock the checked zip, extract it, and let the
   PowerShell window run scripts (DEPLOYMENT.md section 1, *The release files*). Then, on the machine, as Administrator,
   from the release's `deploy` folder, run:

   ```powershell
   .\Test-HC360ServerNode.ps1 -ConcurrentUsers 40
   ```

   It changes nothing. Send its JSON report to HyFib. Expect a Warn on the processor cores (6 of 8) until the load test.
   *Check:* no Fail, and a decision written beside each Warn.
2. **Buy and connect the UPS and the two USB backup disks** (section 3, *To buy before go-live*).
   *Check:* in the preflight, *UPS on USB* passes and *Second disk for backup copies* lists the USB disk. *Disk write
   cache* is Pass, or a Warn that the installer's fsync setting covers (step 5): with no UPS yet, a power cut can lose
   work still in the disk's cache, which the UPS and that setting prevent.
3. **Prepare Windows:**
   - Rename the machine HC360-SRV and restart.
   - Run Windows Update until nothing is pending.
   - Turn on BitLocker for every fixed disk, with the TPM, and keep the recovery keys in the hospital's password safe.
     The USB backup disks are left out: HC360 encrypts the backups on them.
   - Give the two USB backup disks the same drive letter, e.g. `E:`.
   - Make a standard (not administrator) Windows account for the person who works at the PC (section 3).
   - Connect it to a gigabit port of the hospital's switch by a Cat5e or Cat6 cable, set the Private profile, and
     reserve the address the report gives for its MAC address.
   - Activate Windows (section 3, *Windows is not activated*).
   - Connect the UPS by USB.
   - Set the time zone to India Standard Time.
   - Keep one remote-access tool, protected, and remove the rest.

   *Check:* run the preflight again. Nothing fails, BitLocker passes, *Network connection* passes (the cable at 1 Gbps),
   and *Activation* passes.
4. **Check the release files again** if any were downloaded since step 1, for a newer release: the same check, and
   the same unblocking, as in step 1. The release installed must be the one checked.
5. **Install PostgreSQL** with `.\Install-PostgreSQL.ps1` (DEPLOYMENT.md section 2). Then measure how fast its disk
   commits, with PostgreSQL's own tool:

   ```powershell
   & "$env:ProgramFiles\PostgreSQL\17\bin\pg_test_fsync.exe" -s 3 -f "$env:ProgramData\pg_test_fsync.out"
   ```

   *Check:* write the figures down. A hard disk manages at most a few hundred flushes a second, an SSD thousands. Run
   the preflight again: *PostgreSQL wal_sync_method* passes (the installer sets `fsync`, which makes PostgreSQL flush the
   disk's write cache at every commit, as PostgreSQL's documentation says to on Windows without a disk cache that is
   off or protected).
6. **Run the load test on this machine, before any real data** ([LOAD-TESTS.md](../testing/LOAD-TESTS.md)). Use a test
   installation with demonstration data and 75 users (PERF-01's figure; the hospital has 40 at once), with the desk
   user's everyday programs open.
   *Check:* PERF-01 is met (p95 of 300 ms or less for reads, 600 ms or less for writes). Then remove the test server's
   data as LOAD-TESTS.md says.
7. **Install HC360** (DEPLOYMENT.md section 3):

   ```powershell
   .\Install-HC360Server.ps1 -CreateDatabase -FacilityCode NH -FacilityName 'Nana Hospitals' `
       -SecondaryBackupDir 'E:\HC360-Backups'
   ```

   `E:` is the USB backup disks' drive letter. Write down the CA fingerprint and the first administrator password.
   *Check:* Administration › System health shows nothing red. Then unplug the USB disk and press **Back up now**
   (Administration › Backups): the copy column says "No", the administrators are told in the app, and System health
   shows `backup.copy_failed`. Plug the disk back in and back up again: the warning clears. Restart the server once:
   HC360 starts with Windows, and the network card is from then on never powered down (DEPLOYMENT.md section 3).
   Then pull the server's network cable for a minute: System health shows "Cable disconnected". Plug it back in:
   System health says it is back, and within a few minutes IT's phone gets one SMS that it is connected again, saying
   when it was lost and when it was back. The server reaches the SMS gateway over that cable, so the alert of the loss
   could not go while it was out: Administration › Notifications shows it suppressed, withdrawn.
8. **Send the backups off site, give the recovery key two holders, and run a restore drill from the off-site copy,
   before go-live.**
   - Set up the off-site copy (DEPLOYMENT.md section 8, *The off-site copy*): the bucket made with Object Lock, an access
     key that may upload but not delete, and the settings under Administration › Backups, *Off-site copy*, approved by
     the second administrator.
   - Make a new recovery key (DEPLOYMENT.md section 3, *A new backup recovery key*). The IT administrator who makes it
     keeps one copy; a second administrator (the medical director or the hospital administrator) keeps another outside
     the hospital, at home or in a bank locker, and types it in under Backups › Recovery key, *I hold this key*. Put the
     quarterly check, when each holder types the key in again, in both their calendars.
   - Let the first night's backup run and go off site. Right after it, export the row counts on the server
     (`Invoke-HC360RestoreDrill.ps1 -ExportReference`). On a spare PC, download the backup and its generation from the
     bucket with the hospital's own login (DEPLOYMENT.md section 8, *Restoring from the off-site copy*), and run the
     restore drill on it (`Invoke-HC360RestoreDrill.ps1 -BackupFile ... -ReferenceFile ...`, section 8, *Restore drill
     every quarter*). Do the same from one of the USB disks.

   *Check:* the night's backup shows Yes in the Backups screen's *Off site* column, and the bucket shows it locked for 35
   days. Backups › Recovery key lists both holders, and System health shows neither `backup.offsite_stale` nor
   `backup.recovery_key_custodians`. Both drills say PASSED: every document in place and as saved, and no table missing or with
   more rows than the server had. Keep their reports, with the time each restore took.
9. **Build the item master in HC360.** No data comes from the current software. Set each scheduled drug's schedule
   from its molecule against the H1 list, and fill in its manufacturer.
   *Check:* the pharmacist-in-charge reviews the H1 and X list before it is used.
10. **Install the workstations** (DEPLOYMENT.md section 5), the pharmacy's first, and the lab PC (section 7).
    *Check:* on each, `Test-NetConnection HC360-SRV -Port 7360` ends with `TcpTestSucceeded : True`, and HC360
    Desktop's connection pill shows "Network: Cable · 1 Gbps" (or "100 Mbps" for a workstation on an older switch,
    which is enough for a workstation).
11. **Run the registers in parallel** (section 2 above).
    *Check:* the pharmacist-in-charge signs them off.
12. **Start the DLT registration: today, 14 November. Owner: the hospital's IT head, with the signatory who may register
    the hospital; HyFib supplies the message text.** Operators drop an SMS whose sender or message is not registered on
    the DLT portal of the telecom operator, so an unregistered alert SMS fails and nobody is told. Register the hospital
    as the sender (entity), then its sender header, then the text of the `system.alert` template (Administration ›
    Message templates), and write down the template id each approval gives. Registration takes days to weeks and may
    not be done by 23 November.
    *Check:* the DLT portal shows the entity, the header and the template approved, and the template id is written
    down. If it is not approved by 20 November, the go-live record says "alert SMS not working yet", and until it is:
    critical alerts reach everyone who may see System health in the app, WhatsApp if the hospital chose it, and the
    heartbeat monitor's own alarm (step 15) by call or e-mail; IT looks at System health at the start of each shift.
13. **Open the SMS gateway account: by 17 November. Owner: the IT head opens it and pays for it; HyFib enters its
    keys.** Choose the gateway the DLT registration was made through (MSG91, for one, takes the DLT template id with
    each SMS). HyFib enters the key under Administration › Settings (stored encrypted), and, once step 12 gives it, the
    template id on the `system.alert` template.
    *Check:* System health shows no `alerts.not_ready`, and Administration › Notifications shows a test SMS from the
    gateway delivered to IT's phone.
14. **Name the on-call and escalation numbers: by 18 November. Owner: hospital management names the people, the IT head
    gives their numbers, HyFib enters them.** Set `alerts.oncallPhone` (IT's mobile, which gets each critical alert),
    `alerts.escalationPhone` (the second contact, such as the IT head: paged after `alerts.escalateMinutes`, 15 if not
    set) and `alerts.supportPhone` (HyFib support's number: paged `alerts.supportMinutes` after that, 30 if not set).
    Write the same three people and numbers on the go-live record and on a sheet at the server.
    *Check:* System health shows neither `alerts.not_ready` nor `alerts.no_escalation`.
15. **Set up the heartbeat monitor outside the hospital: by 18 November. Owner: the IT head creates the check, HyFib
    enters its address.** A service such as Healthchecks.io or Better Stack, with a period of 5 minutes, a grace time of 5
    to 10, and alerts to IT and to HyFib support by call or e-mail as well as SMS (a call or e-mail needs no DLT
    registration). HyFib sets `alerts.heartbeatUrl.secret` to its address, and the firewall lets the server reach the
    monitor's host (DEPLOYMENT.md section 4, *Alerts that reach someone*). The server fetches the address only while
    its database and its own web host answer, so a wedged server stops it too.
    *Check:* System health shows no `alerts.heartbeat_off`, and the monitor shows a heartbeat every 5 minutes.
16. **Drill the alerts: by 21 November, after step 7. Owner: the IT head with HyFib, on the server, with IT's phone in
    hand.** Start an alert drill (Administration › System health › Alert drills) and let it go unacknowledged until the
    second contact's SMS arrives, then acknowledge it. Then stop the `HC360Server` service for longer than the monitor's
    period plus its grace time, and start it again.
    *Check:* IT's phone got the drill's SMS within five minutes, the second contact's phone got its SMS after
    `alerts.escalateMinutes`, and the monitor's alarm reached IT and HyFib support while the service was stopped. The
    drill's timings (told in the app, SMS sent, acknowledged) and the monitor's alarm time are written on the go-live
    record. If step 12 is not finished, the drill is repeated the day the template is approved.

## 5. Who or what shows each step is right

None of these depends on HyFib's word:

| Claim | Shown by |
|---|---|
| The machine is fit | The preflight's report, read from the machine itself; then the installer's own checks and Administration › System health |
| Windows is licensed | Settings › System › Activation, and the preflight's *Activation* |
| The server's cable is right, and its loss is noticed | The preflight's *Network connection* at 1 Gbps; the cable pulled at step 7: System health, then the SMS on IT's phone that it is back, saying when it was lost |
| Every workstation reaches the server | `Test-NetConnection HC360-SRV -Port 7360` from each, and its connection pill showing the cable |
| It is fast enough | The load test on this machine (PERF-01), and pg_test_fsync's figures for its disk |
| The data survives a failure | A backup verified, downloaded from the off-site bucket, then restored on another PC (step 8) |
| Each night's backup leaves the server | The Backups screen's *Off site* column says Yes for each night's backup, and the bucket holds it, locked for 35 days. At each weekly swap, the disk going off site holds the last night's backup file, and `backup verify --in` passes on it (DEPLOYMENT.md section 8). That a missing copy is noticed: the disk unplugged at step 7, System health's `backup.offsite_stale`, and an alert drill's SMS on IT's phone for the critical alert |
| Two people hold the recovery key | Backups › Recovery key lists both, and System health shows no `backup.recovery_key_custodians`; once a quarter, each types it in again, or System health says so (`backup.recovery_key_unchecked`) |
| The registers are right | The day-by-day reconciliation against the current software's printouts, signed by the pharmacist-in-charge, and then the Drugs Inspector |
| Each drug is in the right schedule | The Schedule H1 list (G.S.R. 588(E), 2013, and later amendments), molecule by molecule |
| Registered to send alert SMS (step 12, owner: IT head; by 20 November) | The DLT portal's approvals of the entity, the sender header and the `system.alert` template, with the template id written down; and the alert drill's SMS on IT's phone (step 16) |
| The SMS gateway works (step 13, owner: IT head; by 17 November) | No `alerts.not_ready` in System health, and a delivered test SMS in Administration › Notifications |
| IT's on-call and escalation numbers are right (step 14, owner: hospital management; by 18 November) | `alerts.oncallPhone`, `alerts.escalationPhone` and `alerts.supportPhone` set, no `alerts.not_ready` or `alerts.no_escalation` in System health, and the second contact's SMS in the drill |
| Someone outside the hospital notices if the server stops or hangs (step 15, owner: IT head; by 18 November) | The monitor shows a heartbeat every 5 minutes, no `alerts.heartbeat_off` in System health, and the monitor's alarm reaching IT and HyFib support while `HC360Server` is stopped (step 16) |
| The alerts reach a person, in time (step 16, owner: IT head with HyFib; by 21 November) | The alert drill's timings and the monitor's alarm time on the go-live record |

## 6. The hospital's answers (10 and 12 November 2026)

| Question | Answer | What it changes |
|---|---|---|
| How many staff use the system at once at the busiest hour? | 40 | The Medium profile: 8 cores asked, 6 here. The load test decides (section 3) |
| Is this PC spare, or someone's desk PC? | Not spare: in daily use at a desk | It departs from DEPLOYMENT.md: a dedicated server is recommended, and if not, the conditions in section 3 apply |
| Is there a UPS with USB, and a NAS or USB disk for backup copies? | No | Both are to be bought before go-live (section 3) |
| Where is the current pharmacy software's database? | Not applicable | No data is moved. HC360's item master is built afresh (section 4, step 9) |
| What should be done about the tramadol supplies already in the H register? | Nothing: not applicable | Only HC360's own item master must class tramadol as H1 (section 2) |

The hospital's answers of 12 November 2026:

| Question | Answer | What it changes |
|---|---|---|
| When is the release? | 23 November 2026 | The steps of section 4 and the checks of section 7 are done by then |
| How are the machines connected? | One server, the clients (nurses, front office, doctors and the rest) on the hospital's LAN | A cabled LAN: what HC360 now does for one, and the checks, are in section 7 |
| The lab PC? | "A different CPU" (photograph: i5-6500T, 8 GB, 238 GB SSD) | Fit for HC360 Desktop and the lab agent; its Windows is to confirm (section 7) |

Still open:
1. Does the pharmacist-in-charge want Doctor before Patient, and "Drug Report" as the title? Which signature lines?
2. Will the hospital buy a dedicated server, or run HC360 on this PC under the conditions in section 3?
3. Are the lab's analysers connected by serial (RS-232) cable to the lab PC, or to the network? Section 7 says what each
   needs.
4. Which Windows, and which version, is the lab PC on (section 7)?

## 7. The cabled LAN and the lab PC (12 November 2026)

### What changed in HC360 for a cabled LAN

HC360 was written for a server on WiFi (SRS assumption A-09). On a cabled LAN that left gaps, closed in this release:
- **The server watches its network cable.** A cable pulled or a switch turned off is told at once, in the app to the
  administrators and by SMS to IT's phone, and so is its return, which says when it was lost and when it was back.
  Before, only the server's WiFi was watched: a cabled server could lose its cable and nobody would be told.
- **A WiFi card beside the cable raises nothing.** A cabled server's idle WiFi card was a critical "WiFi is
  disconnected" in System health for as long as it ran, and a WiFi that dropped beside the cable sent critical SMS.
- **System health shows the cable and its speed** ("Cable · 1 Gbps"), and says "Not connected" for a server with no
  link at all.
- **The installer keeps the network card at full power.** It only meant to for a WiFi card, and passed Windows a
  parameter Windows does not have, so it never did.
- **The preflight grades the cable's speed** (1 Gbps passes, 100 Mbps warns) **and Windows activation.**
- **HC360 Desktop says "Cable"** in its connection pill on a cabled workstation, no longer counts a weak WiFi beside the
  cable as a weak connection, and tells the user of a cabled workstation that cannot reach the server that the fault is
  beyond their PC, rather than to check their WiFi.

### The network, before go-live

| Check | How | Passes when |
|---|---|---|
| The server's cable | The preflight's *Network connection* | Pass: 1 Gbps. At 100 Mbps, change the cable (Cat5e or Cat6) or the switch port |
| The switches stay up in a power cut | The switch the server is on, and those between it and the wards, on a UPS; then switch the mains off for five minutes | Workstations on UPS keep working, and HC360 Desktop shows no offline banner |
| The server's cable loss is noticed | Section 4, step 7: pull the cable for a minute | System health shows "Cable disconnected"; once it is in, it says the cable is back, and IT's phone gets one SMS that it is connected again, saying when it was lost and when it was back |
| Each workstation reaches the server | `Test-NetConnection HC360-SRV -Port 7360` on each | `TcpTestSucceeded : True` |
| HC360 Desktop finds the server by itself | Setup on each workstation without `-ServerUrl` | It lists HC360-SRV. If not, multicast (mDNS) does not cross the switches: use `-ServerUrl` (DEPLOYMENT.md section 1) |

### The lab PC

| | The photograph shows | HC360 needs | Verdict |
|---|---|---|---|
| Processor | Intel Core i5-6500T: 4 cores, 4 threads, 2.5 GHz, a 35 W part from 2015 (6th generation) | HC360 Desktop (WebView2) and, for serial analysers, the lab agent: any 64-bit (x64) PC | Enough. HC360 Desktop and the lab agent are the same x64 programs on any CPU |
| Memory | 8 GB, 2400 MHz | — | Enough |
| Storage | 238 GB SSD | — | Enough |
| Graphics | Intel HD Graphics 530 | — | Enough |
| Name | DESKTOP-RLOF787 | Any; HC360 names the workstation at enrolment ("Lab PC") | — |
| Windows | Settings › About in the Windows 11 style; the version is not shown; product ID 00330-80000-00000-AA881 | Windows 10 version 2004 or later, still getting security updates | **To confirm** (below) |

**Its Windows.** The i5-6500T is older than Windows 11's supported processors (8th generation and later). If the PC is on
Windows 11, it was installed past that check, and Microsoft does not promise such a PC its updates. If it is on Windows
10, Windows 10's support ended on 14 October 2025, and only Extended Security Updates keep it patched. Either way it is
the PC that relays results into patient records, so: read the edition and version (`winver`, or Settings › System ›
About › Windows specifications), and check that Windows Update has installed the November 2026 security update.
*Check:* Windows Update shows the 10 November 2026 security update installed, and Windows is activated. If it cannot be
kept updated, give the lab a PC that can be, or keep this one off everything but the hospital LAN.

**Its analysers.** For analysers on **serial** cables, this PC runs the HC360 lab agent (DEPLOYMENT.md section 6,
*Lab analysers*). A PC asleep relays nothing, and an analyser may give up on a result nobody takes, so while the agent
relays an analyser it keeps the PC from sleeping when nobody uses it. It does not stop someone putting the PC to sleep:
staff leave it on.
*Check:* once the analyser is set up (Lab setup › Analysers › Connection), as Administrator:

```powershell
powercfg /requests
```

lists under SYSTEM `HC360 lab agent: relaying analyser results from COM3` (its COM port), and a result sent at night,
after the PC has gone untouched for longer than its sleep timer, reaches the worklist. If the agent's line is not
listed, the Application event log (source *HyFib HC360 Lab Agent*) says why: set the PC never to sleep then
(`powercfg /change standby-timeout-ac 0`). For analysers on the
**network**, the lab PC needs nothing more than HC360 Desktop: each analyser connects to the server, with a fixed
address, through the firewall rule `-LabPorts` and `-LabAnalyserAddresses` open (DEPLOYMENT.md section 6).

### The release itself

The release installed on 23 November must include .NET's security release of 10 November 2026 (runtime 10.0.15):
the advisories published that day (one of them high) affect the runtime of earlier builds. It is built with the .NET
SDK 10.0.404 (`global.json`), which carries it, and the server takes its runtime with it (self-contained).
*Check:* once installed, on the server:

```powershell
(Get-Item "$env:ProgramFiles\HyFib\HealthCare360\Server\Microsoft.AspNetCore.dll").VersionInfo.ProductVersion
```

starts with 10.0.15 or later.
