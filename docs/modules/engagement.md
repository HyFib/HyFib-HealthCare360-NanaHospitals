# Engagement module (healthcare CRM, campaigns, feedback, portal preferences)

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Engagement` · schema `engagement` · depends on Platform and
Patients (layer 3). It covers these SRS requirements:
* **CRM-01..05**: leads, follow-up calls, consent-only SMS/WhatsApp campaigns, patient 360°, referring doctors.
* **FB-01..04**: feedback forms (QR, kiosk, portal, SMS link), low-score complaints, complaint handling, analytics.
* **PP-05/06**: portal feedback and communication preferences.

Patient consents are the Patients module's `PatientConsent` records, so each patient's consent history stays in
one place. Engagement never keeps its own copy.

## Permissions

| Permission | Default roles | Purpose |
|---|---|---|
| `crm.leads.manage` | CrmExecutive, HospitalAdmin, FrontOffice | Capture and work leads; convert them to patients and appointments |
| `crm.followups.manage` | CrmExecutive, HospitalAdmin | Follow-up call worklist, reminders, staff-captured feedback, feedback links |
| `crm.campaigns.manage` | CrmExecutive, HospitalAdmin | Draft campaigns, preview consented audiences, view delivery |
| `crm.campaigns.send` | CrmExecutive, HospitalAdmin | Schedule, hold and cancel sending. It is separate from drafting, so a hospital can make an approver different from the author |
| `crm.referrals.manage` | CrmExecutive, HospitalAdmin | Referring doctors and referral analytics |
| `crm.patient360.read` | CrmExecutive, HospitalAdmin | Patient 360° view |
| `feedback.read` | CrmExecutive, QualityManager, HospitalAdmin | Responses and analytics |
| `feedback.manage` | CrmExecutive, QualityManager, HospitalAdmin | Forms and QR codes, staff-captured feedback, feedback links |
| `feedback.complaints.manage` | CrmExecutive, QualityManager, HospitalAdmin | Complaint handling; receives new-complaint alerts |

Portal endpoints use the Patients module's `portal.access` and are always limited by `PortalPatientScope`. Public
endpoints are anonymous and use the Platform's `"public"` rate limit (60 requests a minute per client).

## Endpoints

All list endpoints accept `page, pageSize, search` and return `{ items, page, pageSize, totalCount }`. Enum columns
are stored as text, so lists never sort by an enum in SQL; priorities and statuses are ranked explicitly.

### Leads (`/api/v1/crm/leads`, `crm.leads.manage`)
| Method | Path | Body → Response |
|---|---|---|
| GET | `/` | filters `status, source, ownerUserId, mine, dueOnly, includeClosed` (default: open leads only). `search` matches the lead number, name or the last digits of the mobile number. Due call-backs come first, then the newest → `LeadListItem` (phone masked, `isFollowUpDue`) |
| GET | `/{id}` | → `LeadDetail` with activities (newest first) |
| POST | `/` | `SaveLeadRequest {name, phone, email, gender, ageYears, city, source, sourceDetail, campaignId, referringDoctorId, interestDepartmentId, interestPackage, notes, ownerUserId, nextFollowUpAt, confirmedNotDuplicate}` → 201 `LeadCreated {id, leadNumber, existingPatientId}`. Returns 409 `lead.duplicate` `{leadId}` when an open lead already has this mobile number. `existingPatientId` hints that the caller is already registered |
| PUT | `/{id}` | same body with `version` → 204 |
| POST | `/{id}/activities` | `AddLeadActivityRequest {type: Call\|Sms\|WhatsApp\|Email\|Visit\|Note, occurredAt, outcome, notes, nextFollowUpAt}` → 201. Any contact except a note moves a New lead to Contacted |
| POST | `/{id}/status` | `ChangeLeadStatusRequest {status, lostReason}` → 204. Lost needs a reason. A lost lead reopens only as New or Contacted. Converted is reached only through `/convert` |
| POST | `/{id}/convert` | `ConvertLeadRequest {patientId \| registerPatient {gender, dateOfBirth \| ageYears, confirmedNotDuplicate}, appointmentId}` → `LeadConversion {leadId, patientId, uhid, appointmentId}`. Registering a patient goes through `PatientRegistrationService`, which applies its duplicate check (409 with candidates). It also needs `patients.register`, so the CRM screen is never a side door around Patients |

### Follow-up calls (`/api/v1/crm/follow-ups`, `crm.followups.manage`)
| Method | Path | Body → Response |
|---|---|---|
| GET | `/` | filters `status, type, priority, assigneeUserId, mine, overdueOnly, includeClosed, dueFrom, dueTo`. `search` matches the UHID or the name. High priority first, then due date → `FollowUpListItem` (phone masked) |
| GET | `/{id}` | → `FollowUpDetail` with the full number to call. Applies the restricted-record (VIP) guard and writes a patient-record-access audit |
| POST | `/` | `CreateFollowUpRequest {patientId, visitId, type, dueDate, assigneeUserId, priority, notes}` → 201. The due date cannot be in the past, and the visit must belong to the patient |
| PUT | `/{id}` | `UpdateFollowUpRequest {dueDate, assigneeUserId, priority, notes, version}` → 204 |
| POST | `/{id}/attempts` | `FollowUpAttemptRequest {outcome, notes, nextDueDate}` → 204 |
| POST | `/{id}/cancel` | `{reason}` → 204 |
| POST | `/{id}/remind` | sends the `followup.reminder` SMS. It needs an active SMS-updates consent and a mobile number |

Types: `PostConsultation, PostDischarge, Vaccination, ChronicReview, HealthCheckupDue, PaymentDue, Feedback`. The call
rules:
* Reached, AppointmentBooked, NotInterested, WrongNumber, Deceased and Other complete the task.
* NotReachable and CallBackLater keep it open. The next call defaults to tomorrow or the date the patient asked for.
* The **third** NotReachable closes the task, so dead numbers do not clog worklists.
* CallBackLater never uses up the attempts.

### Referring doctors (`/api/v1/crm`, `crm.referrals.manage`)
| Method | Path | Body → Response |
|---|---|---|
| GET | `/referring-doctors` | `activeOnly, from, to, search` → `ReferringDoctorListItem` with `referrals` and `lastReferralAt` in the period, most referrals first |
| GET | `/referring-doctors/{id}` | → `ReferringDoctorDetail` |
| GET | `/referring-doctors/{id}/stats` | `from, to` → `ReferralStats` (counts by IST month, department and visit type). Counts only, never the referred patients |
| GET | `/referrals/unmatched` | `from, to` → the 100 most frequent "referred by" texts that match no doctor, as candidates for a new doctor or an extra spelling |
| POST/PUT | `/referring-doctors[/{id}]` | `SaveReferringDoctorRequest {name, clinic, phone, email, specialty, city, registrationNumber, alternateNames[≤10], notes, isActive, version}`. Returns 409 when the registration number is already used |

Visits record the referrer as free text, so referral counts come from matching that text:
1. The distinct texts of the period (default: the last 12 months, at most 3 years) are grouped in SQL.
2. Each text is matched in memory by `ReferralAttribution`, using the doctor's name and alternate names:
   * Honorifics and punctuation are ignored.
   * A multi-word name may appear inside a longer text ("Dr. R. Kumar, Anna Nagar").
   * A single-word name must match the whole text.
3. Each text counts for **one** doctor: the most specific (longest) matching name wins, then the lowest id.

### Campaigns (`/api/v1/crm/campaigns`)
| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/` · `/{id}` | manage or send | filters `status, channel`, `search` by name → `CampaignListItem` / `CampaignDetail` |
| GET | `/{id}/delivery` | manage or send | → `CampaignDelivery {recipients, queued, sent, failed, skipped, optedOut}` from the notification logs |
| POST | `/audience-preview` | campaigns.manage | `SegmentInput {minAge, maxAge, gender, city, lastVisitFrom, lastVisitTo, departmentIds[≤50]}` → `AudiencePreview {count, sample[5] (UHID, masked name, age, city)}` |
| POST/PUT | `/[{id}]` | campaigns.manage | `SaveCampaignRequest {name, channel: Sms\|WhatsApp, templateKey, message (SMS ≤ 480 chars), segment, version}`. Only drafts can be edited |
| POST | `/{id}/schedule` | campaigns.send | `{scheduledAt}` → 204 |
| POST | `/{id}/unschedule` | campaigns.send | back to draft before sending starts |
| POST | `/{id}/cancel` | campaigns.send | `{reason}`. Messages already queued are still delivered |

**Consent gate (DPDP Act 2023).** `CampaignAudienceQuery` is always applied, and it is not part of the segment. A
patient is in the audience only when all of these hold:
* They hold a granted, unrevoked `MarketingCommunication` consent, and no later refusal supersedes it.
  `SmsWhatsAppUpdates` consent does **not** count.
* They are active, not deceased, not merged, and have a mobile number.
* They are registered at the facility or have visited it.

The segment filters are then applied:
* Ages are inclusive, and an unknown date of birth is excluded.
* City matching is case-insensitive.
* The "last visit" range uses the patient's most recent non-cancelled visit at this facility, on IST dates.
* Departments match any visit to them at this facility.

Consent is re-checked **at send time**, not only when the campaign is scheduled.

**Scheduling** needs all of the following:
* a future time;
* at least one consenting patient;
* an active English template of the campaign's key and channel, for this facility or for all facilities (messages
  use the facility's own template when it has one). Every version of that template must contain `{{optout}}`;
* the `portal.baseUrl` setting set to an https address.

**Sending.** `CampaignDispatcher` runs every minute on job nodes (`PlatformOptions.RunsJobs`):
* It sends only inside the TRAI promotional window, 10:00–21:00 IST.
* It works under a cluster-wide advisory lock.
* Each batch has up to 200 patients and runs in its own transaction, with at most 5 batches per campaign each minute.
* Every message goes through `INotificationService`, the outbox, so it survives internet outages.
* Every message carries a personal opt-out link. A `CampaignRecipient` row records the NotificationLog id and the
  SHA-256 hash of the opt-out token.
* A campaign completes when a batch finds nobody left.

### Patient 360° and opt-out (`/api/v1/crm/patients/{patientId}`)
| Method | Path | Permission | Response |
|---|---|---|---|
| GET | `/360` | crm.patient360.read | `Patient360View` (see below). Applies the restricted-record (VIP) guard, and each view writes a patient-record-access audit |
| POST | `/marketing-opt-out` | any of crm.campaigns.manage, crm.followups.manage, crm.leads.manage, crm.patient360.read, patients.consents | `{reason}` → 204. It revokes every active marketing grant and records the refusal, even for a patient who never opted in |

`Patient360View` contains:
* identity, with the phone masked;
* visits at this facility: total, last 12 months, first and last visit, last "referred by", visit types, top 5 departments;
* spend across the group: billed, paid, billed in the last 12 months, bill count, last bill;
* feedback: count, average rating, last NPS and sentiment, low scores;
* the last 5 complaints and the open follow-ups;
* communication: marketing and updates consent, when marketing consent last changed, campaigns received, opted out
  by link;
* the lead the patient converted from.

### Feedback forms (`/api/v1/feedback/forms`)
| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/` · `/{id}` | feedback.read or .manage | `activeOnly, type` → `FeedbackFormDto` including `publicToken` and `qrUrl` (`{portal.baseUrl}/feedback/form/{token}`) |
| POST/PUT | `/[{id}]` | feedback.manage | `SaveFeedbackFormRequest {code, title, type: OP\|IP\|Portal\|General, aspects[Doctor\|Nursing\|FrontDesk\|Billing\|Cleanliness\|Food\|Overall], includesNps, allowsComment, introText, thankYouText, departmentId, isActive, version}`. The code must be unique in the facility (409 otherwise), and a form must ask at least one question |
| POST | `/{id}/rotate-token` | feedback.manage | issues a new public token, which retires the old QR posters |

### Feedback responses, links and analytics (`/api/v1/feedback`)
| Method | Path | Permission | Body → Response |
|---|---|---|---|
| GET | `/responses` | feedback.read | filters `formId, departmentId, patientId, channel, sentiment, lowOnly, from, to` → `FeedbackResponseListItem` (average rating, sentiment, low flag, complaint number, `isAnonymous`; the comment is not included) |
| GET | `/responses/{id}` | feedback.read | → `FeedbackResponseDetail`. A linked patient's UHID and name are shown, and the view is audited |
| POST | `/responses` | feedback.manage or crm.followups.manage | `StaffFeedbackRequest {formId, patientId, visitId, ratings, nps, comment, wantsCallback, contactPhone}` → 201. The channel is Staff, for example during a follow-up call |
| POST | `/invitations` | feedback.manage or crm.followups.manage | `SendFeedbackLinkRequest {patientId, visitId, formId}` → 202 `{invitationId}`. Sends a personal link for one of the patient's visits here. The OP or IP form is chosen from the visit type |
| GET | `/analytics` | feedback.read | `from, to, formId, departmentId` → `FeedbackAnalytics` |

`FeedbackAnalytics` contains:
* responses and NPS;
* the average rating: the mean of each response's own average, so every response weighs the same;
* low scores;
* the average of each aspect;
* counts by sentiment and by channel;
* a monthly IST trend of responses, NPS, average and low scores;
* scores by department;
* complaints: raised, open, overdue, resolved, average turnaround hours, and the percentage resolved within the SLA.

### Complaints (`/api/v1/feedback/complaints`, `feedback.complaints.manage`)
| Method | Path | Body → Response |
|---|---|---|
| GET | `/` | filters `status, priority, source, departmentId, ownerUserId, mine, openOnly, overdueOnly, from, to`. `search` matches the complaint number. Open first, then high priority, then the nearest deadline → `ComplaintListItem` (`isOverdue`, `turnaroundHours`) |
| GET | `/{id}` | → `ComplaintDetail`. Audited when a patient is linked |
| POST | `/` | `CreateComplaintRequest {source: WalkIn\|Phone\|Email\|Portal, patientId, departmentId, category, description, contactName, contactPhone, priority, ownerUserId}` → 201. Feedback complaints are only raised automatically |
| PUT | `/{id}` | `UpdateComplaintRequest {ownerUserId, priority, departmentId, category, version}`. A new owner is notified in-app |
| POST | `/{id}/status` | `{status, note}`. Allowed moves: Open → InProgress; Open or InProgress → Resolved (a resolution note is required); Resolved → Closed; Resolved → InProgress (reopen, a reason is required, and the reopen is counted) |

The resolution SLA is **24 h for High and 72 h for Normal** priority, counted from when the complaint is raised.
Raising the priority tightens the deadline, and lowering it never extends a deadline already promised. Turnaround is
measured from raised to resolved.

### Public, anonymous (`/api/v1/public`, `"public"` rate limit)
| Method | Path | Behaviour |
|---|---|---|
| GET | `/feedback/forms/{token}` | QR or kiosk form → `PublicFeedbackForm {title, introText, aspects, includesNps, allowsComment, hospital}` |
| POST | `/feedback/forms/{token}?kiosk=` | `SubmitFeedbackRequest {ratings, nps, comment, wantsCallback, contactPhone}` → `FeedbackReceipt {thankYou, complaintNumber}`. Anonymous (channel QrCode or Kiosk); the complaint number is returned only when a call-back was requested |
| GET | `/feedback/invitations/{token}` | the form of a personal link. Returns 422 `feedback.link_used` or `feedback.link_expired` (14 days) |
| POST | `/feedback/invitations/{token}` | submits once, linked to the patient and the stay (channel SmsLink). The link is used up in the same transaction, and a unique index turns a double submit into a 409 |
| GET | `/unsubscribe/{token}` | → `{alreadyOptedOut}`. GET never changes anything, because SMS apps and link scanners pre-fetch links |
| POST | `/unsubscribe/{token}` | revokes marketing consent and marks the campaign message as opted out. Repeating it is harmless (204) |

What the public endpoints never do:
* They never return anything about a patient: no name, UHID or number.
* An unknown or malformed token gets one generic 404 ("This link is not valid."). Tokens are checked for shape
  (16–64 URL-safe characters) before any database lookup.

### Patient portal (`/api/v1/portal`, `portal.access`)
| Method | Path | Body → Response |
|---|---|---|
| GET | `/feedback/forms` | active Portal forms of every active facility (form, hospital name) |
| POST | `/feedback` | `PortalFeedbackRequest {patientId, formId (Portal or General), visitId, ratings, nps, comment, wantsCallback, contactPhone}` → `{responseId, thankYou, complaintNumber}`. The visit must be the patient's own visit at that hospital |
| GET | `/feedback?patientId=` | the patient's feedback with complaint number and status (latest 100) |
| GET | `/communication-preferences?patientId=` | → `CommunicationPreferences {marketing, updates, marketingChangedAt, updatesChangedAt}` |
| PUT | `/communication-preferences` | `{patientId, marketing, updates}` → 204. Only actual changes are recorded (given or withdrawn "via patient portal") |

Every patient id is checked with `PortalPatientScope`: the verified mobile number's family records only. Any other id
gets 403 `portal.patient_denied`.

## Feedback rules (unit-tested)
* **Low score** (FB-03): any aspect rated 2 or less, or an NPS of 6 or less. A low score automatically creates a
  complaint (source Feedback) in the same transaction and alerts `feedback.complaints.manage` holders. Priority is
  **High** for a 1-star rating or an NPS of 3 or less, otherwise Normal. The complaint is routed to the visit's
  department, else the form's department.
* **Call-back.** When the patient asked for a call-back, their number is stored on the response and the complaint, and
  the `complaint.acknowledged` SMS gives them the complaint number. Anonymous feedback stores nothing else about the
  person.
* **NPS** is % promoters (9–10) minus % detractors (0–6), to one decimal.
* **Sentiment** (`SentimentAnalyzer`) is rule-based and offline, so comments never leave the server:
  * An English and common Hinglish/Tamil word list scores ±2 per word.
  * A negation ("not", "nahi", "illa") flips the next sentiment word within three words.
  * The ratings add (average − 3) × 2, and the NPS adds +2 (9–10) or −2 (0–6).
  * A total of 2 or more is Positive, −2 or less is Negative, and anything between is Neutral.
* **Answers must fit the form.** Only the form's aspects can be rated, each 1–5. NPS and comment are accepted only
  when the form asks for them, and at least one question must be answered.

## Automation (domain event handlers, run inside the raiser's transaction)
| Event (raised by) | Handler effect |
|---|---|
| `PatientDischarged` (Inpatient) | See "After a discharge" below |
| `AppointmentBooked` (Scheduling) | Open leads with the patient's mobile number are marked converted, but only when the names match (`LeadMatching`: same first name, or the same initial plus a shared word), because families share numbers. Open PostConsultation, Vaccination, ChronicReview and HealthCheckupDue follow-ups complete as AppointmentBooked. This is not counted as a call attempt |
| `BillFinalized`, `BillCancelled`, `PaymentReceived`, `RefundPaid` (Billing) | Append-only `PatientSpendEntry` rows (with the facility), one per bill, cancellation, receipt or refund, idempotent, for the net 360° spend |
| `PatientsMerged` (Patients) | Follow-ups, feedback, invitations, complaints, spend, converted leads and campaign recipients are re-pointed to the surviving patient in all facilities. Where the survivor already received a campaign, the duplicate's row is kept |

**After a discharge.** The handler never fails the discharge for an Engagement reason. A missing form, consent,
mobile number or portal address only skips the survey, and the reason is logged. The handler does two things:
* **Follow-up call.** It adds a PostDischarge call due **2 days after the IST discharge date**:
  * The call is idempotent per admission (`discharge:{admissionId}`).
  * The priority is High for LAMA, DAMA and absconded patients.
  * No call is made for Expired discharges or deceased patients.
* **Feedback link.** It sends a personal IP feedback link by SMS (`feedback.request`). The link is single use, valid
  for 14 days, and at most one is sent per form and stay. It is sent only when all of these hold:
  * the patient has an active SMS-updates consent and a mobile number;
  * the discharge is not Expired or Absconded;
  * `portal.baseUrl` is set.

No events are raised by this module.

## Background job
`CampaignDispatcher`: see Campaigns above. It returns immediately on API-only nodes.

## Notifications
| Template key | Channel | When |
|---|---|---|
| `campaign.message` | SMS, WhatsApp | campaign messages: `{{message}}` plus the mandatory `{{optout}}` link. Seeded here |
| `complaint.acknowledged` | SMS | a low-score complaint when the patient asked for a call-back: `{{reference}}`. Seeded here |
| `feedback.request` | SMS | personal feedback link: `{{link}}`. Platform default |
| `followup.reminder` | SMS | follow-up due: `{{doctor}}, {{date}}, {{link}}`. Platform default |

In-app notifications: a new complaint goes to `feedback.complaints.manage` holders (a warning when the complaint is
High priority), and a complaint assignment goes to the new owner. Real-time: `feedback.complaint.created` to the
facility. Notifications carry the complaint number and area, never the patient's comment.

## Settings
`portal.baseUrl` (per facility or global; shared with Scheduling and Telehealth) is the public portal address used in
SMS links:
* A bare host name is read as https.
* Only an https URL without credentials is accepted.
* A malformed value reads as "not configured".

## Number series
| Key | Format | Reset |
|---|---|---|
| `crm.lead` | `{FAC}LD{YY}{SEQ:5}` | yearly |
| `feedback.complaint` | `{FAC}CMP{YY}{SEQ:5}` | yearly |

## Seed data (`EngagementSeeder`, order 130, idempotent, never re-creates deleted rows)
* Per active facility, four forms with NPS and comment:
  * `OPD`: out-patient; Doctor, Nursing, Front desk, Billing, Cleanliness, Overall.
  * `IPD`: in-patient; Doctor, Nursing, Food, Cleanliness, Billing, Overall.
  * `PORTAL`: Doctor, Front desk, Billing, Overall.
  * `QR-GEN`: general QR posters; Cleanliness, Front desk, Overall.
* All-facility English templates `campaign.message` (SMS and WhatsApp) and `complaint.acknowledged` (SMS).
* **Before go-live**, set each SMS template's DLT template id and each WhatsApp template name.

## Tables (`engagement` schema)
`leads`, `lead_activities`, `follow_up_tasks`, `referring_doctors`, `campaigns`, `campaign_recipients`,
`feedback_forms`, `feedback_responses`, `feedback_invitations`, `complaints`, `patient_spend_entries`.

Notable constraints:
* `follow_up_tasks`: unique (source, source_reference, type) for automatic tasks.
* `campaign_recipients`: unique (campaign, patient) and unique opt-out token hash.
* `feedback_forms`: unique public token.
* `feedback_invitations`: unique token hash.
* `feedback_responses`: unique `invitation_id`.
* `patient_spend_entries`: unique (kind, source id).

Append-only tables are `[NotAudited]`: `campaign_recipients`, `feedback_invitations`, `patient_spend_entries`. **EF
migrations are not included** and must be generated by the maintainers.

## Security and privacy
* Marketing is consent-only, enforced in one tested query and re-checked at send time. Withdrawing is one tap: the
  SMS opt-out link or the portal. Staff can also record an opt-out. Every change is a consent record with its source.
* Personal feedback and opt-out tokens are 128-bit random values and are stored only as SHA-256 hashes. QR tokens
  identify a form, never a person, and can be rotated.
* Lists show masked mobile numbers. Opening a follow-up, a linked feedback response, a linked complaint or the 360°
  view is audited as patient-record access, and follow-ups and the 360° view apply the VIP restricted-record guard.
* All data is facility-scoped except Patients' group-wide identity and the spend read model. Leads, campaigns,
  complaints and so on of another facility return 404.

## Known limitations (for maintainers)
* Spend is net: cancelled bills (`BillCancelled`) drop out and refunds paid out (`RefundPaid`) reduce the amount
  paid. Rows record the facility, but the 360° view shows the patient's spend across all facilities, like the
  patient's identity.
* Lead conversion with a new registration saves the patient, through the Patients service's own transaction, before
  the lead. If the second save fails, the patient exists and the conversion can be retried with `patientId`.
