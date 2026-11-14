# Privacy module (DPDP Act 2023: requests, nominees, breaches, retention, consent managers, children, SDF duties)

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
| Consent through a consent manager registered with the Board, withdrawn as easily as given | Act s6(4), s6(7) to (9); Rules r4, First Schedule | consent managers and artefacts |
| A child's data: a parent's or lawful guardian's verifiable consent; no tracking or targeted advertising | Act s9; Rules r10 to r12, Fourth Schedule | guardian consent, campaigns |
| A Significant Data Fiduciary: a DPO based in India, impact assessments and a yearly data audit | Act s10; Rules r13 | Significant data fiduciary |
| Erasure that a restore from a backup does not undo; the backup period said plainly | Act s8(7), s12(3) | erasure ledger |

## Permissions

| Permission | Default roles | Purpose |
|---|---|---|
| `privacy.requests.read` | HospitalAdmin, QualityManager, MrdOfficer | View requests, the data summary and the erasure assessment |
| `privacy.requests.manage` | HospitalAdmin, QualityManager, MrdOfficer | Start, verify the requester, assign, note, decide corrections, erase, answer |
| `privacy.requests.log` | FrontOffice, HospitalAdmin, QualityManager, MrdOfficer | Record a request at the desk (or from e-mail, letter or phone); record a nominee |
| `privacy.breaches.manage` | HospitalAdmin, QualityManager | The breach register |
| `privacy.breaches.approve` | HospitalAdmin | Approve closing a breach, and taking patients off its notification list, asked for by someone else: the Data Protection Officer (grant it to the DPO's or privacy officer's role; MAKER-CHECKER.md, slice 14) |
| `privacy.retention.manage` | HospitalAdmin | Retention periods, the clean-up and the review list |
| `privacy.settings.manage` | HospitalAdmin | The grievance officer's contact and the response period |
| `privacy.sdf.manage` | HospitalAdmin, QualityManager | The Significant Data Fiduciary page: the DPO, impact assessments, data audits |
| `privacy.sdf.approve` | HospitalAdmin | Approve an impact assessment someone else sent (the DPO's role; never one's own) |
| `privacy.consent_managers.manage` | HospitalAdmin | Register the consent managers the hospital accepts consent from |

Patients use the portal with `portal.access`, limited to the patients registered with their mobile number
(`PortalPatientScope`). Opening a request, a data summary or an erasure assessment is patient-record access: it is
audited, and a restricted (VIP) record needs `patients.restricted.read` or break-the-glass.

Two things the default roles leave to the hospital:
- **Restricted (VIP) patients.** Only a HospitalAdmin can open their requests by default: QualityManager and MrdOfficer
  have neither `patients.restricted.read` nor break-the-glass. Give the grievance officer's role
  `patients.restricted.read` if they are to handle them. A restriction is added at once; lifting it, and taking a record
  out of a sensitive category, wait for a second person (maker-checker slice 7: someone else who opens restricted
  records; another doctor who manages the categories). A category's removal is asked about the record's categories, so
  neither the request nor its decision shows in the patient's history to staff who may not manage them.
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
registered number, that number is verified as theirs, and no other patient is registered with it: a number a family shares says only that someone in the family holds the phone, so a request from it waits for staff to confirm identity, as for a record that is not verified, which the portal does not show at all); one made for a family member waits for staff. Declining needs no checked identity, and a grievance
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
| The audit trail | Permanently while the hospital keeps it so; once super administrators set a period, until the last access plus that period (see the retention table). It is shown to the principal, but holds nothing else back: the request keeps the rest until the latest of the other holds, as the audit trail is never erased by a request anyway |
| The message log | The last message plus the log period |

The request records what was erased and until when the rest is kept. The retention review lists it until then.

### Data summary (`DataSummary`)

`{generatedAt, hospital, person, categories[], consents[], sharedWith[], processors[], nominee, contact, periods}`:

- **`person`**: the registration details as held: `uhid, fullName, gender, dateOfBirth, isDateOfBirthEstimated` (worked
  out from the age given at registration; the portal and the print say so), `phone, alternatePhone, email, address,
  emergencyContact, abhaNumber` (masked), `abhaAddress, aadhaarLast4` (`XXXX-XXXX-1234`), `hasPhoto, registeredAt,
  registeredAtFacility`.
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
| POST | `/requests` | `PortalDataRequestCreate {patientId, type: Correction\|Erasure\|Grievance, requestedBy: Self\|ParentOrGuardian\|FamilyMember, requesterName?, details?, corrections?}` → 201 `{id, requestNumber, dueAt}`. A `Self` request is marked `PortalSignIn` only for a patient on the account's own number whose number is verified and that no other active patient shares (`PortalPatientScope.SignInIdentifiesAsync`; integration tests in `PrivacyTests`); on a shared number, and for a family member seen through a family link (the code went to another number), staff confirm the identity |
| POST | `/requests/{id}/withdraw` | → 204 |
| GET · PUT · DELETE | `/nominee` | GET/DELETE `?patientId=`; PUT `PortalNomineeRequest {patientId, name, relationship, phone?, email?}`; GET → `{nominee}` |
| GET | `/notices?patientId=` (optional) · `/notices/{id}` | → `PortalBreachNotice {id, patientIds[], reference, title, notifiedAt, awareAt, occurredFrom, occurredTo, description, location, dataCategories[], consequences, mitigation, principalSteps, contact}`; `patientIds` are the people it concerns among those the user may act for |

Access needs no request: the summary is always there. A patient who wants a formal copy of their medical records uses
the existing record request (`/portal/records/request`).

## Public (`/api/v1/public/privacy`, anonymous, rate-limited)

→ `PublicPrivacyInfo {hospital, contact {title, name, email, phone, address}, responseDays, accessLogYears, dpo,
backupWindowDays}`: the grievance officer and response period for the privacy notice, how long the record access log is
kept (null while permanently), the Data Protection Officer once named, and the longest erased data stays in backups. Until an officer is named, the first facility's e-mail, phone and address stand in.

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
| POST | `/{id}/close` | `{note?}` → 202 with the approval (`privacy.breach_close`); 422 `privacy.breach_board_report_required` until the detailed report is recorded with the Board's reference. The Data Protection Officer closes it, never the one who asked; `BreachDetail.closureAskedAt` while it waits |
| POST | `/{id}/cert-in-reported` | `CertInReportedRequest {at, reference?}`: CERT-In was told (after `awareAt`, not in the future); 422 `privacy.breach_not_cert_in` without a `certInType`, `privacy.breach_cert_in_reported` once recorded |
| GET | `/{id}/report` | → `BreachReport {hospital, legalName, address, registrationNumber, contact, breach, generatedAt}` for printing (audited) |
| GET | `/{id}/affected` | → `PagedResult<AffectedPatientItem {patientId, uhid, fullName, phone (masked), hasEmail, addedAt, notifiedAt, noticeChannels}>` |
| POST | `/{id}/affected` | `{patientIds?[], uhids?[]}` (up to 5,000; a merged record counts as the one it was merged into) → `{added, alreadyListed, notFound[]}` |
| POST | `/{id}/affected/remove` | `RemoveAffectedRequest {patientIds[≤500], reason}` → 202 with the approval (`privacy.breach_affected_remove`); only patients not yet sent the notice (`privacy.breach_already_notified`). They stay listed until the Data Protection Officer approves; audited with each UHID and the reason. `BreachDetail.removalAskedAt` while it waits |
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
| `messages.log` | 366 days | 366 | Deleted |
| `integrations.log` | 366 days | 366 | Deleted: the integration log and the payment gateway's webhook events (`billing.gateway_webhook_events`, ids only) |
| `signin.log` | 366 days | 366 | Deleted: code requests, ended sessions and their refresh tokens, expired refresh tokens, ended break-the-glass grants (the audit trail keeps each emergency access) |
| `records.outpatient` | 3 years | 3 | Reviewed by MRD (never deleted automatically) |
| `records.inpatient` | 5 years | 5 | Reviewed by MRD |
| `records.mlc` | permanently | — | Kept |
| `finance` | 8 years | 8 | Kept (used for erasure holds) |
| `audit` | permanently, or the years super administrators set | 8 years once set | Kept (tamper-evident, never edited). Not editable here: holders of `admin.system.manage` set it, one asking and another approving (`/api/v1/admin/audit/retention`, ADR-015); months older than the period go only by a purge another approves, exported first |
| `backups` | from `HealthCare360:Backup:Retention` (342 days by default) | — | Deleted by the backup job and its nightly upkeep |

The logs are kept 366 days, not 365: a year has at most 366 days, so every line is kept the full year the DPDP Rules 2025
ask for (rules 6 and 8), across a 29 February too. A period changed to less before is read as 366.

| Method | Path | Body → Response |
|---|---|---|
| GET | `/` | → `RetentionRuleDto[] {key, title, description, basis, action: Delete\|Review\|Keep, unit: Days\|Years, value, defaultValue, minimumValue, maximumValue, editable, systemManaged, lastRemoved, lastRunAt}`; `systemManaged`: set only by super administrators, two people agreeing (the audit trail) |
| PUT | `/{key}` | `{value}` within the allowed range; changing a medical records period moves the retention date of every completed chart of that kind (Clinical handles `RecordRetentionChanged` in the same transaction). `audit` is refused (422 `privacy.retention_fixed`), and so is its setting in the settings API |
| GET | `/runs?take=` | → `RetentionRunDto[] {id, startedAt, finishedAt, trigger, startedByUserId, startedByName, totalRemoved, results[{key, removed, before}], error}` |
| GET | `/preview` | → what a clean-up would remove now, per key |
| POST | `/run` | → 202: queues a clean-up; the job runs it within minutes |
| GET | `/review` | → `RetentionReview {outpatientChartsDue, inpatientChartsDue, erasureHolds[{requestId, requestNumber, patientId, uhid, principalName, retainedUntil, retainedPermanently, isDue}]}` |

**The clean-up.**

- `RetentionJob` runs every night after 02:00 IST, and within minutes of a request, on nodes that run jobs.
- A PostgreSQL session lock keeps two nodes from running it at once.
- It deletes in batches of 1,000 with set-based statements.
- Those statements bypass the per-row audit, so each batch commits with an audit entry of its own
  (`privacy.retention.purged`: the rule, the cut-off and the ids of the records removed, never what they held), and
  each run is recorded with one more (`privacy.retention.run`). A run stopped part-way, by a failure or a server shutting
  down, leaves every batch it removed in the trail; a batch that failed is rolled back with its entry.
- Documents, deleted ones included, stay with the medical record: the file store keeps a deleted document's encrypted
  file for medico-legal reasons.

## Settings (`/api/v1/privacy/settings`, `privacy.settings.manage`)

GET → `PrivacySettingsView {settings {contact {title, name, email, phone, address}, responseDays}, published}`.
PUT `SavePrivacySettingsRequest {title?, name?, email?, phone?, address?, responseDays (1–90)}`. Stored as
`privacy.contact` and `privacy.responseDays`.

`/cert-in`: the point of contact the hospital designates to CERT-In (Directions of 28 April 2022, direction (iii)), who
CERT-In writes to and who reports an incident within the 6 hours. GET and PUT `CertInContact {name, designation?,
email, phone, address?, version?}`; name, e-mail and phone are required. Stored as `privacy.certInContact`. Until it is
complete, System health shows the warning `privacy.cert_in_contact_missing`. The page is Privacy › Settings.

### Privacy notice at registration

The registration form records that the desk gave the patient the privacy notice (DPDP Act s.5; Rules r3) as a
`PrivacyNotice` consent with the notice's version, the setting `privacy.noticeVersion` (default `1`, at most 16
characters). Change the setting when the notice changes: notices given from then on carry the new version. A consent
captured on the consents screen without a version also takes it. The notice is a standing consent, like data sharing:
withdrawing it ends the earlier ones.

## Notifications

| Template | Channels | Values |
|---|---|---|
| `privacy.request.received` | SMS, e-mail | `reference, type, received, due, contact` |
| `privacy.request.closed` | SMS, e-mail | `reference, outcome, response, contact` |
| `privacy.breach.notice` | SMS, e-mail | `reference, summary, link, description, when, data, consequences, mitigation, steps, contact` |
| `privacy.guardian.code` | SMS | `code, minutes`: the code that verifies a guardian's consent for a child |

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
| `privacy.dpia` | `DPIA{YY}{SEQ:3}` | group-wide, yearly |

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
- **Patients:** `ChildData` and `IGuardianConsents` (implemented here): the consents screen refuses marketing for a child
  and, without a guardian's verified consent, sharing, research and photography.
- **Engagement:** campaign audiences are adults only; the portal refuses marketing for a child.

## Consent managers (Act s6(7) to (9); Rules r4)

A consent manager is a platform registered with the Data Protection Board through which a patient gives, reviews and
withdraws consent. The hospital registers each one it accepts (Privacy › Consent managers): a short code, its name and
Board registration number, the address withdrawals go to, and a signing secret HC360 makes and shows once, to give to
the consent manager. The secret is kept encrypted with the data key.

**The protocol.** The Board has not published a wire format yet, so HC360 speaks a small one of its own
(`ConsentManagerProtocol`), and an adapter translates once the Board's is out:

- The consent manager POSTs events to `/api/v1/consent-managers/{code}/events` (anonymous, rate-limited as a partner),
  on the patient portal's public address (it answers on the staff app's too; DEPLOYMENT.md, "Network checklist"), with `X-CM-Timestamp` (Unix seconds) and `X-CM-Signature`, the hex HMAC-SHA256 of `{timestamp}.{body}` under the
  shared secret. A bad signature, or a timestamp more than five minutes from the server's clock, is 401. Bodies are at
  most 32 KB. Each `eventId` is acted on once (a repeat answers `duplicate`).
- `consent.granted`: `{eventId, kind, artefact {id, principal {uhid, mobile}, purpose, dataItems[], validFrom,
  validUntil, givenByGuardian}}`. The UHID and the mobile number must both match an active patient. The purpose is one
  that needs consent and is not health care: `DataSharing`, `Research`, `SmsWhatsAppUpdates`, `MarketingCommunication`,
  `Photography`.
- `consent.withdrawn`: `{eventId, kind, artefact {id}}`.
- A withdrawal made at the hospital goes back as `POST {baseUrl}/artefacts/{id}/withdrawal`, signed the same way, and is
  sent again every quarter of an hour until the consent manager answers 2xx (at most 48 tries).

**Honouring an artefact.** An accepted artefact is recorded as the patient's consent of its purpose (`patient_consents`,
version `cm:{code}`, with the artefact, the consent manager and the data items in its notes; a sharing consent ends on
the artefact's last day). Every existing check that reads consents therefore honours it: campaigns, update messages,
record sharing, research. It ends:

- at the consent manager (`consent.withdrawn`), when the patient's consent is revoked here;
- in the portal or at the desk (`POST /api/v1/portal/privacy/consent-artefacts/{id}/withdraw`,
  `POST /api/v1/privacy/consent-artefacts/{id}/withdraw`), when the consent is revoked at once and the consent manager is told;
- when its validity ends, or the consent it made is withdrawn on the consents screen, at the next quarter-hourly round
  (`ConsentManagerService.SweepAsync`); the consent manager is told of a withdrawal made here.

| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/api/v1/privacy/consent-managers` | consent_managers.manage | → `ConsentManagerDto[] {id, code, name, registrationNumber, baseUrl, isSimulator, isActive, activeArtefacts, version}` |
| POST | `/api/v1/privacy/consent-managers` | consent_managers.manage | `SaveConsentManagerRequest {code, name, registrationNumber, baseUrl?, isSimulator}` → 201 `{id, code, secret}` (the secret is shown once) |
| POST | `/api/v1/privacy/consent-managers/{id}/deactivate` | consent_managers.manage | → 204: its events are refused from then on; its artefacts stay and can still be withdrawn |
| POST | `/api/v1/privacy/consent-managers/{id}/simulate` | consent_managers.manage | `SimulateConsentEventRequest {kind, artefactId, uhid?, mobile?, purpose?, dataItems?, validFrom?, validUntil?, givenByGuardian}` → the answer, through the real signed path. Only for the built-in simulator, on a training or demonstration server (`SeedDemoData`) |
| GET | `/api/v1/privacy/patients/{patientId}/consent-artefacts` | requests.read or requests.log | → `ConsentArtefactDto[] {id, consentManagerId, consentManagerName, artefactId, patientId, purpose, dataItems, validFrom, validUntil, status, inForce, givenByGuardian, receivedAt, withdrawnAt, withdrawnVia, withdrawalPending}` |
| GET | `/api/v1/portal/privacy/consent-artefacts?patientId=` | portal.access | The same, for the patients the account may act for; the portal's *Consents* page |

## Children (Act s9; Rules r10 to r12)

A child is a patient under 18 (`ChildData`). An unknown date of birth is not taken to be a child's for the consent
checks, and is never taken to be an adult's for campaigns.

- **Health care needs nothing more.** Clinical establishments are exempt from s9(1) and s9(3) to the extent needed to
  provide health services and protect the child's health (Fourth Schedule, Part A, item 1). Treatment, reports,
  reminders and update messages, records, bills and the portal family view go on as before.
- **Anything else that needs consent waits for a guardian.** Recording `DataSharing`, `Research` or `Photography` as
  granted for a child, and accepting a consent manager's artefact for one, needs a parent's or lawful guardian's
  verified consent first (422 `consent.child_guardian_unverified`, `privacy.cm_child_guardian`). An artefact for a
  child must say `givenByGuardian`.
- **No marketing, tracking or targeted advertising, ever** (s9(3), not lifted by consent): marketing consent is refused
  for a child at the desk, in the portal and from a consent manager (422 `consent.child_marketing`), and campaign
  audiences contain only patients known to be 18 or older (`CampaignAudienceQuery.IsKnownAdult`). HC360 has no
  behavioural profiling of patients to switch off.

**The guardian's consent** (Privacy, on the patient; front desk and officers):

- Who: the guardian's name, relationship (`Parent`, `LawfulGuardian`) and mobile number, and that the member of staff
  checked they are an adult (rule 10(1)).
- Verified either by a 6-digit code sent by SMS to the guardian's own number (`privacy.guardian.code`; 10 minutes, 5
  tries, at most 3 codes a number in 15 minutes; only its SHA-256 salted with the consent's id is kept), or by the
  guardian's identity document seen at the desk (its type and only its last four characters, never the number).
- One verified consent per child; a newer one replaces it. Withdrawn as easily as given; what was recorded under it
  stays, and nothing new outside health care is recorded until a guardian consents again.

| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/api/v1/privacy/patients/{patientId}/guardian-consent` | requests.log or requests.read | → `GuardianConsentView {patientId, isChild, dateOfBirth, verified, consents[]}` |
| POST | `…/guardian-consent` | requests.log | `StartGuardianConsentRequest {guardianName, relationship, guardianPhone, guardianIsAdult, method: Otp\|Document, documentType?, documentLast4?}` → 201 `GuardianConsentDto`; 422 `privacy.not_a_child` for an adult |
| POST | `…/guardian-consent/{id}/verify` | requests.log | `{code}` → the consent, verified; a wrong code counts a try (422 `privacy.guardian_code_wrong`) |
| POST | `…/guardian-consent/{id}/revoke` | requests.log | `{reason}` → 204 |

## Significant Data Fiduciary (Act s10; Rules r13)

Privacy › Significant data fiduciary. Kept whether or not the Central Government has notified the hospital; the
designation (`privacy.sdf`: `designated`, `notifiedOn`) changes what System health insists on.

- **The Data Protection Officer**: name, designation, e-mail, phone, address, and that they are based in India
  (s10(2)(a)); required once designated. Once named, the DPO is on the public privacy notice
  (`PublicPrivacyInfo.dpo`), beside the grievance officer.
- **Impact assessments (DPIA)**, numbered `DPIA{YY}{SEQ:3}`: the processing activity, its description, the personal data,
  whose data, the purpose, the risks (each with likelihood and impact, low to high), the measures and their owners,
  and a review date after today and within twelve months. A draft is sent for approval (202, approval
  `privacy.dpia`), and someone else who holds `privacy.sdf.approve` approves it as it was sent: never whoever sent it
  (`approval.self`), and not once it changed (`approval.stale`) or its review date passed. Changing an approved DPIA
  makes it a draft again. A DPIA is retired when the processing stops.
- **Data audits**: the period, the independent data auditor, the scope, findings, significant observations and when
  they were reported to the Board (required once there are some), and when the next audit is due (twelve months
  after the period ends, unless set earlier).
- **Reminders**: once a day after 09:00 IST, holders of `privacy.sdf.manage` are told 30 and 7 days before a DPIA's
  review date and the next audit, and once when either is overdue; each reminder goes once.
- **System health**, while designated: `privacy.dpo_missing`, `privacy.dpia_overdue`, `privacy.data_audit_due`.

| Method | Path | Body → Response |
|---|---|---|
| GET | `/api/v1/privacy/sdf` | → `SdfOverview {settings, dpias, dpiasDueSoon, dpiasOverdue, nextAuditDue, auditOverdue}` |
| GET · PUT | `/api/v1/privacy/sdf/settings` | `SdfSettings {designated, notifiedOn?, dpo {name, designation, email, phone, address, basedInIndia}, version?}` |
| GET | `/api/v1/privacy/sdf/dpias?includeRetired=` | → `DpiaListItem[] {id, number, activity, status, highestRisk, reviewDue, reviewOverdue, daysToReview, approvedAt, approvedByName, awaitingApprovalSince}` |
| GET · POST · PUT | `/api/v1/privacy/sdf/dpias[/{id}]` | `SaveDpiaRequest {activity, description, dataCategories[], dataPrincipals, purpose, risks[{description, likelihood, impact}], mitigations[{description, owner}], reviewDue, version?}` → `DpiaDetail` |
| POST | `/api/v1/privacy/sdf/dpias/{id}/submit` | `{reason, version}` → 202 the approval request |
| POST | `/api/v1/privacy/sdf/dpias/{id}/retire` | `{reason, version}` → 204 |
| GET · POST · PUT | `/api/v1/privacy/sdf/audits[/{id}]` | `SaveDataAuditRequest {periodFrom, periodTo, auditor, scope, findings?, significantObservations?, completedOn?, reportedToBoardOn?, reportReference?, nextDue?, version?}` → `DataAuditDto` |

## Erasure and backups (Act s8(7), s12(3))

**Why not crypto-shredding.** Destroying a patient's own key would make their data unreadable in every backup at once,
but HC360 has no per-patient keys: the field keys are per purpose (ABHA and policy numbers, secrets, documents; HKDF
from the data key ring), most personal data is in plain columns protected by the database and the encrypted backup as
a whole, and every backup escrows every key so it restores on new hardware (SECURITY.md, Backups and keys). A
per-patient key that a backup also escrows would still open that backup. So erased data stays in backups until they
are deleted, and HC360 says so, and keeps a restore from bringing it back.

**How long.** On the Server Node and on the second copy, backups are kept grandfather-father-son
(`HealthCare360:Backup:Retention`, DEPLOYMENT.md section 8): every backup of the last 14 days, then the last of each
week for 8 weeks and of each month for 12 months, removed by the nightly backup and its upkeep. The oldest monthly
backup is therefore at most 342 days old (`RetentionDays`, from the counts: 11 months of 31 days and the day between),
and nothing older is kept but the newest full backup when backups have stopped. The last backup made before an erasure
is gone at most `RetentionDays + 1` days after it (`ErasureService.BackupWindowDays`, 343 by default). While the
off-site copy is on (DEPLOYMENT.md section 8), each backup is also kept in the bucket, locked for `retainDays` (35 by
default) and removed by the bucket's lifecycle the day after, so the backup period counted is the longer of the two
(`BackupRetention`, in Platform): 342 days by default, a window of 343. The erasure answer (`ErasureService.BackupSentence`), the public notice
(`PublicPrivacyInfo.backupWindowDays`) and the portal's privacy notice say so. It counts on the bucket's lifecycle
expiring each copy as its lock passes, as DEPLOYMENT.md sets it; a backup uploaded late, after the line was down, is
kept its full lock from its upload. Copies a hospital moves elsewhere by hand, and a bucket that locks its copies by its
own rules (`retainDays` 0), are outside this: keep those no longer than the notice says.

**The erasure ledger** (`ErasureLedger`): every erasure is appended, once its transaction commits, to
`privacy/erasure-ledger.log` beside the audit anchors (in the shared folder when there are several servers), and to a
copy beside the backups' second copy when one is set. One JSON object a line: the entry, the request, the patient, the
items and when, by id only, with no name or number. Each line is sealed with the audit seal key (HMAC-SHA256); a line
whose seal does not verify is reported and never applied. Should writing it fail, the erasure stands, the audit trail
records `privacy.erasure_ledger_failed`, and the next replay writes it.

**The replay** (`ErasureLedgerReplay`), as the database is brought up to date at start-up (a restore always ends with a
start; a seeder after all others), every hour on the nodes that run jobs, and on request. An entry is in the database
when its request records every item it erased, or its replay is recorded (`privacy.erasure_replays`). Otherwise its
items are applied again for the patient in one transaction with the replay row and a critical audit entry
(`privacy.erasure_reapplied`), and the grievance officers are told. A portal account other patients now sign in with is
held back, as it would be for a request. The other way round, an erasure the database has and the ledger does not is
written to the ledger, so the ledger also covers erasures made before it existed. A PostgreSQL lock keeps two nodes from
replaying at once. `ErasureRestoreTests` restores a real backup with `backup decrypt` and pg_restore, as
Restore-HC360.ps1 does, and proves the erasure comes back and is applied again, once.

| Method | Path | Permission | Response |
|---|---|---|---|
| GET | `/api/v1/privacy/erasure-ledger` | requests.read | → `ErasureLedgerStatus {path, mirrorPath, entries, rejected, backupRetentionDays, replays[]}` |
| POST | `/api/v1/privacy/erasure-ledger/replay` | requests.manage | → `ErasureReplayOutcome {entries, rejected, reapplied[], backfilled, mirrorCompleted}`; 409 while another server replays |

## Not covered yet

- **Deleting clinical records after their retention period.** It needs the medical records committee's and a legal
  adviser's sign-off, and a way to handle free text that names the patient. Records past their period are listed for
  review instead.
- **Cross-border transfer** rules (s16): no transfer outside India is made today.
- **The Board's own consent manager format**: HC360's protocol is its own until the Board publishes one.
- **Erasure and the audit trail.** Erasure never touches the audit trail: it is append-only and hash-chained, and its
  change details keep the old values. The hospital keeps it permanently unless two super administrators set a period of
  at least 8 years (ADR-015). The data summary says the period in force ("Permanently, and never edited", or "For 8
  years, and never edited", in English, Hindi and Tamil in the portal), and so does the erasure answer ("The record
  access log is kept permanently and never edited", or "until <date>"), apart from what the law keeps, since the law does
  not require it. So do, from the public notice's `accessLogYears` (`/api/v1/public/privacy`, null while permanent),
  the portal's erasure form (the access log is under the list of what the law keeps, as the hospital's own decision,
  and the confirmation names it apart), a completed request in the portal and in the staff app (said even when the law
  keeps nothing else), and the portal's public privacy notice (the access log, with times and network addresses, kept
  permanently or for the years set; other security records deleted after a while). A request's `retainedUntil` and
  `retainedPermanently` describe what the law keeps only. The printed privacy notice must say the same: when the hospital updates its text, it raises
  `privacy.noticeVersion` so patients are shown the new version. Erased data also stays in backups until they expire
  (at most 31 days by default, 37 with the off-site copy on; see Erasure and backups).
