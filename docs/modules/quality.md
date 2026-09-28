# Quality module (IRMS: incidents, RCA, CAPA, risk register, NABH indicators)

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Quality` · schema `quality` · depends on Platform only.
Patients and staff are referenced by id: `PatientId` from the Patients module, and staff as Platform user ids.
Covers SRS **IRMS-01..06**: incident reporting, severity grading, investigation/RCA, CAPA tracking, a risk register
with likelihood × impact scoring, and NABH quality indicators.

## Permissions

| Permission | Default roles | Purpose |
|---|---|---|
| `quality.incidents.report` | all staff roles | Report incidents (optionally anonymously), see own named reports, work on CAPAs one owns |
| `quality.incidents.manage` | QualityManager, HospitalAdmin | Review, grade, assign, investigate and close incidents; receives sentinel alerts |
| `quality.capa.manage` | QualityManager, HospitalAdmin | Plan CAPAs, verify effectiveness, cancel |
| `quality.risks.manage` | QualityManager, HospitalAdmin | Risk register |
| `quality.indicators.manage` | QualityManager, HospitalAdmin | Indicator definitions, values, trends, scorecard |

The dashboard needs any of the four management permissions.

## Endpoints

### Incidents (`/api/v1/quality/incidents`)
| Method | Path | Permission | Body → Response |
|---|---|---|---|
| POST | `/` | incidents.report | `ReportIncidentRequest {type, severity, occurredAt, location, departmentId, patientId, involvedStaffUserIds, description (≥ 20 chars), immediateAction, witnesses, sentinelKind, flagAsSentinel, isAnonymous}` → 201 `IncidentReceipt {id, incidentNumber, isSentinel, severity, type}` |
| GET | `/mine` | incidents.report | own **named** reports → `MyIncidentItem[]` |
| GET | `/` | incidents.manage | filters `status, type, severity, sentinelOnly, openOnly, departmentId, from, to`, `search`; sort `occurredAt, reportedAt, severity, number` → `IncidentListItem` (incl. open CAPA count). Severity sorts by harm (NearMiss → Death), not alphabetically: enums are stored as text |
| GET | `/{id}` | incidents.manage | → `IncidentDetail` (next allowed statuses, involved staff names, investigation status, CAPAs); writes a PHI-access audit entry when a patient is linked |
| PUT | `/{id}` | incidents.manage | `UpdateIncidentRequest {type, severity, sentinelKind, flagAsSentinel, location, departmentId, patientId, involvedStaffUserIds, version}` (re-grading; becoming sentinel alerts the team) |
| POST | `/{id}/transition` | incidents.manage | `TransitionIncidentRequest {status, note}` |
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
only after a completed RCA, except when a duplicate report is closed straight from Reported.

**Anonymous reporting.** With `isAnonymous` the reporter's id and name are not stored. The insert runs through
`AnonymousReportingScope`: a fresh DI scope on a flow without the HTTP context, so the platform's `CreatedBy` stamp
and data-audit event record no user, session, device or IP. The scope fails closed (422
`incident.anonymity_unavailable`) if it could still see the user. Later edits by the quality team are audited
normally. Alerts never name the reporter.

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
