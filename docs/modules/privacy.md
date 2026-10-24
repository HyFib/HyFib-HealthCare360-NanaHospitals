# Privacy module (DPDP Act 2023: requests, nominees, breaches, retention)

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Privacy` · schema `privacy` · layer 8 (references Patients,
Clinical, Engagement and Billing). Covers SRS **PRV-01..04** for India's Digital Personal Data Protection Act 2023 and
the DPDP Rules 2025, whose duties for hospitals come into force in May 2027:

| Duty | Law | Where |
|---|---|---|
| Access: a summary of the data held, its purposes, and who received it | Act s11 | data summary (portal and staff) |
| Correction, completion and updating | Act s12(1), (2) | correction requests |
| Erasure, except what the law requires the hospital to keep | Act s12(3), s8(7) | erasure requests, retention schedule |
| Grievance redressal, answered within a published period (at most 90 days) | Act s8(10), s13; Rules r14 | grievance requests |
| Nominating someone to act after death or incapacity | Act s14 | nominees |
| Publishing whom to contact, and naming them in every reply | Act s8(9); Rules r9 | privacy settings, messages |
| Breach intimation: the Board without delay and a detailed report within 72 hours; each person affected | Act s8(6); Rules r7 | breach register |
| A cyber security incident of a kind CERT-In lists reported to CERT-In within 6 hours of being noticed | CERT-In Directions under IT Act s70B(6), 28 April 2022, (ii) and Annexure I | breach register (`certInType`) |
| Logs of processing kept at least one year | Rules r6, r8(3) | retention schedule |

## Permissions

| Permission | Default roles | Purpose |
|---|---|---|
| `privacy.requests.read` | HospitalAdmin, QualityManager, MrdOfficer | View requests, the data summary and the erasure assessment |
| `privacy.requests.manage` | HospitalAdmin, QualityManager, MrdOfficer | Start, verify the requester, assign, note, decide corrections, erase, answer |
| `privacy.requests.log` | FrontOffice, HospitalAdmin, QualityManager, MrdOfficer | Record a request at the desk (or from e-mail, letter or phone); record a nominee |
| `privacy.breaches.manage` | HospitalAdmin, QualityManager | The breach register |
| `privacy.retention.manage` | HospitalAdmin | Retention periods, the clean-up and the review list |
| `privacy.settings.manage` | HospitalAdmin | The grievance officer's contact and the response period |

Patients use the portal with `portal.access`, limited to the patients registered with their mobile number
(`PortalPatientScope`). Opening a request, a data summary or an erasure assessment is patient-record access: it is
audited, and a restricted (VIP) record needs `patients.restricted.read` or break-the-glass.

Two things the default roles leave to the hospital:
- **Restricted (VIP) patients.** Only a HospitalAdmin can open their requests by default: QualityManager and MrdOfficer
  have neither `patients.restricted.read` nor break-the-glass. Give the grievance officer's role
  `patients.restricted.read` if they are to handle them.
- **Correcting registration details** needs `patients.register`, which the front desk and HospitalAdmin have and
  QualityManager and MrdOfficer do not. The officer decides the correction; the desk or an administrator makes it in
  the patient record.

## Requests (`/api/v1/privacy`)

Types: `Access`, `Correction`, `Erasure`, `Grievance`. Statuses: `Submitted` → `InProgress` → `Completed`,
`PartlyCompleted` or `Declined`; `Withdrawn` by the requester. Channels: `Portal`, `FrontDesk`, `Email`, `Letter`,
`Phone`. Requester: `Self`, `ParentOrGuardian`, `Nominee`, `FamilyMember`, `AuthorisedRepresentative`. Identity check:
`PortalSignIn` (set by the portal only), `PhotoId`, `KnownToStaff`, `Other`.

| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/overview` | requests.read | → `PrivacyOverview {open, overdue, dueThisWeek, awaitingIdentityCheck, openBreaches, breachReportsOverdue}` |
| GET | `/requests` | requests.read | `page, pageSize, search` (request number), `status, type, patientId, assignedToUserId, mine, openOnly, overdueOnly` → `PagedResult<DataRequestListItem {id, requestNumber, type, status, channel, patientId, uhid, principalName, requestedBy, receivedAt, dueAt, isOverdue, identityVerified, assignedToUserId, assignedToName, closedAt}>`, open first by deadline |
| GET | `/officers` | requests.manage | → `PrivacyOfficer[] {id, displayName}`: active staff holding `privacy.requests.manage` in the current facility, to assign requests to |
| GET | `/requests/{id}` | requests.read | → `DataRequestDetail {…list fields, contactPhone, contactEmail, requesterName, details, corrections[{index, field, fieldLabel, currentValue, requestedValue, decision, note}], identityCheck, identityNote, identityVerifiedAt, identityVerifiedBy, response, closedAt, closedBy, erasedItems[], retainedUntil, retainedPermanently, hasSummary, events[{at, kind, text, by, isPublic}], version}` |
| POST | `/requests` | requests.log | `CreateDataRequestRequest {type, channel (not Portal), patientId?, principalName?, contactPhone?, contactEmail?, requestedBy, requesterName?, details?, corrections?[{field, requestedValue}], identityCheck?, identityNote?}` → 201 `{id, requestNumber, dueAt}`. Without a patient: a name and a phone or e-mail to reply to |
| POST | `/requests/{id}/start` | requests.manage | Submitted → InProgress; the person starting it becomes the owner if it has none |
| POST | `/requests/{id}/verify` | requests.manage | `{check, note?}` (`note` required for `Other`) |
| POST | `/requests/{id}/assign` | requests.manage | `{userId?}` (null unassigns) |
| POST | `/requests/{id}/notes` | requests.manage | `{text}`: a staff note, never shown to the requester |
| POST | `/requests/{id}/corrections` | requests.manage | `{items[{index, decision: Accepted|Declined, note?}]}`; declining needs a note |
| GET | `/requests/{id}/summary` | requests.read | → `DataSummary` (below): the one given, for a completed access request; otherwise a fresh one |
| GET | `/requests/{id}/erasure` | requests.read | → `ErasureAssessment {options[{item, title, description, available, reason}], holds[{key, title, reason, until, permanently}], keepUntil, keepPermanently}` |
| POST | `/requests/{id}/erasure` | requests.manage | `{items[]}` from `messages`, `feedback`, `nominee`, `portal` → `ErasureOutcome {erased[], keepUntil, keepPermanently, summary}` |
| POST | `/requests/{id}/close` | requests.manage | `{outcome: Completed|PartlyCompleted|Declined, response}` |
| GET · PUT · DELETE | `/patients/{patientId}/nominee` | requests.log (GET also requests.read) | `SaveNomineeRequest {name, relationship, phone?, email?}` (a phone or an e-mail is required); GET → `{nominee: NomineeDto {name, relationship, phone, email, recordedVia, updatedAt} \| null}` |

**Rules.** The deadline is the published response period (default 30 days, at most 90). Personal data is given out,
changed or erased only after the requester's identity, and their authority to act for the patient, has been checked.
A portal request the patient makes for themselves counts as checked (they signed in with a code sent to the patient's
registered number); one made for a family member waits for staff. Declining needs no checked identity, and a grievance
can be answered without one. A correction is completed only when every requested change is accepted or declined.
Registration details are corrected by staff in the patient record, with its duplicate checks and audit. A health-record
correction is answered by the clinical team, who add a correction rather than rewrite the record. Completing an access
request keeps the data summary exactly as it was given. The portal allows at most 5 open requests per patient.

**Correctable details** (`field`): `name`, `dateOfBirth`, `gender`, `mobile`, `alternatePhone`, `email`, `address`,
`emergencyContact`, `guardian`, `language`, `healthRecord`, `other`.

**Erasure.** What can go at once:

| Item | Effect |
|---|---|
| `messages` | Withdraws marketing and optional update consent |
| `feedback` | Removes the patient, visit, contact details and comments from their feedback, complaints and enquiries. The ratings and complaints stay in the quality records. Their feedback invitations are deleted, so a link sent earlier cannot add a new response tied to them |
| `nominee` | Removes the nominee |
| `portal` | Closes the portal account, ends its sessions and ends its family links both ways (the relatives it saw, and the accounts that saw the patient); not offered when other patients sign in with the same number |

What is kept (`holds`):

| Hold | Kept |
|---|---|
| Medical records | Permanently if any visit or chart is medico-legal; otherwise until the latest of the last visit plus the out-patient period, the last discharge plus the in-patient period, and any chart's retention date |
| Bills | The last bill plus the finance period |
| The audit trail | The last access plus 10 years |
| The message log | The last message plus the log period |

The request records what was erased and until when the rest is kept. The retention review lists it until then.

### Data summary (`DataSummary`)

`{generatedAt, hospital, person, categories[], consents[], sharedWith[], processors[], nominee, contact, periods}`:

- **`person`**: the registration details as held: `uhid, fullName, gender, dateOfBirth, phone, alternatePhone, email,
  address, emergencyContact, abhaNumber` (masked), `abhaAddress, aadhaarLast4` (`XXXX-XXXX-1234`), `hasPhoto,
  registeredAt, registeredAtFacility`.
- **`categories[]`**: `{key, title, description, purpose, basis, retention, retentionKind, items[{key, label, count, first, last}]}`.
  - `retention` is the English sentence. `retentionKind` (`MedicalRecords, Finance, MessageLog, Audit, WhileInUse,
    WithRecords`) and `periods {outpatientYears, inpatientYears, financeYears, messageLogDays, auditYears}` let a client
    say it in another language.
  - An item's `key` is its table (`clinical.encounters`), stable for translating the label.
  - Only categories with records are included, except `registration`, which is always there.
  - Keys are stable, and the portal translates by key: `registration, visits, clinical, medicines, tests, stays,
    procedures, support, billing, virtual, communication, consents, documents, account, safety, ai, access-log,
    privacy`.
- **`consents[]`**: `{type, granted, capturedAt, revokedAt}`.
- **`sharedWith[]`**: `{name, role, data}`: insurers and TPAs from claims and pre-authorisations, and outside systems
  that read the record through the FHIR interface.
- **`processors[]`**: the SMS, WhatsApp, e-mail and payment providers the hospital has turned on, and the AI
  provider when AI is on. The training simulator is not a processor.

The summary is built from `PersonalDataCatalogue`:

- Every table with a patient column is catalogued; `PersonalDataCatalogueTests` fails for one that is not.
- Supporting tables are listed but not counted.
- A counted table must have an index on its patient column.
- Records deleted in the application are not counted; all facilities are.

## Portal (`/api/v1/portal/privacy`, `portal.access`)

| Method | Path | Body → Response |
|---|---|---|
| GET | `/status?patientId=` | → `PortalPrivacyStatus {hasNominee, nomineeName, openRequests, maxOpenRequests, notices}` for the front page. Counts only, so it is not audited as record access |
| GET | `/summary?patientId=` | → `DataSummary` |
| GET | `/requests?patientId=` (optional) | → `PortalDataRequestItem[] {id, requestNumber, patientId, type, status, receivedAt, dueAt, closedAt}` (newest first, at most 100) |
| GET | `/requests/{id}` | → `PortalDataRequestDetail {id, requestNumber, patientId, type, status, requestedBy, requesterName, identityConfirmed, details, corrections[{field, currentValue, requestedValue, decision, note}], receivedAt, dueAt, closedAt, response, erasedItems[], retainedUntil, retainedPermanently, timeline[{at, kind, text}], canWithdraw, contact}`. Only the public history is shown: received, identity confirmed, started, erased, answered, withdrawn. `identityConfirmed` false on a request made for someone else means staff still have to confirm the requester may act for them |
| POST | `/requests` | `PortalDataRequestCreate {patientId, type: Correction\|Erasure\|Grievance, requestedBy: Self\|ParentOrGuardian\|FamilyMember, requesterName?, details?, corrections?}` → 201 `{id, requestNumber, dueAt}`. A `Self` request is marked `PortalSignIn` only for a patient on the account's own number; for a family member seen through a family link (the code went to another number), staff confirm the identity |
| POST | `/requests/{id}/withdraw` | → 204 |
| GET · PUT · DELETE | `/nominee` | GET/DELETE `?patientId=`; PUT `PortalNomineeRequest {patientId, name, relationship, phone?, email?}`; GET → `{nominee}` |
| GET | `/notices?patientId=` (optional) · `/notices/{id}` | → `PortalBreachNotice {id, patientIds[], reference, title, notifiedAt, awareAt, occurredFrom, occurredTo, description, location, dataCategories[], consequences, mitigation, principalSteps, contact}`; `patientIds` are the people it concerns among those the user may act for |

Access needs no request: the summary is always there. A patient who wants a formal copy of their medical records uses
the existing record request (`/portal/records/request`).

## Public (`/api/v1/public/privacy`, anonymous, rate-limited)

→ `PublicPrivacyInfo {hospital, contact {title, name, email, phone, address}, responseDays}`: the grievance officer
and response period for the privacy notice. Until an officer is named, the first facility's e-mail, phone and address
stand in.

## Breach register (`/api/v1/privacy/breaches`, `privacy.breaches.manage`)

Statuses: `Open` → `BoardInformed` → `ReportSubmitted` → `Closed`. Nature: `Confidentiality, Integrity,
Availability`. Data categories: `Identity, Contact, Health, Financial, GovernmentId, Credentials, Children`.

| Method | Path | Body → Response |
|---|---|---|
| GET | `/` | `openOnly, search, page, pageSize` → `PagedResult<BreachListItem {id, breachNumber, title, status, awareAt, reportDueAt, reportOverdue, estimatedAffected, affectedListed, notified, boardInformedAt, reportSubmittedAt, certInType, certInReportDueAt, certInReportedAt, certInReportOverdue}>` |
| GET | `/{id}` | → `BreachDetail {…, description, occurredFrom, occurredTo, nature[], location, dataCategories[], consequences, mitigation, principalSteps, cause, findings, remediation, principalNotice, boardInformedAt, reportDueAt, extendedUntil, reportSubmittedAt, boardReference, closedAt, closingNote, notifiedAt, withoutContact, missingForReport[], missingForNotice[], version, certInType, certInReportDueAt (awareAt + 6 h), certInReportedAt, certInReference, certInReportOverdue}` |
| POST | `/` | `SaveBreachRequest {title, description, awareAt, occurredFrom?, occurredTo?, nature[], location?, dataCategories[], estimatedAffected?, consequences?, mitigation?, principalSteps?, cause?, findings?, remediation?, principalNotice? (≤ 320, the SMS), version?, certInType? (a CERT-In Annexure I kind, or none; kept as it was once CERT-In has been told)}` → 201 `{id, breachNumber}`; alerts every holder. `cause` is rule 7(2)(b)(i) (the events and reasons), `findings` rule 7(2)(b)(iii) (any findings about who caused it) |
| PUT | `/{id}` | same, with `version` |
| POST | `/{id}/board-informed` | `{at, reference?}`: the Board was told |
| POST | `/{id}/extension` | `{until, reference?}`: the Board allowed more time (after the 72 hours) |
| POST | `/{id}/report-submitted` | `{at, reference?}`: needs the Board told first, and `missingForReport` empty |
| POST | `/{id}/close` | `{note?}`: a note is required when the report was never sent |
| POST | `/{id}/cert-in-reported` | `CertInReportedRequest {at, reference?}`: CERT-In was told (after `awareAt`, not in the future); 422 `privacy.breach_not_cert_in` without a `certInType`, `privacy.breach_cert_in_reported` once recorded |
| GET | `/{id}/report` | → `BreachReport {hospital, legalName, address, registrationNumber, contact, breach, generatedAt}` for printing (audited) |
| GET | `/{id}/affected` | → `PagedResult<AffectedPatientItem {patientId, uhid, fullName, phone (masked), hasEmail, addedAt, notifiedAt, noticeChannels}>` |
| POST | `/{id}/affected` | `{patientIds?[], uhids?[]}` (up to 5,000; a merged record counts as the one it was merged into) → `{added, alreadyListed, notFound[]}` |
| DELETE | `/{id}/affected/{patientId}` | only before the patient was notified |
| POST | `/{id}/notify` | needs `missingForNotice` empty and the breach not closed → `{queued, withoutContact, alreadyNotified}` |

**The 72-hour clock** starts at `awareAt`. `reportDueAt` = `awareAt` + 72 h, or the extension. `reportOverdue` is
true until the report is recorded.

**Notices.**

- Every listed patient not yet notified gets the SMS `privacy.breach.notice`: the short notice and a link to the full
  notice at `/portal/my-privacy/notices/{id}`. Patients with an e-mail address also get the full notice by e-mail.
- Patients with neither are counted in `withoutContact`, so the hospital can reach them another way. They are looked
  at again on the next run, and see the notice when they sign in.
- A breach whose notices went out appears in the affected patients' portal.

## Retention (`/api/v1/privacy/retention`, `privacy.retention.manage`)

| Key | Kept | Minimum | After the period |
|---|---|---|---|
| `messages.outbox` | 30 days | 7 days | Deleted: sent messages' full text, sign-in codes included (pending ones are never touched) |
| `messages.log` | 365 days | 365 | Deleted |
| `integrations.log` | 365 days | 365 | Deleted: the integration log and the payment gateway's webhook events (`billing.gateway_webhook_events`, ids only) |
| `signin.log` | 365 days | 365 | Deleted: code requests, ended sessions and their refresh tokens, expired refresh tokens, ended break-the-glass grants (the audit trail keeps each emergency access) |
| `records.outpatient` | 3 years | 3 | Reviewed by MRD (never deleted automatically) |
| `records.inpatient` | 5 years | 5 | Reviewed by MRD |
| `records.mlc` | permanently | — | Kept |
| `finance` | 8 years | 8 | Kept (used for erasure holds) |
| `audit` | 10 years | 10 | Kept (tamper-evident, never edited) |
| `backups` | from `HealthCare360:Backup:RetentionDays` | — | Deleted by the backup job |

| Method | Path | Body → Response |
|---|---|---|
| GET | `/` | → `RetentionRuleDto[] {key, title, description, basis, action: Delete\|Review\|Keep, unit: Days\|Years, value, defaultValue, minimumValue, maximumValue, editable, lastRemoved, lastRunAt}` |
| PUT | `/{key}` | `{value}` within the allowed range; changing a medical records period moves the retention date of every completed chart of that kind (Clinical handles `RecordRetentionChanged` in the same transaction) |
| GET | `/runs?take=` | → `RetentionRunDto[] {id, startedAt, finishedAt, trigger, startedByUserId, startedByName, totalRemoved, results[{key, removed, before}], error}` |
| GET | `/preview` | → what a clean-up would remove now, per key |
| POST | `/run` | → 202: queues a clean-up; the job runs it within minutes |
| GET | `/review` | → `RetentionReview {outpatientChartsDue, inpatientChartsDue, erasureHolds[{requestId, requestNumber, patientId, uhid, principalName, retainedUntil, retainedPermanently, isDue}]}` |

**The clean-up.**

- `RetentionJob` runs every night after 02:00 IST, and within minutes of a request, on nodes that run jobs.
- A PostgreSQL session lock keeps two nodes from running it at once.
- It deletes in batches of 1,000 with set-based statements.
- Each run is recorded, with an audit entry, because those statements bypass the per-row audit.
- Documents, deleted ones included, stay with the medical record: the file store keeps a deleted document's encrypted
  file for medico-legal reasons.

## Settings (`/api/v1/privacy/settings`, `privacy.settings.manage`)

GET → `PrivacySettingsView {settings {contact {title, name, email, phone, address}, responseDays}, published}`.
PUT `SavePrivacySettingsRequest {title?, name?, email?, phone?, address?, responseDays (1–90)}`. Stored as
`privacy.contact` and `privacy.responseDays`.

## Notifications

| Template | Channels | Values |
|---|---|---|
| `privacy.request.received` | SMS, e-mail | `reference, type, received, due, contact` |
| `privacy.request.closed` | SMS, e-mail | `reference, outcome, response, contact` |
| `privacy.breach.notice` | SMS, e-mail | `reference, summary, link, description, when, data, consequences, mitigation, steps, contact` |

- Seeded by `PrivacySeeder` (order 195) in English. SMS templates need their DLT template id before they can be sent.
- In-app alerts:
  - a new request, to `privacy.requests.manage` holders in its facility;
  - a new breach, to `privacy.breaches.manage` holders.
- Realtime events on the facility topic: `privacy.request.created`, `privacy.request.updated`.

## Number series

| Series | Format | Scope |
|---|---|---|
| `privacy.request` | `{FAC}DPR{YY}{SEQ:5}` | per facility, yearly |
| `privacy.breach` | `DPB{YY}{SEQ:4}` | group-wide, yearly |

## Changes in other modules

- **Platform:**
  - `RecordRetention` holds the medical records periods, in settings;
  - the `RecordRetentionChanged` event.
- **Clinical:**
  - completing a chart dates its retention with the periods in force;
  - `RecordRetentionChangedHandler` moves existing charts' dates when a period changes.
- **Engagement:** `PatientFeedbackEraser` anonymises the feedback, complaints and enquiries of a patient who asked for
  erasure.
- **Indexes by patient:** `billing.insurance_claims`, `engagement.campaign_recipients`, `ai.usage_records`.

## Not covered yet

- **Deleting clinical records after their retention period.** It needs the medical records committee's and a legal
  adviser's sign-off, and a way to handle free text that names the patient. Records past their period are listed for
  review instead.
- **Consent managers** (Rules r4), **Significant Data Fiduciary** duties (DPIA and audit), and **cross-border
  transfer** rules.
- **Erasure and the audit trail.** Erasure never touches the audit trail: it is append-only and hash-chained, and its
  change details keep the old values. Erased data also stays in backups until they expire (30 days by default).
