# Nana Hospitals, Theni: server and backup hardware to buy

**Status: procurement specification, 14 November 2026.** It follows
[NANA-THENI-READINESS.md](NANA-THENI-READINESS.md) (40 staff at once, a cabled LAN, no UPS, no backup disks, and the
recommendation of a dedicated server) and sizes each item from SRS 3.2 (the Medium profile), DEPLOYMENT.md sections 1,
8 and 9, and from how the backup code actually behaves. Figures marked **assumed** are to be replaced by measurement
(section 5).

## 1. What decides the sizes

Three facts from the code set the backup storage, and they are not in the SRS table:

1. **Every nightly backup is a full copy**: the whole database dump (`pg_dump --format=custom --compress=6`) *and every
   stored document*, encrypted with AES-256-GCM (`src/server/HyFib.HealthCare360.Platform/Jobs/Backup.cs`). There are no
   incremental backups. Encrypted files do not compress or deduplicate, so neither a NAS nor a disk can shrink them.
2. **30 copies are kept in both places.** `CleanupOld` deletes files older than `HealthCare360:Backup:RetentionDays`
   (30) in the server's own backup folder and in the USB/NAS copy folder alike.
3. **The server's own backup folder is on the Windows disk by default** (`%ProgramData%\HyFib\HealthCare360\backups`),
   beside PostgreSQL. The installer has no parameter for it; it is `HealthCare360:Backup:Directory` in
   `hc360.settings.json`. A backup that would not fit is refused (`EnsureRoomAsync`), so the disk does not fill to zero,
   but from then on **no backup is made** until space is freed.

So backup space is about **30 × (database dump + all documents)**, and it grows every day the hospital adds documents.

### Growth, assumed until measured

The documents HC360 stores are patient photographs, scanned uploads at the front office, outsourced lab reports,
cardiology uploads (ECG and echo PDF, DICOM, SCP-ECG), consent and against-advice declarations, record copies and HR
credentials. Bills, prescriptions and reports are made into PDFs when asked for, and are not stored.

| Scenario | Documents a day | Dump growth a year | One backup after 1 year | 2 years | 3 years |
|---|---|---|---|---|---|
| Low | 0.1 GB | 3 GB | 40 GB | 80 GB | 120 GB |
| **Baseline (assumed)** | **0.25 GB** (~150 scans, ~100 photos, ECG PDFs) | **5 GB** | **96 GB** | **193 GB** | **289 GB** |
| High (echo DICOM cine loops uploaded into HC360) | 0.75 GB | 8 GB | 282 GB | 564 GB | 846 GB |

Space needed at the **baseline**:

| Where | Copies held | After 1 year | 2 years | 3 years |
|---|---|---|---|---|
| Server's own backup folder (30 nights + backups before updates, +10 %) | ~33 | 3.2 TB | 6.4 TB | 9.5 TB |
| Each of two USB disks swapped weekly (connected about 3 of the 30 days' weeks) | ~22 | 2.1 TB | 4.2 TB | 6.4 TB |
| Live data on the system SSD (Windows, PostgreSQL, documents, WAL kept for a standby, the temporary dump while a backup runs) | — | ~250 GB | ~400 GB | ~550 GB |

**Consequence:** with the SRS's 1 TB disk alone and the default folder, backups stop within about 1 to 3 months
(baseline: about 3 months; high: about 5 weeks). The purchase below puts the backups on their own disks.

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
| Backup and WAL disks (D:) | **2 × 8 TB enterprise 7,200 rpm SATA HDD, RAID-1** | The server's own 30 nights of backups (about 2½ years at the baseline), the WAL archive (≥ 1.5 GB a day) and weekly base backups, if point-in-time recovery is turned on (DEPLOYMENT.md section 9). On disks other than the database's, so one failure does not take both |
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
| Rotated USB disks | **2 × 8 TB external USB 3.x HDD**, given the same drive letter (`E:`), one connected and one off site, swapped weekly in the day | NANA-THENI-READINESS.md section 3. 8 TB holds about 22 nights for 3 years at the baseline (6.4 TB). Encrypted by HC360, so a disk taken home is unreadable without the recovery key |
| A fire-resistant safe or off-site location | For the disk off site, the backup recovery key and the exported hospital CA, kept apart | A building fire must not take the server, the backups and the key together |

With A and B the hospital has the 3-2-1 rule: three copies (the database, the server's own backups, the USB disks), on
two kinds of media (SSD and HDD), one off site. The off-site copy is up to 7 days old; swap twice a week if that is too
much to lose in a fire.

### C. Power: must buy

| Item | Specification | Why |
|---|---|---|
| Server UPS | **2 kVA online (double-conversion) UPS, pure sine wave, with a USB HID port** that Windows shows as a battery, plus an external battery pack for **≥ 30 minutes at the measured load** (server, monitor and core switch: about 250–350 W) | SRS 3.2, every profile: 30 minutes on USB. Online, not line-interactive, because of voltage swings and the generator changeover. HID matters: HC360 reads power cuts from Windows' battery, not from the maker's software |
| Network UPS | **1 kVA line-interactive UPS** in each rack or closet with switches or access points between the server and the wards | A server on its UPS reaches nobody once its switch is off (DEPLOYMENT.md section 1) |

### D. Warm standby: recommended, second phase

Without a standby, a failed server means new hardware, a restore of the last nightly backup, up to a day of data lost
and hours of work (SRS REL-02). With one, about half an hour and under a second's worth of data (DEPLOYMENT.md
section 9, SRS DED-20).

A second machine like A, with: the same CPU and 32 GB ECC; **2 × 1.92 TB enterprise SSD RAID-1**; TPM 2.0; the same
Windows; its own **1 kVA online UPS**. It needs no 8 TB disks: on a failover it reads the keys and documents from the USB
disk. Put it in another room or block from the primary.

Not recommended as the standby: the current i5-12400 PC. It is someone's desk PC, switched off at night, without ECC
or a UPS, and a standby must be on, untouched, all the time.

### E. Not included

- **PACS image storage.** HC360 links to a PACS but stores no images (SRS section 10, out of scope). If the hospital installs Orthanc
  or buys a PACS, size its storage separately (a CR study is 10–30 MB, a CT 100–500 MB).
- Workstations: the lab PC and the others are assessed in NANA-THENI-READINESS.md section 7.

## 3. Settings the purchase needs

At installation (NANA-THENI-READINESS.md section 4, step 7):

```powershell
.\Install-HC360Server.ps1 -CreateDatabase -FacilityCode NH -FacilityName 'Nana Hospitals' -SecondaryBackupDir 'E:\HC360-Backups'
```

then, in `%ProgramData%\HyFib\HealthCare360\config\hc360.settings.json`, move the server's own backups to the 8 TB
mirror and restart the `HC360Server` service:

```json
{ "HealthCare360": { "Backup": { "Directory": "D:\\HC360-Backups" } } }
```

If point-in-time recovery is wanted: `.\Enable-HC360Replication.ps1 … -WalArchiveDirectory D:\HC360-WAL`
(DEPLOYMENT.md section 9). BitLocker on C: and D:, not on the USB disks (HC360 encrypts what is on them).

## 4. Decisions for the hospital

1. **Keep 30 days of backups, or fewer?** 30 is the default and is named in the privacy notice as how long erased data
   stays in backups (SRS PRV-07). 14 days halves the backup disks and shortens that period; changing it needs the
   privacy notice changed too.
2. **Echo DICOM into HC360 or into a PACS?** In HC360, it triples the growth (the high scenario) and every one of those
   files is copied 30 times. In a PACS, HC360 keeps a link.
3. **A warm standby now, or in a second phase?**

## 5. What shows the purchase is right

None of these depends on the vendor's or HyFib's word:

| Claim | Shown by |
|---|---|
| The server meets SRS Medium | `Test-HC360ServerNode.ps1 -ConcurrentUsers 40`: no Fail; cores, RAM, NVMe/SSD, TPM, BitLocker, UPS on USB, second disk, 1 Gbps all pass |
| It is fast enough for 40 staff | The load test at 75 users on this hardware (docs/testing/LOAD-TESTS.md): p95 ≤ 300 ms reads, ≤ 600 ms writes |
| The SSDs flush safely and fast | `pg_test_fsync`: thousands of flushes a second on the RAID-1 SSDs, hundreds at most on a disk without power-loss protection |
| The UPS holds 30 minutes and HC360 sees it | Mains off with the server under load: the time to the UPS's low-battery shutdown, and Administration › System health showing the power cut |
| The disks are big enough | After the first week, Administration › Backups shows each backup's size. Replace the baseline's 96 GB a year with (size today ÷ days since go-live × 365) and redo section 1. Then every quarter |
| The backups restore | `backup verify --in` on the USB disk at each weekly swap; a restore on a spare machine before go-live and every quarter (DEPLOYMENT.md section 8) |
| A failed disk is survived | Pull one SSD and one HDD of each mirror at go-live: Windows and HC360 keep running, and the controller rebuilds onto the disk put back |
