# Nana Hospitals, Theni: server and backup hardware to buy

**Status: procurement specification, revised 14 November 2026** for the backups as they now work on `main`: documents
kept once beside the backups ([incremental-backup.md](../design/incremental-backup.md)), and the built-in off-site copy
to a bucket in India (DEPLOYMENT.md section 8, "The off-site copy"). It follows
[NANA-THENI-READINESS.md](NANA-THENI-READINESS.md) (40 staff at once, a cabled LAN, no UPS, no backup disks, and the
recommendation of a dedicated server) and sizes each item from SRS 3.2 (the Medium profile), DEPLOYMENT.md sections 1,
8 and 9, and the backup code. Figures marked **assumed** are to be replaced by measurement (section 6). Section 3
compares keeping the off-site copy on USB disks, in the cloud, or hosting HC360 in the cloud.

## 1. What decides the sizes

How HC360's backups use space (`src/server/HyFib.HealthCare360.Platform/Jobs/Backup.cs`, DEPLOYMENT.md section 8):

1. **Each night's backup** is an encrypted archive holding the database dump (`pg_dump --format=custom --compress=6`),
   the keys, and the list of documents it needs.
2. **Each stored document is kept once**, under `blobs\` beside the backups, and each night copies only the day's new
   documents. A document no retained backup needs is removed with the last backup that needed it.
3. **30 backups are kept** (`HealthCare360:Backup:RetentionDays`), in the server's backup folder and on the USB disk.
4. **The off-site bucket** holds the documents in weekly generations, because HC360's key may not delete there: about
   six copies of the documents, plus one dump a night for 35 days (`retainDays`).
5. **The server's backup folder is on the Windows disk by default**
   (`%ProgramData%\HyFib\HealthCare360\backups`). Put it on its own disks (`HealthCare360:Backup:Directory`): at year 3
   the folder alone is about 0.73 TB.

So a location holds about **(all documents) + 30 × (one dump)**, and the bucket about **6 × (all documents) + 35 ×
(one dump)**. The dumps are now the largest term on the server: 30 × 15 GB = 450 GB of the 0.73 TB at year 3.

### Growth, assumed until measured

The documents HC360 stores are patient photographs, scanned uploads at the front office, outsourced lab reports,
cardiology uploads (ECG and echo PDF, DICOM, SCP-ECG), consent and against-advice declarations, record copies and HR
credentials. Bills, prescriptions and reports are made into PDFs when asked for, and are not stored.

**Baseline (assumed):** 0.25 GB of new documents a day (about 150 scans, 100 photographs, ECG PDFs), averaging about
200 KB each, and a database dump of about 5 GB after the first year, 10 GB after the second and 15 GB after the third.
If echo DICOM cine loops are uploaded into HC360, the documents grow about three times as fast.

| Where | After 1 year | 2 years | 3 years | 3 years, in the month after a data-key rotation |
|---|---|---|---|---|
| Documents (the `files` folder) | 91 GB | 183 GB | 274 GB | 274 GB |
| Server's backup folder (D:) | 0.24 TB | 0.49 TB | 0.73 TB | 1.0 TB |
| Each USB disk (swapped weekly) | 0.17 TB | 0.34 TB | 0.51 TB | 0.79 TB |
| Off-site bucket | 0.72 TB | 1.45 TB | 2.2 TB | — |
| Live data on the system SSD (C:): Windows, PostgreSQL, documents, WAL kept for a standby, the temporary dump while a backup runs | ~250 GB | ~400 GB | ~550 GB | — |

A data-key rotation re-encrypts every document, so for 30 days the old copies stay beside the new ones.

## 2. What to buy

### A. Primary Server Node: must buy

A tower server (the class of a Dell PowerEdge T360, HPE ProLiant ML30 Gen11 or Lenovo ThinkSystem ST250 V3), not a
desktop PC: ECC memory, enterprise disks with power-loss protection, a RAID controller and a warranty with next-day
on-site service.

| Item | Specification | Why |
|---|---|---|
| Processor | Intel Xeon E-2400 series, **8 cores / 16 threads** (e.g. E-2478 or E-2488), 3 GHz+ | SRS 3.2 Medium: 8 cores. The current i5-12400 has 6 |
| Memory | **32 GB DDR5 ECC**, as 2 × 16 GB (two slots left free for 64 GB later) | SRS Medium: 32 GB. ECC because a flipped bit in a database page is silent damage. The load test decides the upgrade |
| System and data disks (C:) | **2 × 1.92 TB enterprise SSD** (NVMe or SATA, read-intensive, *with power-loss protection*), **RAID-1** on the hardware RAID controller | SRS Medium asks 1 TB mirrored. 1.92 TB holds the live data for 5 years at the baseline (~0.9 TB) with the 10 % free System health wants. Power-loss protection lets PostgreSQL's flushes complete safely and quickly (`pg_test_fsync` shows it) |
| Backup and WAL disks (D:) | **2 × 4 TB enterprise 7,200 rpm SATA HDD, RAID-1** | The server's 30 nights of backups (0.73 TB at year 3, 1.0 TB after a key rotation, about 1.5 TB at year 5), the WAL archive (≥ 1.5 GB a day) and weekly base backups, if point-in-time recovery is turned on (DEPLOYMENT.md section 9). On disks other than the database's, so one failure does not take both |
| RAID controller | Hardware RAID with a cache protected by battery or flash (PERC H355 / HPE MR408i class) | Mirrors both pairs; a failed disk is replaced while running |
| TPM | **TPM 2.0 module fitted** (often an option on tower servers: check the quote) | BitLocker with the TPM on every fixed disk before patient data (DEPLOYMENT.md section 1, SRS SEC-05) |
| Network | 2 × 1 GbE onboard; one cable to a gigabit port of the core switch | DEPLOYMENT.md section 1: 1 Gbps passes, 100 Mbps warns |
| Remote management | iDRAC / iLO / XCC basic, on its own port, on the hospital LAN only, never the internet | IT can power-cycle and see the console without a remote-access tool on Windows |
| Power supply | Redundant, hot-plug (2 ×) | One failed supply does not stop the hospital |
| Operating system | **Windows Server 2025 Standard** (16-core base licence) | See note below |
| Warranty | 3 or 5 years, next business day on site, keep failed disks (disks hold patient data) | |

**Windows licence (to confirm with a Microsoft reseller).** The SRS allows Windows 11 Pro, but Windows 11's licence
limits how many other devices may use software on it, and 40 or more workstations connect to this server all day.
Windows Server 2025 Standard is the clean licence for a server; ask the reseller whether Client Access Licences are
needed for the hospital's users or devices, and buy them if so.

### B. Backup copies that leave the server: must buy

| Item | Specification | Why |
|---|---|---|
| Rotated USB disks | **2 × 2 TB external USB 3.x HDD, formatted NTFS**, given the same drive letter (`E:`), one connected and one off site, swapped weekly in the day | NANA-THENI-READINESS.md section 3. 0.51 TB at year 3, 0.79 TB after a key rotation. NTFS, not exFAT, which wastes up to 128 KB on every document. Each disk restores on its own: a disk back from off site catches up on the documents it missed |
| Off-site bucket | An S3-compatible bucket in India (AWS or OCI, Mumbai or Hyderabad), with Object Lock; see section 3 | The copy that survives fire, theft and ransomware without anyone swapping disks |
| Upload capacity | A night-time upload of **10 Mbps sustained now, 20 Mbps by year 2, 30 Mbps by year 3** (section 3) | Each week's first backup uploads every document; it must be off site within a day |
| A fire-resistant safe or off-site location | For the disk off site, the backup recovery key and the exported hospital CA, kept apart | A building fire must not take the server, the backups and the key together |

With A and B the hospital has the 3-2-1 rule and more: the database, the server's own backups, the USB disks and the
bucket, on SSD, HDD and cloud storage, two of them out of the building, one of them locked so that nobody can delete
it.

### C. Power: must buy

| Item | Specification | Why |
|---|---|---|
| Server UPS | **2 kVA online (double-conversion) UPS, pure sine wave, with a USB HID port** that Windows shows as a battery, plus an external battery pack for **≥ 30 minutes at the measured load** (server, monitor and core switch: about 250–350 W) | SRS 3.2, every profile: 30 minutes on USB. Online, not line-interactive, because of voltage swings and the generator changeover. HID matters: HC360 reads power cuts from Windows' battery, not from the maker's software |
| Network UPS | **1 kVA line-interactive UPS** in each rack or closet with switches or access points between the server and the wards, and for the internet router | A server on its UPS reaches nobody once its switch is off (DEPLOYMENT.md section 1), and uploads nothing once the router is off |

### D. Warm standby: recommended, second phase

Without a standby, a failed server means new hardware, a restore of the last nightly backup, up to a day of data lost
and hours of work (SRS REL-02). With one, about half an hour and under a second's worth of data (DEPLOYMENT.md
section 9, SRS DED-20).

A second machine like A, with: the same CPU and 32 GB ECC; **2 × 1.92 TB enterprise SSD RAID-1**; TPM 2.0; the same
Windows; its own **1 kVA online UPS**. It needs no backup disks: on a failover it reads the keys and documents from the
USB disk or the bucket. Put it in another room or block from the primary.

Not recommended as the standby: the current i5-12400 PC. It is someone's desk PC, switched off at night, without ECC
or a UPS, and a standby must be on, untouched, all the time.

### E. Not included

- **PACS image storage.** HC360 links to a PACS but stores no images (SRS section 10, out of scope). If the hospital
  installs Orthanc or buys a PACS, size its storage separately (a CR study is 10–30 MB, a CT 100–500 MB).
- Workstations: the lab PC and the others are assessed in NANA-THENI-READINESS.md section 7.

## 3. On premises or cloud: where the off-site copy lives

HC360 runs inside the hospital and keeps working without the internet, so the server and its own backups (section 2,
A) are needed whatever is chosen. The choice is where the copies that leave the building live. Hosting HC360 itself
in the cloud is [CLOUD-HYBRID-PLAN.md](CLOUD-HYBRID-PLAN.md); it is included for completeness.

| | A. On premises only (section 2, A and B without the bucket) | B. A, plus HC360's off-site copy to a bucket | C. Cloud-hosted HC360 (CLOUD-HYBRID-PLAN, not built) |
|---|---|---|---|
| A failed disk | Survived: mirrored SSDs and HDDs | As A | Survived: managed database with a standby |
| Ransomware on the server | Partly: the disk off site is untouched; the connected disk and the server's own copies can be encrypted | Survived: each copy is locked for 35 days in compliance mode, and HC360's key cannot delete | Survived: provider snapshots |
| Fire, flood or theft at the hospital | Up to 7 days lost (the off-site disk's age) | Up to 1 day lost: each verified backup is uploaded within 15 minutes | Seconds lost |
| Time to recover | ~4 h on the same hardware (SRS REL-02); on new hardware, the time to buy it | As A, plus the download: ~6.5 h for year 3's 289 GB (a dump and its generation's documents) at 100 Mbps | Under 2 min in Mumbai; under 1 h to Hyderabad |
| Internet down | Works fully | Works fully; uploads resume where they stopped | Read-only, plus a limited set of entries captured offline; needs two ISPs and 5G |
| Upfront (indicative) | ₹5.3–7.7 lakh | As A | ₹2–4 lakh edge servers, plus migration |
| Running cost | Disks every 4–5 years; staff time for the weekly swap | + about ₹2,500–7,500 a month, growing with the data (below) | ₹70,000–1,15,000 a month |
| Relies on people | The weekly swap (a missed one is alerted after 3½ days) | Nothing to swap for the bucket; System health alerts when uploads stall | On-call cloud operations |
| Data stays in India (DPDP) | Yes | Yes: AWS or OCI, Mumbai or Hyderabad. Encrypted with the recovery key before upload; the store never has it | Yes, Mumbai and Hyderabad |
| Erased data leaves the backups within | 31 days | 44 days (the privacy notice says so while the off-site copy is on) | Per the plan |
| Ready in HC360 | Yes | Yes, on `main`: Administration › Backups › Off-site copy, approved by a second administrator; uploads resume after a cut; System health warns "No copy of the backups leaves the building" on a live server without it | No: 6–9 months of work in phases |

**Recommendation: B from go-live**, if the hospital's uplink carries it (below), keeping the two USB disks as the copy
no network can reach. C only with several branches sharing one record, or if the hospital does not want a server room
(CLOUD-HYBRID-PLAN section 1).

### The upload link

Each night uploads that night's dump and the day's new documents: 5–15 GB. Once a week a new generation starts, and
that night's backup uploads **every document** before the backup itself. System health turns critical when the newest
verified backup is still not off site a day after it was made, so the generation's documents and the dump must go up
within 24 hours:

| | Year 1 | Year 2 | Year 3 |
|---|---|---|---|
| The generation night: all documents + one dump | 96 GB | 193 GB | 289 GB |
| Sustained upload needed to finish within 24 h | 9 Mbps | 18 Mbps | 27 Mbps |

Buy, or confirm, **at least 10 Mbps of upload now and 30 Mbps by year 3**, and measure it at night (section 6). On a
slower line the uploads still finish, part by part, but the critical alert comes every week.

### What the bucket costs

At the baseline, on AWS Mumbai, S3 Standard (HC360 sets no storage class), counting the weekly upload of every
document as one request each at about 200 KB a document:

| | Year 1 | Year 2 | Year 3 |
|---|---|---|---|
| Stored (6 generations of documents + 35 dumps) | 0.72 TB · ₹1,600 a month | 1.45 TB · ₹3,200 | 2.2 TB · ₹4,800 |
| Upload requests (every document, every week) | ~2 million · ₹900 a month | ~4 million · ₹1,800 | ~6 million · ₹2,700 |
| **Total a month** | **~₹2,500** | **~₹5,000** | **~₹7,500** |
| One restore download (a dump and its documents) | 96 GB · free | 193 GB · ~₹900 | 289 GB · ~₹1,800 |

At about US$0.025 per GB-month, US$0.005 per 1,000 uploads, US$0.109 per GB downloaded beyond 100 GB a month, and ₹88
per US$. These are remembered list prices, **to re-quote** in the AWS or OCI calculator. OCI charges far less per
request and nothing to download at this size, so it is likely the cheaper store here. The request line depends on the
average document size, which **must be measured** (incremental-backup.md, Appendix A): smaller documents mean more
requests.

### Setting up the off-site copy

Follow DEPLOYMENT.md section 8, "The off-site copy". In short:

1. With the hospital's own administrator account for the store, create the bucket in India with Object Lock, public
   access blocked, SSE-S3 encryption (not KMS keys), and the two lifecycle rules (backups 35 days, documents 42).
2. Make an access key for HC360 alone that may upload and read back but not list, delete or change the bucket. Keep
   the store's administrator credentials off the Server Node.
3. Administration › Backups › **Off-site copy**: switch it on with the store address, region, bucket, folder `hc360/`
   and the key. A second administrator approves it.
4. **Back up now**: within 15 minutes the backup's **Off site** column says Yes.

## 4. Settings the purchase needs

At installation (NANA-THENI-READINESS.md section 4, step 7):

```powershell
.\Install-HC360Server.ps1 -CreateDatabase -FacilityCode NH -FacilityName 'Nana Hospitals' -SecondaryBackupDir 'E:\HC360-Backups'
```

then, in `%ProgramData%\HyFib\HealthCare360\config\hc360.settings.json`, move the server's own backups to the 4 TB
mirror and restart the `HC360Server` service:

```json
{ "HealthCare360": { "Backup": { "Directory": "D:\\HC360-Backups" } } }
```

Then set up the off-site copy (section 3). If point-in-time recovery is wanted:
`.\Enable-HC360Replication.ps1 … -WalArchiveDirectory D:\HC360-WAL` (DEPLOYMENT.md section 9). BitLocker on C: and D:,
not on the USB disks (HC360 encrypts what is on them). Copy whole backup folders, never a `.hc360bak` file alone: a
backup without its `blobs\` folder cannot put the documents back.

## 5. Decisions for the hospital

1. **Keep 30 days of backups, or fewer?** With documents kept once, retention now mostly sizes the dumps (450 GB of
   the server's 0.73 TB at year 3). 14 days would save about a third of the backup disks and shorten how long erased
   data stays in backups (SRS PRV-07), so the privacy notice would change too.
2. **Echo DICOM into HC360 or into a PACS?** In HC360 it triples the documents' growth, and the bucket holds six copies
   of every document. In a PACS, HC360 keeps a link.
3. **Which store, and what uplink?** AWS or OCI, in Mumbai or Hyderabad, and an upload of at least 10 Mbps now and
   30 Mbps by year 3 (section 3).
4. **A warm standby now, or in a second phase?**

## 6. What shows the purchase is right

None of these depends on the vendor's or HyFib's word:

| Claim | Shown by |
|---|---|
| The server meets SRS Medium | `Test-HC360ServerNode.ps1 -ConcurrentUsers 40`: no Fail; cores, RAM, NVMe/SSD, TPM, BitLocker, UPS on USB, second disk, 1 Gbps all pass |
| It is fast enough for 40 staff | The load test at 75 users on this hardware (docs/testing/LOAD-TESTS.md): p95 ≤ 300 ms reads, ≤ 600 ms writes |
| The SSDs flush safely and fast | `pg_test_fsync`: thousands of flushes a second on the RAID-1 SSDs, hundreds at most on a disk without power-loss protection |
| The UPS holds 30 minutes and HC360 sees it | Mains off with the server under load: the time to the UPS's low-battery shutdown, and Administration › System health showing the power cut |
| The disks are big enough | Run the queries of incremental-backup.md Appendix A before go-live and after a month; then the backup folder's size each quarter against section 1 |
| The backups restore | `backup verify --in` on the USB disk at each weekly swap; a restore on a spare machine before go-live and every quarter (DEPLOYMENT.md section 8) |
| A failed disk is survived | Pull one SSD and one HDD of each mirror at go-live: Windows and HC360 keep running, and the controller rebuilds onto the disk put back |
| The off-site copy works and cannot be deleted | The **Off site** column says Yes within 15 minutes of **Back up now**. With the store's own credentials, download the newest backup to a spare PC, `backup verify` and restore it, and time it. Try to delete it with HC360's key: refused |
| The uplink carries it | An upload speed test at night; then, after the first generation night, the Off site time of that backup against section 3, and no `backup.offsite_stale` critical warning |
