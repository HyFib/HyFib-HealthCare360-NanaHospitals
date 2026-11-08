# Quality module (IRMS: incidents, RCA, CAPA, risk register, NABH indicators)

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Quality` · schema `quality` · depends on Platform and Patients
(an MvPI report names its patient by initials, age and sex from the registration). Staff are referenced as Platform user
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
Sentinel event (critical) and moderate-harm incident (warning) go to `quality.incidents.manage` holders. Other
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

## Seed data (`QualitySeeder`, order 120, idempotent by code)
Twenty-two monthly NABH indicators:
* Patient care: OPD and IPD initial assessment time, OPD waiting time, lab reports within TAT.
* Medication safety: medication error rate, ADR rate.
* Infection control: hand hygiene compliance, needle-stick injury rate, SSI, CLABSI, VAP, CAUTI.
* Patient safety: patient fall rate, pressure ulcer incidence, sentinel events analysed within 30 days.
* Surgical care: unplanned return to OT within 48 h, re-intubation within 48 h, transfusion reaction rate.
* Outcomes and operations: mortality rate, discharge TAT, bed occupancy.
* Patient experience: patient satisfaction index.

Targets are starting points for the quality committee to tune.
