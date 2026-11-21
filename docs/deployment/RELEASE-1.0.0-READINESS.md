# Release 1.0.0: readiness report

**Status: not released, and not ready for patients.** Assessed again on 18 November 2026, for the candidate
`31eb025f`, `main` after #377. Its CI run on `main` (#1215) passed all six jobs, with 12,473 tests.

This assessment adds what the earlier ones did not have:

- an independent review of the code on the paths where a defect harms a patient, money or privacy: medication, the
  laboratory, identity and access, and billing;
- mechanical scans of the whole server for the faults that hit hospital systems;
- mutation testing of the safety rules, to see whether the tests catch a fault (section 3.5).

**The review found 3 blockers and 12 high-severity defects** (section 1.1). Each was traced in the code by the
reviewer, then checked again by the author of this report. None of them is caught by today's tests, and none is in
the known limits.

Three things stand between this candidate and patients:

1. **The code review's findings (C).** Every blocker and high finding is fixed before the first wave it touches goes
   live, each with a test that fails before the fix (section 1.2). Wave 1 needs three at most.
2. **The release signing key (R2).** Only HyFib can make it. Until it exists, no release can be tagged.
3. **The hospital's own evidence** (section 7): the machine's preflight, the UPS and backup disks, the load test on its
   hardware, a restore drill, the ASVS sign-off, and the staff's acceptance of each wave's journeys. None of these is
   evidenced in the repository yet.

This report replaces the assessment of 16 November, whose candidate was `31cae7c0`. It was written from:

- the candidate's CI run on `main`, its CodeQL run, the daily security audit on it, and the 8 CI runs on `main` since
  16 November;
- the repository's tags and releases, and its open issues and pull requests;
- the code at the candidate: four area reviews, the scans in section 3.4, and the mutation tests in section 3.5;
- [TRACEABILITY.md](../requirements/TRACEABILITY.md), [ASVS-L2.md](../security/ASVS-L2.md),
  [NANA-THENI-READINESS.md](NANA-THENI-READINESS.md), and the hospital's answers
  ([accounts](NANA-THENI-ANSWERS-ACCOUNTS.md), [laboratory](NANA-THENI-ANSWERS-LAB.md)).

The appendix lists every source. What could not be checked is marked **to confirm**.

This report covers the first release to Nana Hospitals, Theni: the version, the evidence that it works, what stops it,
and how it is built, installed and undone. [MODULE-ROLLOUT-PLAN.md](MODULE-ROLLOUT-PLAN.md) covers what goes live
after it, one department at a time.

## What changed since 16 November

| | 16 November (`31cae7c0`) | 18 November (`31eb025f`) |
|---|---|---|
| .NET tests on the candidate | 12,164: 12,154 passed, 10 skipped | 12,473: 12,463 passed, 10 skipped |
| Endpoints, in route groups | 1,671 in 227 | The same |
| Tables, in schemas | 441 in 23 | The same |
| Database migrations | 149 | The same: none since 14 November |
| Requirements | 173 Done, 10 Blocked | The same |
| The code reviewed for harm | Not done | Medication, laboratory, identity and access, billing: 3 blockers, 12 high, 14 medium |

Merged since, in pull requests #363 to #379:

- **The hospital's answers.**
  - The Tally day book follows the accountant's answers (#376).
  - The laboratory follows the lab's answers (#377): 10-digit tube numbers, the Yumizen H500's parameters, calculated
    results, the blood gas, and ↑ and ↓ beside the flags.
  - What is still open is in the two answers documents.
- **Version updates** (#363, #364, #366, #375): GitHub Actions; six npm packages; 22 NuGet packages, among them
  EF Core and ASP.NET Core 10.0.15, Quartz 4.7.2 and WebView2; and jsdom 31.
  - The report of 16 November advised leaving version updates until after 1.0.0. These stay within .NET 10, and CI
    passed on each.
  - The .NET 11 updates (#362, #367 to #370) were merged on 19 November, after this report's candidate. CI passed on
    the result (`f26a6308`): SDK 11.0.100 builds HC360 for .NET 10 and its packages still carry runtime 10.0.15. The
    release now refuses a package that carries an older runtime than its ASP.NET Core packages
    (`tools/release/Test-BundledRuntime.ps1`).
- **An advisory accepted** (#379): GHSA-fc5q-39xc-vjpf, a ReDoS in picomatch 4.0.7. Only build and test tools use
  picomatch, and no fixed version exists. It is accepted in `src/web/pnpm-workspace.yaml` until 2 December (section 8).
- **This report** (#374).

## Where the blockers stand

| # | State | Shown by | Still needed |
|---|---|---|---|
| C (new) | **Open.** 3 blockers and 12 high-severity defects in the code | Section 1.1: each finding traced in the code at `31eb025f`, and checked again | Fix each with a test, wave by wave (section 1.2) |
| R2 | **Open.** `ReleaseKeys.Trusted` is still empty at `31eb025f`, so a tagged release stops at *Refuse to release an unsigned HC360 Desktop* | `src/shared/HyFib.HealthCare360.WindowsInterop/Updates/ReleaseKeys.cs` | **HyFib makes the key pair** (`Sign-HC360Release.ps1 -NewKey`). It stores the private key as the `HC360_RELEASE_SIGNING_KEY` secret, keeps a copy offline, and merges the public key into `ReleaseKeys.cs` (SECURITY.md, *Release signing key*). The first version each workstation installs must already trust it |
| R6 | **To confirm.** Dependabot's alerts could not be read in this session. In what ships, CI's audits and the daily security audit (#29, on `31eb025f`) find nothing beyond the accepted picomatch advisory | The audits; `.github/security-advisories.json`, which records none | Read the alerts on the Security tab, then fix each, or write down why it neither ships nor runs in the release build |
| R10 | **Open.** GitHub's CodeQL default setup still refuses the repository's own results. Run #1138's jobs say "The results are not uploaded to code scanning". Alerts 23 and 24 are still to be dismissed | CodeQL run #1138, on `31eb025f` | Someone allowed to manage security alerts dismisses 23 and 24, or code scanning moves to advanced setup only |
| S1 | Unchanged. The programs are not signed (Authenticode) | — | Send the checksums by a second channel with each release; a code-signing certificate later |

R1, R3 to R5 and R7 to R9 were closed before 16 November, and stay closed. The report of 16 November gives each.

## 1. The answer in one page

**Can HC360 be used in production? Not yet.** By every automatic measure, the code at `31eb025f` is a strong release
candidate. But an independent review found defects that would harm patients, money or privacy in normal use, and no
release can be made until the signing key exists.

**What the evidence shows is sound.**

- **CI run #1215 passed all six jobs.** 12,473 tests ran against a real PostgreSQL, with none failing. Both release-1
  journeys of SRS §9 pass. So do the watchdog, the certificate probe, the warm standby and the desktop updater checks.
- **Static analysis.** No high or critical CodeQL finding is open. The 13 medium ones are ruled false positives in
  SECURITY.md. The package audits find nothing in what ships.
- **The scans** (section 3.4):
  - no money held in floating point;
  - no SQL built from input;
  - no unfinished-code markers (TODO, FIXME, `NotImplementedException`) in what ships;
  - every swallowed exception is narrow and counted;
  - business dates are in IST throughout, with three small exceptions.
- **The reviewers checked the controls that matter most against the code.**
  - **Pharmacy and stores:**
    - stock cannot go negative;
    - expired, blocked and recalled batches are never dispensed;
    - Schedule H, H1, X and NDPS sales need a prescription and the buyer's identity;
    - the drug registers are append-only;
    - every unsafe request carries an idempotency key, so a retry is not applied twice.
  - **Laboratory:**
    - auto-validation holds back critical, abnormal, calculated and delta-failed results;
    - a critical value needs a documented read-back call before verification;
    - verified results are never overwritten by an analyser.
  - **Access:**
    - tokens are signed with ES256 only;
    - an ended session is refused on its next request;
    - the portal's one-time codes are limited and single-use;
    - every portal endpoint checks the patient's scope.
  - **Billing:**
    - every bill balances to the paisa, and so does its Tally voucher;
    - a refund is bounded by what was paid, and a second person approves it;
    - card and UPI payments are recorded once;
    - two concurrent edits of a bill conflict rather than both apply.
- **Mutation testing** (section 3.5). Of 137 faults seeded one at a time into six safety rules, the tests caught 88
  (64%).
  - They caught every fault in the rule that decides what laboratory results are released without a person (14 of
    14), and in the money rounding and the GST split (5 of 5).
  - The faults they missed are at boundaries and in rarer paths. Section 3.5 lists them, with the tests to add.

**What stops it.**

- The 15 defects of section 1.1.
- The signing key (R2).
- The hospital's gates and sign-offs (section 7), none of which is evidenced yet.

**Recommendation**

1. **Fix wave 1's findings first:** M4 (the controlled-drug registers after a downtime), P3 (sessions after a facility
   is taken away), and B3 if wave 1 uses Tally. Then fix the rest wave by wave (section 1.2).
   - Each fix comes with a test that fails before it.
   - Before each wave, also add the tests section 3.5 names for the rules that wave uses.
2. **HyFib's own steps:** make the signing key (R2), read Dependabot's alerts (R6), and have the two CodeQL alerts
   dismissed (R10).
3. **Make a release candidate.** Tag `1.0.0-rc.1` on a commit with the wave-1 fixes whose CI passed. Install it on the
   Theni machine for wave 0, with no patient data: the preflight, the load test and the restore drill.
4. **Go live with wave 1** once the hospital's gates in section 7 are signed. Each later wave goes live once its own
   findings are fixed and its staff have accepted its journeys.

### 1.1 What the code review found

**How it was done.**
- Four reviewers each read one area's code and tests at `31eb025f`. They looked for defects with a concrete path to
  harm, and recorded what they found sound.
- Every blocker and high finding below was then traced again in the code by the author of this report.
- The medium findings are as the reviewers reported them.

**Severity.**
- *Blocker*: likely in normal use to harm a patient, put a result on the wrong patient, corrupt a statutory record, or
  show a restricted patient's record to staff not entitled to it.
- *High*: real harm under specific but plausible conditions, or a statutory failure.

**Blockers**

| # | Area | What happens | Where | Stops |
|---|---|---|---|---|
| M1 | Medication | The medication chart (MAR) schedules only 24 hours of an order's doses each time the pharmacy issues it. The in-patient issue proposes the whole course, for example 10 vials for 5 days. From the second day the order is active but has no doses: nothing shows as due or overdue, and no missed dose is recorded. Doses come back only if a nurse presses *Schedule next 24 hours* on each order, each day | `Modules.Inpatient/Services/MarService.cs:53, 77–92, 666–681`; the quantity the issue proposes, `src/web/apps/staff/src/routes/_app/pharmacy/queue/$entryId.tsx:339` | Wave 5 |
| L1 | Laboratory | A mistyped tube number puts a whole analyser run on another patient's tube. Theni uses no barcodes: the technician types the 10-digit number on the H500 and the C150. The numbers are sequential, with no check digit, so one wrong or swapped digit usually names a real tube drawn minutes before or after. The host query then sends that patient's order, the analyser echoes their UHID, and the patient check passes. Where the test and the analyser allow auto-validation, the results can be verified without a person seeing them | `Modules.Diagnostics/DiagnosticsNumberSeries.cs:22–28`; `Services/InstrumentResultSink.cs:105–143, 238–258, 592–602` | Wave 4 |
| P1 | Privacy | The visit list and its search, a visit's details, a patient's visit history and payer, and the OPD queue show restricted (VIP) patients' full names, with no masking and no access log. The queue and a visit's details also show complaints. This reaches 22 of the 24 roles that read visits: all but the two that may open restricted records. SECURITY.md promises that "lists don't name them", and patient search and the doctor's worklist do mask them | `Modules.Patients/Features/Visits/VisitEndpoints.cs:13–25, 60–125, 140–181`; `Features/Queue/QueueEndpoints.cs:56, 92–106` | Wave 2 |

**High**

| # | Area | What happens | Where | Stops |
|---|---|---|---|---|
| M2 | Medication | When an order is stopped, an overdue dose of it stays due. It shows as an ordinary overdue dose, and it is accepted as given after the stop. Only doses scheduled from the stop on are cancelled, and the check before a dose never looks at the order's status | `MarService.cs:404–411, 966–973`; `Modules.Inpatient/Domain/Medication.cs:152–167` | Wave 5 |
| M3 | Medication | Ward medication orders (the doctor's own, verbal or telephone) get no allergy, interaction, dose or antimicrobial check. Later e-prescriptions do not see them as current medicines either | `MarService.cs:122–217`; `Modules.Clinical/Services/PrescriptionSafetyService.cs:41–56` | Wave 5 |
| M4 | Medication | A pharmacy sale entered from paper after a downtime writes its controlled-drug register line at the paper's time, but with the stock balance at the time it is entered. From then on, the H1, X and NDPS running balances, Form 3H and the narcotic count are wrong. The register is append-only, so the line cannot be corrected | `Modules.Pharmacy/Services/DispensingService.cs:443–444, 486`; `ControlledDrugRegisterService.cs:53, 64`; `NdpsService.cs:252–262` | **Wave 1** |
| M5 | Medication | Every issue against a ward indent, and every direct issue, charts a new "As directed" order with a dose due at once, even when the drug is already on the chart. The ward's due list does not show the note to confirm it with the doctor | `MarService.cs:684–711`; `Modules.Pharmacy/Services/InpatientPharmacyService.cs:336–343` | Wave 5 |
| L2 | Laboratory | A critical value phoned to the ward before verification can be changed, then verified on the old call: the check asks only whether the result was ever called. If the new value is not critical, the ward is never told that the value it was given was wrong | `Modules.Diagnostics/Services/LabResultService.cs:183–190` | Wave 4 |
| L3 | Laboratory | An amended report is never sent to the patient. For walk-in and outside-referral orders, no one is told at all. The patient's "report ready" message is sent only at first release, and the doctor's notice needs a doctor named on the order | `Services/LabReportService.cs:305–335, 354, 412–418` | Wave 4 |
| P2 | Privacy | Merging a VIP's duplicate into an unrestricted record lifts the restriction without the restriction-lift approval. The merge carries the sensitive categories, ABHA and Aadhaar over, but not the VIP flag, and nothing refuses to merge a restricted record | `Modules.Patients/Services/PatientRegistrationService.cs:425–447, 536–571` | Wave 2 |
| P3 | Access | Taking a facility away from a user leaves their open sessions working in it for up to 12 hours. Only deactivating a user ends their sessions, and a refresh issues the same facility again. At a hospital with one facility the usual step is deactivation, which does end sessions, so the risk at Theni is lower | `Platform/Identity/AccessAdminEndpoints.cs:494–501, 532–537`; `AuthService.cs:777–875`; `PlatformServiceCollectionExtensions.cs:213–229` | Wave 1, as it touches every wave |
| B1 | Billing | A bill cancelled after a credit note, a write-off, or a rejected claim's deduction is reversed again in full. Tally's ledgers end up wrong. GSTR-1 drops the invoice but still counts its credit notes, so it under-reports the GST | `Modules.Billing/Services/BillService.cs:421–457`; `TallyService.cs:164–179`; `GstReturnService.cs:144–167` | Wave 2 |
| B2 | GST | A bill's place of supply follows the payer's GSTIN state. So taxable lines billed to a payer registered in another state carry IGST: room rent above ₹5,000 a day, for example. IGST Act s.12(4) puts health services where they are performed, so the tax is CGST and SGST of Tamil Nadu | `Services/BillPlaceOfSupply.cs`; `Domain/Bills.cs:300–315` | Wave 2, the first that bills payers |
| B3 | Accounts | Paper bills, receipts from the manual book and paper pharmacy sales entered after their day went to Tally never reach Tally. They are dated by the paper, and nothing sends the day again | `BillService.cs:387`; `ReceiptService.cs:80–107`; `DispensingService.cs:486`; `TallyJobs.cs:144–169` | Wave 1 if it uses Tally; wave 2 |
| B4 | Billing | A ward return after an insured or package stay's final bill becomes a supplementary bill on self-pay terms, with a negative amount. That amount is refundable to the patient, though the insurer or the package paid for the medicine. The refund still needs a second person's approval | `Services/IpBillingService.cs:554–569`; `Modules.Pharmacy/Services/InpatientPharmacyService.cs:640–656` | Wave 5 |

**Medium, as the reviewers reported them**

- **Medication:**
  - **M6.** An in-patient e-prescription line typed in, not picked from the drug master, is charted as neither
    high-alert nor controlled unless "Controlled" is ticked, so its doses need no witness.
  - **M7.** Refills approved by a pharmacist, and counter sales to a registered patient, get no allergy or interaction
    check.
  - **M8.** The substitution rule ignores dosage form and release: a tablet for an injection, or a plain tablet for a
    modified-release one.
  - **M9.** The admission named by the client is not checked against the patient on prescriptions, indents and direct
    issues.
  - **M10.** The NDPS accounts and the shift count recognise an essential narcotic only by the item's optional generic
    name. Wave 1's item master must give it on every narcotic.
- **Laboratory:**
  - **L4.** Results of orders registered at the lab counter never reach the ordering doctor's review list.
  - **L5.** Verification is not tied to the value the pathologist reviewed, and "verify normal results only" is
    enforced only in the browser.
  - **L6.** A sample that fails partway through an analyser message can be half saved by the next sample's save.
  - **L7.** The first amended copy of a report prints without its revision number.
- **Privacy:**
  - **P4.** A patient's documents list applies the VIP rule but not the sensitive-category rule.
  - **P5.** VIP names are not masked in the visitor-pass list or the privacy-request register.
  - **P6.** A merge leaves the duplicate's laboratory and imaging records under the duplicate, by design. So the
    surviving record's lab history, its portal and its DPDP summary miss them.
- **Billing:**
  - **B5.** Debit notes are never posted to Tally, though GSTR-1 and the IRP get them.
  - **B6.** The canteen's taxable sales are not in HC360's GSTR-1, GSTR-3B or their JSON.
  - `docs/modules/billing.md` lists B5 and B6 as not covered yet. Section 2 now lists them too, for the release notes.

**Low, from the scans** (section 3.4):

- The age at death in the mortuary register and the age on a medico-legal document are worked out from UTC dates. The
  medico-legal age also divides days by 365.2425. Either can be a year short around a birthday, for an event between
  midnight and 05:30 IST.
- ABDM's consent records give the date their data is erased as a UTC date (`AbdmConsentRequestService.cs:214`).

### 1.2 Which wave each finding stops

Waves as in [MODULE-ROLLOUT-PLAN.md](MODULE-ROLLOUT-PLAN.md).

- **Every blocker and high finding is fixed before the first wave it touches goes live.** No wave is accepted with one
  open, and none is in the accepted risks (section 8).
- **A medium finding** is fixed before its wave, or the wave's department in-charge signs a written procedure for it.
  Section 7 gates both.

| Wave | Goes live | Fix first: every blocker and high | Medium: fixed, or a signed procedure |
|---|---|---|---|
| 0 Foundation | Install, preflight, load test, restore drill; no patient data | None (R2 for the tag) | — |
| 1 Pharmacy and stores | Stores, pharmacy | M4, P3; B3 if Tally is used | M7 (counter sales), M8, M10 (the item master) |
| 2 Front office and OP billing | Front office, appointments, billing, doctor fees | P1, P2, B1, B2, B3 | B5, P4, P5 |
| 3 Doctors | EMR and e-prescribing, emergency | None | M8 (e-prescriptions dispensed) |
| 4 Diagnostics | Laboratory, radiology, cardiology | L1, L2, L3 | L4 to L7, P6 |
| 5 In-patient | Wards and nursing, insurance, theatre | M1, M2, M3, M5, B4 | M6, M9 |
| 6 Back office | HR, support services, quality, MRD | None | B6 (the canteen) |
| 7 Outside and insights | Portal, video, ABDM, MIS | None | — |

**Fixing L1.** A check digit inside the 10 digits (9 digits and a Damm or Verhoeff digit, checked before the tube is
looked up) catches every single wrong digit and every swap of two neighbouring digits. While tube numbers are typed by
hand, also keep auto-validation off on those analysers. Have the technician check the patient's name on the analyser's
screen against the tube's label before each run.

## 2. What the release is

| | |
|---|---|
| Product and version | HyFib HealthCare360 1.0.0 (`Directory.Build.props`) |
| Candidate commit | `31eb025f` (`main`, 18 November 2026) |
| Built by | `release.yml`, on a tag `v*.*.*`, on a Windows runner |
| Packages | `HC360-Server-<version>-win-x64.zip`: the server, self-contained, with the staff app, the portal and the deployment scripts · `HC360-Desktop-<version>-win-x64.zip`: HC360 Desktop with its updater and installer, and its `.manifest.json` and `.manifest.sig` when signed · `HC360-LabAgent-<version>-win-x64.zip`: the lab agent for PCs with serial analysers · `SHA256SUMS.txt` · one CycloneDX SBOM for each package and web app |
| The server machine needs | Windows 10 2004 or later (Pro or above, for BitLocker); PostgreSQL 17, from `Install-PostgreSQL.ps1`; Microsoft Edge, for PDFs |
| Workstations need | Windows 10 or 11 with the WebView2 runtime (Windows 11 has it) |
| What it holds | The whole product: 18 modules, 1,671 endpoints in 227 route groups, 441 tables in 23 schemas, 149 database migrations, 43 groups of staff screens (counted at the candidate with MODULE-ROLLOUT-PLAN.md's commands) |
| Requirements | 183: 173 Done, 10 Blocked, none Partial or Missing ([TRACEABILITY.md](../requirements/TRACEABILITY.md)) |

**Known limits.** These belong in the release notes:

- **Built on the server, with no screen yet.** These come from the production-readiness review of 14 November:
  - supplier returns (purchase debit notes), payable adjustments, and stock levels for each store;
  - HR's statutory payroll: EPF, ESIC, bank files, TDS challans, Form 16, Form 24Q, gratuity, bonus, and employees'
    tax declarations;
  - doctors' TDS returns (26Q and 16A) and their tax profiles;
  - quality audit templates and starting an audit, an HAI case entered by hand, and registering SMART apps;
  - about 45 to 55 settings with no admin screen, among them `lab.requirePaymentBeforeCollection`,
    `inpatient.bedDayPolicy` and `doctorfees.tds.percent`.
- **Not in the accounts yet** (`docs/modules/billing.md`):
  - debit notes are not posted to Tally (B5);
  - the canteen's taxable sales are not in the GST returns (B6).
- **The HPR registry check** runs against its simulator until the hospital is connected to ABDM's HPR service, so a
  real doctor shows as not found.
- **The review of 14 November's ten hardening items** (section D) are open. None is a blocker.
- **Hindi and Tamil** have not been reviewed by native speakers (PP-06).
- **The ASVS L2 checklist**, as assessed on 30 September, has 130 requirements Met, 61 Partial and 8 Gap, and it is
  not signed (SEC-06).
- **The hospital's open answers.**
  - Tally: T2, T9, T16, T18 to T21 and N13 are unanswered, and the formats of T10 and the Tally partner's sample JSON
    were not received.
  - The laboratory: L1 to L3 are unanswered, and the price list and the list of outsourced tests were not received.
  - Both answers documents give the detail.

**A first release has no upgrade to test.** Its 149 migrations run on an empty database. The way an upgrade is undone
(section 6) first matters at 1.0.1.

**No release notes exist.** For 1.0.0 they should say:

- that this is the first release;
- what it contains (TRACEABILITY.md);
- its known limits (above, the Blocked rows, S1 and S2, and any finding of section 1.1 not yet fixed);
- what the hospital must provide (the *Who must act* column of TRACEABILITY.md).

## 3. The evidence

### 3.1 CI on the candidate

CI run #1215, on `31eb025f`, 18 November 2026, 05:14 to 06:23 UTC. Every job passed.

| Job | What it checks | Result |
|---|---|---|
| Server and desktop (.NET), on Windows | C# formatting; package restore and audit; the build, with warnings as errors; every .NET test against a real PostgreSQL, the end-to-end journeys among them. Then: the database watchdog starts a crashed PostgreSQL again; the certificate probe runs as SYSTEM from Task Scheduler; a warm standby streams from the primary and takes over; the desktop updater is published as the release publishes it | 12,473 tests: 12,463 passed, 0 failed, 10 skipped, in 40 min. The end-to-end tests took 38 min 46 s. Every later check passed |
| Server installer, in PowerShell 5.1 and 7 | Sixteen scripts: the settings file, the database sign-in, the two database accounts, the restore marker, the restore drill's decisions, the backup sizing arithmetic, restarts after a failure, `-NoStart`, Windows Update's active hours, power settings, firewall rules, the standby scripts, the desktop's self-update, release signing with a throwaway key, the release's version and tag check, and the server preflight | Passed |
| Web apps (TypeScript) | A secret scan of the change's history; the package audit; formatting; type checking; lint; unit tests; the demo; the build | Passed. The audit passes because #379 accepts GHSA-fc5q-39xc-vjpf |
| Accessibility (WCAG 2.2 AA) | The staff app and the portal, in Chromium | Passed |
| SBOMs (CycloneDX) | The SBOMs the release attaches | Passed |
| Security patch windows (SEC-11) | No advisory past its patch window, and none unrecorded | Passed |

**The release-1 journeys of SRS §9 are among them.** Both passed in run #1215:

- `OutpatientJourneyTests.A_new_patient_is_registered_seen_tested_treated_and_billed`: register → appointment →
  consult → lab → pharmacy → bill;
- `InpatientJourneyTests.An_insured_patient_is_admitted_charged_for_the_stay_discharged_and_the_insurer_settles_the_claim`:
  admit → bed → charges → discharge bill → insurance claim.

**What CI's tests do not reach.** None of the 15 findings of section 1.1 is caught by a test. Each is a hand-over
between two parts of the product that the tests take one at a time, for example:

- the pharmacy's issue and the ward's chart (M1, M5);
- a downtime's paper and the registers or Tally (M4, B3);
- a cancellation and the credit notes before it (B1);
- a merge and the VIP rule (P2).

These are what the staff's acceptance of each wave's journeys is for (section 7).

**The skipped tests** state their own reason. Some need Redis, which a single-server hospital does not use. Others need
`openssl`, or another operating system. Which ten were skipped is in the run's test reports, which were not read here
(*to confirm*).

### 3.2 How stable the tests are

| CI runs on `main` since 16 November (#1194 to #1215) | Count |
|---|---|
| Passed | 7 |
| Failed | 1 |

The one failure, #1210 at the merge of #376, was the package audit and the security patch windows. It failed on
GHSA-fc5q-39xc-vjpf, an advisory that reached the npm audit on 17 November, and #379 accepted it. No test has failed on
`main` since #1186.

Two lessons came from the pull request of #377:

- **A test failed after midnight IST.** `GstComplianceTests`'s hand-recorded e-way bill test failed at 18:47 UTC,
  17 minutes after midnight IST: it typed e-way bills as generated before their invoices. It was fixed in `0a01ec87`,
  in that pull request. The same test also checked a 14-digit e-way bill number that would have been accepted from
  1 January 2027; that check is fixed too.
- **The end-to-end tests once ran past the 60-minute limit.** They usually take 35 to 40 minutes. On one run they took
  over 60, on a runner whose build was 30% slower than usual, across midnight IST, and `dotnet test --timeout 60m`
  stopped them. The next run took 35 minutes. Watch how close the suite comes to the limit as it grows.

### 3.3 Checks that run on a schedule

- **Security audit**, every day at 06:00 IST. Run #29 on `31eb025f`, 18 November, passed.
- **Load test**, every Monday at 02:00 IST. Run #41 on 16 November, on `381fa7c3`, passed. It runs on a shared runner
  with front-office accounts only, so its timings are only reported. They decide nothing about the hospital's machine.
- **CodeQL** runs on every change, with the `security-extended` queries. While default setup is on as well (R10), its
  jobs check their own results and list them in their summaries. On the candidate (run #1138):
  - **C#:** 12 findings, none high or critical open, and 1 high suppressed in the source (`IdentitySeeder.cs:162`).
  - **TypeScript:** 4 findings, none high or critical open, and 2 high suppressed (`payments.ts:194`,
    `offlineUnlock.ts:238`).
  - **Actions:** none.
  - The 13 medium findings are the ones SECURITY.md's table rules false positives.
- **Dependabot.** The five .NET 11 updates were merged on 19 November (SDK 11.0.100, building for .NET 10). The
  alerts are R6.

### 3.4 The scans

These were run on the whole server (`src/server`, `src/shared`, migrations left out) at the candidate.

| Looked for | Found | Verdict |
|---|---|---|
| A business date taken from UTC or the server's clock (`DateTime.Now`, `UtcNow.Date`, `DateOnly.FromDateTime(….UtcDateTime)`) | 5 places. 3 are wrong: the mortuary's age at death, the medico-legal document's age, and ABDM's erase date (section 1.1, Low). The backup generations' names in UTC are internal, and the certificate check's local time is correct. `HospitalDateTests` already refuses the commonest form | Sound, but for the three |
| Money or a quantity in `double` or `float` | None in billing, pharmacy, stores, HR or payments. One ratio in asset depreciation | Sound |
| SQL built from strings | 10 raw SQL calls, none built from input. 5 commands put identifiers in their text: database and table names HC360 makes itself, a fixed channel, and a checked password verifier | Sound |
| Exceptions swallowed | 6 empty `catch` blocks, each of one narrow type (`JsonException`, `CryptographicException`, `OperationCanceledException`, `SemaphoreFullException`). The two that read audit anchors and the erasure ledger count each unreadable line | Sound |
| Endpoints open without sign-in | 50, in 27 files: sign-in, health, webhooks, report and prescription QR verification, device and lab agent push, and public lists. `EndpointAuthorizationTests` reviews the list | Sound by name. The identity review read the sign-in and portal ones |
| Unfinished code: TODO, FIXME, HACK, `NotImplementedException` | None in what ships, server or web | Sound |
| `async void`, and warnings turned off | None, and 4 `#pragma` lines, each with its reason | Sound |
| Waiting on async work synchronously | 7 places: settings and configuration read through synchronous wrappers, and one short lock | Low risk |

### 3.5 Mutation testing

Mutation testing changes a rule in a small way and checks that some test fails. A change no test notices is a fault the
tests would let through.

**How it was done.**

- Each fault was a one-line change to a rule at `31eb025f`, made one at a time.
- After each change, the module's whole unit test project was built and run, and then the file was put back.
- A fault counts as caught when any test failed.
- The baselines passed before and after: Diagnostics 917 tests, Billing 828, Pharmacy 118.
- Stryker.NET 5.1.0 was tried first and set aside. With xUnit v3 on Microsoft Testing Platform it could not capture
  coverage, and it reported every fault in `ResultInterpreter.cs` as missed. The faults made by hand disprove that.

| Rule | File | Faults | Caught | Score |
|---|---|---|---|---|
| What laboratory results may be released without a person | `Modules.Diagnostics/Domain/Laboratory/Rules/AutoValidationPolicy.cs` | 14 | 14 | 100% |
| Money: rounding to the paisa, and splitting GST into CGST and SGST | `Money` (`ValueObjects.cs`) | 5 | 5 | 100% |
| The calculated results' formulas | `ResultFormulas.cs` | 24 | 17 | 71% |
| The reference interval, by age and sex | `ReferenceRangeSelector.cs` | 15 | 9 | 60% |
| Bill totals: concessions, discounts, packages, the payer's share | `Modules.Billing/Domain/BillTotalsCalculator.cs` | 36 | 21 | 58% |
| Calculated results: inputs, refusals, taking a result back | `CalculatedResults.cs` | 12 | 7 | 58% |
| A pharmacy sale: returns, cancellation, who may refund | `Modules.Pharmacy/Domain/PharmacySale.cs` | 12 | 6 | 50% |
| The flags: H, L, HH and LL from a value | `ResultInterpreter.cs` | 19 | 9 | 47% |
| **All** | | **137** | **88** | **64%** |

Eleven of the missed faults change nothing a test could see, such as a guard repeated a line later. Without them, 88
of 126 were caught (70%).

**What the score says.** Two of the rules are fully pinned by their tests:
- **auto-validation**, the gate between a result and its release: dropping any one of its conditions fails a test;
- **the paisa rounding and the GST split.**

Each fault the tests missed is not a defect: the code as it stands was right in every case. Each is a place where a
later change could break the rule without a test noticing. They cluster at boundaries (a value exactly at a limit) and
in rarer paths.

**The missed faults that matter, and the tests to add before the wave that uses each rule**

| Missed | What a test should hold | Where | Wave |
|---|---|---|---|
| CR8, CR7 | A calculated result is never taken from an input on another tube, or on another ordered item, of the same order | `CalculatedResults.cs:124, 127` | 4 |
| CR10, CR11 | A verified calculated result goes back to unverified when its input is withdrawn, or when it can no longer be calculated | `CalculatedResults.cs:78, 104` | 4 |
| CR4 | A formula never writes into a result kept in another unit | `CalculatedResults.cs:95` | 4 |
| RI6 | A value read against an interval with no numeric limits is not flagged Normal. Normal is the only flag auto-validation accepts | `ResultInterpreter.cs:104` | 4 |
| RI8 to RI11, RI14 | Values written "<=x" or "≤x"; values exactly at a "<x" or ">x" limit; critical text typed in another case | `ResultInterpreter.cs:124–198` | 4 |
| RS15 | A critical answer listed only for the female interval still counts when a patient is read against both | `ReferenceRangeSelector.cs:81` | 4 |
| RF7, RF8, RF10, RF11, RF19, RF22, RF23 | Each formula's refusal exactly at its limit. For example, changing the globulin check from `<= 0` to `< 0` would divide by zero when globulin is exactly 0 | `ResultFormulas.cs` | 4 |
| PH11, PH12 | Whoever sold an invoice, or took its payment, cannot cancel or refund it: the separation of duties | `PharmacySale.cs:180` | 1 |
| PH1, PH7, PH8 | An item that expires today; each half of the "has returns" check before a cancellation | `PharmacySale.cs:209, 257` | 1 |
| BT27, BT8, BT6, BT7, BT2, BT3, BT14 | A discount larger than the bill or the line; a concession on a line that allows none; a discount on a free-treatment bill; a package cover above the gross or below zero; a co-pay outside 0 to 100% | `BillTotalsCalculator.cs:99–135, 203` | 2 |

### 3.6 What CI does not prove, and what does

| Claim | Not shown by CI because | Shown by | Who |
|---|---|---|---|
| The release workflow works when tagged | It has run only on pull requests, which neither sign nor publish | A run on `v1.0.0-rc.1`, with every file attached | HyFib |
| The installers work on the hospital's PC | CI uses its own Windows image, with no files downloaded | The preflight report, then System health after the install | Hospital IT, with HyFib |
| The server is fast enough there | GitHub's runners are not the hospital's machine | The load test at 75 users (PERF-01), with the peak mix of billing, pharmacy, lab, ward and portal steps (LOAD-TESTS.md), run on the real 6-core desk PC as a site task, and `pg_test_fsync` | Hospital IT, with HyFib |
| The data survives a lost disk | — | A backup restored on another PC. The weekly test restore also checks each backup, from the first week | Hospital IT |
| Each wave's journeys work as the staff work | The tests take one part at a time; the findings of section 1.1 lie between parts | Each wave's staff run their own day's work on the training server, with the downtime kit once, and sign it | Each department's in-charge |
| The analysers' results reach the right patient | The analysers are simulated | A day of real samples on the H500 and the C150, with typed tube numbers checked against the worklist (L1) | The laboratory in-charge |
| The day book is right in Tally | Tally is simulated | One day posted and imported into the hospital's Tally company, checked by the accountant | The accountant |
| Workstations update themselves | No key is trusted (R2) | The second release, offered by the server and taken by a workstation | HyFib |
| It is secure enough to hold patients | A test suite is not an audit | The ASVS L2 checklist, signed; an independent security test by a CERT-In-empanelled auditor | The hospital's security officer |
| The pharmacy's registers are right | — | Three days in parallel, reconciled and signed, including one downtime drill (M4) | The pharmacist-in-charge, then the Drugs Inspector |

## 4. How a release is built and shipped

| Step | What happens | State |
|---|---|---|
| 1. Tag `vX.Y.Z` | Starts `release.yml`. It refuses a commit CI has not passed on `main`: the newest CI run of a push of that commit decides, so a re-run that passed counts. It checks that the tag names the version | The version check is tested in CI. The CI check has not run yet: it runs only on a tag. So tag a commit of `main` whose own CI run passed |
| 2. `Publish-HC360.ps1` | Web apps: install from the lockfile, type check, tests, build. .NET: every test. It publishes the server (self-contained, web apps included), HC360 Desktop and the lab agent, stops if the developer settings reach the server package, and writes `SHA256SUMS.txt` | Passed end to end on #333 and on #361. Not run since on `main`, because no change touched the release |
| 3. `Sign-HC360Release.ps1` | Signs HC360 Desktop's manifest (ECDSA P-256) with `HC360_RELEASE_SIGNING_KEY`. Without the secret, it builds unsigned and says so in the job summary | Tested in CI with a throwaway key; never run for real |
| 4. Check the signature | `desktop verify` with the keys built into this version, when the package was signed. An unsigned package stops a tagged release | **No key is built in (R2)**: until the key exists, no release can be made |
| 5. Patch windows | No advisory past its window, and none unrecorded | Works |
| 6. SBOMs | CycloneDX, for each package and web app. They, and HC360 Desktop's manifest and signature, are added to `SHA256SUMS.txt` | Works |
| 7. Attach to the GitHub release | Zips, manifests, signature, SBOMs, checksums | Not yet run |
| — | Authenticode signature on the programs | **None (S1)** |

## 5. Installing 1.0.0 at Theni (waves 0 and 1)

Follow [NANA-THENI-READINESS.md](NANA-THENI-READINESS.md), section 4. This release adds six things to it:

1. **In step 1, before anything runs.** Check the files against `SHA256SUMS.txt` and the copy HyFib sent another
   way (S1). Unblock the checked zip, and let the window run scripts (R4; DEPLOYMENT.md section 1).
2. **After step 7 (install HC360).** Under Hospital setup › Features, turn the **patient portal** and **video
   consultations** off. They are on by default, and they belong to wave 7. ABDM is already off by default.
3. **Accounts.** Give roles to pharmacy and stores staff and to administrators only. Without department switches,
   roles are what keeps the other departments closed in wave 1.
4. **Integrations.** Leave SMS, WhatsApp, payments and Tally unset unless wave 1 uses them. A live server refuses the
   simulators (DEPLOYMENT.md, *Going live on a database used for training*).
5. **Supplier returns.** Before wave 1, decide how the stores return expired or damaged stock to a supplier: on paper,
   posted once a screen exists, or through screens HyFib builds first. Payable adjustments and stock levels for each
   store wait the same way. The item master's own reorder levels work.
6. **The item master and the downtime kit (M4, M10).**
   - Give every narcotic its generic name, as Form 3H names it.
   - M4 is fixed before wave 1 (section 1.2). The parallel run's downtime drill then checks the registers after paper
     sales of H1, X and NDPS drugs are entered.

**Why wave 1 needs no other new code.** In wave 1, no live department hands work to one that is not live:

- the EMR is not used, so no orders and no prescriptions are signed;
- nobody is admitted, so no ward charges or medication chart lines are made;
- the stores' movements of register drugs go to the pharmacy's registers, and both are live.

The background services of other departments find nothing to work on. The pharmacy's refill reminders do run: decide
in wave 1 whether they send SMS.

The seams in MODULE-ROLLOUT-PLAN.md, section 3.4, begin at wave 2, with one: an emergency visit registered at the
front office opens a case on the ER board, which goes live in wave 3. Close those cases at wave 3's take-on. From
wave 3 the seams are many.

## 6. Undoing it

- **Wave 1 itself.** The pharmacy's present software runs alongside HC360 during the parallel run. Undoing the go-live
  means going back to entering in it alone. Print what was entered in HC360 that day from its registers, and enter it
  by hand.
- **Upgrades from 1.0.1 onwards.** The way back is the previous package and the backup HC360 takes before an update.
  - Never use the previous package alone. On a database a newer version changed, start-up stops and changes nothing
    (rollout item 7).
  - DEPLOYMENT.md section 7, *Going back*, gives the steps.
  - Rehearse each upgrade on a restored copy first.
- **HC360 Desktop.** Until R2 is closed, every new version is installed on each workstation with
  `Install-HC360Desktop.ps1`.

## 7. Go or no-go: the checklist

| Gate | Evidence | Signed by |
|---|---|---|
| The wave's blocker and high findings fixed | Each finding of section 1.2's *Fix first* column, closed with a test that failed before the fix; CI green on the commit | HyFib engineering lead |
| The wave's medium findings decided | Each finding of section 1.2's *Medium* column: fixed with a test, or a written procedure for it | The wave's department in-charge |
| The blockers closed | R2's key in `ReleaseKeys.cs`; CI green on the tagged commit; Dependabot's alerts read, each fixed or written down (R6); CodeQL alerts 23 and 24 dismissed (R10) | HyFib engineering lead |
| The release workflow works | A green run on `v1.0.0-rc.1`, with every file attached | HyFib engineering lead |
| The checksums reached IT by a second channel | IT's note of the values compared | Hospital IT officer |
| The machine is fit | The preflight with no Fail, and a decision against each Warn | Hospital IT officer |
| UPS and backup disks | The preflight: *UPS on USB* passes; the USB disk is listed for backup copies | Hospital IT officer |
| BitLocker on every fixed disk | The preflight | Hospital IT officer |
| Fast enough | The load test at 75 users meets PERF-01 | Hospital IT officer and HyFib |
| The data survives a lost disk | The first night's backup verified, and restored on another PC | Hospital IT officer |
| Alerts reach a person | The SMS sender registered on DLT, the on-call numbers set, the outside monitor live, and the alert drill's timings (NANA-THENI-READINESS.md, steps 12 to 16) | Hospital IT head |
| Secure enough to hold patients | The ASVS L2 checklist, signed (SEC-06) | The hospital's security officer |
| The item master is right | The H1 and X list reviewed; every narcotic's generic name given (M10) | Pharmacist-in-charge |
| Supplier returns decided | How the stores return stock to suppliers in wave 1, written down (section 5) | Pharmacist-in-charge and the stores in-charge |
| The wave's journeys accepted | The wave's staff ran a day's work on the training server, including a downtime drill, and signed it | Each department's in-charge |
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
| The medium findings of section 1.1 not yet fixed | Each is covered by a written procedure its wave's department in-charge signed (section 7). Blocker and high findings are never accepted: each is fixed first | Each is fixed |
| picomatch GHSA-fc5q-39xc-vjpf (ReDoS) | Only build and test tools use it, on the repository's own patterns; nothing that ships contains it; no fixed version exists | 2 December 2026, or a fixed picomatch |
| The ASVS L2 checklist's Partial and Gap rows | The security officer's sign-off decides each | SEC-06 is signed |
| Translations not reviewed | Patients first see them in wave 7 | The reviewers |

## Appendix: sources

From GitHub, on 18 November 2026:

- Releases and tags: none. Open issues: none. Open pull requests at `31eb025f`: Dependabot's #362 and #367 to #370 (.NET 11), merged on 19 November.
- CI on the candidate: run #1215 (44242861292, `31eb025f`, a push to `main`), with six jobs, all passed.
  - The .NET job (134594673331) printed `total: 12473, failed: 0, succeeded: 12463, skipped: 10, duration: 39m 58s`.
  - Its test step ran from 05:40 to 06:20 UTC, and the end-to-end tests took 38 min 46 s.
- CI on `main` since 16 November: #1194, #1195, #1196, #1197, #1203, #1213 and #1215 passed. #1210 (44225819854,
  `d8955eed`) failed at the package audit and the patch windows, on GHSA-fc5q-39xc-vjpf.
- On the pull request of #377:
  - Run 44136025766 on `0043849c`: the end-to-end tests stopped at 1 h 00 min 01 s, and `GstComplianceTests` failed
    at 18:47 UTC.
  - Run 44154024477 on `0a01ec87`: the .NET tests passed, with the end-to-end tests taking 35 min.
- Security audit: run #29 (44262097984), 18 November, on `31eb025f`, passed. Load test: run #41 (43778619774), 16
  November, on `381fa7c3`, passed.
- CodeQL on the candidate: run #1138 (44242861427). Its jobs' summaries: C# (134594674102) 12 findings, 0 high or
  critical, 1 suppressed; TypeScript (134594673897) 4 findings, 0 high or critical, 2 suppressed; Actions
  (134594674049) none. Each says "The results are not uploaded to code scanning".

From the repository, at `31eb025f`:

- `src/shared/HyFib.HealthCare360.WindowsInterop/Updates/ReleaseKeys.cs`: `Trusted` holds only a commented example.
- `.github/security-advisories.json`: no advisories. `src/web/pnpm-workspace.yaml`: `auditConfig.ignoreGhsas` holds
  GHSA-fc5q-39xc-vjpf, with its reason and 2 December.
- TRACEABILITY.md's summary: 183 requirements, 173 Done, 10 Blocked. ASVS-L2.md: 130 Met, 61 Partial, 8 Gap, 54 N/A,
  assessed on 30 September.
- The figures in section 2: the commands in MODULE-ROLLOUT-PLAN.md's appendix, run at the candidate.
- `docs/security/roles.json`: 24 of the 32 roles read visits by default (`visits.read`); 2 of them,
  SuperAdmin and HospitalAdmin, may open restricted records (`patients.restricted.read`).
- `git log --merges 31cae7c0..31eb025f`: pull requests #363 to #379.

The code review (section 1.1), at `31eb025f`:

- Four reviewers each read one area's code and tests: medication (Clinical, Pharmacy, Inpatient, Supply chain),
  the laboratory (Diagnostics and the laboratory integrations), identity and access (Patients, Privacy, the Platform's
  identity, authorization and auditing, the portal, ABDM), and billing (Billing, payments, Tally, GST).
- The author of this report traced each blocker and high finding again, at the lines given.
- The reviewers also listed what they did not reach. These parts were not reviewed:
  - medicines given outside the medication chart (emergency, theatre, dialysis, chemotherapy, blood);
  - QC internals, EQAS, outsourcing, and the lab agent's authentication;
  - FHIR and SMART token issuance, and ABDM's receiving side;
  - acquirer settlement matching, chargebacks, PM-JAY, doctors' fees and TDS.
