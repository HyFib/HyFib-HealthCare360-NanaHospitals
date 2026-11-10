# Release 1.0.0: readiness report

**Status: not released. The recommendation is no-go until the six items in section 1 are closed.** Written on 10
November 2026 from:

- commit `ad5acb37`, the head of `main`, and its CI run (#1030);
- the last 30 completed CI runs on `main`;
- the repository's tags and releases on GitHub;
- the release workflow and the scripts it runs;
- [TRACEABILITY.md](../requirements/TRACEABILITY.md) and [NANA-THENI-READINESS.md](NANA-THENI-READINESS.md).

The appendix lists every source. What could not be checked is marked **to confirm**.

This report covers the first release to Nana Hospitals, Theni: the version, the evidence that it works, what stops it,
and how it is built, installed and undone. [MODULE-ROLLOUT-PLAN.md](MODULE-ROLLOUT-PLAN.md) covers what goes live
after it, one department at a time.

## 1. The answer in one page

**No release exists yet.** The repository has no tag and no GitHub release, and the release workflow
(`.github/workflows/release.yml`) has never run. `Directory.Build.props` says 1.0.0.

**The code at `ad5acb37` is a sound candidate.** Its CI run passed all six jobs:

- 10,832 .NET tests against a real PostgreSQL: 10,822 passed, none failed, 10 skipped;
- the installer's tests, in Windows PowerShell 5.1 and in PowerShell 7;
- the web apps' checks, and accessibility in a browser (WCAG 2.2 AA);
- the SBOMs and the security patch windows.

173 of the 183 requirements are Done. The other 10 are Blocked, each waiting on someone outside HyFib.

**It cannot be released as it stands.** Six things stand in the way, all on HyFib's side:

| # | What stops it | Found by | Fix |
|---|---|---|---|
| R1 | **The release workflow would most likely fail on its first run.** It runs every test again (`Publish-HC360.ps1`), and the end-to-end tests then have no database: no `HC360_TEST_DATABASE`, so they try a Linux PostgreSQL container, which a Windows runner cannot start (`ci.yml` says so above its own step that starts one) | Reading `release.yml`, `Publish-HC360.ps1` and `TestDatabase.cs`. Not run | Copy CI's step *Start PostgreSQL for the end-to-end journeys* into `release.yml`, before the publish |
| R2 | **HC360 trusts no release signing key.** `ReleaseKeys.Trusted` is empty. HC360 Desktop never updates itself, and if the signing secret is set, the release fails at its own signature check. A key added in a later version reaches a workstation only when someone reinstalls it by hand | `src/shared/HyFib.HealthCare360.WindowsInterop/Updates/ReleaseKeys.cs` | Before tagging 1.0.0, make the key pair (`Sign-HC360Release.ps1 -NewKey`), store the private key as the workflow's secret, keep a copy offline, and add the public key. The first version each workstation installs must already trust it |
| R3 | **A test failed on `main` and nobody knows why.** `WebAuthnUnlockTests.A_refused_answer_stays_on_the_audit_trail_when_the_session_ends_meanwhile` failed on `653e7dde`: the unlock request was still running after 30 s and had not reached the session's row. Nothing changed the test or the code before `ad5acb37` passed. It is in screen unlock, a security path | CI run #1024, its test log | Find the cause: a slow runner, or an unlock that waits 30 s on something else. Running CI again until it is green explains nothing |
| R4 | **The install scripts may not run on the hospital's PC.** They are not signed. A browser marks a downloaded zip as from the internet, and Windows' execution policy then refuses the scripts in it (RemoteSigned refuses such scripts; Restricted refuses all scripts). DEPLOYMENT.md does not mention it. CI cannot see it, because its scripts come from a checkout, not a download | Microsoft's *about_Execution_Policies*; a search of `docs` and `deploy` | After checking the hashes, unblock the extracted folder (`Get-ChildItem -Recurse \| Unblock-File`) and say so in DEPLOYMENT.md section 1. *To confirm* on the Theni PC: `Get-ExecutionPolicy -List` |
| R5 | **Nothing checks the tag against the version.** A tag `v1.0.1` on a commit that still says 1.0.0 publishes packages named 1.0.0 | `release.yml`: the tag only starts the workflow | One step that compares the tag (`GITHUB_REF_NAME`) with `Directory.Build.props` and stops on a difference |
| R6 | **GitHub holds 34 open Dependabot alerts on `main` (12 high, 19 moderate, 3 low), while CI's audits find none.** CI audits what ships: the .NET solution and `src/web/pnpm-lock.yaml`. The repository has two more npm lockfiles, for tools: the demo site (`.github/demo-site`) and the SBOM tool (`.github/sbom`), which runs inside the release build. The likely reading is that the alerts are there, or are advisories the project accepted (*to confirm*) | GitHub's reply to this branch's push; `ci.yml`; SECURITY.md, *Patching security advisories* | Read every high alert on the Security tab. For each: update the package, or show that it neither ships nor runs in the release build, and write that down. Then audit the tool lockfiles in CI as well |

Also needed, though none of them blocks the tag:

- **S1. The programs are not signed (Authenticode).** `SHA256SUMS.txt` sits on the same release page as the packages,
  so it shows that a download is complete, not who made it. Until a code-signing certificate signs them, send the
  checksums to the hospital's IT by a second channel (an e-mail from a known HyFib address, or read out by phone),
  and have IT compare them before anything is extracted.
- **S2. Make a release candidate first.** Set `Version` and `InformationalVersion` in `Directory.Build.props` to
  `1.0.0-rc.1`, tag it, and install it on a test server. HC360's version numbers allow this (`ReleaseVersion`:
  `1.0.0-rc.1` comes before `1.0.0`). Use the Theni machine before it holds any real data, which is wave 0's load
  test anyway. Tag 1.0.0 only once that install has worked.

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

1. HyFib closes R1 to R6 and starts S1's second channel. Then it tags `1.0.0-rc.1` and installs it on the test server.
2. Once that works, HyFib tags 1.0.0 from the same code.
3. Install 1.0.0 at Theni for **wave 0 and wave 1 only** (foundation, then pharmacy and stores), as in section 5.
   Wave 1 needs no new code (section 5). The department switches of MODULE-ROLLOUT-PLAN.md are first needed at
   wave 3, when the EMR goes live and the laboratory is still on paper.
4. Go-live then waits on the hospital's gates in section 7: the UPS, the backup disks, BitLocker, the preflight, the
   load test, the restore drill and the ASVS sign-off.

## 2. What the release is

| | |
|---|---|
| Product and version | HyFib HealthCare360 1.0.0 (`Directory.Build.props`) |
| Candidate commit | `ad5acb37` (`main`, 10 November 2026) |
| Built by | `release.yml`, on a tag `v*.*.*`, on a Windows runner |
| Packages | `HC360-Server-<version>-win-x64.zip`: the server, self-contained, with the staff app, the portal and the deployment scripts · `HC360-Desktop-<version>-win-x64.zip`: HC360 Desktop with its updater and installer, and its `.manifest.json` and `.manifest.sig` when signed · `HC360-LabAgent-<version>-win-x64.zip`: the lab agent for PCs with serial analysers · `SHA256SUMS.txt` · one CycloneDX SBOM for each package and web app |
| The server machine needs | Windows 10 2004 or later (Pro or above, for BitLocker); PostgreSQL 17, from `Install-PostgreSQL.ps1`; Microsoft Edge, for PDFs |
| Workstations need | Windows 10 or 11 with the WebView2 runtime (Windows 11 has it) |
| What it holds | The whole product: 18 modules, 1,661 endpoints, 439 tables in 23 schemas, 140 database migrations, 43 groups of staff screens (counted in MODULE-ROLLOUT-PLAN.md, appendix) |
| Requirements | 183: 173 Done, 10 Blocked, none Partial or Missing ([TRACEABILITY.md](../requirements/TRACEABILITY.md)) |

**A first release has no upgrade to test.** Its 140 migrations run on an empty database. The way an upgrade is undone
(section 6) first matters at 1.0.1.

**No release notes exist.** For 1.0.0 they should say:

- that this is the first release;
- what it contains (TRACEABILITY.md);
- its known limits (the Blocked rows, and S1 and S2 above);
- what the hospital must provide (the *Who must act* column of TRACEABILITY.md).

## 3. The evidence

### 3.1 CI on the candidate

CI run #1030, on `ad5acb37`, 10 November 2026, 10:08 to 10:58 UTC. Every job passed.

| Job | What it checks | Result |
|---|---|---|
| Server and desktop (.NET), on Windows | C# formatting; package restore and audit; build, with warnings as errors; every .NET test, the end-to-end journeys among them, against a real PostgreSQL. Then: the database watchdog starts a crashed PostgreSQL again; the certificate probe runs as SYSTEM from Task Scheduler; a warm standby streams from the primary and takes over; the desktop updater is published as the release publishes it | 10,832 tests: 10,822 passed, 0 failed, 10 skipped, in 33 min. Every later check passed |
| Server installer, in PowerShell 5.1 and 7 | Twelve scripts: the settings file, the database sign-in, the two database accounts, restarts after a failure, `-NoStart`, Windows Update's active hours, power settings, firewall rules, the standby scripts, the desktop's self-update, release signing with a throwaway key, and the server preflight | Passed |
| Web apps (TypeScript) | A secret scan of every commit; package audit and patch windows; formatting; type checking; lint; unit tests; the demo; the build | Passed |
| Accessibility (WCAG 2.2 AA) | The staff app and the portal, in Chromium | Passed |
| SBOMs (CycloneDX) | The SBOMs the release attaches | Passed |
| Security patch windows (SEC-11) | No advisory past its patch window, and none unrecorded. `.github/security-advisories.json` records none | Passed |

**The skipped tests** state their own reason. Some need Redis, which a single-server hospital does not use; some need
`openssl`, or another operating system. Which ten skipped in this run is in its test reports, which were not read
here (*to confirm*).

### 3.2 How stable the tests are

| Last 30 completed CI runs on `main` | Count |
|---|---|
| Passed | 19 |
| Failed | 2 |
| Cancelled: a newer merge replaced the run | 9 |

Each failure was a single test, out of about 10,700:

| Run | Commit | Test | Cause | Status |
|---|---|---|---|---|
| #980 | `c3602887` | `OutpatientJourneyTests`, the portal-booked journey | The test made a time past midnight. The run was at about 23:00 IST | Fixed in the test (`5316ea9e`) |
| #1024 | `653e7dde` | `WebAuthnUnlockTests`, a refused unlock while the session ends | Not known: the unlock took more than 30 s to reach the lock it waits on | **Open (R3)** |

So one green run is good evidence, but it is not proof that the suite is stable. R3 has to be explained, not re-run.

### 3.3 Checks that run on a schedule

- **Security audit**, every day at 06:00 IST. It last ran on 10 November, on `653e7dde`, and passed. It covers the
  candidate too: between `653e7dde` and `ad5acb37` only deployment scripts, their tests, `ci.yml` and two documents
  changed, and no package list.
- **Load test**, every Monday at 02:00 IST. It last ran on 9 November IST, on `ec80fc81`, and passed. It runs on a
  shared runner, so its timings are only reported; they decide nothing about the hospital.
- **CodeQL** runs on every change. Its alerts on `main` were not read here (*to confirm*, on the repository's
  Security tab).
- **Dependabot.** When this branch was pushed, GitHub replied that it found 34 vulnerabilities on the default branch:
  12 high, 19 moderate and 3 low. CI's package audits and the patch-window check passed on the candidate, and
  `.github/security-advisories.json` records none. The alerts themselves could not be read with the tools in this
  session, so which packages they name is *to confirm* (R6).

### 3.4 What CI does not prove, and what does

| Claim | Not shown by CI because | Shown by | Who |
|---|---|---|---|
| The release workflow works | It has never run | A run on `1.0.0-rc.1` | HyFib |
| The installers work on the hospital's PC | CI uses its own Windows image, with no files downloaded | The preflight report, then System health after the install | Hospital IT, with HyFib |
| The server is fast enough there | GitHub's runners are not the hospital's machine | The load test at 75 users (PERF-01), and `pg_test_fsync` | Hospital IT, with HyFib |
| The data survives a lost disk | — | A backup restored on another PC | Hospital IT |
| Workstations update themselves | No key is trusted (R2) | The second release, offered by the server and taken by a workstation | HyFib |
| It is secure enough to hold patients | A test suite is not an audit | The ASVS L2 checklist, signed | The hospital's security officer |
| The pharmacy's registers are right | — | Three days in parallel, reconciled, signed | The pharmacist-in-charge, then the Drugs Inspector |

## 4. How a release is built and shipped

| Step | What happens | State |
|---|---|---|
| 1. Tag `vX.Y.Z` | Starts `release.yml` | Works. **No check of tag against version (R5)** |
| 2. `Publish-HC360.ps1` | Web apps: install from the lockfile, type check, tests, build. .NET: every test. Publishes the server (self-contained, web apps included), HC360 Desktop and the lab agent. Writes `SHA256SUMS.txt` | **The tests have no database (R1)** |
| 3. `Sign-HC360Release.ps1` | Signs HC360 Desktop's manifest (ECDSA P-256) with `HC360_RELEASE_SIGNING_KEY`. Without the secret, it builds unsigned and says so in the job summary | Tested in CI with a throwaway key |
| 4. Check the signature | `desktop verify` with the keys built into this version, when the package was signed | **No key is built in (R2)**: a signed package fails here |
| 5. Patch windows | No advisory past its window, and none unrecorded | Works |
| 6. SBOMs | CycloneDX, for each package and web app, added to `SHA256SUMS.txt` | Works |
| 7. Attach to the GitHub release | Zips, manifests, signature, SBOMs, checksums | Not yet run |
| — | Authenticode signature on the programs | **None (S1)** |

## 5. Installing 1.0.0 at Theni (waves 0 and 1)

Follow [NANA-THENI-READINESS.md](NANA-THENI-READINESS.md), section 4. This release adds four things to it:

1. **Before step 4 (check the files).** Compare the hashes with the checksums HyFib sent by the second channel (S1),
   then unblock the extracted folder (R4).
2. **After step 7 (install HC360).** Under Hospital setup › Features, turn the **patient portal** and **video
   consultations** off. They are on by default, and they belong to wave 7. ABDM is already off by default.
3. **Accounts.** Give roles to pharmacy and stores staff and to administrators only. Without department switches,
   roles are what keeps the other departments closed in wave 1.
4. **Integrations.** Leave SMS, WhatsApp, payments and Tally unset unless wave 1 uses them. A live server refuses the
   simulators (DEPLOYMENT.md, *Going live on a database used for training*).

**Why wave 1 needs no new code.** In wave 1 no live department hands work to one that is not live:

- the EMR is not used, so no orders and no prescriptions are signed;
- nobody is admitted, so no ward charges or medication chart lines are made;
- the stores' movements of register drugs go to the pharmacy's registers, and both are live.

The background services of other departments find nothing to work on. The pharmacy's refill reminders do run: decide
in wave 1 whether they send SMS. The seams in MODULE-ROLLOUT-PLAN.md, section 3.4, begin at wave 3.

## 6. Undoing it

- **Wave 1 itself.** The pharmacy's present software runs alongside HC360 during the parallel run. Undoing the
  go-live means going back to entering in it alone. Print what was entered in HC360 that day from its registers, and
  enter it by hand.
- **Upgrades from 1.0.1 onwards.** The way back is the previous package and the backup HC360 takes before an update.
  Never the previous package alone: nothing found at start-up stops an older version from starting on a database a
  newer one has changed (MODULE-ROLLOUT-PLAN.md, section 2). Rehearse each upgrade on a restored copy first.
- **HC360 Desktop.** Until R2 is closed, every new version is installed on each workstation with
  `Install-HC360Desktop.ps1`.

## 7. Go or no-go: the checklist

| Gate | Evidence | Signed by |
|---|---|---|
| R1 to R6 closed | The pull requests that close them; CI green on the tagged commit; each high Dependabot alert fixed or written down | HyFib engineering lead |
| The release workflow works | A green run on `v1.0.0-rc.1`, with every file attached | HyFib engineering lead |
| The checksums reached IT by a second channel | IT's note of the values compared | Hospital IT officer |
| The machine is fit | The preflight with no Fail, and a decision against each Warn | Hospital IT officer |
| UPS and backup disks | The preflight: *UPS on USB* passes; the USB disk is listed for backup copies | Hospital IT officer |
| BitLocker on every fixed disk | The preflight | Hospital IT officer |
| Fast enough | The load test at 75 users meets PERF-01 | Hospital IT officer and HyFib |
| The data survives a lost disk | The first night's backup verified, and restored on another PC | Hospital IT officer |
| Secure enough to hold patients | The ASVS L2 checklist, signed (SEC-06) | The hospital's security officer |
| The item master is right | The H1 and X list reviewed | Pharmacist-in-charge |
| Go | Every row above | The hospital's named decision-maker |

After go-live, the pharmacist-in-charge signs the parallel run (NANA-THENI-READINESS.md, section 2).

## 8. Risks accepted with 1.0.0

| Risk | Why it is accepted | Until |
|---|---|---|
| A 6-core desk PC where the SRS asks for 8 cores, kept in daily use | The load test decides; the readiness document's conditions apply | The hospital decides on a dedicated server (readiness document, section 6) |
| Up to a day of entries lost if the disk fails | The backup runs nightly at 01:30. WAL archiving would bring the loss down to 15 minutes (DEPLOYMENT.md, section 9) | The hospital decides whether to archive the WAL to the USB disk |
| Every workstation upgraded by hand | Only if R2 is put off | R2 is closed |
| Programs not signed | The second channel for the checksums (S1) | A code-signing certificate |
| Translations not reviewed | Patients first see them in wave 7 | The reviewers |

## Appendix: sources

From GitHub, on 10 November 2026:

- Releases and tags: none.
- Release workflow runs: none.
- CI runs on `main`: the last 30 completed. #1030 (`ad5acb37`, run 42796583920): six jobs, all passed. The .NET job
  (129763199704) printed `total: 10832, failed: 0, succeeded: 10822, skipped: 10`. #1024 (`653e7dde`, run
  42765853117): failed in its .NET job (129662137166), one test, 30.04 s against 30 s, at
  `WebAuthnUnlockTests.cs:338`. #980 (`c3602887`, run 42663896829): failed in
  `OutpatientJourneyTests.FreeSlotTodayAsync`.
- Security audit: run #20, 10 November, passed. Load test: run #23 (scheduled), 9 November IST, passed.
- `git push` of this branch, 10 November: "GitHub found 34 vulnerabilities on HyFib/HyFib-HealthCare360-NanaHospitals's
  default branch (12 high, 19 moderate, 3 low)."

From the repository, at `ad5acb37`:

- `.github/workflows/release.yml`, `ci.yml` (the PostgreSQL step and the comment above it), `security-audit.yml`,
  `load.yml`, `.github/security-advisories.json`;
- `deploy/windows/Publish-HC360.ps1`, `tools/release/Sign-HC360Release.ps1`;
- `src/shared/HyFib.HealthCare360.WindowsInterop/Updates/ReleaseKeys.cs`;
- `tests/HyFib.HealthCare360.IntegrationTests/TestDatabase.cs` and `WebAuthnUnlockTests.cs`;
- `git log -- tests/HyFib.HealthCare360.IntegrationTests/WebAuthnUnlockTests.cs`: last changed in `b4232ac9`, before the
  failure;
- `git diff --stat 653e7dde ad5acb37`: five files, none a package list;
- a search of `docs`, `deploy` and `README.md` for `ExecutionPolicy` and `Unblock-File`: none found;
- a search of `.github`, `deploy` and `tools` for Authenticode or `signtool`: none found.

From Microsoft: *about_Execution_Policies* and *Unblock-File* (learn.microsoft.com, PowerShell 7.6), for R4.
