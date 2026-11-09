# Quality module (IRMS: incidents, RCA, CAPA, risk register, NABH indicators)

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Quality` · schema `quality` · depends on Platform, Patients
(an MvPI report names its patient by initials, age and sex from the registration), and Clinical, Inpatient, Diagnostics
and Pharmacy, whose records the computed indicators and infection surveillance read (never write). The theatre and blood
bank (SpecialtyCare, the same layer) are read through `ISurgeryAndTransfusionFacts`. Staff are referenced as Platform user
ids. Covers SRS **IRMS-01..06**: incident reporting, severity grading, investigation/RCA, CAPA tracking, a risk register
with likelihood × impact scoring, and NABH quality indicators; and adverse events with medical devices reported to the
Materiovigilance Programme of India ([MvPI](#device-adverse-events--mvpi-apiv1qualitymvpi)).

## Permissions

| Permission | Default roles | Purpose |
|---|---|---|
| `quality.incidents.report` | all staff roles | Report incidents (optionally anonymously), see own named reports, work on CAPAs one owns |
| `quality.incidents.manage` | QualityManager, HospitalAdmin | Review, grade, assign, investigate and close incidents; receives sentinel alerts; reports device adverse events to MvPI |
| `quality.incidents.approve` | QualityManager, HospitalAdmin | Approve closing a sentinel or never event, and re-grading one out of sentinel, asked for by someone else; never the incident's reporter (MAKER-CHECKER.md, slice 14) |
| `quality.capa.manage` | QualityManager, HospitalAdmin | Plan CAPAs, verify effectiveness, cancel |
| `quality.risks.manage` | QualityManager, HospitalAdmin | Risk register |
| `quality.indicators.manage` | QualityManager, HospitalAdmin | Indicator definitions, values, trends, scorecard |
| `quality.downtime.manage` | QualityManager, HospitalAdmin | The downtime log: record downtime and drills, end them, sign off their back-entry ([downtime.md](downtime.md)) |
| `quality.hai.manage` | QualityManager, HospitalAdmin | Infection surveillance: review cases against the NHSN definitions, see the HAI rates; receives the positive-culture alerts. Give it to the infection control nurse and officer |
| `quality.hai.devices` | Nurse, Doctor, QualityManager, HospitalAdmin | Record central lines, urinary catheters and ventilation put in and taken out (device days) |
| `quality.audits.manage` | QualityManager, HospitalAdmin | NABH internal audits: checklists, the schedule, closure |

The dashboard needs any of the four management permissions.

## Endpoints

### Incidents (`/api/v1/quality/incidents`)
| Method | Path | Permission | Body → Response |
|---|---|---|---|
| POST | `/` | incidents.report | `ReportIncidentRequest {type, severity, occurredAt, location, departmentId, patientId, involvedStaffUserIds, description (≥ 20 chars), immediateAction, witnesses, sentinelKind, flagAsSentinel, isAnonymous, involvesDevice}` → 201 `IncidentReceipt {id, incidentNumber, isSentinel, severity, type}` |
| GET | `/mine` | incidents.report | own **named** reports → `MyIncidentItem[]` |
| GET | `/` | incidents.manage | filters `status, type, severity, sentinelOnly, openOnly, departmentId, from, to`, `search`; sort `occurredAt, reportedAt, severity, number` → `IncidentListItem` (incl. open CAPA count). Severity sorts by harm (NearMiss → Death), not alphabetically: enums are stored as text |
| GET | `/{id}` | incidents.manage | → `IncidentDetail` (next allowed statuses, involved staff names, investigation status, CAPAs, `involvesDevice`, `isDeviceRelated`, the MvPI reports raised from it, `closureAskedAt` and `downgradeAskedAt` while a sentinel event's closing or re-grading waits); writes a PHI-access audit entry when a patient is linked |
| PUT | `/{id}` | incidents.manage | `UpdateIncidentRequest {type, severity, sentinelKind, flagAsSentinel, location, departmentId, patientId, involvedStaffUserIds, version, involvesDevice, reason?}` (re-grading; becoming sentinel alerts the team) → 204. Re-grading a sentinel event so it is no longer sentinel needs a `reason` and answers 202 with the approval (`quality.sentinel_downgrade`): nothing is saved until the quality manager approves |
| POST | `/{id}/transition` | incidents.manage | `TransitionIncidentRequest {status, note}` → 204; closing a sentinel event (on its completed RCA) answers 202 with the approval (`quality.sentinel_close`) and the incident stays where it is until approved |
| POST | `/{id}/assign` | incidents.manage | `AssignIncidentRequest {userId}` (a Reported incident moves to UnderReview; the assignee is notified) |
| GET | `/{id}/investigation` | incidents.manage | → `InvestigationDto` |
| PUT | `/{id}/investigation` | incidents.manage | `SaveInvestigationRequest {method: FiveWhys|Fishbone, problemStatement, whys[], factors[{category: People|Process|Equipment|Environment|Communication|Policy, description, isRootCause}], findings, recommendations, investigatorUserIds}` → investigation id. The first save moves the incident to Investigation |
| POST | `/{id}/investigation/complete` · `/reopen` | incidents.manage | completion needs a problem statement, findings and at least one root cause, plus ≥ 3 whys (five whys) or ≥ 2 factors (fishbone) |

**Severity** grades: `NearMiss, NoHarm, Mild, Moderate, Severe, Death`. An incident is **sentinel** when the severity
is Death or Severe, when a never event is selected (`WrongSiteSurgery, WrongPatient, WrongProcedure,
RetainedForeignObject, InfantAbductionOrWrongDischarge, PatientSuicide, AboIncompatibleTransfusion, SexualAssault,
UnexpectedDeath, Other`), or when a person flags it. A sentinel event immediately notifies every
`quality.incidents.manage` holder in the facility (critical in-app alert). Moderate harm raises a warning. Nothing
else notifies.

**Workflow** (`IncidentWorkflow`, unit-tested):
`Reported → UnderReview | Closed (duplicate/invalid)`, `UnderReview → Investigation | CapaInProgress | Closed`,
`Investigation → CapaInProgress | Closed`, `CapaInProgress → Investigation | Closed`, `Closed → UnderReview`
(reopen). Closing and reopening need a note. Leaving Investigation needs a completed RCA. CapaInProgress needs at
least one CAPA. Closing from CapaInProgress needs every CAPA verified or cancelled. A sentinel event can be closed
only after a completed RCA, from any status, Reported included (`incident.sentinel_needs_rca`; a duplicate of a
sentinel report is re-graded out of sentinel first).

**Second person (MAKER-CHECKER.md, slice 14).** Closing a sentinel or never event, and re-grading one so that no RCA is
due, wait for a holder of `quality.incidents.approve` (the approvals inbox), never the one who asked (`approval.self`)
nor the incident's reporter (`incident.reporter_approves`, 403, the request still waiting). The change is made only over
the incident as asked (`approval.stale`), the RCA and CAPAs checked again, and audited `quality.incident.sentinel_closed`
or `quality.incident.sentinel_downgraded` with both names. A never event the theatre raises (`IIncidentRegister`,
retained item) is sentinel and named to whoever settled the count, as before.

**Anonymous reporting.** With `isAnonymous` the reporter's id and name are not stored. The insert runs through
`AnonymousReportingScope`: a fresh DI scope on a flow without the HTTP context, so the platform's `CreatedBy` stamp
and data-audit event record no user, session, device or IP. The scope fails closed (422
`incident.anonymity_unavailable`) if it could still see the user. Later edits by the quality team are audited
normally. Alerts never name the reporter.

**A medical device involved.** The reporter, or the quality team on review, marks an incident `involvesDevice` (an
implant, an instrument, a monitor, a consumable). An incident is *device-related* (`isDeviceRelated`) when it is an
`EquipmentFailure` or is so marked; its screen then offers **Report to MvPI**.

### Device adverse events — MvPI (`/api/v1/quality/mvpi`)
India's Materiovigilance Programme (MvPI, run by the Indian Pharmacopoeia Commission as the national coordination centre
under the Medical Devices Rules 2017) asks hospitals to report adverse events with medical devices on its *Medical
Device Adverse Event (MDAE) reporting form*. HC360 keeps the hospital's copy (`MvpiReport`, table
`quality.mvpi_reports`, numbered `quality.mvpi`). The quality team raises a report from a device-related incident (the
incident's day, place, account and action taken, its patient, and a death graded as a death) or for a patient or no
patient at all. The device is filled in from what billing says of the devices charged to the patient
(`IBillingStatusProvider.GetDevicesAsync`): the newest implant becomes the device (brand as its name, maker, batch as
the lot, serial, implanted on the day it was used, single use), and the others are offered to pick.

What the report holds, in the form's order:
* **Patient**: initials (never the name; `PatientNames.Initials`), age in completed years on the day of the event, sex,
  weight; or *no patient involved*.
* **Event**: date of the event and the date the hospital became aware, where it happened, what happened, seriousness
  (death with its date, life-threatening, hospitalisation initial or prolonged, disability, congenital anomaly,
  intervention to prevent permanent impairment, other) and the patient's outcome (recovered, recovering, not recovered,
  fatal, unknown).
* **Device** (`MvpiDevice`, one `jsonb` column): brand or trade name, common name or category, risk class A–D (MDR 2017),
  manufacturer and address, importer, model, catalogue, serial and lot numbers, UDI, made and expiry dates, software
  version, implantable with the dates implanted and explanted, single use or reusable and whether reprocessed, operated
  by a health professional, the patient or someone else, and whether it is kept for evaluation (where: quarantine),
  not available, or returned to the manufacturer (when).
* **Reporter**: name (whoever raised it), designation, department and the date of the report; the hospital's name,
  address and contact print from the facility.
* **Sent to MvPI**: the date, by whom, MvPI's (or the monitoring centre's) reference; a follow-up report names the report
  it follows.

**Workflow** (`MvpiReport`, unit-tested): a **Draft** may be saved half written, but its facts must fit together (no
event in the future, aware on or after the event, a fatal outcome with death as the seriousness, a death on or after the
event, implanted before the event, explanted after implanting, made before expiry, a patient or *no patient*, not
both). It is recorded as **Submitted** only with what MvPI needs (`Missing()`, also on the DTO): the patient or *no
patient*, where and what happened, the date of a death, the device's name and manufacturer and one of its model,
catalogue, serial or lot number or UDI, the implant date of an implantable device, where a device kept for evaluation is
kept or when it went back to the manufacturer, and the reporter's designation; otherwise 400 `mvpi.incomplete` with the
fields. A sent report cannot change (422 `mvpi.submitted`): what is learned later goes on a **follow-up** report (a new
draft copied from it, naming it), and only its reference can be corrected or added, with a reason (the data audit keeps
the reference it replaced). There is no due date in HC360: MvPI's guidance asks for prompt reporting, serious events
first.

| Method | Path | Body → Response |
|---|---|---|
| GET | `?status&from&to&incidentId` | the register, newest event first (`from`/`to` on the day of the event; at most 500) → `MvpiReportListItem[]` |
| GET | `/prefill?incidentId&patientId` | → `MvpiPrefill` (the incident's event and patient, the devices billed to the patient, the newest implant as the device); a patient's lookup is audited as PHI access |
| GET | `/{id}` · `/{id}/document` · `/{id}/pdf` | → `MvpiReportDto` (with `missing`) / the printable MDAE form / its PDF; audited as PHI access when it names a patient |
| POST | `/` | `SaveMvpiReportRequest {incidentId, patientId, noPatient, weightKg, eventOn, awareOn, location, description, seriousness, deathOn, outcome, device, reporterDesignation, reporterDepartment}` → `MvpiReportDto` (a draft) |
| PUT | `/{id}` | the same with `version`; the incident stays the one it was raised from; 422 `mvpi.submitted` once sent |
| POST | `/{id}/submit` | `SubmitMvpiReportRequest {submittedOn, submittedBy, reference, version}`: `submittedBy` defaults to the user; on or after the hospital became aware, not in the future |
| POST | `/{id}/reference` | `CorrectMvpiReferenceRequest {reference, reason, version}` (sent reports only) |
| POST | `/{id}/follow-up` | → a new draft naming the sent report (`followUpOfId`) |

Every route needs `quality.incidents.manage`: the reports are raised from the incident register and hold the same
patient's data, so the register's managers keep them. A report that names a patient opens only for those who may open
the patient's record (a restricted record stays closed). The register and the printable form are on **Quality → Device
adverse events (MvPI)** (`/quality/mvpi` in the staff app). The form prints in the MDAE form's order (patient, event,
device, reporter, sending), with ticked boxes for its choices, and names the patient by initials only.

Not built: sending to MvPI's online system (ADRMS) or by e-mail (the printed or PDF form is sent, and the reference
recorded); devices from the biomedical asset register (equipment failures of the hospital's own equipment are filled in
by hand); the in-vitro diagnostic device form.

### CAPA (`/api/v1/quality/capas`)
| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/` | capa.manage | filters `status, openOnly, overdueOnly, ownerUserId, incidentId, source`, `search` → `CapaListItem` |
| GET | `/mine` | incidents.report | own actions (any facility), `includeClosed` |
| GET | `/{id}` | capa.manage or the owner | → `CapaDetail` |
| POST | `/` | capa.manage | `CreateCapaRequest {source: Incident|Risk|Audit|Complaint|IndicatorBreach|Other, incidentId, riskId, type: Corrective|Preventive, action, ownerUserId, dueDate}` → 201 id. The owner is notified. An incident under review or investigation moves to CapaInProgress when the workflow allows |
| PUT | `/{id}` | capa.manage | `UpdateCapaRequest {action, ownerUserId, dueDate, effectivenessCheckDue, version}` |
| POST | `/{id}/start` | owner or capa.manage | |
| POST | `/{id}/complete` | owner or capa.manage | `CompleteCapaRequest {note}`; an effectiveness check is scheduled for +30 days and the quality team is notified |
| POST | `/{id}/verify` | capa.manage, **not the owner** | `VerifyCapaRequest {effective, note}`. Not effective sends the action back to InProgress for rework and notifies the owner |
| POST | `/{id}/cancel` | capa.manage | `CancelCapaRequest {reason}` |

CAPA states: `Open → InProgress → Completed → Verified`, plus `Cancelled`. An action is overdue when it is open or in
progress past its due date.

### Risk register (`/api/v1/quality/risks`, `quality.risks.manage`)
| Method | Path | Body → Response |
|---|---|---|
| GET | `/` | filters `category, rating, status (default: all but Closed), departmentId, reviewOverdue`, `search`; sort `score (default desc), residualScore, reviewDate, number, title` → `RiskListItem` |
| GET | `/matrix?residual=` | 5 × 5 heat map of open risks → `RiskMatrixCell {likelihood, impact, score, rating, count}` |
| GET | `/{id}` | → `RiskDetail` |
| POST | `/` | `SaveRiskRequest {title, description, category, departmentId, likelihood, impact, residualLikelihood, residualImpact, existingControls, mitigationPlan, ownerUserId, reviewDate, status}` → 201 `{id, riskNumber, score, rating}` |
| PUT | `/{id}` | `SaveRiskRequest` with `version` |
| POST | `/{id}/review` | `ReviewRiskRequest {likelihood, impact, residualLikelihood, residualImpact, existingControls, nextReviewDate (future), note, version}` |

`RiskMatrix` (unit-tested): score = likelihood × impact. Scores 1–4 are **Low**, 5–9 **Medium**, 10–16 **High** and
20–25 **Extreme**. Residual risk must be scored as a pair and can never exceed the inherent risk.

### Quality indicators (`/api/v1/quality/indicators`, `quality.indicators.manage`)
| Method | Path | Body → Response |
|---|---|---|
| GET | `/?category&includeInactive` | → `IndicatorDto` (with the latest value and target status) |
| POST/PUT | `/`, `/{id}` | `SaveIndicatorRequest {code, name, category, numeratorDefinition, denominatorDefinition, unit: Percent|PerThousand|Minutes|Hours|Days|Ratio|Count, multiplier?, target, direction: HigherIsBetter|LowerIsBetter, frequency: Monthly|Quarterly|Annually, nabhReference, sortOrder, isActive}` |
| GET | `/{id}/values?from&to` | → `IndicatorValueDto[]` |
| PUT | `/{id}/values` | `RecordIndicatorValueRequest {periodStart (any date in the period), numerator, denominator (> 0), notes}`; upserts the facility's value for that period and stores `meetsTarget` |
| DELETE | `/values/{valueId}` | |
| GET | `/{id}/trend?from&to` | → `IndicatorTrendDto {points, latest, average, trend: InsufficientData|Improving|Stable|Worsening}` (default: last 12 periods) |
| GET | `/scorecard?period` | every active indicator's value for the period (default: last month) → `ScorecardRow[]` |

`IndicatorMath` (unit-tested): value = numerator ÷ denominator × multiplier, to 2 decimals. The multiplier defaults to
100 for % and 1,000 per thousand. The target is met when value ≥ target (higher is better) or value ≤ target (lower
is better). Quarters start in Jan, Apr, Jul and Oct, and years start on 1 April (financial year). The trend compares
the latest value with the mean of up to three previous values. A change of ±5 % or less counts as Stable.

### Computed indicators (`/api/v1/quality/indicators`, `quality.indicators.manage`)
An indicator HC360 can measure from its own records is **computed** (`QualityIndicator.ComputedBy`, the key of its
computation in `IndicatorMeasures`); the rest are **entered by hand** as before. A computed indicator takes no value by
hand (`PUT /{id}/values` → 422 `indicator.computed`). Every computation is for one facility and a period of IST calendar
days, both inclusive; each one's numerator and denominator are below, and its arithmetic is in `IndicatorFormulas`
(unit-tested with worked examples).

| Code | Numerator | Denominator | Unit |
|---|---|---|---|
| `ER-IA-TIME` | minutes from arrival in casualty to the first doctor's assessment (`EmergencyCase.DoctorSeenAt`), cancelled visits left out | emergency patients seen by a doctor | average minutes |
| `OPD-IA-TIME` | minutes from the visit's registration to the start of its first consultation (`Encounter.StartedAt`) | OPD visits with a consultation | average minutes |
| `OPD-WAIT` | minutes from a queue token's issue to the start of service | tokens served | average minutes |
| `IPD-IA-TIME` | minutes from admission to the first consultant visit | admissions with a consultant visit | average minutes |
| `DIAG-WAIT-LAB` | minutes from an out-patient lab order to its first sample collected | OPD orders with a sample | average minutes |
| `DIAG-WAIT-RAD` | minutes from the patient's arrival in radiology to the start of the study | studies started (with an arrival recorded) | average minutes |
| `LAB-TAT` | tests released within the test's turnaround time (`LabTest.TatHours`) from the order | tests released whose test has a turnaround time | % |
| `MED-ERR-1000` | medication-error incidents of a patient who was an in-patient then (staff reports and missed doses) | in-patient days, midnight census | per 1,000 |
| `ADR-RATE` | ADR reports whose reaction started during an admission | admissions in the hospital at some time in the period | % |
| `SSI-RATE` | confirmed SSI cases, date of event in the period | operations completed | % |
| `CLABSI` · `CAUTI` · `VAP` | confirmed cases of the type, date of event in the period | central-line · urinary-catheter · ventilator days | per 1,000 |
| `FALL-RATE` | falls recorded on the nursing chart | in-patient days | per 1,000 |
| `PRESSURE-ULCER` | pressure injuries not present on admission | in-patient days | per 1,000 |
| `SENTINEL-RCA` | sentinel events reported in the period with the RCA completed within 30 days of the report | sentinel events reported | % |
| `RTN-ICU-48` | transfers out of critical care (`BedAssignment.IsCriticalCare`) followed by critical care again within 48 h | transfers out of critical care to a ward | % |
| `RTN-OT-48` | urgent or emergency operations started within 48 h of the same patient's previous operation in the stay | operations completed | % |
| `REINTUB-48` | extubations (planned or accidental end of a ventilator episode) followed by ventilation again within 48 h in the stay | extubations | % |
| `TRANSF-RXN` | transfusion reactions (onset in the period) | units transfused at the bedside | % |
| `MORTALITY` | in-patients who died (`AdmissionStatus.Expired`) | discharges of every kind | % |
| `LAMA-DAMA` | in-patients who left or were discharged against medical advice | discharges of every kind | % |
| `DISCH-TAT` | minutes from starting the discharge to the patient leaving, planned discharges | planned discharges started in HC360 | average minutes |
| `ALOS` | days in hospital (discharge day − admission day, a same-day stay 1) | discharges | average days |
| `BED-OCC` | in-patient days | beds in service today × days in the period | % |

Choices made in these definitions: in-patient days are the midnight census, as the in-patient census counts them (a
same-day stay gives none). A wait whose end is recorded before its start is left out, not counted as zero. Moves between
ICU beds are one critical-care spell, and a spell ending with the stay (discharge, death) is no transfer out. An
"unplanned" return to OT is one booked as urgent or emergency; a staged elective operation is not one. Bed occupancy uses
today's beds in service (the hospital keeps no history of bed counts). The SSI rate of a month firms up later (an SSI may
surface up to 30 days, or 90 with an implant, after the operation), so recomputing a past month is expected.

**Monthly computation.** `IndicatorComputationJob` computes the month just ended for every active facility after 02:00 IST
on each of the first 10 days of the next month (so documentation entered late is taken in); then the month's values stand
until someone recomputes them. Job nodes only, under a cluster-wide advisory lock. A stored computed value carries
`isComputed` and `computedAt`, and the denominator note (patient days from N stays, device days from N episodes). A value
the quality team **entered by hand** for the period (before the indicator was computed) is never overwritten
(`KeptHandEntry`); a period with nothing to measure stores nothing; an indicator whose records are not in this
installation (no theatre or blood bank) is reported as `SourceUnavailable`, not as zero.

| Method | Path | Body → Response |
|---|---|---|
| POST | `/compute` | `ComputeIndicatorsRequest {period}` (any day in the month; default last month; a month still running is computed to today) → `ComputedIndicator[] {code, periodStart, outcome: Stored|KeptHandEntry|NothingToMeasure|SourceUnavailable, numerator, denominator, value, meetsTarget}` |
| POST | `/{id}/compute` | the same for one indicator; 422 `indicator.not_computed` for one entered by hand |
| GET | `/{id}/drilldown?period` | → `IndicatorDrilldown` computed now: numerator, denominator, value, the stored value beside it, the note, and the records (role Numerator / Denominator / Excluded with why, kind, reference such as the admission, visit, incident or case number, the patient's UHID, a restricted record showing none, time, minutes for a wait), at most 2,000. Audited as PHI access: one entry for the list and one per patient listed |

The indicator seeder adds the computed indicators and switches a seeded indicator to computed only while its numerator
and denominator are still the text it seeded (one the committee redefined stays theirs, by hand). Entered by hand: hand
hygiene compliance, needle-stick injuries per worker at risk, medication errors per chart reviewed (`MED-ERR`), patient
satisfaction.

### Infection surveillance — HAI (`/api/v1/quality/hai`)
NABH HIC asks for surveillance of hospital-acquired infections. HC360 surveys CLABSI, CAUTI, VAP and SSI against the CDC/NHSN
surveillance definitions.

**Device register** (`DeviceEpisode`, table `quality.device_episodes`): nothing else in HC360 records lines, catheters or
ventilation, so ward and ICU nurses record each central line, urinary catheter and spell of ventilation as it goes in (by
the admission number; during the stay or the day before it, in casualty or theatre) and as it comes out, with why: planned
(a planned extubation), accidental (an unplanned extubation), replaced, the patient died, the patient left with it. A
second ventilator episode cannot open while one is. **Device days** (`DeviceDays`): a calendar day (IST) on which a patient
had the device in place for any part of it, the day of insertion being device day 1, counted once per patient per device
type (two central lines on a day are one central-line day). NHSN's manual count is a daily snapshot; this count from
episodes can be slightly higher for devices in and out on the same day. **Device eligibility**: the device in place for
more than two consecutive calendar days on the date of event, and in place on that day or the day before.

**Cases** (`HaiCase`, table `quality.hai_cases`, numbered `quality.hai`): *Suspected* → *Confirmed* (only when the checklist
meets the definition: 422 `hai.definition_not_met` with what is missing) or *RuledOut* (with a reason); either can be
reopened with a reason. The checklists (`HaiCriteria`, unit-tested):
* **CLABSI** (LCBI): a recognised pathogen from one or more blood specimens (LCBI 1), or the same common commensal from
  two or more blood specimens on separate occasions with fever > 38.0 °C, chills or hypotension (LCBI 2); not secondary to
  an infection at another site; central line eligible.
* **CAUTI** (SUTI): urine culture with no more than two species, one ≥ 10⁵ CFU/ml; fever, suprapubic tenderness or
  costovertebral angle pain, or urgency, frequency or dysuria only once the catheter is out; catheter eligible.
* **VAP** (PNU1): serial chest imaging with a new and persistent or progressive infiltrate, consolidation or cavitation; a
  systemic sign (fever, leukopenia < 4,000 or leukocytosis ≥ 12,000, altered mental status at 70 or over); two pulmonary
  signs (purulent sputum, cough or dyspnoea, rales, worsening gas exchange); ventilator eligible.
* **SSI**: exactly one depth (superficial incisional, deep incisional, organ/space); a finding for that depth (purulent
  drainage, an organism from an aseptic specimen, the incision opened with signs, an abscess, the surgeon's diagnosis for a
  superficial one); organ/space also a site-specific criterion; the date of event within 30 days of the operation (90 for
  deep or organ/space with an implant).
Not separate checklists: LCBI 3 (infants), asymptomatic bacteraemic UTI, PNU2/PNU3 and the ventilator-associated event
tiers (VAE), and each organ/space site's own criteria.

**Culture alerts.** `CultureAlertJob` (every 10 minutes, job nodes, advisory lock) looks at cultures verified in the last
three days that grew an organism: blood on a patient with a central line, urine with a urinary catheter, sputum on a
ventilator (in place on the collection day or removed the day before), and a swab, tissue or body fluid within 90 days of
an operation. Each raises a *Suspected* case (origin CultureAlert, linked to the episode or the operation, date of event the
collection day) and alerts the `quality.hai.manage` holders. A culture raises a case of a type once (unique index). The
laboratory raises no event when it verifies a result, so the alert comes within minutes, not at once.

| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/devices?inPlace&admissionId&device` | hai.devices or hai.manage | → `DeviceEpisodeDto[]` (UHID, admission, ward, device days) |
| POST | `/devices` | hai.devices or hai.manage | `InsertDeviceRequest {admissionId or admissionNumber, device, detail, insertedAt, notes}` → 201 |
| POST | `/devices/{id}/remove` | hai.devices or hai.manage | `RemoveDeviceRequest {removedAt, reason, version}`; 422 `hai.device_removed` the second time |
| GET | `/cases?status&type&from&to` | hai.manage | → `HaiCaseListItem[]` (UHID, whether the checklist meets the definition) |
| GET | `/cases/{id}` | hai.manage | → `HaiCaseDetail` (the checklist, device eligibility and device day, days after the operation, what is missing); audited as PHI access |
| POST | `/cases` | hai.manage | `RaiseHaiCaseRequest {type, patientId, deviceEpisodeId, eventOn, organism, notes}` → 201 (a case the team raises) |
| PUT | `/cases/{id}` | hai.manage | `ReviewHaiCaseRequest {criteriaMet[], eventOn, deviceEpisodeId, surgeryBookingId, surgeryReference, operatedOn, notes, version}` |
| POST | `/cases/{id}/confirm` · `/rule-out` · `/reopen` | hai.manage | `DecideHaiCaseRequest {note, version}` |
| GET | `/cases/{id}/surgeries` | hai.manage | the patient's operations in the 90 days before the date of event (SSI) |
| GET | `/rates?from&to` | hai.manage | → `HaiRateRow[]` per month (default last 12): patient days, device days, confirmed cases, rates per 1,000 device days (SSI per 100 operations), device utilisation ratios, cases still to review |

On **Quality → Infection surveillance** (`/quality/hai`): cases to review, the rates, the devices in place (nurses see only
these and record devices).

### Internal audits (`/api/v1/quality/audits`)
NABH internal audits by chapter (AAC, COP, MOM, PRE, HIC, PSQ, ROM, FMS, HRM, IMS). **Checklists** (`AuditTemplate`, group-
wide, items as `jsonb`): one starting checklist per chapter is seeded (`AuditTemplateSeeder`, order 121, idempotent by code),
cited by chapter only, for the hospital to adapt to its NABH self-assessment. **Audits** (`QualityAudit`, numbered
`quality.audit`, with `AuditFinding` rows copied from the checklist when scheduled): *Scheduled → InProgress → Completed →
Closed*, or *Cancelled*. The lead auditor (any member of staff) or the quality team records each item compliant, not
compliant (with what was found) or not applicable. Completion needs every item answered and a CAPA for every non-compliance
(422 `audit.incomplete` with what is left); completing a recurring audit (monthly, quarterly, six-monthly, yearly) schedules
the next one. A non-compliance's **CAPA** is raised by the quality team (`quality.capa.manage`) in the CAPA register (source
Audit) with an owner and a due date; the owner does it, and its effectiveness is verified by someone other than the owner
(`capa.self_verification`). The audit **closes** only when every CAPA it raised is verified or cancelled (422
`audit.capas_open`). An audit that raised CAPAs cannot be cancelled. Compliance = compliant ÷ items that apply.

| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET/POST/PUT | `/templates`, `/templates/{id}` | audits.manage | `SaveAuditTemplateRequest {code, title, chapter, description, items[{key, text, reference}], isActive, version}` |
| GET | `/?status&chapter` | audits.manage | → `AuditListItem[]` (overdue, compliance, gaps, open CAPAs) |
| GET | `/mine` | incidents.report | the audits the user leads |
| GET | `/{id}` | audits.manage, or the lead auditor | → `AuditDetail` (findings with their CAPAs, what stops completion) |
| POST | `/` | audits.manage | `ScheduleAuditRequest {templateId, scheduledOn, leadAuditorUserId, departmentId, area, recurrence}` → 201; the auditor is notified |
| POST | `/{id}/start` · `/findings` · `/complete` | the lead auditor or audits.manage | `RecordFindingRequest {itemKey, result, observation, version}`, `CompleteAuditRequest {summary, version}` |
| POST | `/{id}/findings/{findingId}/capa` | capa.manage | `RaiseFindingCapaRequest {type, action, ownerUserId, dueDate, version}` |
| POST | `/{id}/close` · `/cancel` | audits.manage | `AuditStepRequest {version}`, `CancelAuditRequest {reason, version}` |

On **Quality → Internal audits** (`/quality/audits`).

### Incidents raised by other modules
Other modules report patient-safety events they record through `IIncidentRegister.ReportAsync(ReportedSafetyEvent)`
(Platform contract), in their own unit of work, so the incident exists only if their record is saved:

| Source | Record | Incident type | Harm |
|---|---|---|---|
| SpecialtyCare (blood bank) | a transfusion reaction reported | TransfusionReaction | mild, moderate, severe (life-threatening too), death |
| Pharmacy (PvPI) | a new ADR report (unless it names an incident already) | AdverseDrugReaction | death; severe for life-threatening, disability or a congenital anomaly; moderate for other seriousness; mild |
| Inpatient (MAR) | a dose recorded as missed (an error of omission) | MedicationError | no harm |
| Inpatient (nursing chart) | a fall | PatientFall | by the injury (none → no harm … death) |
| Inpatient (nursing chart) | a pressure injury not present on admission | PressureInjury | stage 1–2 mild, 3 moderate, 4 / unstageable / deep tissue severe |

The incident names its source (`sourceModule`, `sourceRecordType`, `sourceRecordId`, `sourceReference`); one record raises
one incident (unique index), and reporting it again answers the incident it raised. The reporter is the member of staff
who saved the record. It is sentinel only when its harm makes it so (severe or death), and escalated as any report. A MAR
refusal and a dose held are not reported: a refusal is the patient's right and a hold a clinical decision, not errors.
Wrong-patient scans are not recorded anywhere in HC360, so they raise nothing.


### Dashboard
`GET /api/v1/quality/dashboard?from&to` (any quality management permission; default is the last 12 months) returns
`QualityDashboard`: incident totals, sentinel count, open incidents by status, incidents by type, severity and IST
month, CAPA summary (open, in progress, awaiting verification, overdue, verified in the period), the 10 most overdue
CAPAs, the 10 highest risks by residual then inherent score, the count of risks due for review, and indicators whose
latest value misses the target.

## Domain events
| Event | Raised when |
|---|---|
| `IncidentReported(IncidentId, IncidentNumber, FacilityId, Type, Severity, IsSentinel)` | a report is saved. It deliberately carries no reporter identity |

No integration events are handled.

## Notifications (in-app)
Sentinel event (critical) and moderate-harm incident (warning) go to `quality.incidents.manage` holders. A possible HAI
from a positive culture (warning) goes to `quality.hai.manage` holders; an audit scheduled to its lead auditor. Other
notices: incident assigned (to the assignee), CAPA assigned or reassigned (to the owner), CAPA completed (to
`quality.capa.manage` holders), CAPA not effective (to the owner). No SMS templates.

## Number series
| Key | Format | Reset |
|---|---|---|
| `quality.incident` | `{FAC}IR{YY}{SEQ:5}` (e.g. `NHIR2600017`) | yearly |
| `quality.capa` | `{FAC}CAPA{YY}{SEQ:4}` | yearly |
| `quality.risk` | `{FAC}RSK{SEQ:4}` | never |
| `quality.downtime` | `{FAC}DT{YY}{SEQ:4}` | yearly |
| `quality.mvpi` | `{FAC}MDAE{YY}{SEQ:5}` (device adverse event report to MvPI) | yearly |
| `quality.hai` | `{FAC}HAI{YY}{SEQ:4}` (infection surveillance case) | yearly |
| `quality.audit` | `{FAC}AUD{YY}{SEQ:4}` (internal audit) | yearly |

## Seed data (`QualitySeeder`, order 120, idempotent by code)
Twenty-two monthly NABH indicators:
* Patient care: OPD and IPD initial assessment time, OPD waiting time, lab reports within TAT.
* Medication safety: medication error rate, ADR rate.
* Infection control: hand hygiene compliance, needle-stick injury rate, SSI, CLABSI, VAP, CAUTI.
* Patient safety: patient fall rate, pressure ulcer incidence, sentinel events analysed within 30 days.
* Surgical care: unplanned return to OT within 48 h, re-intubation within 48 h, transfusion reaction rate.
* Outcomes and operations: mortality rate, discharge TAT, bed occupancy.
* Patient experience: patient satisfaction index.

Of these, all but hand hygiene, needle-stick injuries, the medication error rate per chart and patient satisfaction are
computed (above), and seven more computed ones are added: ER initial assessment time, laboratory and imaging waiting time,
medication errors per 1,000 patient days, return to ICU within 48 h, LAMA/DAMA rate and ALOS. Targets are starting points
for the quality committee to tune.
