# HealthCare360 gap review: 21 November 2026 (HEAD `f26a6308`)

## How this review was done

- **Starting point.** It did not start from `TRACEABILITY.md`'s verdict (173 Done, 10 Blocked).
  - It started from the code and tests at HEAD and from the release review in `RELEASE-1.0.0-READINESS.md` §1.1, made at `31eb025f`.
  - That review read only four areas: medication, laboratory, privacy and billing.
- **Who read what.** Five read-only reviewers, each reading the code and its tests for one scope:
  1. the 31eb025f findings, checked against their fix commits and at HEAD;
  2. in-patient (not the MAR), blood bank, operation theatre (OT), dialysis, the Medical Termination of Pregnancy (MTP) register and emergency;
  3. patients, scheduling, engagement, virtual care and the portal;
  4. supply chain, support services, HR and quality;
  5. platform, interop, analytics, AI and deployment.
- **Checking the findings.**
  - Each finding cites file:line evidence.
  - The lead re-traced these in the code:
    - the telehealth guest re-admission;
    - the AI processing agreement;
    - the credential gate;
    - OT consent;
    - restraints;
    - ER disposition.
  - The lead overruled one reviewer finding: the Tamil Nadu Professional Tax slabs were reported as wrong. They are kept as a "confirm locally" item, because the seeded slabs appear to match Greater Chennai's 2024 revision (from recollection, not checked against the notification).
- **Not done.** No tests were run, and the React screens were not compared with the server rules.
- **What would show these findings are right.** For each one, a failing test, written to its scenario, before the fix.

Severity follows the readiness report. **P0** means likely harm to a patient, a statutory failure, or data loss; fix before the wave it touches. **P1** is medium: fix, or have the department in-charge sign a written procedure. **P2** is low.
Waves are as in `MODULE-ROLLOUT-PLAN.md`: 1 Pharmacy/stores, 2 Front office/OP billing, 3 Doctors/ER, 4 Diagnostics, 5 In-patient/OT/insurance, 6 Back office, 7 Portal/video/ABDM/MIS.

## Status of the 31eb025f findings

- **Fully fixed by the commits of 19 November, each with a test:** 12 of the 15 blockers and highs (M1, M2, M4, M5, L2, L3, P1, P2, P3, B1, B2 and B3).
- **Partly fixed:** M3, L1 and B4 (listed below).
- **Fixed in passing:** B5, by `2668bafe`.
- **Still open:** the other mediums and lows (listed under P1 and P2).

## P0: fix before the wave goes live

| # | Module | Gap | Evidence | Wave |
|---|---|---|---|---|
| 1 | Supply chain: cold chain | A temperature excursion raises an alert, but nothing quarantines the stock. The at-risk items are free text, the excursion closes at the first reading back in range, and FEFO keeps issuing the batches (insulin, vaccines). | `SupplyChain/Services/ColdChainService.cs:227-275`, `Domain/ColdChain.cs:122-162`, `Domain/FefoAllocator.cs:119-124` | **1** |
| 2 | Platform: build | The ".NET 11 held until after 1.0.0" pull requests are merged. global.json pins SDK 11.0.100, and the Microsoft.Extensions packages are 11.0.0, running on a net10.0 / 10.0.15 shared framework. The readiness assessment was not made on this build, and no test checks the runtime it bundles. Revert the merges, or re-assess on this build. | `global.json:3`, `Directory.Packages.props:45,49,62,77`, `RELEASE-1.0.0-READINESS.md:64,361,545` | **0** |
| 3 | Platform: backup | The only off-machine backup copy is optional, and a missing one raises a warning, not a critical alert. A default install keeps its backups on the server's own disk, and point-in-time recovery needs a separate script. Make a second copy a go-live gate. | `deploy/windows/Install-HC360Server.ps1:95,1004`, `SystemHealthBackups.cs:55,188` | **0** |
| 4 | Emergency | An ER death or abscondence closes on free-text notes alone. No death certificate (MCCD), no police intimation for a medico-legal case (MLC), no second person, and no event: the patient stays "alive" for bookings and SMS. The IPD path demands all of these. | `Clinical/Domain/Emergency/EmergencyCase.cs:153-202`; compare `Inpatient/Domain/DischargeRules.cs:43-100` | 3 |
| 5 | HR to Clinical | A lapsed TNMC or TNNMC registration blocks nothing. Credentials only feed an alert job; no code outside HR reads them, so rostering and prescribing are not gated. | `HR/Domain/Credential.cs:72-98`, `HR/Services/RosterService.cs:99-162` | 3 |
| 6 | OT | Surgical and anaesthesia consent is a checklist tick. `ConfirmSafety` and `Start` never look for a recorded `PatientConsent`. The consent types `HighRiskProcedure` and `Anaesthesia` are used nowhere outside the file that defines them. Transfusion, by contrast, does check consent. | `SpecialtyCare/Domain/OperationTheatre/PreOpChecklist.cs:28`, `SurgeryBooking.cs:205-290` | 5 |
| 7 | Blood bank | No two-sample (group-check) rule for a patient never grouped before: one sample, grouped once, leads to cross-match and issue. A wrong-blood-in-tube sample goes undetected. The time the sample was drawn is not recorded. | `SpecialtyCare/.../BloodRequest.cs:89-120`, `TransfusionService.cs:137-138` | 5 |
| 8 | Inpatient: restraints | A restraint episode keeps the ward it was ordered on. After a transfer, the receiving ward never sees the restraint or its overdue checks. After discharge the episode stays Active for ever. No job looks for overdue checks. | `Inpatient/Features/Nursing/RestraintEndpoints.cs:165,296`; `TransferService` and `DischargeService` never reference restraints | 5 |
| 9 | CSSD | A load without implants is released on chemical indicators while its biological indicator (BI) is pending. `ContainsImplants` is set by the operator. A failed BI recalls only its own load: there is no look-back to the last passing BI, and no minimum BI frequency. | `SupportServices/Domain/Cssd/SterilizationLoad.cs:32,112-140`, `CssdRecall.cs:22-37` | 5 |
| 10 | Medication (M3, partial) | Ward orders now get the allergy, interaction, dose and route check, but not the antimicrobial stewardship check. | `MarService.AddOrderAsync` (commit `6bd828cc`) | 5 |
| 11 | Billing (B4, partial; unverified) | A late return of an item charged on an *interim* bill may still go to a self-pay supplementary bill, as a refund to the patient. `ReturnCredits.IsCreditedOn` matches only the final bill's lines. No test covers it. | `Billing/Domain/ReturnCredits.cs:66-80` | 5 |
| 12 | Virtual care | A forwarded telehealth guest link lets a second person straight into a running call. Re-admission matches *any* earlier admitted guest, because guests have a null `UserId`, and the failed-join counter is per connection. | `VirtualCare/Hubs/TelehealthHub.cs:341-368` | 7 |
| 13 | AI | The text paths (note structuring, assistant) never check `ai.processingAgreement`; only voice does. When no patient is open, redaction is by pattern only, so names and diagnoses reach the cloud model. VIP and sensitive-category patients are not excluded. | `AI/Services/AiTextServiceRouter.cs`, `Features/Voice/VoiceEndpoints.cs:49`, `PatientDirectory.cs:30` | 7, or whenever AI is switched on |
| 14 | Laboratory (L1, partial) | Tubes numbered before the check-digit switch are still found by exact number, so a typo among them can still land on a neighbouring tube. This matters only on a migrated database, not on a fresh one. | `TubeNumbers.Matching`, `lab.tubeCheckDigitSince` | 4 |
| 15 | Rollout | Department switches are not built: plan items 1 to 6, about 1,661 endpoints, 29 background services and 41 event seams. From wave 3, a live EMR will fill worklists in departments still on paper. | `MODULE-ROLLOUT-PLAN.md` §2, §9 | Before 3 |

## P1: medium

**Medication and pharmacy**
- **M6:** an in-patient prescription line typed in by hand is charted as not high-alert and needing no witness (`MarService.cs:722-743`).
- **M7:** refills and counter sales get no allergy or interaction check (`RefillService.cs:99-120`).
- **M8:** substitution ignores dosage form and release (`DispensingPolicies.cs:52-104`).
- **M9:** the client's AdmissionId is not checked against the patient (`InpatientPharmacyService.cs:111,214`).
- **M10:** NDPS (narcotic) accounting relies on an optional generic name (`Ndps.cs:13-15`).

**Laboratory**
- **L4:** results ordered at the lab counter never reach the ordering doctor (`DiagnosticReportReleasedHandler.cs:34-47`).
- **L5:** verification is not tied to the value reviewed, and "normal only" is enforced in the browser only.
- **L6:** a failed analyser message can be half-saved by the next sample's save (`LabMessageProcessor.cs:73-92`).
- **L7:** the first amendment prints without its revision number (`LabReportDocument.cs:62`).
- **P6:** a merge leaves the duplicate's lab and imaging records behind.

**Privacy**
- **P4:** the patient documents list skips the sensitive-category rule (`FrontOfficeEndpoints.cs:691-710`).
- **P5:** VIP names are unmasked in visitor passes and the privacy-request register.

**Billing**
- **B6:** canteen sales are missing from GSTR-1 and GSTR-3B.
- **Stale traceability:** `TRACEABILITY.md:338` still says a payer in another state is billed IGST, which contradicts the B2 fix.

**Blood bank, OT, dialysis and in-patient**
- **Cross-match window:** the 72-hour window runs from the bench test, not from when the sample was drawn (`CrossMatch.cs:31`).
- **Transfusion reaction:** a report neither stops the transfusion nor quarantines sibling components (`TransfusionService.cs:475-533`).
- **Dialysis serology:** it can be typed in without a lab result, a new patient defaults to Negative, and an overdue rescreen does not block a session (`DialysisService.cs:79-80`, `DialysisSession.cs:111-146`).
- **OT counts:** sign-out counts can be marked "not applicable" for any procedure (`SurgicalCounts.cs:172-177`).
- **No paediatric early warning score:** there is no early warning for patients under 16 (`News2.cs:85-88`), and pregnant patients are scored without an exclusion.
- **Discharge holds:** they ignore open NEWS2 escalations, active restraints and tests drawn but not resulted (`DischargeService.cs:528-537`).

**Front office, scheduling and remote patient monitoring (RPM)**
- **Duplicate check-in:** two desks checking in at once can create two open visits and two tokens. The check runs outside the transaction, and no unique index stops it (`VisitService.cs:70-110`).
- **Duplicate registration:** the duplicate check's `ConfirmedNotDuplicate` is a plain request flag, with no permission, reason or audit. The check takes no lock (`PatientRegistrationService.cs:133-148`).
- **Doctor's leave:** it does not move or notify patients automatically, and reminders still say "see you tomorrow". The stranded-patient list stops at 500 (`StrandedPatientNotifier.cs`, `ScheduleService.cs:261`, `AppointmentReminderService.cs:64-72`).
- **RPM alerts:** an unacknowledged critical alert is never escalated (`VirtualCareJobs.cs`). Care-team paging is silently skipped when there is no team or no phone on file (`RpmAlertService.cs:144-151`).

**Supply chain, support services, HR and quality**
- **GRN:** goods receipt does not compare the rate with the purchase order's rate, and accepts lines with no PO line on a PO-linked receipt (`GoodsReceiptService.cs:107-137`).
- **Incidents:** an incident can close while its CAPA is completed but not verified (`IncidentService.cs:446-448`, `IncidentRules.cs:67-70`).
- **CAPA effectiveness:** it can be verified on day 0 (`Capa.cs:102-136`).
- **Mortuary:** a body can be released with no MCCD, no check of the bill, and no two-identifier tag match. The police clearance for an MLC is unchecked free text (`Mortuary.cs:132-198`).
- **Biomedical waste (BMW):** a breach of the 48-hour limit raises no alert, and the Schedule I yellow sub-categories are missing (`BioMedicalWaste.cs:4-17,80`).
- **PF and ESI:** their rates and ceilings are code constants. The ₹25,000 ESI ceiling for staff with a disability is missing, and there is no minimum-wage floor (`StatutoryDeductions.cs:11-13,49-51`).
- **Concurrent stock issue:** no test runs parallel issues (the design is sound).

**Platform**
- **SMART tokens:** the facility grant is not re-checked on each request, so access lasts up to 15 minutes after a grant is removed (`SmartAuthentication.cs:70-82`).

## P2: low

- **Dates and ages:**
  - age at death in the mortuary register is worked out from UTC dates (`MortuaryService.cs:143`);
  - the medico-legal age is worked out from UTC dates and days ÷ 365.2425 (`MedicoLegalDocuments.cs:142`);
  - the ABDM erase date is a UTC date (`AbdmConsentRequestService.cs:214`).
- **In-patient and OT:**
  - a NEWS2 escalation reminder names the old ward and bed after a transfer or discharge;
  - one user can confirm every role's part of the WHO checklist.
- **Portal and telehealth:**
  - there is no limit on how many future bookings one portal account can hold;
  - portal feedback may lack idempotency (unverified);
  - telehealth recording consent is a flag only.
- **Platform:**
  - the OTP lockout layer was not traced;
  - MFA is not required for clinical roles by default;
  - there is no generic "background job stalled" alert;
  - outbox delivery is at least once, and handler idempotency is unverified.
- **HR and stores:**
  - the biometric (ADMS) device's IP is pinned to whoever calls first;
  - an Expiry-reason stock adjustment is not checked against the batch's expiry;
  - income-tax rates are a code table, to confirm against the Finance Act;
  - an incident can be assigned to a reviewer who was involved in it;
  - TN Professional Tax slabs: confirm Theni municipality's rates.

## Documentation and outside parties (not code defects)

- **Stale reports:** `RELEASE-1.0.0-READINESS.md` and `TRACEABILITY.md` predate the fixes of 19 November.
  - They still list every blocker and high as open.
  - They do not record what remains of M3, L1 and B4.
  - Their candidate commit and test counts are out of date.
- **Not built, by the documents' own account:**
  - uploading to IHIP and Ni-kshay (done by hand);
  - a ward duty rota;
  - the e-way bill API;
  - antibiotic time-outs beyond the restricted list;
  - staff fingerprint sign-in.
- **Waiting on outside parties:**
  - the ABDM sandbox and production credentials;
  - an IRP/GSP sandbox run;
  - the WHO ICD-API and the licensed ICD-10 file;
  - UIDAI (AUA) onboarding;
  - sign-off of the ASVS L2 checklist (61 Partial, 8 Gap);
  - native review of the Hindi and Tamil translations;
  - performance runs on the Server Node hardware;
  - the release signing key (`ReleaseKeys.Trusted` is empty);
  - the hospital's unanswered Tally and laboratory questions.
