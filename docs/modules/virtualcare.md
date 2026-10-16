# VirtualCare module

Telehealth video consultations (WebRTC signalling, waiting room, chat) and Remote Patient Monitoring.

| | |
|---|---|
| Project | `src/server/Modules/HyFib.HealthCare360.Modules.VirtualCare` (layer 5, references Patients and Scheduling) |
| Schema | `virtualcare` — `telehealth_sessions`, `telehealth_participants`, `telehealth_messages`, `rpm_programs`, `rpm_enrollments`, `rpm_devices`, `rpm_readings`, `rpm_thresholds`, `rpm_alerts` |
| Requirements | TEL-01..06, RPM-01..05 |
| Tests | `tests/Modules/HyFib.HealthCare360.Modules.VirtualCare.Tests` (join tokens, session state machine, thresholds, units, adherence, FHIR and device parsers, device keys, ICE settings, alerts, permissions, prescription notice channels); end to end, `TeleconsultationJourneyTests` in `tests/HyFib.HealthCare360.IntegrationTests` |

## Telehealth

* **TelehealthSession** – appointment (optional), patient, practitioner, department, visit, status
  `Scheduled | WaitingRoom | InProgress | Completed | Cancelled | Missed`, scheduled start/end, waiting since,
  started/ended, duration, link sent (count), recording consent (who/when), audio-only used, quality notes.
* **Join link** – `{portal.baseUrl}/portal/telehealth/{sessionId}?t={token}` (server-relative when `portal.baseUrl`
  is not set or is not https). The 256-bit token is returned once; only `SHA-256("{sessionId}:{token}")` is stored, compared in
  constant time, so a token opens exactly one session. It expires two hours after the scheduled end, when the session
  closes, or when a new link is issued. Patients can join from `telehealth.earlyJoinMinutes` (default 30) before.
* **Media never touches the server** (peer-to-peer WebRTC); the server keeps metadata only. `TelehealthParticipant`
  rows record who joined, was admitted and left, plus worst round-trip time, packet loss, lowest bitrate and audio-only
  fallback (TEL-05). `TelehealthMessage` stores the in-call chat.
* **Visit for EMR (TEL-04)** – `POST …/start` creates a `Telehealth` visit through `VisitService` (no queue token) and
  moves the appointment to `InConsultation`; Clinical writes notes/prescriptions against that visit. When a
  prescription is signed for a teleconsultation visit, the patient is told it is on the portal (where the hospital
  requires the doctor's DSC, once it is signed with it: Clinical ERX-04). The call screen's
  "Open chart" and "Notes and prescription" open in a new browser tab, or in HC360 Desktop in a second window, so the
  call keeps running while the doctor writes.
* Sessions nobody started become `Missed` one hour after their scheduled end (job).
* **Switched off** ([features.md](features.md), `features.telehealth`): every `/api/v1/telehealth` and
  `/api/v1/portal/telehealth` endpoint answers 503 `feature.disabled`, except `end` and `cancel`, so a call already
  running can be closed and a booked one called off. `JoinAsClinician`, `JoinAsPatient` and `JoinAsGuest` refuse to
  join while video is off for the session's facility, and nothing else on the hub works without joining. The hub has no
  switch of its own, because guests and patients connect without a facility. A call already running is not cut off.
  `JoinAsPatient` also needs the patient portal to be on. Remote patient monitoring is not affected.

### Signalling hub — `/hubs/telehealth`

The hub accepts anonymous connections (guests have no JWT) and authorises every method. SignalR sends the JWT as
`access_token` for staff and portal users.

| Method (client → server) | Who | Effect |
|---|---|---|
| `JoinAsClinician(sessionId)` → `TelehealthJoinResult` | staff with `telehealth.consult` at the session's facility | joins the room; sees who is waiting; PHI access audited |
| `JoinAsPatient(sessionId)` → `TelehealthJoinResult` | portal user with `portal.access` whose `PortalPatientScope` covers the patient | waiting room (re-admitted automatically after a reconnect) |
| `JoinAsGuest(sessionId, token)` → `TelehealthJoinResult` | holder of the SMS/WhatsApp link | waiting room; sees first names only; 5 failed tries abort the connection |
| `Admit(sessionId, participantId?)` | clinician | admits one/all waiting; starts the clock |
| `SendOffer` / `SendAnswer(sessionId, sdp)`, `SendIceCandidate(sessionId, candidate)` | admitted participants | relayed to the others in the room |
| `SendChat(sessionId, text)` | admitted participants | stored and broadcast (max 2 000 chars) |
| `ChangeMediaMode(sessionId, audioOnly)` | admitted participants | audio-only fallback, recorded |
| `ReportQuality(sessionId, ConnectionQualitySample)` | joined participants | worst stats stored (at most every 15 s) |
| `Leave(sessionId)` / disconnect | anyone | left time recorded; waiting room cleared if empty |

Server → client events: `participantJoined`, `participantLeft`, `waitingRoomChanged`, `waitingRoomLeft`, `admitted`,
`offer`, `answer`, `iceCandidate` (`{ from, data }`), `chatMessage`, `mediaModeChanged`, `sessionUpdated`,
`sessionEnded`. `TelehealthJoinResult` = session id, participant id, role, status, admitted, scheduled start,
practitioner/patient names, ICE servers, waiting list (clinicians) and the last 50 chat messages (admitted).
Presence is stored with connection ids, so with the Redis backplane any API node can admit a patient.

### Endpoints — `/api/v1/telehealth`

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/config` | `telehealth.sessions.read` or `portal.access` | → `TelehealthConfig { iceServers[], earlyJoinMinutes, hubPath }` |
| GET | `/sessions?date&to&practitionerId&status&mine` | `telehealth.sessions.read` | ≤ 31 days → `TelehealthSessionListItem[]` |
| GET | `/sessions/{id}` | `telehealth.sessions.read` | → `TelehealthSessionDetail` (participants with quality, message count, link info); guard + PHI audit |
| POST | `/sessions` | `telehealth.sessions.manage` | `CreateTelehealthSessionRequest` (appointmentId **or** patientId + practitionerId + scheduledStartAt, durationMinutes 5–120, sendLink) → 201 `TelehealthLinkIssued { sessionId, joinUrl, expiresAt, sent }` |
| POST | `/sessions/{id}/start` | `telehealth.consult` | → `TelehealthSessionDetail` (visit created, appointment InConsultation) |
| POST | `/sessions/{id}/end` | `telehealth.consult` | `EndTelehealthSessionRequest` (qualityNotes) → `TelehealthSessionDetail` |
| POST | `/sessions/{id}/cancel` | `telehealth.sessions.manage` | `CancelTelehealthSessionRequest` (reason) → `TelehealthSessionDetail` |
| POST | `/sessions/{id}/resend-link` | `telehealth.sessions.manage` | → `TelehealthLinkIssued` (old link stops working) |
| POST | `/sessions/{id}/recording-consent` | `telehealth.consult` | `RecordingConsentRequest { granted }` → 204 |
| GET | `/sessions/{id}/messages` | `telehealth.sessions.read` | → `TelehealthMessageDto[]` (PHI audit) |
| POST | `/sessions/{id}/messages` | `telehealth.consult` | `PostTelehealthMessageRequest { text }` → 201 `TelehealthMessageDto` |

### Patient portal — `/api/v1/portal/telehealth`

Permission `portal.access`, scoped by `PortalPatientScope`.

| Method | Path | Response |
|---|---|---|
| GET | `/sessions` | `PortalTelehealthSession[]` (upcoming and last 30 days; `joinOpensAt`, `canJoinNow`) |
| GET | `/sessions/{id}/join` | `TelehealthJoinInfo` (doctor, department, times, `canJoinNow`, hub path, ICE servers) |
| GET | `/sessions/{id}/messages` | `TelehealthMessageDto[]` |

## Remote Patient Monitoring

* **RpmProgram** (`HTN`, `DM`, `CARD`, `POSTDC` seeded) with kind and expected readings per day.
* **RpmEnrollment** – patient, programme, care team (practitioner ids, primary), status
  `Active | Paused | Completed | Withdrawn` (paused keeps readings but raises no alerts), start/end, goals, notes.
  A new enrolment gets the programme's default limits (`DefaultThresholds`).
* **RpmDevice** – serial (unique), type `BpMonitor | Glucometer | PulseOximeter | WeightScale | Thermometer |
  EcgPatch`, assignment, last seen, API key hash (`rpm_{deviceId:N}.{secret}`, shown once, SHA-256 stored, constant-time
  check). Devices may only report what they measure.
* **RpmReading** (`[NotAudited] Entity`) – type `SystolicBp | DiastolicBp | HeartRate | SpO2 | Glucose | Weight |
  Temperature | EcgRhythm`, value in canonical units (mmHg, bpm, %, mg/dL, kg, °C; °F, mmol/L, lb, kPa converted),
  text (ECG interpretation), measured/received at, severity, out-of-range flag, source `Device | Fhir | Manual`.
  Implausible values are rejected; retries are de-duplicated (unique enrolment + type + measurement second).
* **RpmThreshold** – per enrolment and type: min/max and critical min/max (validated order).
* **RpmAlert** – `Threshold | DeviceSilence`, severity `Warning | Critical`, status `Open | Acknowledged | Resolved`,
  occurrences, acknowledged/resolved by/at, notes. One active alert per enrolment and type; repeats are counted,
  a warning that turns critical is escalated, reopened and re-notified.

### Endpoints — `/api/v1/rpm`

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/dashboard` | `rpm.read` | → `RpmDashboard` (active/paused enrolments, open/critical alerts, silent devices, readings last 24 h, enrolments without a reading today) |
| GET | `/programs?activeOnly` | `rpm.read` | → `RpmProgramDto[]` |
| POST / PUT | `/programs`, `/programs/{id}` | `rpm.manage` | `SaveRpmProgramRequest` → 201 `{ id }` / 204 |
| GET | `/enrollments?status&programId&practitionerId&search` | `rpm.read` | → `PagedResult<RpmEnrollmentListItem>` (critical alerts first) |
| GET | `/enrollments/{id}` | `rpm.read` | → `RpmEnrollmentDetail` (care team, thresholds, devices, open alerts); guard + PHI audit |
| POST | `/enrollments` | `rpm.manage` | `EnrollPatientRequest` (patientId, programId, careTeamPractitionerIds, primaryPractitionerId, startDate, goals, notes) → 201 `{ id }` |
| PUT | `/enrollments/{id}` | `rpm.manage` | `UpdateEnrollmentRequest` (care team, status, statusReason — required to withdraw, goals, notes, **version**) → 204 |
| DELETE | `/enrollments/{id}` | `rpm.manage` | only while it has no readings or alerts → 204 |
| PUT | `/enrollments/{id}/thresholds` | `rpm.manage` | `SetThresholdsRequest { thresholds[{ type, min, max, criticalMin, criticalMax }], version }` → 204 |
| GET | `/enrollments/{id}/readings?type&from&to` | `rpm.read` | → `PagedResult<RpmReadingDto>` |
| POST | `/enrollments/{id}/readings` | `rpm.manage` | `RecordReadingsRequest` (≤ 50 manual readings) → `IngestResult` |
| GET | `/enrollments/{id}/trends?types=SystolicBp,DiastolicBp&from&to` | `rpm.read` | ≤ 366 days (default 30) → `RpmTrend[] { type, label, unit, threshold, points[], daily[{ date, min, max, average, count }] }` |
| GET | `/enrollments/{id}/adherence?days=30` | `rpm.read` | → `RpmAdherenceDto { windowDays, daysWithReadings, percent, missedDates, expectedReadingsPerDay, readingsPerDay[] }` |
| GET | `/devices?enrollmentId&activeOnly&unassigned` | `rpm.read` | → `RpmDeviceDto[]` |
| POST | `/devices` | `rpm.manage` | `RegisterDeviceRequest` (serialNumber, type, manufacturer, model, enrollmentId?) → 201 `DeviceKeyIssued { deviceId, serialNumber, apiKey, ingestPath, headerName }` |
| POST | `/devices/{id}/rotate-key` | `rpm.manage` | → `DeviceKeyIssued` |
| POST | `/devices/{id}/assign` | `rpm.manage` | `AssignDeviceRequest { enrollmentId? }` (null = unassign) → 204 |
| POST | `/devices/{id}/deactivate` | `rpm.manage` | → 204 (key stops working) |
| GET | `/alerts?status&severity&enrollmentId&patientId` | `rpm.read` | active by default, critical first → `PagedResult<RpmAlertDto>` |
| POST | `/alerts/{id}/acknowledge` | `rpm.alerts.manage` | `AlertActionRequest { notes }` → 204 |
| POST | `/alerts/{id}/resolve` | `rpm.alerts.manage` | `AlertActionRequest { notes }` → 204 |
| POST | `/ingest` | anonymous, `RequireRateLimiting(RateLimitPolicies.Device)` (120 a minute per device key), header `X-Device-Key` | see below → `IngestResult { accepted, duplicates, rejected[], alertsRaised }` |

**Device ingestion (`POST /api/v1/rpm/ingest`).** Unknown or wrong keys get the same 401; bodies over 1 MB are
refused. Bodies accepted (≤ 500 readings):

```json
{ "readings": [ { "type": "SystolicBp", "value": 128, "unit": "mmHg", "measuredAt": "2026-09-26T08:15:00+05:30" },
                { "type": "BloodPressure", "systolic": 142, "diastolic": 91, "pulse": 78 },
                { "type": "ecg", "text": "Normal sinus rhythm" } ] }
```

or a FHIR R4 `Observation` / `Bundle` of Observations identified by LOINC (`8480-6`, `8462-4`, `85354-9` panel
components, `8867-4`, `59408-5`/`2708-6`, `2339-0`/`2345-7`/`41653-7`/`15074-8`, `29463-7`, `8310-5`, `8601-7`
with `valueCodeableConcept`). 422 when the device is not assigned; 400 when nothing could be accepted.

**Alert routing (RPM-03).** New or escalated alerts: SMS/WhatsApp `rpm.alert` to care-team practitioners with a mobile
number (first name + UHID only), queued in the ingestion transaction; in-app notification to every holder of
`rpm.alerts.manage` in the facility after commit.

## Events handled

| Event | Effect |
|---|---|
| `AppointmentBooked` (`IsTelehealth`) | creates the session and queues `telehealth.link` in the booking's transaction (idempotent per appointment) |
| `AppointmentCancelled` | cancels the not-yet-started session, invalidates the link, tells waiting clients |
| `VisitCreated` (Patients) | links the visit to the appointment's session |
| `PrescriptionSharedWithPatient` (Clinical) | for a teleconsultation visit: `prescription.shared` to the patient by SMS, at the moment Clinical sends the WhatsApp message it sends for every OP prescription — at signing, or, where the hospital requires the doctor's DSC, once signed with it; once per prescription |

No events are raised. Real time: `telehealth.changed` on `Topics.Facility(facilityId)` (`{ id, status, practitionerId }`).

## Notifications

| Template | Channels | Recipient |
|---|---|---|
| `telehealth.link` | SMS, WhatsApp | patient — values name, doctor, time, link, hospital |
| `rpm.alert` | SMS (default), WhatsApp (seeded here) | care team — patient, reading, time |
| `rpm.device_silent` | SMS, WhatsApp (seeded here) | patient — name, device, since |
| `prescription.shared` | SMS (seeded here); WhatsApp (default) is sent by Clinical | patient — name, doctor, date |

## Background job

`VirtualCareJobs` (every 15 minutes, only when `PlatformOptions.RunsJobs`): marks unstarted sessions `Missed` one hour
after their end; raises a `DeviceSilence` alert once per silence for assigned devices of active enrolments with no
contact for `rpm.deviceSilenceHours`, nudges the patient and notifies the care team in-app.

## Settings

| Key | Default | Meaning |
|---|---|---|
| `telehealth.iceServers` | `[{"urls":["stun:stun.l.google.com:19302"]}]` | RTCIceServer array (STUN/TURN with username/credential); may be stored as a secret |
| `telehealth.earlyJoinMinutes` | 30 | waiting room opens this long before the start |
| `rpm.deviceSilenceHours` | 24 | silence alert threshold (1–720) |
| `portal.baseUrl` | — | the hospital's public https address, base of the join link |

`portal.baseUrl` is the hospital's public https address (for example `https://care.nanahospitals.in`); patient links are `{base}/portal/...`.
It is read through the Platform helper `PortalAddress`, shared with Engagement, Scheduling and printed QR codes.

## Permissions

| Permission | Default roles |
|---|---|
| `telehealth.sessions.read` | Doctor, Nurse, FrontOffice, HospitalAdmin |
| `telehealth.sessions.manage` | Doctor, Nurse, FrontOffice |
| `telehealth.consult` | Doctor |
| `rpm.read` | Doctor, Nurse |
| `rpm.manage` | Doctor, Nurse, HospitalAdmin |
| `rpm.alerts.manage` | Doctor, Nurse |

## Seed data

* Always: programmes `HTN`, `DM`, `CARD`, `POSTDC`; templates listed above.
* `SeedDemoData` only: video sessions for demo telehealth appointments (no SMS); two monitored patients —
  hypertension with a BP monitor (two weeks, twice daily, one critical reading) and cardiac care with a pulse oximeter
  and weighing scale (SpO2 dipping to 91 % today) — with their alerts. Demo device keys are discarded; rotate a key to
  connect a real device.
