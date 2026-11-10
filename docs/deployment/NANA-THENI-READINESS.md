# Nana Hospitals, Theni: the drug registers and the server machine

**Status: assessment, before installation.** Written on 10 November 2026 from four photographs sent from the site (two
registers printed by the pharmacy's current software, and two Settings › About screens of the machine meant to run
HC360) and from the code as it stands. A photograph does not show everything that decides go-live. What it cannot show
is marked **to confirm**, and [`Test-HC360ServerNode.ps1`](../../deploy/windows/Test-HC360ServerNode.ps1) reads it from the
machine (section 4, step 1).

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

These matter before any data is moved into HC360:
- **A drug in the wrong register.** A tramadol and paracetamol tablet (TAB ULTRACET) is on the *Schedule H* printout.
  Tramadol has been in Schedule H1 since the 2013 notification, and HC360's catalogue seeds it as H1 and controlled
  ([ItemCatalogSeed.cs](../../src/server/Modules/HyFib.HealthCare360.Modules.SupplyChain/Seed/ItemCatalogSeed.cs)). The
  current software classes it as H (that printout is filtered to "Medicine Type: Schedule-H"), so its supplies have
  gone to the H register and not to H1. Loaded into
  HC360 as it is, that item master would make HC360 repeat the mistake. When the item master is moved, set each item's
  schedule from its molecule against the H1 list; do not copy it. The pharmacist-in-charge decides what to do about the
  entries already made.
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
| Processor | Intel Core i5-12400: 6 cores, 12 threads | 8 cores ([DEPLOYMENT.md](DEPLOYMENT.md) section 1; SRS 3.2 Medium, up to 75 users at once). SRS Small, up to 25 users: 6 cores | Meets Small, short of Medium. The load test decides |
| Memory | 32 GB (31.7 GB usable), 3200 MT/s | 32 GB (Medium) | Meets |
| Storage | 1.39 TB in all, 218 GB used | A 1 TB NVMe SSD (mirrored for Medium), plus a second disk or NAS for backup copies | **To confirm.** 1.39 TB is not a usual size for one drive, so there are probably several disks. Which one holds Windows, and whether it is an SSD, is not shown. PostgreSQL and HC360's data go on the Windows disk |
| Graphics | Intel UHD 730 | Not used | — |
| Name | DESKTOP-RMI9PCM, a name Windows chose | A fixed name, e.g. HC360-SRV | Rename before installing |
| BitLocker, TPM | Not shown (Windows 11 Pro has both) | On for every fixed disk, with the TPM, before HC360 holds patient data | **To confirm** |
| Network | Not shown | An Ethernet cable to the access point, the Private profile and a DHCP reservation | **To confirm** |
| UPS | Not shown | 30 minutes or more, connected by USB | **To confirm** |
| Use | Everyday programs on the taskbar (browsers, WhatsApp, Office and others) and a desk in use around it | "One dedicated Windows machine" | **Decide** (below) |

### A dedicated server or a desk PC

[DEPLOYMENT.md](DEPLOYMENT.md) asks for one dedicated machine. HC360 Desktop can also run on the server itself ("This
computer", section 5), so one machine can do both. But a PC someone works at all day brings risks a server must not
have:
- **It is switched off at night**, and the backup runs at 01:30. The preflight counts the last 30 days' shutdowns and
  restarts.
- **Everyday programs run on it**: browsers, WhatsApp, downloads and remote-access tools, all on the machine that holds
  the keys to every patient record.
- **Power cuts.** Without a UPS on USB, a cut can damage the database. The preflight counts the stops Windows did not
  expect.

Make it the dedicated server, and have its user work at another PC. If the hospital cannot spare it, keep personal
programs off it, give staff a standard (not administrator) Windows account, never switch it off, and put it on a UPS.
Record that decision in writing, because it departs from DEPLOYMENT.md.

### Is a 6-core i5 enough?

That depends on how many staff use HC360 at once at the busiest hour, which is not yet known.
- **Up to 25:** the Small profile. The i5 meets it.
- **26 to 75:** the Medium profile asks for 8 cores. The only load test run so far, on a 4-core, 16 GB development VM,
  met PERF-01 at 75 users (p95 171 ms, [LOAD-TESTS.md](../testing/LOAD-TESTS.md)), so the i5-12400 may well be
  enough. The load test on this machine decides, before go-live.

The documents disagree on sizing. DEPLOYMENT.md section 1 gives 8 cores and 32 GB for "up to about 250 concurrent
users", but SRS 3.2 gives the same as Medium (up to 75 users) and asks 12 cores and 64 GB for Large (up to 150). The
preflight follows the SRS.

## 4. Deploying to this machine, step by step

Each step ends with a check, and the next step starts only once it passes.

1. **Read the machine.** On it, as Administrator, from the release's `deploy` folder, run:

   ```powershell
   .\Test-HC360ServerNode.ps1 -ConcurrentUsers <staff at once at the busiest hour>
   ```

   It changes nothing. Send its JSON report to HyFib.
   *Check:* no Fail, and a decision written beside each Warn.
2. **Copy the current software's data first.** If the preflight finds that software's database on this machine (under
   *Database engines*), back it up to a copy kept off the machine before anything is installed. It is the source for
   HC360's item master and for the parallel run. If the database is elsewhere, find where.
3. **Prepare Windows:**
   - Rename the machine HC360-SRV and restart.
   - Run Windows Update until nothing is pending.
   - Turn on BitLocker for every fixed disk, with the TPM, and keep the recovery keys in the hospital's password safe.
   - Use an Ethernet cable to the access point if at all possible, set the Private profile, and reserve the address the
     report gives for its MAC address.
   - Connect the UPS by USB.
   - Set the time zone to India Standard Time.
   - Keep one remote-access tool, protected, and remove the rest.

   *Check:* run the preflight again. Nothing fails, and BitLocker passes.
4. **Check the release files** with `Get-FileHash` against `SHA256SUMS.txt` (DEPLOYMENT.md section 1).
5. **Install PostgreSQL** with `.\Install-PostgreSQL.ps1` (DEPLOYMENT.md section 2). Then measure how fast its disk
   commits, with PostgreSQL's own tool:

   ```powershell
   & "$env:ProgramFiles\PostgreSQL\17\bin\pg_test_fsync.exe" -s 3 -f "$env:ProgramData\pg_test_fsync.out"
   ```

   *Check:* write the figures down. A hard disk manages at most a few hundred flushes a second, an SSD thousands.
6. **Run the load test on this machine, before any real data** ([LOAD-TESTS.md](../testing/LOAD-TESTS.md)). Use a test
   installation with demonstration data and the hospital's number of users.
   *Check:* PERF-01 is met (p95 of 300 ms or less for reads, 600 ms or less for writes). Then remove the test server's
   data as LOAD-TESTS.md says.
7. **Install HC360** (DEPLOYMENT.md section 3):

   ```powershell
   .\Install-HC360Server.ps1 -CreateDatabase -FacilityCode NH -FacilityName 'Nana Hospitals' `
       -SecondaryBackupDir '<the second disk or the NAS share>'
   ```

   Write down the CA fingerprint and the first administrator password.
   *Check:* Administration › System health shows nothing red.
8. **Run a restore drill before go-live.** Let the first night's backup run, check it with `backup verify`, and restore
   it on a spare PC with `Restore-HC360.ps1` (DEPLOYMENT.md section 8).
   *Check:* the restored copy opens and holds what the original holds.
9. **Load the item master** from the current software's export. Set each scheduled drug's schedule from its molecule
   against the H1 list, and fill in its manufacturer.
   *Check:* the pharmacist-in-charge reviews the H1 and X list before it is used.
10. **Install the workstations** (DEPLOYMENT.md section 5), the pharmacy's first.
11. **Run the registers in parallel** (section 2 above).
    *Check:* the pharmacist-in-charge signs them off.

## 5. Who or what shows each step is right

None of these depends on HyFib's word:

| Claim | Shown by |
|---|---|
| The machine is fit | The preflight's report, read from the machine itself; then the installer's own checks and Administration › System health |
| It is fast enough | The load test on this machine (PERF-01), and pg_test_fsync's figures for its disk |
| The data survives a failure | A backup verified, then restored on another PC |
| The registers are right | The day-by-day reconciliation against the current software's printouts, signed by the pharmacist-in-charge, and then the Drugs Inspector |
| Each drug is in the right schedule | The Schedule H1 list (G.S.R. 588(E), 2013, and later amendments), molecule by molecule |

## 6. Questions for the hospital

1. How many staff use the system at once at the busiest hour? The answer picks the Small or Medium profile, and says
   whether 6 cores are enough.
2. Is this PC spare, or someone's desk PC? If it is a desk PC, where will that person work instead?
3. Is there a UPS with USB, a NAS or a USB disk for backup copies, and an Ethernet point near the access point?
4. Where is the current pharmacy software's database? Can it export the item master and a day's sales?
5. Does the pharmacist-in-charge want Doctor before Patient, and "Drug Report" as the title? Which signature lines?
6. What should be done about the tramadol supplies recorded in the H register so far?
