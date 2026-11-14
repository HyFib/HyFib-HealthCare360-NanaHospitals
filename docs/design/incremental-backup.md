# Incremental document backup: measurements, failure model, design

Status: **Step 0 (design), for review before implementation.** Nothing here is built yet.

Problem: every nightly backup (`BackupService`, `src/server/HyFib.HealthCare360.Platform/Jobs/Backup.cs`) holds the
database dump **and every stored document**, and 30 are kept in each location, so backup storage is about
30 × (dump + all documents) per location. Stored documents almost never change, so they need to be copied once, not
every night.

Some of what follows corrects the plan as first proposed. The corrections are marked **Changed after reading the code**.

---

## 1. What the code does today (the facts the design rests on)

| Fact | Where | Consequence for the design |
|---|---|---|
| Documents are stored **already encrypted**: `EncryptedFileStore.SaveAsync` writes `IFieldProtector.ProtectBytes(content, "files")` to `files/yyyy/MM/<file-id>.bin`. The format is a version byte, an 8-byte key id (not for the original key), a **random 96-bit nonce**, a 16-byte tag, then AES-256-GCM ciphertext. | `Platform/Files/FileStore.cs`, `Platform/Security/FieldProtection.cs:351` | A document file can go into the backup folder **as it is**. Without the field keys it is unreadable, and every archive escrows those keys under the recovery key. No second encryption layer is needed. |
| The AAD is only the purpose string (`"files"`), not the file id. | `FieldProtection.cs:372` | GCM alone does not stop one document's file from being swapped for another's. The archive's index must bind each storage path to the SHA-256 of its file, and restore must check it. |
| The ciphertext is randomized, so two uploads of the same PDF give different files. | same | Naming a blob by the SHA-256 of its **ciphertext** reveals nothing about the content. **Changed after reading the code:** an HMAC name is unnecessary. Duplicate uploads are not de-duplicated, which is fine because the gain comes from immutability, not de-duplication. |
| A document file changes in one case: after a **data-key rotation**, `FieldKeyMover` rewrites it in place under the new key (temp file + `File.Move(overwrite)`). This happens under an exclusive advisory lock that the backup takes shared. | `Platform/Security/FieldKeyMover.cs:419-495` | A blob must be identified by its content hash, not its path. After a rotation every document gets a new blob once, so for 30 days the store holds about **2× the documents**. Sizing must allow for that. |
| A field key is **retired** (deleted from the server) only once nothing is encrypted with it. Each archive escrows the whole ring as it was at backup time. The current code lists documents *before* exporting the ring. | `KeyRotation.cs:158`, `Backup.cs:245-248` | Invariant: every blob an archive references is encrypted under a key in **that archive's** ring. The key id can be read from the blob's first bytes without decrypting, so `verify` can check this. Keep the order: list documents, then export the ring. |
| The **recovery key** encrypts only the archive (`HC360BK3` envelope). The server holds only the current recovery key (plus one pending confirmation). | `BackupEncryption.cs`, `BackupRecoveryKey.cs` | **Changed after reading the code:** blobs are not under the recovery key, so rotating it has no effect on blobs. No per-blob data keys or envelope encryption are needed. **But** after a rotation the server cannot decrypt older archives, so it cannot read *their* blob lists to know what is still referenced. See section 5.3. |
| `IFieldProtector.DeriveKey(purpose)` gives a key derived from the **original** master key, which "does not change when the key is rotated" and is never retired. It is escrowed in every archive. | `FieldProtection.cs:63` | A stable key for authenticating plaintext metadata in the backup folder. A restored or failed-over server derives the same one. |
| No code deletes the content of a kept document. `DeleteAsync` soft-deletes the row and keeps the file "for medico-legal retention". `Discard` removes files only for rows rolled back in the same transaction. Erasure (`ErasureService`) erases messages, feedback, nominee and portal account; **it does not touch stored files**. | `FileStore.cs`, `Privacy/Services/ErasureService.cs:49` | Document blobs leave the backups only when a file has left the `files` folder: today after a key-move rewrite or a rollback discard, later after a retention purge or a document erasure if one is added. The design handles this generically: a blob goes once no retained archive references it, within the same R+1 days that `ErasureService.BackupSentence` already promises. Tests have to create this case synthetically. |
| Retention (`CleanupOld`) deletes archives by `File.GetCreationTimeUtc` and a 1-day rule for `.partial` files. | `Backup.cs:427` | File times are rewritten by copies and clocks. Retention must go by the date in the archive name or manifest, and must never delete the newest complete archive in a location. |
| The secondary copy is a single `File.Copy` of the archive. `BackupRun.SecondaryCopied` is **one flag per run**, with no notion of *which* disk. | `Backup.cs:286-298` | With rotated USB disks, each disk holds a different set of archives. Copying blobs must compare against **the disk that is mounted now**, not against a flag. |
| Two nodes can each run **Back up now** while the nightly job runs on the lease holder. `RunAsync` is not serialized across nodes, and DEPLOYMENT.md §8 asks for the same `SecondaryDirectory` on every node. | `Backup.cs:147`, `JobSlotClaim.cs` | Sweeping a shared folder needs a lock: a PostgreSQL advisory lock, plus a lock file for offline tools. |
| `Restore-HC360.ps1` and the failover step pass the archive path straight to `backup decrypt` / `backup restore --in <path>`, which read it in place. | `deploy/windows/Restore-HC360.ps1:110,156`, DEPLOYMENT.md §9 step 3 | A `blobs\` folder next to the archive is found without changing the script's interface. Copying **only** the `.hc360bak` file elsewhere stops working, and the error must say so plainly. |
| `BackupArchive.ReadAsync` refuses any tar entry it does not know ("Unexpected entry"). | `BackupArchive.cs:197` | An **older** server given a new-format archive refuses it rather than restoring it without documents, so it fails closed. **Changed after reading the code:** a new envelope (`HC360BK4`) is unnecessary. The change belongs in the archive payload (`BackupArchive.Format` 2 → 3), and the `HC360BK3` envelope stays. Downgrade consequence: see F17. |
| CI runs the .NET tests on `windows-latest` (`.github/workflows/ci.yml:19,142`). This container has no .NET SDK. | | The NTFS-specific behaviour (rename, timestamps) gets exercised in CI. Implementation steps here need the SDK installed. |
| `docs/deployment/NANA-THENI-HARDWARE.md`, cited in the brief for the sizing numbers, **does not exist** on this branch or on `main`. The hardware facts are in `NANA-THENI-READINESS.md` §2 (storage row, line 121) and §3 (the two USB disks, line 188). | | The doc updates go to `NANA-THENI-READINESS.md` (or a new hardware doc, if you prefer). The brief's 0.25 GB/day has no source in the repository. |

---

## 2. Measurements

### 2.1 What can and cannot be measured from here

This repository and container hold no hospital data. The real document volume at Nana Hospitals, Theni, can only be
measured on their server. The brief's figure of **0.25 GB of documents a day is an assumption**, and its source document
is missing (section 1). The queries in Appendix A measure what the sizing needs, from the database and from the
`files` folder. **Run them on the Nana server (read-only) before go-live, and again after a month of use**, and put the
results in section 2.3.

### 2.2 Sizing model

Let *R* be the retained archives in a location, *D* the dump size, *I* the archive's document index (about 60 bytes per
document, see 5.2), *Docs* the size of the `files` folder now, and *Docs_R* the bytes of document files that changed or
left in the last *R* days (after a data-key rotation that is all of *Docs*; otherwise about 0).

- **Today:** `R × (D + Docs)`
- **Proposed:** `R × (D + I) + Docs + Docs_R`

Using the brief's assumptions (0.25 GB/day of documents). The dump sizes are **derived** by subtracting the documents
from the brief's per-backup totals (96 GB year 1, 289 GB year 3), giving about 5 GB and 15 GB. They are assumptions,
not measurements.

| | Year 1 | Year 3 | Year 3, within 30 days of a data-key rotation |
|---|---|---|---|
| Documents (*Docs*) | 91 GB | 274 GB | 274 GB (+274 GB of old blobs) |
| Server backup folder today, R = 30 | 2.9 TB | 8.7 TB | 8.7 TB |
| Server backup folder proposed, R = 30 | 0.24 TB | 0.73 TB | 1.0 TB |
| Each USB disk today (about 16 of the last 30 nights on each of two weekly-swapped disks) | 1.5 TB | 4.6 TB | 4.6 TB |
| Each USB disk proposed | 0.17 TB | 0.51 TB | 0.79 TB |
| Index *I* (assuming ~200 KB per document, which **must be measured**) | 28 MB/archive | 82 MB/archive | |

(The brief's 9.5 TB and 6.4 TB count a few more archives per location than R = 30, such as pre-migration and manual
backups. The ratio is the same.)

What the model shows beyond the brief:

1. **After this change the dump is the largest term:** 30 × 15 GB = 450 GB of the 0.73 TB in year 3. Fewer retained
   dumps (for example 7 daily + 4 weekly) or PostgreSQL 17 incremental base backups are the next lever. That is out of
   scope here, and it would change the "within *R*+1 days" erasure sentence (`ErasureService.BackupWindowDays`).
2. **The 1 TB Windows disk is still too small at year 3 even with this change:** about 274 GB of live documents, plus
   the database (probably 2–4× its compressed dump, about 30–60 GB), plus 0.73–1.0 TB of backups. The backup folder has
   to be on another disk regardless (`HealthCare360:Backup:Directory`). This change moves the problem from "3 months"
   to "about 2 years" on a shared 1 TB disk. It does not remove it.
3. **USB disks:** 1 TB lasts beyond year 3 except in the month after a data-key rotation. **2 TB NTFS disks** cover
   a 3-year horizon with margin.

### 2.3 Measured at Nana Hospitals

*To fill in from Appendix A:* document count, bytes, average and p50/p90/p99 size, by content type and category;
documents and bytes per day over the last 30 days; dump size per night from `backup_runs`; filesystem and cluster size
of the server disk and of each USB disk.

---

## 3. Failure model

Every row becomes a test (section 7). "Location" means the primary backup folder, or whichever USB disk is mounted at
`Backup:SecondaryDirectory` that night.

| # | What happens | What must hold |
|---|---|---|
| F1 | Power cut or kill while blobs are being written | Each blob is written to `*.tmp`, flushed, then renamed, so a blob present under its final name is complete. The archive is written **after** all its blobs. A crash leaves only orphan blobs, which the next sweep removes under the lock. |
| F2 | Crash after the archive is final, before the secondary copy | As today the run fails the copy. The next run copies its own archive and blobs. Nothing half-copied is ever referenced. |
| F3 | A USB disk that was off site for a week comes back | That night's copy writes the blobs the new archive references and the disk lacks (about a week of documents), then the archive. **Every archive on every disk restores from that disk alone.** |
| F4 | A disk comes back after more than R days | The new archive and its blobs are copied first. Only then does retention remove expired archives and sweep. The newest complete archive in a location is never deleted. |
| F5 | Disk unplugged in the middle of a copy | The archive goes last, so the disk keeps only complete archives plus orphan blobs. The next copy skips blobs already there (same name and length) and resumes. |
| F6 | Disk full | The copy fails with today's warnings (`backup.copy_failed`). The disk stays consistent (as F5). The space check on the primary counts only the new documents, not all of them. |
| F7 | Bit rot or a damaged sector on a USB disk | `backup verify` re-hashes every referenced blob. A scheduled scrub checks the mounted disk's blobs over a rolling window. A bad blob is reported by archive and storage path. |
| F8 | Data-key rotation (documents rewritten) during or between backups | The existing shared/exclusive document lock keeps documents still while they are listed and copied. Rewritten documents get new blob ids, so they are copied once more and the old blobs expire after R days. |
| F9 | A field key is retired | Old archives keep it in their escrowed ring. A new archive never references a blob under it, because retirement requires zero use. `verify` checks each blob's key id (from its header) against the archive's ring. |
| F10 | The recovery key is rotated | New archives use the new key. The server **cannot open** older archives any more, yet sweeping needs their blob lists, which is why each archive has an authenticated plaintext blob list beside it (5.3). Restoring an older archive needs its own recovery key, as today. |
| F11 | Restore on new hardware, or failover to the standby | Blobs are restored **byte for byte**, so their ids do not change. The USB disk and NAS need no recopy. The first backup on the new server fills its own empty local folder with every document once, and the space check must expect that. |
| F12 | Two nodes back up at once (nightly + Back up now) into one shared secondary folder | All writing to and sweeping of a location happens under a PostgreSQL advisory lock for "backup store", plus a lock file (`hc360-store.lock`) that offline tools honour. With no lock there is no sweep. |
| F13 | Another installation (a training server, a restore drill) points at the same folder | A store marker (`hc360-store.json`: installation id from the original-key fingerprint, format version) is written on first use. A server whose fingerprint differs refuses to write or sweep there, and says why. |
| F14 | A document's content leaves the `files` folder (erasure, retention purge, rollback discard, key-move rewrite) | Its blob goes at the first sweep after the last archive referencing it expires: within R+1 days, as `ErasureService.BackupSentence` already states. |
| F15 | A blob list is tampered with, or a blob is deleted by hand | The blob lists carry an HMAC (5.3). One that does not verify, or an archive whose blob list is missing, means **no sweep** in that location and a warning. A missing blob is reported by `verify` and on the Backups screen. |
| F16 | An operator copies only the `.hc360bak` file to the standby | `verify` and `restore` say how many documents are missing and which `blobs` folder they looked in, and change nothing. |
| F17 | An older HC360 reads a new archive (rollback, DEPLOYMENT.md §7 "Going back") | It refuses ("Unexpected entry"). The older version's `CleanupOld` still deletes old archives by date but never sweeps `blobs\`, which only leaks space. The upgrade notes must say that backups made after the upgrade need this version or later to restore. |
| F18 | The USB disk is exFAT | exFAT's default cluster on large disks is 128 KB, so about 64 KB is wasted per blob (about 90 GB at 1.4 million documents), and it has no journal. Disks should be **NTFS**. The preflight and the Backups screen should warn on exFAT. |
| F19 | Clock change, or copies that reset file times | Retention reads the archive's date from its name or manifest. Sweeping goes by references only, never by file times. |
| F20 | Pre-migration backups (database only) | They reference no blobs, and retention treats them as today. |

**What a stolen USB disk reveals, compared with today.** Today one archive hides the number, sizes and dates of the
documents. With loose blobs the disk shows the number of documents, each size (plaintext + 29 or 37 bytes) and, unless
it is cleared, each file's write time. Content and file names stay unreadable without the recovery key (which unlocks
the field keys). Proposed mitigation: set blob timestamps to a fixed value. Size padding is not recommended (it costs
space for little gain). **Decision for you, section 6.**

---

## 4. Prior art: restic and borg

| Their design | Taken? | Why |
|---|---|---|
| Snapshot (here: the archive) written **after** the data it references; a crash leaves only garbage | **Yes** | It is what makes F1, F2 and F5 safe without a journal. |
| Content-addressed blobs named by SHA-256 | **Yes**, of the stored ciphertext | Ciphertext is randomized, so the name reveals nothing (section 1). |
| Prune = mark (all retained snapshots) and sweep, under an **exclusive lock**, refusing if any snapshot is unreadable | **Yes** | F12, F15. This is restic's `prune` behaviour. |
| `check --read-data`: re-read and re-hash everything | **Yes**, as `backup verify` plus a scheduled scrub | F7. |
| Content-defined chunking (split files to deduplicate edits) | **No** | Stored documents are written whole and never edited. The whole file is the unit. |
| Pack files (many blobs in one file) | **No** | Packs exist to save requests on cloud object stores and to hide sizes. On a local disk they make erasure require **repacking**. Loose files make "delete the blob" a single delete. The cost is the file count (about 1.4 million at year 3 under the 200 KB assumption), which NTFS handles in fan-out folders. The first copy to a new disk takes longer. |
| A repository-wide index | **No** | Each archive carries its own index (5.2), so any archive plus the `blobs` folder is enough for a restore. There is no shared index to corrupt or rebuild. |
| Repository key separate from data keys | **Not needed** | The field-key ring escrowed in each archive already plays this role (section 1). |
| borg append-only mode | **No** (for now) | Retention and erasure must be able to delete. Ransomware resistance comes from the off-site disk. |

---

## 5. Design

### 5.1 Layout in each location

```
<Backup:Directory or SecondaryDirectory>\
  hc360-store.json                          store marker (F13)
  hc360-store.lock                          held while writing or sweeping (F12)
  hc360-20270114-013000-scheduled.hc360bak  archive: HC360BK3 envelope, payload format 3
  hc360-20270114-013000-scheduled.refs      authenticated blob list (5.3)
  blobs\ab\cd\abcd…(64 hex).blob            one stored document file, byte for byte
  erasure-ledger.log                        unchanged; never touched by retention or sweeping
```

### 5.2 Archive payload, format 3

The same tar inside the same `HC360BK3` envelope: manifest, keys, `database.dump`, as today. The `files/…` entries are
replaced by **one** entry, `documents.idx`. It is a compact binary list, one record per document: storage path, SHA-256
of the stored file, length. It is compressed before encryption. The manifest gains `"Format": 3`, `DocumentCount`,
`DocumentBytes` (as now) and the SHA-256 of `documents.idx`. Readers keep format 2 (documents inside) and envelope
versions 1, 2 and 3.

### 5.3 Blob list beside each archive (`.refs`)

The sorted blob ids the archive references, in plain text, plus an HMAC-SHA256 under
`IFieldProtector.DeriveKey("backup-refs")`, which comes from the original master key and is the same on every server
restored from these backups. The seal also covers the archive's name and the installation, so a list copied beside
another archive is refused. The sweep reads only `.refs` files, so it needs no recovery key and keeps working across
recovery-key rotations (F10). A missing or failed `.refs` file means no sweep (F15). Restore and verify **do not trust**
`.refs`. They use the encrypted `documents.idx`.

**Every archive has a list** (refined in step 1). When a folder is first marked, each archive already there gets an
empty list, because archives made before documents were kept apart reference none. From then on, an archive without a
list blocks the sweep, whatever its date. That includes an archive written by an older HC360 after a downgrade, which
the operator then has to resolve.

### 5.4 One run, in order

1. Take the store lock. Check the store marker.
2. Dump the database. Take the document lock (shared). List the documents, then export the ring (keep today's order).
3. For each document: hash the stored file. If `blobs/<id>` is missing, copy it to `*.tmp`, flush and rename. The space
   check counts only these new bytes.
4. Write the archive (`.partial`, then verify it by reading it back, which **also re-hashes the blobs it references**).
   Write `.refs`, **then** rename the archive into place, so an archive in the folder always has its list. A crash in
   between leaves only an orphan list.
5. Release the document lock.
6. Secondary location (the disk mounted now): take its lock, check its marker, copy the blobs it lacks, then the archive
   and `.refs`, then retention + sweep **on that disk only**, using its own `.refs` files.
7. Retention + sweep on the primary.

Retention: delete archives (and their `.refs`) older than *R* days by the date in their name, keeping the newest.
Archives with other names are never deleted. Sweep: delete blobs no remaining `.refs` names.

**No time-based grace periods** (refined in step 1). Every writer holds the store lock, so whatever the sweep finds
while holding it (`*.tmp`, `*.partial`, a `.refs` without its archive) comes from a crashed run and is removed at once.
Nothing in the sweep reads a clock or a file time (F19).

**Hashing every document every night** reads the whole `files` folder from the server's disk, not the USB disk. Step 3
adds a cache of (storage path, length, last write time) → blob id, kept in the primary folder. A key-move rewrite
changes the last write time, so it never serves a stale id.

### 5.5 Restore, verify, failover

`backup verify|restore|decrypt --in <archive>` finds `blobs\` beside the archive, or takes `--blobs <folder>`.
Restore checks every document (present, length, SHA-256, and key id in the archive's ring) **before** changing
anything, as today, then copies the blobs into `files\<storage path>`, keeping files already there. `Restore-HC360.ps1`
and DEPLOYMENT.md §9 step 3 keep their commands. The documentation changes to "copy the whole backup folder, not one
file".

### 5.6 Destinations that delete by age: generations (added after #346)

#346 added two destinations where HC360 cannot delete and something else deletes by age:

- **The off-site bucket.** Each object is locked for `retainDays` (35) in compliance mode. The bucket's lifecycle rule
  expires everything under the prefix 35 days after it was created, and HC360's key may upload but never delete.
- **An append-only or WORM share** as `Backup:SecondaryDirectory`. HC360 may create and write files but not delete or
  rename them, and the NAS's own retention removes files after some days.

A content-addressed store with a sweep (5.1–5.4) is unsafe there. A document copy written two years ago is still
needed by last night's archive, yet deletion by age removes it after 35 days. Rename is refused too, so write-then-rename
is impossible. These destinations use **generations** instead (agreed 2026-11-14):

```
<destination>\
  gen-20270111\blobs\ab\cd\abcd…(64 hex).blob      every document the archives of that week need
  gen-20270118\blobs\…
  hc360-20270114-013000-scheduled.hc360bak        format 3 archive; its index names blob ids only
```

- **A generation lasts G = 7 days.** The first run of a generation copies **every** document the archive needs into it.
  Each later run that week copies only the documents not yet in it. An archive only ever uses its own generation, so a
  generation is complete for every archive made in it.
- **Expiry.** A copy written at a generation's start is needed until the generation's last archive (made up to G − 1
  days later) expires, `retainDays` after that. In the bucket each copy is locked until generation start + G +
  `retainDays`. The lifecycle needs a second rule for `<prefix>gen-` with `Days` = `retainDays` + G (42); the archives'
  rule stays at `retainDays`. A NAS's own retention must be at least `RetentionDays` + G days (37).
- **Storage there:** about (`retainDays` + G) / G ≈ 6 copies of the documents, plus one dump per archive, instead of
  35 full backups. At year 3 under the brief's assumptions: about 1.6 TB + 0.5 TB in the bucket, instead of about 10 TB.
- **Erasure window.** An erased document's copy can stay until its generation expires, so the promise becomes
  `retainDays` + G + 2 days (44) while the off-site copy is on (`BackupRetention.DaysAsync`, the erasure answer and the
  privacy notice). No scheme does better without a key that can delete. The last archive before an erasure must stay
  restorable for `retainDays`, and its generation's copies expire on a fixed date.
- **Restore** does not need to know the layout. A blob is found by its id in `blobs\`, or in any `gen-*\blobs\` beside
  the archive, and checked against the SHA-256 the archive's index gives. For the bucket, download the archive and the
  `gen-` folders with it (`aws s3 sync`).
- **On an append-only share**, copies are written straight under their final name (`CreateNew`, never renamed) and
  checked by reading them back. A copy cut short is never trusted: the next run writes `<id>.1.blob` beside it, and
  restore takes the first copy whose hash matches. No timestamp is ever set there: a NAS that deletes by file date would
  delete a copy stamped 2000-01-01 at once.
- **Which mode a secondary folder is in** is recorded in its store marker (`hc360-store.json`, `"appendOnly"`). It is
  set when `HealthCare360:Backup:SecondaryAppendOnly` is true, or when, on first use, the folder refuses to rename or
  delete a probe file. It is never cleared by itself. A later prune that finds it cannot delete switches the folder to
  append-only, and warns. A WORM folder that allows deletion for a grace period after each write is caught only by the
  setting, so DEPLOYMENT.md tells administrators of append-only and WORM shares to set it.

Failure rows for these destinations:

| # | What happens | What must hold |
|---|---|---|
| F21 | The bucket's lifecycle expires a generation copy | Only after `retainDays` + G from the generation start, by which time every archive using it has expired. HC360's key cannot read the bucket's lifecycle, so HC360 cannot check the generation rule's `Days`: DEPLOYMENT.md sets it, and the restore drill checks it. |
| F22 | An upload of a generation copy is cut off | The multipart state is per copy. The archive is uploaded only after all its copies are confirmed (size and checksum read back), so an archive off site always has its copies. |
| F23 | A copy on an append-only share is cut short | It stays (it cannot be deleted). The next run writes `<id>.1.blob`; restore and verify take the first copy whose SHA-256 matches. |
| F24 | A WORM share that allowed deletes during a grace period, with the setting forgotten | Copies were written as for a deletable folder. A later delete refused at prune switches the folder to append-only and warns; the next run starts a generation there. Until then the share's own retention may remove copies, which verify reports by archive. DEPLOYMENT.md tells administrators to set the setting. |
| F25 | Off-site copy on, then off, then on again | A new generation starts whenever the current one is older than G days or its folder or prefix changed. A generation is never resumed after a gap longer than G. |

---

## 6. Decisions (agreed 2026-11-14)

1. **What a stolen USB disk reveals:** accepted. Document count and sizes are visible. Every stored copy gets a fixed
   timestamp (`BackupStore.BlobTimestamp`), so upload dates are not.
2. **Where the sizing goes:** `NANA-THENI-READINESS.md` §2–3. No new hardware doc.
3. **USB disks must be NTFS.** FAT and exFAT are flagged (`BackupStore.UnsuitableFileSystem`). The warning on the
   Backups screen and in the preflight comes with step 3.
4. **Appendix A will be run on the Nana server** by someone with access. Until then the sizing in 2.2 stays marked as
   assumed.
5. **The off-site bucket and append-only shares use generations** (5.6), G = 7 days. The off-site erasure window
   becomes `retainDays` + G + 2 (44 days by default).

## 7. Implementation plan (each step its own commit)

1. `BackupStore`: layout, marker, lock, blob put/has/hash, `.refs` write/read with HMAC, retention, sweep. Unit tests
   for F1, F5, F12, F13, F15, F19.
2. Archive format 3 (`documents.idx`), with format 2 still read. Tests: round trip, a format 2 archive and a v1 file
   still restore.
3. `BackupService` uses the store, in three commits:
   - **3a.** The server's folder and a deletable secondary (the USB disks): format 3, the hash cache, the space check
     counting only new documents, the NTFS warning (F3, F4, F6).
   - **3b.** Append-only secondary folders: the marker mode, the probe, generations, writing without rename (F23, F24).
   - **3c.** The off-site upload: generation copies before the archive, their locks, the lifecycle and IAM docs, and the
     erasure window (F21, F22, F25).
4. Restore, verify and decrypt with `--blobs`. Restore-from-secondary-alone and stale-disk tests (F3, F16). Key rotation:
   data key (F8, F9) and recovery key (F10).
5. A model-based test: seeded random sequences of {backup, add document, key-move rewrite, discard, rotate recovery key,
   swap or unplug disk, crash at step *k*, advance a day}. Invariant after every step: every retained archive in every
   location restores completely from that location alone, and no blob outlives R+1 days after its last reference
   (F14). It uses a seeded random generator rather than adding FsCheck, unless you want the dependency.
6. A growth benchmark over 60 simulated nights, asserting size ≈ `R × (D + I) + Docs + Docs_R`.
7. Docs: DEPLOYMENT.md §7 (downgrade note), §8, §9 step 3, the readiness doc's sizing. A restore-drill script for a
   clean Windows VM from one USB disk (row counts plus per-document hashes, timed).

---

## Appendix A. Measurements to run on the Nana server (read-only)

In `psql` against the HC360 database:

```sql
-- Documents: count, bytes, and size distribution, by type (content is kept for soft-deleted rows, so all rows count)
SELECT content_type, category, count(*) AS documents, round(sum(size_bytes) / 1e9, 2) AS gb,
       round(avg(size_bytes) / 1e3) AS avg_kb,
       percentile_cont(ARRAY[0.5, 0.9, 0.99]) WITHIN GROUP (ORDER BY size_bytes) AS p50_p90_p99_bytes
FROM platform.stored_files GROUP BY ROLLUP (content_type, category) ORDER BY gb DESC NULLS FIRST;

-- Growth: documents and GB a day, last 60 days (IST)
SELECT (created_at AT TIME ZONE 'Asia/Kolkata')::date AS day, count(*) AS documents, round(sum(size_bytes) / 1e9, 3) AS gb
FROM platform.stored_files WHERE created_at > now() - interval '60 days' GROUP BY 1 ORDER BY 1;

-- Backups: size and document count per run (status 1 = Succeeded; kind 0 scheduled, 1 manual, 2 pre-migration)
SELECT started_at, kind, round(size_bytes / 1e9, 2) AS gb, document_count, secondary_copied
FROM platform.backup_runs WHERE status = 1 ORDER BY started_at DESC LIMIT 60;

-- The database itself
SELECT pg_size_pretty(pg_database_size(current_database()));
```

In PowerShell on the server, with each USB disk connected in turn:

```powershell
$files = Get-ChildItem "$env:ProgramData\HyFib\HealthCare360\files" -Recurse -File
"{0:N0} files, {1:N1} GB" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1GB)
Get-Volume | Select-Object DriveLetter, FileSystem, AllocationUnitSize, Size, SizeRemaining
```
