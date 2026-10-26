# Feature switches

Parts of HealthCare360 an administrator can turn off for the whole hospital, under **Hospital setup > Features** in the
staff app. A switch that is off is enforced by the server: the endpoints of the feature refuse to work, and the staff
app and the patient portal hide the feature and explain why instead of showing errors.

| | |
|---|---|
| Code | `src/server/HyFib.HealthCare360.Platform/Features` (catalogue, reader, gate, admin endpoints) |
| Staff app | `src/web/apps/staff/src/lib/featureSwitches.ts`, `lib/features.ts`, `lib/FeatureOffState.tsx`, `modules/features`, route `/setup/features` |
| Portal | `src/web/apps/portal/src/lib/featureSwitches.ts`, `lib/features.ts`, `components/FeatureOff.tsx` |
| Tests | `FeatureFlagsTests`, `FeatureGateTests` (Platform.Tests); `FeatureSwitchTests` (ArchitectureTests: every portal, video and ABDM endpoint has its switch, and what must keep working has none); `AbdmEndpointTests` (ArchitectureTests: every ABDM endpoint has its switch and permission); `FeatureSwitchTests`, `AbdmExportTests` (IntegrationTests); `PublicDirectoryTests` (Scheduling.Tests); staff `lib/modules.test.ts`, `modules/features/*.test.*`, `modules/emr/AbdmRecordButton.test.tsx`, `modules/emr/abdmRecords.test.tsx`; portal `app.test.tsx` |

## The switches

The patient portal and video consultations worked before they had a switch, so they are **on** until an administrator
turns them off and an upgrade changes nothing. ABDM health records are new, the hospital decides whether to use them,
and once the ABDM gateway is live they send records outside the hospital, so they are **off** until an administrator
turns them on.

| Key | Name | While it is off | Keeps working |
|---|---|---|---|
| `features.patientPortal` | Patient portal | Patients cannot sign in or register (`/api/v1/auth/otp/*`), a portal session cannot be refreshed, every `/api/v1/portal/*` API and the public doctor directory (`/api/v1/public/doctors/*`, online booking) answer 503, and a patient's sign-in opens no endpoint that needs one. A signed-in patient cannot join a video consultation from the portal. The portal shows "The patient portal is not available right now. Please contact the hospital." instead of sign-in and every signed-in page | Report and prescription checks from printed QR codes, feedback and unsubscribe links from SMS, the privacy notice, video consultation links sent by SMS (while video consultations are on), payment-gateway webhooks, and everything the staff app uses |
| `features.telehealth` | Video consultations | Every `/api/v1/telehealth/*` and `/api/v1/portal/telehealth/*` endpoint answers 503, except ending and cancelling a consultation. Nobody joins a consultation on the hub `/hubs/telehealth`: every way to join checks the switch for the consultation's facility (the hub has no switch of its own, as guests and patients connect without a facility). Nothing new is booked, rescheduled or waitlisted as a video consultation (422 `feature.disabled`), and the public directory offers no video (`offersTelehealth: false`, in-person times only). The staff app hides video links, quick actions and the widget, the worklist's Video action and the video option when booking; the portal hides video consultations | Remote patient monitoring. Video appointments already booked stay in the diary: staff move them to an in-person visit (reschedule) or cancel them. A call already running is not cut off |
| `features.abdm` | ABDM health records (EHR-08, off by default) | Every `/api/v1/abdm/*` endpoint answers 503: no discharge summary, lab report, consultation note or prescription is exported as an ABDM document, and the ABDM gateway's set-up cannot be read; the gateway's callbacks answer 503 too (no linking, consent, transfer of records or scan and share); the staff app does not offer **Download ABDM record** on discharge summaries or prescriptions, nor the ABHA scan-and-share queue, nor ABHA checks on the patient's Identity tab ([interop.md](interop.md#abdm-health-records-ehr-08)) | ABHA numbers and addresses: captured at registration, edited on the patient record, searched and shown masked. Discharge summaries, lab reports, consultations and prescriptions, their printouts and PDFs, and the FHIR API (`/fhir/r4`) |

## How a switch is read

* The setting store holds the switch (`features.<code>`, JSON `true` or `false`).
  `IFeatureFlags.IsEnabledAsync(feature, facilityId)` reads the hospital-wide value, else the catalogue default. It is
  the master switch: while it is off, the feature is off everywhere. While it is on, a facility's own `false` turns the
  feature off for that facility; a facility's `true` changes nothing. Any value other than `true` or `false` (`"no"`,
  `null`, a number) is ignored, so a typo never turns a feature off.
* **Facility.** Requests use the signed-in user's facility. Patients, guests and anonymous callers have none, so they
  get the hospital-wide value, which is right for them because no branch can be on while it is off. Where the facility
  becomes known, its own switch is checked as well: the public directory uses the branch the patient asks for
  (`facilityId`), a booking the facility of the slot, a waitlist request the facility it waits at, and the telehealth hub
  the consultation's facility. The Features screen sets the hospital-wide value; `false` saved for one facility under
  Administration > Settings (same key) turns the feature off for that branch alone.
* **When a change applies.** Settings are cached for a minute, but saving a key expires every cached value of that key
  on this server at once (hospital-wide and every facility), and on every other server on the database through its
  notifications (SCAL-01), once the change is committed. The staff app asks for the switches again when they are five minutes old and the window is next used, and at
  once when a screen meets a refusal; the portal does the same with `GET /api/v1/system/info`.

## Server

* `FeatureCatalog` lists each feature with its plain name, description, what happens while it is off, its default, and
  the words of the refusal for staff and for patients.
* `.RequireFeature(FeatureKeys.Telehealth)` on an endpoint, group or hub adds `RequiresFeatureMetadata`.
  `FeatureGateMiddleware` (after authorization, before the idempotency store) answers 503 for an endpoint whose feature
  is off, before its handler runs:

  ```json
  { "status": 503, "title": "Video consultations are turned off", "detail": "Book an in-person visit instead. …",
    "code": "feature.disabled", "data": { "feature": "telehealth" } }
  ```

  The detail is worded for staff, or for patients and anonymous callers. `.RequireFeature(feature, request => …)` applies
  the switch to some requests only (the portal's own session refreshes). The gate also adds the patient portal to every
  endpoint that needs a sign-in when the caller is a patient. The apps do not retry a `feature.disabled` answer.
* An option of a request that otherwise works (a video appointment) is refused with
  `FeatureProblems.DisabledError(...)`: 422, the same code and data.
* `GET /api/v1/system/info` (anonymous) includes `features: { patientPortal, telehealth }`, the hospital-wide values,
  for the sign-in page and the portal. `GET /api/v1/settings/public` (any signed-in user) gives every switch as the
  server enforces it for the user's facility (`"features.telehealth": false`).

### Admin endpoints — `/api/v1/admin/features`

Permission `admin.settings.manage`.

| Method | Path | Request → Response |
|---|---|---|
| GET | `/` | → `FeatureDto[] { key, code, name, description, whenOff, defaultEnabled, enabled }` (`enabled` is the hospital-wide value) |
| PUT | `/{key}` | `SetFeatureRequest { enabled }` (required) → `FeatureDto`. `key` is `features.telehealth` or `telehealth`. Saves the hospital-wide setting and the audit entry `feature.changed` (category admin, details `{ feature, enabled, wasEnabled }`, warning when turned off) in one transaction |

## Apps

* **Staff app.** `NavLink`, `QuickAction` and `DashboardWidget` take an optional `feature`.
  `visibleModules(user, manifests, features)` drops what is switched off, for the menu, the home quick actions and
  widgets, the Ctrl+K palette and the voice assistant (which says "Video consultations are turned off.").
  `useFeatureOn('telehealth')` hides options inside screens. Until the switches are known, a feature is at the
  server's default (`isFeatureOn`): on, except ABDM health records. A screen opened while its feature is off shows
  `FeatureOffState`, a plain explanation with the way to the Features screen for administrators, and `ErrorState` shows
  any `feature.disabled` answer as information without a retry. The Features screen asks before turning anything off
  and says what will stop.
* **Portal.** It reads the switches from `GET /api/v1/system/info`, and records any `feature.disabled` answer
  (`onFeatureDisabled` of the API client). While the portal is off, sign-in, the signed-in pages and the doctor
  directory show the not-available page, with "Try again"; while video consultations are off, video pages, links,
  options and the join link say they are not available. A call already on screen is never cut off.

## Adding a feature

1. Add a `FeatureDefinition` to `FeatureCatalog` (and its key to `FeatureKeys`): name, description, what happens while
   it is off, default (on, unless the feature is new and must start off), and the refusal for staff and patients.
2. Put `.RequireFeature(FeatureKeys.X)` on its endpoint groups and hubs. Check options inside shared requests with
   `IFeatureFlags` and refuse them with `FeatureProblems.DisabledError`. Add its endpoints to `FeatureSwitchTests` in
   the architecture tests.
3. Staff app: add the code to `FEATURE_KEYS`, `FEATURE_DEFAULTS` and `FEATURE_OFF_TEXT` (`lib/featureSwitches.ts`)
   and its icon to `FeatureOffState`, set `feature` on its links, quick actions and widgets, and show
   `FeatureOffState` on its screens. Portal: add it to `PortalFeature`, `SystemFeatures` on the server and the
   portal's `SystemInfo` if patients see it.
4. Document it in the table above. The Features screen lists it without a change.
