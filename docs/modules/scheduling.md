# Scheduling module

Doctor OPD schedules, appointments, front-desk check-in and Online Doctor Appointment booking.

| | |
|---|---|
| Project | `src/server/Modules/HyFib.HealthCare360.Modules.Scheduling` (layer 3, references Patients) |
| Schema | `scheduling` — `doctor_schedules`, `schedule_exceptions`, `appointments`, `waitlist_entries` |
| Requirements | PMS-01, PMS-02, PMS-03, OA-01..05, TEL-01 (booking side) |
| Tests | `tests/Modules/HyFib.HealthCare360.Modules.Scheduling.Tests` (slot engine, booking rules, appointment state machine, reminders, schedules, waitlist, permissions) |

## Concepts

* **DoctorSchedule** – a recurring weekly OPD session: practitioner, facility, department, weekday, start/end (IST
  wall clock), slot minutes (5–120), patients per slot (1–20), mode `InPerson | Telehealth | Both`, room, effective
  from/to, active. A doctor's sessions may never overlap in time at any branch (409 `schedule.overlap`).
* **ScheduleException** – dated `Leave | Block | ExtraSession`. No times = whole days. Leave usually applies at every
  branch (`FacilityId = null`); an extra session belongs to the current facility and carries its own slot settings.
* **Slot engine** (`Domain/SlotEngine.cs`, pure) – sessions + extra sessions − leave/blocks − seats taken by active
  appointments, on the IST calendar, never before `NotBefore` (now for staff; now + online cut-off for the public and
  portal). Range capped at 62 days.
* **Appointment** – number `{FAC}AP{YY}{SEQ:7}` (series `appointment`, per facility, yearly reset). Status
  `Booked → Confirmed → CheckedIn → InConsultation → Completed`, or `Cancelled | NoShow | Rescheduled`. Channel
  `WalkIn | Phone | Online | Portal | Kiosk | WhatsApp`; visit type `New | FollowUp`; `IsTelehealth`; fee quoted at
  booking (doctor's new/follow-up fee); payment `Unpaid | PaidOnline | PayAtHospital` with gateway order/payment ids;
  cancellation code + text + who; reschedule history (`RescheduledFromId`/`RescheduledToId`); reminder flags; `VisitId`.
* **WaitlistEntry** – a patient waiting for a doctor within a date window and part of day
  (`Any | Morning | Afternoon | Evening`). Freed slots are offered (SMS/WhatsApp), never booked automatically.

## Booking rules and concurrency

* Booking runs in one transaction: `pg_advisory_xact_lock` on the slot (practitioner + start) and on the patient
  (acquired in a fixed order), then the slot is recomputed, capacity checked, the patient's overlapping bookings
  checked (any doctor, any branch), a seat (1..capacity) allocated and a number drawn.
* Partial unique index `ux_appointments_practitioner_slot_seat (practitioner_id, starts_at, seat_number) WHERE status
  NOT IN ('Cancelled','Rescheduled') AND is_deleted = false` makes over-booking impossible even across API nodes.
* Staff may overbook a full slot only with `scheduling.appointments.manage` (`overbook: true`).
* Online (portal) bookings: only `AvailableForOnlineBooking` doctors, not inside the cut-off, not beyond the booking
  window, one open booking per doctor per day; portal cancel/reschedule closes at the cut-off.
* While video consultations are switched off ([features.md](features.md)), nothing is booked, rescheduled or added
  to the waitlist as a video consultation, from any channel: 422 `feature.disabled`. The switch is read for the
  facility of the slot booked (a waitlist request: the facility it waits at), so an online booking at a branch with
  video switched off is refused too. Video appointments already booked stay; rescheduling one with
  `isTelehealth: false` moves it to an in-person visit, and cancelling works as always.
* Rescheduling books the new slot and marks the old row `Rescheduled` in the same transaction; online prepayment moves
  to the new booking; the freed slot is offered to the waitlist.
* Check-in is allowed on the appointment day only. It calls `VisitService.CreateAsync` (visit + queue token; no token
  for telehealth). If the patient already has an open OP visit today in that department, the appointment is linked to
  it (`linkedExistingVisit: true`).

## Endpoints

All staff endpoints are facility-scoped by the platform query filter. Validation errors are 400, rule violations 422,
conflicts (slot full, overlap, duplicates, concurrency) 409.

### Schedules and exceptions — `/api/v1/scheduling`

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/schedules?practitionerId&departmentId&activeOnly` | `schedules.manage` or `appointments.read` | → `ScheduleDto[]` (names, `slotsPerSession`, `version`) |
| GET | `/schedules/{id}` | same | → `ScheduleDto` |
| POST | `/schedules` | `scheduling.schedules.manage` | `CreateSchedulesRequest` (practitionerId, departmentId, **weekdays[]**, startTime, endTime, slotMinutes, capacityPerSlot, mode, room, effectiveFrom/To, notes) → 201 `SchedulesCreated { ids }` |
| PUT | `/schedules/{id}` | `scheduling.schedules.manage` | `UpdateScheduleRequest` (+ isActive, **version**) → `ScheduleSaved { id, affectedAppointments[] }` |
| DELETE | `/schedules/{id}` | `scheduling.schedules.manage` | → `ScheduleSaved` (future bookings the change strands) |
| GET | `/exceptions?practitionerId&from&to` | `schedules.manage` or `appointments.read` | → `ScheduleExceptionDto[]` (default: today + 90 days) |
| POST | `/exceptions` | `scheduling.schedules.manage` | `SaveScheduleExceptionRequest` (practitionerId, type, fromDate, toDate, startTime?, endTime?, reason, allFacilities, extra-session fields) → 201 `ScheduleSaved` (bookings inside new leave/blocks) |
| PUT | `/exceptions/{id}` | `scheduling.schedules.manage` | `SaveScheduleExceptionRequest` (+ version) → `ScheduleSaved` |
| DELETE | `/exceptions/{id}` | `scheduling.schedules.manage` | → 204 |

### Slots — `/api/v1/scheduling/slots`

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/slots?practitionerId|departmentId&from&to&mode&includeFull` | `appointments.read` or `appointments.book` | ≤ 31 days → `PractitionerAvailability[] { practitionerId, practitionerName, departmentName, offersTelehealth, freeSlots, slots[] }`; `SlotDto { startsAt, endsAt, date, localStart, localEnd, capacity, booked, available, mode, facilityId, departmentId, room }` |

### Appointments — `/api/v1/scheduling/appointments`

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/` | `scheduling.appointments.read` | paging + `from,to,practitionerId,departmentId,patientId,status,channel,telehealth,search` (UHID/mobile/name/appointment no.); sort `startsAt,number,patient,status,bookedAt` → `PagedResult<AppointmentListItem>` |
| GET | `/calendar` | `scheduling.appointments.read` | `from,to` (≤ 31 days), `practitionerId,departmentId,status,includeCancelled` → `CalendarAppointment[]` (max 3 000) |
| GET | `/summary?date&departmentId` | `scheduling.appointments.read` | → `AppointmentDaySummary` (status counts, telehealth/online counts, per-doctor capacity, booked, checked-in, no-show, free slots) |
| GET | `/{id}` | `scheduling.appointments.read` | → `AppointmentDetail` (with reschedule `history[]`); restricted-record guard + PHI access audit |
| POST | `/` | `scheduling.appointments.book` | `BookAppointmentRequest` (patientId, practitionerId, startsAt, channel ≠ Portal, visitType, isTelehealth, reason, notes, overbook, notifyPatient, waitlistEntryId) → 201 `AppointmentDetail` |
| POST | `/{id}/reschedule` | `scheduling.appointments.manage` | `RescheduleAppointmentRequest` (startsAt, practitionerId?, isTelehealth?, reason, overbook, notifyPatient) → `AppointmentDetail` of the **new** booking |
| POST | `/{id}/cancel` | `scheduling.appointments.manage` | `CancelAppointmentRequest` (reasonCode `PatientRequest|DoctorUnavailable|HospitalRequest|Duplicate|PaymentNotReceived|Other`, reason — required for Other, notifyPatient) → `AppointmentDetail` |
| POST | `/{id}/confirm` | `scheduling.appointments.manage` | → `AppointmentDetail` |
| POST | `/{id}/no-show` | `scheduling.appointments.manage` | only after the start time → `AppointmentDetail` |
| POST | `/{id}/check-in` | `scheduling.appointments.manage` | `CheckInAppointmentRequest` (payerType, payerId, priority, chiefComplaint, issueToken; send `{}` for defaults) → `CheckInResult { appointmentId, visitId, visitNumber, tokenLabel, linkedExistingVisit }` |

### Waitlist — `/api/v1/scheduling/waitlist`

| Method | Path | Permission | Request → Response |
|---|---|---|---|
| GET | `/?practitionerId&departmentId&status` | `scheduling.appointments.read` | open entries by default → `PagedResult<WaitlistEntryDto>` |
| POST | `/` | `scheduling.appointments.book` | `AddToWaitlistRequest` (patientId, practitionerId, preferredFrom/To ≤ 60 days, preferredSession, isTelehealth, channel, notes) → 201 `WaitlistEntryDto` |
| POST | `/{id}/cancel` | `scheduling.appointments.manage` | → 204 |
| POST | `/{id}/book` | `scheduling.appointments.book` | `BookFromWaitlistRequest` (startsAt, visitType, reason, overbook) → 201 `AppointmentDetail` |

### Online Doctor Appointment (public) — `/api/v1/public/doctors`

Anonymous, `RequireRateLimiting("public")`. Only active doctors with `AvailableForOnlineBooking` and at least one
active session are listed; requests carry no facility, so every branch is shown unless `facilityId` is passed.
Booking online needs the portal, so every endpoint answers 503 `feature.disabled` while the patient portal is switched
off. While video consultations are off (for `facilityId`, else hospital-wide), no doctor offers them
(`offersTelehealth: false`, no `nextTelehealthAt`), `telehealth=true` lists nobody and its slots answer 422
`feature.disabled`, and sessions and slots for both modes are shown as in person ([features.md](features.md)).

| Method | Path | Request → Response |
|---|---|---|
| GET | `/?search&specialtyId&departmentId&facilityId&language&telehealth&page&pageSize` | pageSize ≤ 50 (default 12) → `PagedResult<PublicDoctor>` (name, gender, qualification, specialty, department, languages, feeNew, feeFollowUp, currency INR, offersTelehealth, facilities, **nextAvailableAt**, **nextTelehealthAt**) |
| GET | `/filters?facilityId` | → `DirectoryFilters` (specialties, departments with doctor counts, languages, facilities, cutoffMinutes, windowDays) |
| GET | `/{id}?facilityId` | → `PublicDoctorProfile` (card, bio, followUpValidityDays, weeklySessions) |
| GET | `/{id}/slots?from&to&telehealth&facilityId` | ≤ 14 days, never inside the cut-off or beyond the window → `PublicSlotDay[] { date, slots[{ startsAt, endsAt, localTime, mode, facilityId, seatsLeft }] }` |

### Patient portal — `/api/v1/portal/appointments`, `/api/v1/portal/waitlist`

Permission `portal.access`; every query is limited by `PortalPatientScope` (own and family records); a record outside
the scope answers 404.

| Method | Path | Request → Response |
|---|---|---|
| GET | `/api/v1/portal/appointments?scope=upcoming|past|all` | → `PortalAppointment[]` (doctor, specialty, department, branch, status, payment, fee, `canCancel`, `canReschedule`, `canPay`) |
| GET | `/api/v1/portal/appointments/{id}` | → `PortalAppointment` |
| POST | `/api/v1/portal/appointments` | `PortalBookAppointmentRequest` (patientId, practitionerId, startsAt, visitType, isTelehealth, reason, payOnline) → 201 `PortalBookingResult { appointment, payment?: PaymentCheckout, message? }` |
| POST | `/api/v1/portal/appointments/{id}/cancel` | `PortalCancelAppointmentRequest` (reason) → 204 |
| POST | `/api/v1/portal/appointments/{id}/reschedule` | `PortalRescheduleAppointmentRequest` (startsAt, reason) → `PortalAppointment` (new booking) |
| POST | `/api/v1/portal/appointments/{id}/payment` | → `PaymentCheckout { provider, orderId, amount, currency, checkoutKey, paymentLinkUrl }` (422 when the gateway is unavailable) |
| POST | `/api/v1/portal/appointments/{id}/payment/confirm` | `ConfirmPaymentRequest` (orderId, paymentId, signature) → `PortalAppointment` |
| POST | `/api/v1/portal/appointments/{id}/payment/verify` | Server-to-server status check with the gateway, for checkouts without a signature (the training gateway) → `PortalAppointment`; 422 `payment.not_captured`, `payment.not_started`, `payment.gateway_unavailable`. Safe to repeat |
| GET | `/api/v1/portal/waitlist` | → `PortalWaitlistEntry[]` |
| POST | `/api/v1/portal/waitlist` | `PortalJoinWaitlistRequest` → 201 `PortalWaitlistEntry` |
| POST | `/api/v1/portal/waitlist/{id}/cancel` | → 204 |

**Online payment.** `IPaymentGateway` is optional: if no gateway is registered, order creation fails, or the fee is
zero, the booking stays and becomes `PayAtHospital`. Confirmation verifies `VerifySignature("{orderId}|{paymentId}",
signature)` (Razorpay convention) and, when the gateway can report it, rejects orders that are not captured. Nothing is
posted to billing; `AppointmentPaymentCaptured` is raised instead.

## Events

| Direction | Event | Notes |
|---|---|---|
| Raised | `AppointmentBooked` (Platform.Contracts) | every booking, including the new row of a reschedule; `Channel` is the enum name |
| Raised | `AppointmentCancelled` (Platform.Contracts) | cancellation (reason text or code description) and the old row of a reschedule (`"Rescheduled to {number}"`) |
| Raised | `AppointmentPaymentCaptured` (`Modules.Scheduling.Domain`) | verified online prepayment — for Billing to issue an advance receipt / refund after cancellation |
| Handled | `VisitCreated` (Patients) | visit with `AppointmentId` → appointment `CheckedIn` + `VisitId` (same transaction) |
| Handled | `VisitStarted` (Patients) | the linked appointment moves to `InConsultation` (queue token started or encounter opened) |
| Handled | `VisitCompleted` (Patients) | appointment `Completed` |
| Handled | `VisitCancelled` (Patients) | check-in reverted (`Booked`/`Confirmed`) |

Real time: `appointments.changed` on `Topics.Facility(facilityId)` with `{ id, practitionerId, departmentId, status,
startsAt }` after every change. Check-ins through the Patients visit screen also publish `visits.changed`.

## Notifications

| Template | Channels | When | Values |
|---|---|---|---|
| `appointment.confirmed` | SMS, WhatsApp | booked or rescheduled | name, doctor, date, time, reference, mode, hospital |
| `appointment.reminder` | SMS, WhatsApp | 24 h and 2 h before (job) | same |
| `appointment.cancelled` | SMS, WhatsApp (WhatsApp template seeded here) | cancelled | same |
| `appointment.waitlist` | SMS, WhatsApp (seeded here) | a matching slot was freed | name, doctor, date, time, link, hospital |

Messages expire at the appointment time (outbox TTL).

## Background job

`AppointmentReminderService` (every 5 minutes, only when `PlatformOptions.RunsJobs`): sends due 24 h / 2 h reminders
exactly once (`Reminder24hSentAt` / `Reminder2hSentAt` saved with the outbox message; concurrent workers lose on the
row version), skips reminders that were due before the booking was made, and expires stale waitlist requests.

## Settings

| Key | Default | Meaning |
|---|---|---|
| `scheduling.onlineBookingCutoffMinutes` | 30 | online booking/changes close this long before the slot |
| `scheduling.onlineBookingWindowDays` | 30 | how far ahead patients can book online |
| `portal.baseUrl` | — | the hospital's public https address, used in waitlist links (`{base}/portal/doctors/{practitionerId}`) |

`portal.baseUrl` is the hospital's public https address (for example `https://care.nanahospitals.in`); patient links are `{base}/portal/...`.
It is read through the Platform helper `PortalAddress` (shared with Engagement, Telehealth and printed QR codes); a
plain http value reads as "not configured", because patient links can carry personal tokens.

## Permissions

| Permission | Default roles |
|---|---|
| `scheduling.schedules.manage` | HospitalAdmin, FrontOffice |
| `scheduling.appointments.read` | FrontOffice, Doctor, Nurse, HospitalAdmin, CrmExecutive |
| `scheduling.appointments.book` | FrontOffice, HospitalAdmin, CrmExecutive, Doctor, Nurse |
| `scheduling.appointments.manage` | FrontOffice, HospitalAdmin, CrmExecutive, Nurse |

## Seed data

* Always: `appointment.cancelled` (WhatsApp) and `appointment.waitlist` (SMS, WhatsApp) templates.
* `SeedDemoData` only: Monday–Saturday 09:00–13:00 and 17:00–19:00, 15-minute slots, one patient per slot for every
  active consultant (mode Both for telehealth doctors), and eight demo appointments today/tomorrow (phone, portal,
  online, walk-in, one video consultation, one cancellation). Demo rows raise no events (no SMS).

No billable service codes are seeded (consultation charges come from Billing's catalogue: `CONS-NEW`, `CONS-FU`, `CONS-TELE`).
