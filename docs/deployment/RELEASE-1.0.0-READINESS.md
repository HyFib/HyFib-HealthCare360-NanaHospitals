# Release 1.0.0: readiness report

**Status: not released.** Assessed again on 16 November 2026, for the candidate `31cae7c0`, `main` after #373. Its
CI run on `main` (#1189) passed all six jobs. CI on `main` had failed at the merges of #361 and #371, and #373 fixed
the cause (R9).

One thing stands before a tag: **the release signing key (R2).** HC360 trusts no key yet, so a tagged release stops at
its own check. Only HyFib can make the key, and never in a CI job or a hosted session. R6 and R10 should be settled
before the tag too.

Then make a release candidate first (S2). Set the version to `1.0.0-rc.1` in a commit of its own, tag that commit once
its own CI run on `main` has passed, and install it on the Theni machine for wave 0.

This report replaces the assessment of 10 November, whose candidate was `ad5acb37`. *What changed since 10 November*
lists what moved. It was written from:

- the candidate and its CI runs, on its pull request and on `main`;
- the last 30 completed CI runs on `main`, and the scheduled checks;
- the repository's tags and releases, and every run of the release workflow;
- the code at the candidate, counted with the commands in [MODULE-ROLLOUT-PLAN.md](MODULE-ROLLOUT-PLAN.md)'s appendix;
- [TRACEABILITY.md](../requirements/TRACEABILITY.md), [ASVS-L2.md](../security/ASVS-L2.md),
  [NANA-THENI-READINESS.md](NANA-THENI-READINESS.md), and the production-readiness review of 14 November.

The appendix lists every source. What could not be checked is marked **to confirm**.

This report covers the first release to Nana Hospitals, Theni: the version, the evidence that it works, what stops it,
and how it is built, installed and undone. [MODULE-ROLLOUT-PLAN.md](MODULE-ROLLOUT-PLAN.md) covers what goes live
after it, one department at a time.

## What changed since 10 November

| | 10 November (`ad5acb37`) | 16 November (`31cae7c0`) |
|---|---|---|
| .NET tests on the candidate | 10,832: 10,822 passed, 10 skipped | 12,164: 12,154 passed, 10 skipped |
| Endpoints, in route groups | 1,661 in 225 | 1,671 in 227 |
| Tables, in schemas | 439 in 23 | 441 in 23 |
| Database migrations | 140 | 149 |
| The release build | Never run | Passed end to end on #333 and on #361 |
| Requirements | 173 Done, 10 Blocked | The same |

Merged since, in pull requests #333 to #373:

- **The production-readiness review of 14 November.** It found 8 blockers, fixed in #346: restore, the off-site
  backup, duplicate saves, Pine Labs, the offline copy, the portal, the outbox and alerting. Its 26 items to fix next
  were fixed in #361: offline working, backup and recovery, security and operations. Its lower-priority items are in
  section 2, *Known limits*.
- **This report's blockers.** R1, R3, R4, R5, R7 and R8 were fixed in #333, and R8's fifteen other pickers in #334.
- **Payments and integrations.** Pharmacy and canteen card and UPI payments on the Pine Labs terminal (#341); Pine
  Labs payments left unrecorded, lab analysers' withdrawn results and ABDM consents (#339); GST e-invoices and e-way
  bills (#340, #342, #344, #345, #360); ABDM auto-linking (#343); results naming another patient held, withdrawn
  results passed on, and the HORIBA Yumizen analysers (#347, #348, #359).
- **Backups.** Incremental document backups, with generations kept off site (#350, #354); the Theni server and backup
  hardware (#349, #355).
- **Operations and security.** The server's network cable watched (#337); WhatsApp care messages in the patient's
  language (#338); a role turned off and on whole, and the roles inventory (#353, #357); secrets scanned in CI and
  every day (#351, #352, #358); .NET's 10 November security release taken (`d905a8e5`).
- **The test restores' scratch database** (#371, #373): R9 below.

## Where the blockers stand

| # | State | Shown by | Still needed |
|---|---|---|---|
| R1 | Done. The release workflow starts the runner's PostgreSQL, and a pull request that changes the release runs the release build, without signing or publishing | The release build passed end to end on #333 (runs 2 and 4 to 7) and on #361 (run 24, `797dbea8`, 1 h 55 min). The candidate adds #371 and #373, which change only the test restores' scratch database and its tests, so the release build has not run on the candidate itself: the tag runs it | — |
| R2 | **Open.** `ReleaseKeys.Trusted` is empty at `31cae7c0`, so a tagged release stops at *Refuse to release an unsigned HC360 Desktop* | `src/shared/HyFib.HealthCare360.WindowsInterop/Updates/ReleaseKeys.cs` | **HyFib makes the key pair** (`Sign-HC360Release.ps1 -NewKey`), stores the private key as the `HC360_RELEASE_SIGNING_KEY` secret, keeps a copy offline, and merges the public key into `ReleaseKeys.cs` (SECURITY.md, *Release signing key*). The first version each workstation installs must already trust it |
| R3 | Done. The test failed because of its own poll of `pg_stat_activity`, not because of screen unlock | It has not failed in the last 30 runs on `main` | — |
| R4 | Done. DEPLOYMENT.md section 1 tells IT to check the hashes, unblock the zip and set a process-scope execution policy before the preflight runs | — | *To confirm* on the Theni PC: `Get-ExecutionPolicy -List` |
| R5 | Done. `tools/release/Test-ReleaseVersion.ps1` checks the version, and that the tag names it | `tests/deploy/Test-ReleaseVersion.ps1`, in CI | — |
| R6 | **To confirm.** GitHub reports two open Dependabot alerts on `main`, both moderate, none high or critical. The alerts themselves could not be read in this session, so which packages they name, and whether those ship, is not known. CI's audits of what ships find none, the daily security audit passed on 16 November, and `.github/security-advisories.json` records none. The nine Dependabot pull requests opened on 16 November (#362 to #370) are version updates, .NET 11 among them; none was raised by an advisory | GitHub's count, given on a push on 16 November; the audits; the pull requests | Read the two alerts on the Security tab, then fix each or write down why it neither ships nor runs in the release build. Leave the version updates until after 1.0.0 |
| R7 | Done. CI fails on two files whose names differ only in case | — | — |
| R8 | Done, and the fifteen other pickers in #334, each with a test | — | — |
| R9 (new) | Done. **A weekly test restore could pass and leave its scratch database, a full copy of every patient's record, on the server.** A database just restored is where autovacuum goes to work. While an autovacuum worker was in the copy, PostgreSQL refused the test-restore account's forced drop ("permission denied to terminate process"), and the copy stayed until the next week's test. This was found because CI on `main` failed at the merges of #361 and #371. #371 stopped the test from seeing other tests' scratch databases, and gave each hospital database's scratch databases names of their own. #373 drops plainly first, which ends autovacuum workers without any special right | Reproduced on PostgreSQL 16.15. Two cases in `PostgresRestoreTests` failed on the old code and pass now. CI on `main` passed at #373's merge (run #1189). | — |
| R10 (new) | **Open.** GitHub's CodeQL default setup holds two alerts, 23 and 24 (`js/clear-text-storage-of-sensitive-data`: the offline unlock verifier and its test). The maintainer ruled both false positives, and they are marked as SECURITY.md prescribes, but default setup ignores those markings. The repository's own CodeQL jobs pass | GitHub's *CodeQL* check on #361 and on later pull requests | Someone allowed to manage security alerts dismisses them on the Security tab (24 as a false positive, 23 as used in tests), or code scanning moves to advanced setup only, which also puts the repository's own findings (section 3.3) on the Security tab |
| S1 | DEPLOYMENT.md section 1 tells IT to compare the checksums with the copy HyFib sends another way | — | HyFib sends them with each release; a code-signing certificate later |

## 1. The answer in one page

**No release exists yet.** The repository has no tag and no GitHub release. The release workflow has run only on
pull requests, where it never signs or publishes. `Directory.Build.props` says 1.0.0.

**The code at `31cae7c0` is a sound candidate.** Its CI run on `main`, #1189, passed all six jobs:

- 12,164 .NET tests against a real PostgreSQL (12,154 passed, none failed, 10 skipped), and after them the database
  watchdog, the certificate probe, the warm standby and the desktop updater;
- the installer's tests, in Windows PowerShell 5.1 and in PowerShell 7;
- the web apps' checks, and accessibility in a browser (WCAG 2.2 AA);
- the SBOMs and the security patch windows.

The release build passed end to end on `797dbea8`, two pull requests earlier. Those two changed only the test
restores' scratch database and its tests.

173 of the 183 requirements are Done. The other 10 are Blocked, each waiting on someone outside HyFib.

**It cannot be tagged until the signing key exists (R2)**, which only HyFib can make. `release.yml` also refuses a
commit whose own CI run on `main` did not pass, so the version commit of S2 needs its run. R6 and R10 should be settled
before the tag too: one is a reading of GitHub's Security tab, the other two dismissals.

Also needed, though neither blocks the tag:

- **S1. The programs are not signed (Authenticode).** `SHA256SUMS.txt` sits on the same release page as the packages,
  so it shows that a download is complete, not who made it. Until a code-signing certificate signs them, send the
  checksums to the hospital's IT by a second channel (an e-mail from a known HyFib address, or read out by phone),
  and have IT compare them before anything is extracted.
- **S2. Make a release candidate first.** Set `Version` and `InformationalVersion` in `Directory.Build.props` to
  `1.0.0-rc.1` in a commit of its own on `main`. Once that commit's own CI run has passed, tag it, and install it on
  the Theni machine before it holds any real data, which is wave 0's load test anyway. Tag 1.0.0 the same way, from
  the same code, only once that install has worked. A commit that changes only the version, or only documents, leaves
  the candidate's code as it is.

**Wave 1 has a gap the report of 10 November missed.** Wave 1 puts the stores live, but three of their tasks exist only
on the server, with no screen that calls them: supplier returns (debit notes for expired or damaged stock sent back),
payable adjustments, and stock levels for each store. Decide before wave 1: return goods on paper and post them once
a screen exists, or have HyFib build the screens first (section 5).

**The SRS's release-1 acceptance (section 9) is not met in full.** For the first two waves it does not need to be:

| Not met | Needed before | Who acts |
|---|---|---|
| ASVS L2 checklist signed (SEC-06) | Any patient data: **wave 1** | The hospital's security officer |
| Response times measured on the hospital's hardware (PERF-01 to PERF-04) | **Wave 0** (the load test in NANA-THENI-READINESS.md, step 6) | HyFib with the hospital's IT |
| Quick registration timed at the desk (FO-01..06) | Wave 2, the front office | The hospital |
| ICD-11 API credentials and the licensed ICD-10 file (MRD-01..06) | Wave 6, medical records | The hospital |
| Aadhaar fingerprint checks with UIDAI (INT-BIO) | Not in any wave; optional | The hospital (AUA licence) |
| Hindi and Tamil reviewed by native speakers (PP-06) | Wave 7, the patient portal | Reviewers |
| ABDM gateway (EHR-08) | Wave 7 | ABDM onboarding |

**Recommendation**

1. HyFib makes the signing key (R2), reads the Security tab (R6), and has the two CodeQL alerts dismissed (R10). It
   starts S1's second channel.
2. The hospital decides how wave 1 handles supplier returns.
3. HyFib tags `1.0.0-rc.1` on a commit CI passed, and installs it on the Theni machine. Once that works, it tags 1.0.0
   from the same code.
4. Install 1.0.0 at Theni for **wave 0 and wave 1 only** (foundation, then pharmacy and stores), as in section 5.
   From wave 3, when the EMR goes live and the laboratory is still on paper, the department switches of
   MODULE-ROLLOUT-PLAN.md are needed.
5. Go-live then waits on the hospital's gates in section 7: the UPS, the backup disks, BitLocker, the preflight, the
   load test, the restore drill and the ASVS sign-off.

## 2. What the release is

| | |
|---|---|
| Product and version | HyFib HealthCare360 1.0.0 (`Directory.Build.props`) |
| Candidate commit | `31cae7c0` (`main`, 16 November 2026) |
| Built by | `release.yml`, on a tag `v*.*.*`, on a Windows runner |
| Packages | `HC360-Server-<version>-win-x64.zip`: the server, self-contained, with the staff app, the portal and the deployment scripts · `HC360-Desktop-<version>-win-x64.zip`: HC360 Desktop with its updater and installer, and its `.manifest.json` and `.manifest.sig` when signed · `HC360-LabAgent-<version>-win-x64.zip`: the lab agent for PCs with serial analysers · `SHA256SUMS.txt` · one CycloneDX SBOM for each package and web app |
| The server machine needs | Windows 10 2004 or later (Pro or above, for BitLocker); PostgreSQL 17, from `Install-PostgreSQL.ps1`; Microsoft Edge, for PDFs |
| Workstations need | Windows 10 or 11 with the WebView2 runtime (Windows 11 has it) |
| What it holds | The whole product: 18 modules, 1,671 endpoints in 227 route groups, 441 tables in 23 schemas, 149 database migrations, 43 groups of staff screens (counted at the candidate with MODULE-ROLLOUT-PLAN.md's commands) |
| Requirements | 183: 173 Done, 10 Blocked, none Partial or Missing ([TRACEABILITY.md](../requirements/TRACEABILITY.md)) |

**Known limits.** These belong in the release notes:

- **Built on the server, with no screen yet.** These come from the production-readiness review, section B, and were
  checked again at the candidate against the staff app's code:
  - supplier returns (purchase debit notes), payable adjustments, and stock levels for each store;
  - HR's statutory payroll: EPF, ESIC, bank files, TDS challans, Form 16, Form 24Q, gratuity, bonus and employees' tax
    declarations;
  - doctors' TDS returns (26Q and 16A) and their tax profiles;
  - quality audit templates and starting an audit, an HAI case entered by hand, and registering SMART apps;
  - about 45 to 55 settings with no admin screen, among them `lab.requirePaymentBeforeCollection`,
    `inpatient.bedDayPolicy` and `doctorfees.tds.percent`.
- **The HPR registry check** runs against its simulator until the hospital is connected to ABDM's HPR service, so a
  real doctor shows as not found.
- **The review's ten hardening items** (section D) are open. None is a blocker.
- **Hindi and Tamil** have not been reviewed by native speakers (PP-06).
- **The ASVS L2 checklist**, as assessed on 30 September, has 130 requirements Met, 61 Partial and 8 Gap, and it is
  not signed (SEC-06).

**A first release has no upgrade to test.** Its 149 migrations run on an empty database. The way an upgrade is undone
(section 6) first matters at 1.0.1.

**No release notes exist.** For 1.0.0 they should say:

- that this is the first release;
- what it contains (TRACEABILITY.md);
- its known limits (above, the Blocked rows, and S1 and S2);
- what the hospital must provide (the *Who must act* column of TRACEABILITY.md).

## 3. The evidence

### 3.1 CI on the candidate

CI run #1189, on `31cae7c0`, 16 November 2026, 07:20 to 08:21 UTC. Every job passed. The same code passed CI on its
pull request, #373 (on `de8d90fb`), with the same counts.

| Job | What it checks | Result |
|---|---|---|
| Server and desktop (.NET), on Windows | C# formatting; package restore and audit; build, with warnings as errors; every .NET test, the end-to-end journeys among them, against a real PostgreSQL. Then: the database watchdog starts a crashed PostgreSQL again; the certificate probe runs as SYSTEM from Task Scheduler; a warm standby streams from the primary and takes over; the desktop updater is published as the release publishes it | 12,164 tests: 12,154 passed, 0 failed, 10 skipped, in 31 min. Every later check passed |
| Server installer, in PowerShell 5.1 and 7 | Sixteen scripts: the settings file, the database sign-in, the two database accounts, the restore marker, the restore drill's decisions, the backup sizing arithmetic, restarts after a failure, `-NoStart`, Windows Update's active hours, power settings, firewall rules, the standby scripts, the desktop's self-update, release signing with a throwaway key, the release's version and tag check, and the server preflight | Passed |
| Web apps (TypeScript) | A secret scan of the change's history; package audit and patch windows; formatting; type checking; lint; unit tests; the demo; the build | Passed |
| Accessibility (WCAG 2.2 AA) | The staff app and the portal, in Chromium | Passed |
| SBOMs (CycloneDX) | The SBOMs the release attaches | Passed |
| Security patch windows (SEC-11) | No advisory past its patch window, and none unrecorded. `.github/security-advisories.json` records none | Passed |

**The skipped tests** state their own reason. Some need Redis, which a single-server hospital does not use; some need
`openssl`, or another operating system. Which ten were skipped is in the run's test reports, which were not read here
(*to confirm*).

### 3.2 How stable the tests are

| Last 30 completed CI runs on `main` (#1045 to #1189) | Count |
|---|---|
| Passed | 13 |
| Failed | 3 |
| Cancelled: a newer merge replaced the run | 14 |

Until #361, each merge to `main` cancelled the run of the one before. Since #361, each push to `main` has a run of its
own, so every merge is tested.

| Run | Commit | What failed | Cause | Status |
|---|---|---|---|---|
| #1046 | `cbe5afb9` | The package audit and the security patch windows | .NET's 10 November security release | Taken in `d905a8e5`, 12 November |
| #1173 | `381fa7c3` | `BackupRestoreTestTests` | R9 | Fixed in #371 and #373 |
| #1186 | `ea12b44c` | `BackupRestoreTestTests` | R9: the scratch database the test restore made, left because autovacuum was in it | Fixed in #373 |

No other test failed. The test whose failure on 10 November had no known cause (R3) has not failed since.

### 3.3 Checks that run on a schedule

- **Security audit**, every day at 06:00 IST. It last ran on 16 November, on `ea12b44c`, and passed. Since then no
  package list has changed: #373 changed code and tests only.
- **Load test**, every Monday at 02:00 IST. It ran on 16 November, on `381fa7c3`, and passed. It runs on a shared
  runner with front-office accounts only, so its timings are only reported; they decide nothing about the hospital.
- **CodeQL** runs on every change, with the `security-extended` queries. While default setup is on as well (R10),
  GitHub refuses the repository's own results, so its jobs check them themselves and list them in their summaries, not
  on the Security tab. On the candidate (run #1112) those jobs passed: no high or critical finding is open, and the
  three high ones SECURITY.md rules false positives are marked in the source. They also list 13 medium findings, which
  do not fail the check. SECURITY.md's table rules every one of them a false positive. The maintainer ruled on the last
  four on 16 November:
  - `KeyRotation.cs`, lines 207 and 211 (log entries created from user input): a key id is logged only once it has
    matched a key in the ring. Any other is refused as not found first, which `KeyRotationTests` now pins.
  - `src/web/demo/capture.mjs`, line 267 (network data written to a file): the tool that records the demo's data,
    run by hand. It is no part of what a hospital installs.
  - The portal's proof-of-work worker, `solver.worker.ts`, line 5 (no origin check on a message): a dedicated worker
    gets messages only from the page that started it.
- **Dependabot.** Two open alerts on `main`, both moderate. See R6.

### 3.4 What CI does not prove, and what does

| Claim | Not shown by CI because | Shown by | Who |
|---|---|---|---|
| The release workflow works when tagged | It has run only on pull requests, which neither sign nor publish | A run on `v1.0.0-rc.1`, with every file attached | HyFib |
| The installers work on the hospital's PC | CI uses its own Windows image, with no files downloaded | The preflight report, then System health after the install | Hospital IT, with HyFib |
| The server is fast enough there | GitHub's runners are not the hospital's machine | The load test at 75 users (PERF-01), with the peak mix of billing, pharmacy, lab, ward and portal steps (LOAD-TESTS.md), run on the real 6-core desk PC as a site task, and `pg_test_fsync` | Hospital IT, with HyFib |
| The data survives a lost disk | — | A backup restored on another PC. The weekly test restore also checks each backup, from the first week | Hospital IT |
| Workstations update themselves | No key is trusted (R2) | The second release, offered by the server and taken by a workstation | HyFib |
| It is secure enough to hold patients | A test suite is not an audit | The ASVS L2 checklist, signed; an independent security test by a CERT-In-empanelled auditor | The hospital's security officer |
| The pharmacy's registers are right | — | Three days in parallel, reconciled, signed | The pharmacist-in-charge, then the Drugs Inspector |

## 4. How a release is built and shipped

| Step | What happens | State |
|---|---|---|
| 1. Tag `vX.Y.Z` | Starts `release.yml`, which refuses a commit CI has not passed on `main` (the newest CI run of a push of that commit decides, so a re-run that passed counts), and checks that the tag names the version | The version check is tested in CI. The CI check has not run yet: it runs only on a tag. So tag a commit of `main` whose own CI run passed |
| 2. `Publish-HC360.ps1` | Web apps: install from the lockfile, type check, tests, build. .NET: every test. Publishes the server (self-contained, web apps included), HC360 Desktop and the lab agent, and stops if the developer settings reach the server package. Writes `SHA256SUMS.txt` | Passed end to end on #333 and on #361 |
| 3. `Sign-HC360Release.ps1` | Signs HC360 Desktop's manifest (ECDSA P-256) with `HC360_RELEASE_SIGNING_KEY`. Without the secret, it builds unsigned and says so in the job summary | Tested in CI with a throwaway key; never run for real |
| 4. Check the signature | `desktop verify` with the keys built into this version, when the package was signed. An unsigned package stops a tagged release | **No key is built in (R2)**: until the key exists, no release can be made |
| 5. Patch windows | No advisory past its window, and none unrecorded | Works |
| 6. SBOMs | CycloneDX, for each package and web app. They, and HC360 Desktop's manifest and signature, are added to `SHA256SUMS.txt` | Works |
| 7. Attach to the GitHub release | Zips, manifests, signature, SBOMs, checksums | Not yet run |
| — | Authenticode signature on the programs | **None (S1)** |

## 5. Installing 1.0.0 at Theni (waves 0 and 1)

Follow [NANA-THENI-READINESS.md](NANA-THENI-READINESS.md), section 4. This release adds five things to it:

1. **In step 1, before anything runs.** Check the files against `SHA256SUMS.txt` and the copy HyFib sent another
   way (S1), unblock the checked zip, and let the window run scripts (R4; DEPLOYMENT.md section 1).
2. **After step 7 (install HC360).** Under Hospital setup › Features, turn the **patient portal** and **video
   consultations** off. They are on by default, and they belong to wave 7. ABDM is already off by default.
3. **Accounts.** Give roles to pharmacy and stores staff and to administrators only. Without department switches,
   roles are what keeps the other departments closed in wave 1.
4. **Integrations.** Leave SMS, WhatsApp, payments and Tally unset unless wave 1 uses them. A live server refuses the
   simulators (DEPLOYMENT.md, *Going live on a database used for training*).
5. **Supplier returns.** Before wave 1, decide how the stores return expired or damaged stock to a supplier: on paper,
   posted once a screen exists, or through screens HyFib builds first. Payable adjustments and stock levels for each
   store wait the same way. The item master's own reorder levels work.

**Why wave 1 needs no other new code.** In wave 1 no live department hands work to one that is not live:

- the EMR is not used, so no orders and no prescriptions are signed;
- nobody is admitted, so no ward charges or medication chart lines are made;
- the stores' movements of register drugs go to the pharmacy's registers, and both are live.

The background services of other departments find nothing to work on. The pharmacy's refill reminders do run: decide
in wave 1 whether they send SMS. The seams in MODULE-ROLLOUT-PLAN.md, section 3.4, begin at wave 2, with one: an
emergency visit registered at the front office opens a case on the ER board, which goes live in wave 3. Close those
cases at wave 3's take-on. From wave 3 they are many.

## 6. Undoing it

- **Wave 1 itself.** The pharmacy's present software runs alongside HC360 during the parallel run. Undoing the
  go-live means going back to entering in it alone. Print what was entered in HC360 that day from its registers, and
  enter it by hand.
- **Upgrades from 1.0.1 onwards.** The way back is the previous package and the backup HC360 takes before an update.
  Never the previous package alone: on a database a newer version changed, start-up stops and changes nothing
  (rollout item 7). DEPLOYMENT.md section 7, *Going back*, gives the steps. Rehearse each upgrade on a restored copy
  first.
- **HC360 Desktop.** Until R2 is closed, every new version is installed on each workstation with
  `Install-HC360Desktop.ps1`.

## 7. Go or no-go: the checklist

| Gate | Evidence | Signed by |
|---|---|---|
| The blockers closed | R2's key in `ReleaseKeys.cs`; CI green on the tagged commit; the two Dependabot alerts read, each fixed or written down (R6); CodeQL alerts 23 and 24 dismissed (R10) | HyFib engineering lead |
| The release workflow works | A green run on `v1.0.0-rc.1`, with every file attached | HyFib engineering lead |
| The checksums reached IT by a second channel | IT's note of the values compared | Hospital IT officer |
| The machine is fit | The preflight with no Fail, and a decision against each Warn | Hospital IT officer |
| UPS and backup disks | The preflight: *UPS on USB* passes; the USB disk is listed for backup copies | Hospital IT officer |
| BitLocker on every fixed disk | The preflight | Hospital IT officer |
| Fast enough | The load test at 75 users meets PERF-01 | Hospital IT officer and HyFib |
| The data survives a lost disk | The first night's backup verified, and restored on another PC | Hospital IT officer |
| Secure enough to hold patients | The ASVS L2 checklist, signed (SEC-06) | The hospital's security officer |
| The item master is right | The H1 and X list reviewed | Pharmacist-in-charge |
| Supplier returns decided | How the stores return stock to suppliers in wave 1, written down (section 5) | Pharmacist-in-charge and the stores in-charge |
| Go | Every row above | The hospital's named decision-maker |

After go-live, the pharmacist-in-charge signs the parallel run (NANA-THENI-READINESS.md, section 2).

## 8. Risks accepted with 1.0.0

| Risk | Why it is accepted | Until |
|---|---|---|
| A 6-core desk PC where the SRS asks for 8 cores, kept in daily use | The load test decides; the readiness document's conditions apply | The hospital decides on a dedicated server (readiness document, section 6) |
| Up to a day of entries lost if the disk fails | The backup runs nightly at 01:30. WAL archiving, which now works on a single server and is encrypted, would bring the loss down to 15 minutes (DEPLOYMENT.md, section 9) | The hospital decides whether to archive the WAL to the take-home disk |
| Every workstation upgraded by hand | Only if R2 is put off | R2 is closed |
| Programs not signed | The second channel for the checksums (S1) | A code-signing certificate |
| Features with no screen (section 2) | Each belongs to a later wave, except supplier returns (section 5) | Their screens are built, wave by wave |
| The ASVS L2 checklist's Partial and Gap rows | The security officer's sign-off decides each | SEC-06 is signed |
| Translations not reviewed | Patients first see them in wave 7 | The reviewers |

## Appendix: sources

From GitHub, on 16 November 2026:

- Releases and tags: none.
- Release workflow runs: on pull requests only, which neither sign nor publish. #361's run 24 (43662761647, on
  `797dbea8`) passed every step in 1 h 55 min, its publish step from 01:23 to 03:15 UTC. Every other run on #361 was
  cancelled by the next push, except run 12 (43593797511), which failed at a web test fixed since.
- CI on the candidate: run #1189 (43823197964, `31cae7c0`, a push to `main`): six jobs, all passed. The .NET job
  (133150799132) printed `total: 12164, failed: 0, succeeded: 12154, skipped: 10`. On #373's head, `de8d90fb` (run
  43815583092), the same.
- CI runs on `main`: the last 30 completed, #1045 to #1189, of which 13 passed, 3 failed and 14 were cancelled.
  - #1173 (43671764955, `381fa7c3`) failed in `BackupRestoreTestTests`, with `hc360_restore_test_96bebb8bb0e7` left.
  - #1186 (43805475089, `ea12b44c`) failed in the same test, with `hc360_restore_test_e7a5fe22_fc2b3babef55` left,
    owned by the test's own account.
  - #1046 (43023566105, `cbe5afb9`) failed at *Restore and audit packages* and the patch windows.
- Security audit: run #27, 16 November, on `ea12b44c`, passed. Load test: run #41 (scheduled), 16 November, passed.
- CodeQL on the candidate: run #1112 (43823198062, `31cae7c0`), three jobs, all passed. GitHub refused each job's
  upload, so each checked its results itself. C# (133150870442): 12 findings, none high or critical open, 1
  suppressed in the source. TypeScript (133150871076): 4 findings, none high or critical open, 2 suppressed. Actions
  (133150870921): none.
- Dependabot: GitHub's answer to the push of `1ed6aea6` to this report's branch, on 16 November, said "GitHub found
  2 vulnerabilities on [the] default branch (2 moderate)". The alerts' own pages could not be read in this session.
- Open pull requests: #362 to #370, Dependabot's, opened on 16 November. No open issues.

From the repository, at `31cae7c0`:

- `src/shared/HyFib.HealthCare360.WindowsInterop/Updates/ReleaseKeys.cs`: `Trusted` holds only a commented example.
- `.github/security-advisories.json`: no advisories.
- TRACEABILITY.md's summary: 183 requirements, 173 Done, 10 Blocked. ASVS-L2.md: 253 rows, 130 Met, 61 Partial, 8 Gap,
  54 N/A, assessed on 30 September.
- The figures in section 2: the commands in MODULE-ROLLOUT-PLAN.md's appendix, run at the candidate.
- The screens in section 2's known limits: the server routes in `src/web/apps/staff/src/test/serverAccess.json`,
  searched for in the staff app's code, tests left out.
- `git log --merges ad5acb37..31cae7c0`: pull requests #333 to #373.

The reproduction of R9, on PostgreSQL 16.15:

- A non-superuser account that may create databases made one, and filled a table there until autovacuum came to it.
- `DROP DATABASE … WITH (FORCE)`, run by that account while the autovacuum worker ran `VACUUM ANALYZE`, answered
  "permission denied to terminate process", and the database stayed.
- A plain `DROP DATABASE` by the same account succeeded in 0.26 s, with the worker present.
