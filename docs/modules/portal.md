# Patient Portal (web app)

Project: `src/web/apps/portal` · package `@hc360/portal` · served at `/portal/` by the HC360 server, on an address of its
own: port 7361 (`HealthCare360:PortalHttpsPort`), where the staff app's is 7360. That address serves the portal and its
API only, and the staff app's address sends `/portal/...` there (SECURITY.md, "Separate addresses"). The build goes to
`src/server/HyFib.HealthCare360.Server/wwwroot/portal`, and the server's fallback `/portal/{**path}` returns its
`index.html`. It covers the SRS Patient Portal requirements **PP-01..06**, patient-side telehealth (**TEL-01..05**)
and the public checks of printed documents (**ERX-04**, **LIS-07**).

It is mobile-first, bilingual (English and Hindi, switchable at any time) and built to WCAG 2.2 AA: 44 px or larger
tap targets, visible focus, headings that take focus after navigation, status never shown by colour alone, and
reduced motion respected.

## Stack and layout

It uses the same stack as the staff console: React 19, TanStack Router (file routes, `basepath: '/portal'`), TanStack
Query, Fluent UI 9, react-hook-form with zod, and `@hc360/core` and `@hc360/ui`. It adds no third-party packages; the
i18n is a small typed dictionary.

| Folder | Contents |
|---|---|
| `src/routes` | File routes. `_auth` holds the signed-in pages. `_open` holds pages anyone can open, which show the signed-in frame when a patient is signed in. `feedback`, `unsubscribe` and `telehealth` are standalone link pages |
| `src/shell` | Frame (header, bottom or side navigation, footer), family-member switcher, language menu, connection banner |
| `src/modules/<feature>` | API query options, labels, pure helpers, feature components, print templates |
| `src/components` | Shared building blocks: links, list rows, notices, sheets, choice cards, document actions |
| `src/i18n` | `messages/<feature>.ts` (English and Hindi side by side), formatters in hospital time (IST) with Indian digit grouping |
| `src/lib` | API client (`clientType: 'portal'`), session, errors, payments, printing, PDF downloads, link tokens |

Commands (run from `src/web`): `pnpm --filter @hc360/portal dev` (port 5361, proxies `/api`, `/hubs` and `/health` to
`HC360_PORTAL_SERVER` or `https://localhost:7361`, the portal's address of the local server), `build`, `typecheck`, `test`,
and `npx eslint apps/portal`.

## Sign-in, session and family members

* **OTP sign-in.** `auth.requestOtp` → `POST /api/v1/auth/otp/request`, then `auth.verifyOtp` →
  `POST /api/v1/auth/otp/verify`. The page never says whether a number is registered. The resend button backs off
  (30 s, 60 s, 2 min, then 5 min), so patients do not hit the server limit. A code that seems to expire implausibly (a
  wrong phone clock) falls back to a five-minute countdown.
* **Tokens.** The access token stays in memory (core session). The refresh token is the server's HttpOnly cookie. The
  portal and the staff console have addresses of their own, but browsers send a host's cookies to all its ports, so each
  keeps its own refresh cookie and each address reads only its own app's: signing in to one never signs the other out,
  and nothing on the portal's address can renew a staff session.
* **Family.** `GET /api/v1/portal/profiles` returns everyone registered with the verified number, then the family
  members on other numbers that the account sees through a family link (below). The patient chooses whose records to
  see, and the switcher is always in the header. Every query key starts `['portal', patientId, …]`, and family-wide
  lists are filtered to the person being viewed. A record that belongs to another family member shows "This belongs
  to …" with a switch button.
* **Family on other numbers (PP-01).** A family member with their own mobile number joins the account in one of two
  ways. In the portal (`/profile/family`), the account holder enters the relative's UHID, mobile number and relation;
  the server sends a 6-digit code to that number when the UHID and the number belong to the same active patient, and
  the relative agrees by passing the code on. At the front desk, staff check both people's identity documents, record
  who agreed (the patient or their guardian), and share the records with the family member's number (the staff
  console's "Portal access" tab on the patient's profile). Either side can end a link: the account holder removes the
  relative, and anyone signed in with the patient's own number sees "Who else sees your records" and stops sharing.
  Staff end links at the front desk too. A link in force gives the account the same access as a patient on its own
  number, and ending it takes effect on the next request.
* **Clearing.** Signing out, idle sign-out (after 20 minutes, never during a video call) and switching family members
  clear the query cache.
* **Unsaved changes (SRS UX-08).** A sheet with a form asks "Discard your changes?" before *Not now*, its close button,
  Esc or a tap outside throw what was typed away, and leaving a page with a form being filled in (a link, Back, signing
  out) asks "Leave without saving?", in the patient's language; closing the tab has the browser ask. Idle sign-out does
  not wait for an answer. How a form gets the guard: *Unsaved changes* in `docs/architecture/FRONTEND-GUIDE.md`.

## Routes

The paths below are router paths. In the address bar they sit under `/portal`.

| Path | Sign-in | Purpose |
|---|---|---|
| `/sign-in` | no | Phone number, then OTP. `?redirect=` accepts same-app paths only |
| `/home` | yes | Greeting; video consultation ready to join; quick actions; next appointment; amount due; recent reports and prescriptions |
| `/doctors`, `/doctors/{practitionerId}` | no | Find a doctor and book. The list takes `?q=`, `?specialtyId=`, `?facilityId=`, `?language=` and `?video=true`. The doctor page takes `?mode=video`, `?branch=`, `?reschedule={appointmentId}` and `?slot=`. Choosing a time while signed out goes to sign-in and returns to the same time |
| `/appointments`, `/appointments/{id}` | yes | Upcoming and past, waiting list, pay, reschedule, cancel, join video |
| `/video`, `/video/{sessionId}` | yes | Video consultations: device check, waiting room, call, chat, audio-only |
| `/telehealth/{sessionId}?t={joinToken}` | no | The SMS or WhatsApp join link (guest join) |
| `/reports`, `/reports/{lab\|radiology\|cardiology}/{id}` | yes | Lab values with ranges and flags in plain words, and radiology and cardiology text. Download PDF (the hospital's official copy) or print |
| `/prescriptions`, `/prescriptions/{id}` | yes | Prescriptions with verification QR code; Download PDF or print; refill requests |
| `/bills`, `/bills/{id}` | yes | Bills, amount due, online payment; Download PDF or print a bill; receipts (print) |
| `/records`, `/records/discharge/{id}`, `/records/request` | yes | Health summary, discharge summaries (Download PDF or print), request a copy of records |
| `/my-feedback`, `/my-feedback/{formId}` | yes | Feedback forms and the patient's feedback with complaint status |
| `/profile`, `/profile/preferences` | yes | Details, family, language, SMS and WhatsApp choices, sign out |
| `/profile/family` | yes | Family on other numbers: the relatives the account sees (Remove), add a relative with a code sent to their number, and the accounts that see the records of someone on this number (Stop sharing) |
| `/feedback/{token}` | no | Personal feedback link (SMS after a visit or stay) |
| `/feedback/form/{token}` | no | QR poster form. Add `?kiosk=true` on a kiosk tablet, which then resets itself after each response |
| `/unsubscribe/{token}` | no | Opt-out link in campaign messages. Opening it changes nothing; the patient confirms with one tap |
| `/verify`, `/verify/prescription/{token}`, `/verify/{lab\|radiology\|cardiology}/{code}` | no | Check that a printed prescription or report is genuine. Shows authenticity details only, never results |
| `/privacy` | no | Privacy notice (DPDP Act 2023) in plain language, with the grievance officer and the reply period from the server |
| `/my-privacy` | yes | "Your privacy": breach notices first, then see your data, correct, erase, raise a concern, your requests, your nominee, messages you get, and whom to contact |
| `/my-privacy/data` | yes | The summary of the data held about the person (DPDP Act s11): details, each kind of record with counts and dates, purpose, basis and how long it is kept, consents, who received it, service providers. Print or save as PDF |
| `/my-privacy/requests`, `/my-privacy/requests/new?type=correction\|erasure\|grievance`, `/my-privacy/requests/{id}` | yes | Requests: list, a new correction, erasure or concern (for yourself, your child, or a family member), and each request with its history, the reply, and withdraw |
| `/my-privacy/nominee` | yes | Name, replace or remove the person's nominee (DPDP Act s14) |
| `/my-privacy/notices/{id}` | yes | A personal data breach notice in full (DPDP Rules 2025, rule 7(1)) |

### Links the server sends or prints

| Link | Page | Token handling |
|---|---|---|
| `{portal.baseUrl}/portal/feedback/{token}` | `/feedback/{token}` | Kept in this tab's session storage and replaced by `_` in the address bar before the page renders |
| `{portal.baseUrl}/portal/feedback/form/{token}` | `/feedback/form/{token}` | Stays in the address bar. It names a form, not a person, and kiosks bookmark and reload it |
| `{portal.baseUrl}/portal/unsubscribe/{token}` | `/unsubscribe/{token}` | Same as the personal feedback link |
| `{portal.baseUrl}/portal/doctors/{practitionerId}` | `/doctors/{practitionerId}` | No token |
| `{portal.baseUrl}/portal/telehealth/{sessionId}?t={token}` | `/telehealth/{sessionId}` | Moved from `?t=` to this tab's session storage. The address bar keeps the session id only |
| `{portal.baseUrl}/portal/verify/prescription/{token}` | `/verify/prescription/{token}` | Stays: a printed QR code is meant to be shared, and the answer holds no personal data |
| `{portal.baseUrl}/portal/verify/{kind}/{code}` | `/verify/{kind}/{code}` | Same as the prescription check |

## APIs used

| Area | Endpoints |
|---|---|
| System | `GET /api/v1/system/info` (hospital name, and whether the portal and video consultations are switched on), `GET /health/live` (connection check) |
| Profiles | `GET /api/v1/portal/profiles` |
| Family on other numbers | `GET /api/v1/portal/family/` → `{linked[{linkId, patientId, fullName, uhid, relation, source, linkedAt}], sharedWith[{linkId, patientId, patientName, accountPhoneMasked, relation, linkedAt}]}`; `POST /api/v1/portal/family/codes {uhid, phone, relation}` → `{codeId, expiresAt}` (the same answer whether or not they match; 422 `portal.family_code_throttled`, `portal.family_own_number`; 409 `portal.family_already_linked`); `POST …/codes/{codeId}/verify {code}` → the relative (400 on a wrong code, 422 `portal.family_code_expired` once it is used up); `POST …/links/{linkId}/end` → 204 |
| Doctors (public) | `GET /api/v1/public/doctors/filters`, `GET /api/v1/public/doctors`, `GET /api/v1/public/doctors/{id}`, `GET /api/v1/public/doctors/{id}/slots` |
| Appointments | `GET /api/v1/portal/appointments?scope=upcoming\|past`, `GET …/{id}`, `POST /api/v1/portal/appointments`, `POST …/{id}/cancel`, `POST …/{id}/reschedule`, `POST …/{id}/payment`, `POST …/{id}/payment/confirm` |
| Waiting list | `GET /api/v1/portal/waitlist`, `POST /api/v1/portal/waitlist`, `POST /api/v1/portal/waitlist/{id}/cancel` |
| Video | `GET /api/v1/portal/telehealth/sessions`, `GET …/sessions/{id}/join`, and hub `/hubs/telehealth` |
| Reports | `GET /api/v1/portal/reports?patientId=`, `GET /api/v1/portal/reports/{lab\|radiology\|cardiology}/{id}`, `GET …/{id}/pdf` (the id is the lab report id, or the scan or heart test's study id) |
| Prescriptions | `GET /api/v1/portal/prescriptions?patientId=`, `GET …/{id}`, `GET …/{id}/pdf`, `GET /api/v1/portal/refills`, `POST /api/v1/portal/refills` |
| Bills | `GET /api/v1/portal/bills?patientId=`, `GET …/{id}`, `GET …/{id}/pdf`, `POST …/{id}/pay`, `POST …/{id}/payments/{orderRowId}/verify` |
| Records | `GET /api/v1/portal/health-records?patientId=`, `GET /api/v1/portal/discharge-summaries/{id}`, `GET …/{id}/pdf`, `POST /api/v1/portal/record-requests` |
| Feedback | `GET /api/v1/portal/feedback/forms`, `GET /api/v1/portal/feedback?patientId=`, `POST /api/v1/portal/feedback` |
| Preferences | `GET` and `PUT /api/v1/portal/communication-preferences` |
| Privacy | `GET /api/v1/portal/privacy/status?patientId=`, `GET …/summary?patientId=`, `GET …/requests?patientId=`, `GET …/requests/{id}`, `POST …/requests`, `POST …/requests/{id}/withdraw`, `GET`, `PUT` and `DELETE …/nominee`, `GET …/notices?patientId=`, `GET …/notices/{id}`; public `GET /api/v1/public/privacy` (see [privacy.md](privacy.md)) |
| Public links | `GET` and `POST /api/v1/public/feedback/invitations/{token}`, `GET` and `POST /api/v1/public/feedback/forms/{token}[?kiosk=true]`, `GET` and `POST /api/v1/public/unsubscribe/{token}` |
| Document checks | `GET /api/v1/public/prescriptions/verify/{token}`, `GET /api/v1/{lab\|radiology\|cardiology}/reports/verify/{code}` |

## When the hospital turns the portal or video off

The hospital can turn the patient portal or video consultations off ([features.md](features.md)). The portal reads
`features` from `GET /api/v1/system/info` (asked again every five minutes) and records any answer refused with
`feature.disabled` (`onFeatureDisabled` of the API client), so a switch turned while a page is open is noticed too.

* **Portal off.** Sign-in, every signed-in page and the doctor directory show "The patient portal is not available
  right now. Please contact the hospital.", say that QR checks and SMS links still work, and offer "Try again", which
  asks the server once more. Document checks, the privacy notice, feedback and unsubscribe links, and video join links
  keep working.
* **Video off.** The menu, the home page and the sign-in page leave video consultations out, the directory offers no
  video, and the video pages and the SMS join link say "Video consultations are not available right now". A video
  appointment can then only be cancelled from the portal; the page asks the patient to contact the hospital about it.
* A video call already on screen is never cut off by either switch.

## Video consultations

The page uses `createHubClient('/hubs/telehealth', …)` from `@hc360/core` (the official SignalR client). A signed-in
patient calls `JoinAsPatient(sessionId)`, and a link holder calls `JoinAsGuest(sessionId, token)`. Both then use `Leave`,
`SendAnswer`, `SendIceCandidate`, `SendChat`, `ChangeMediaMode` and `ReportQuality`. The page listens for `admitted`,
`participantJoined`, `participantLeft`, `offer`, `answer`, `iceCandidate`, `chatMessage`, `mediaModeChanged`,
`sessionUpdated` and `sessionEnded`.

The signalling contract with the staff console (`modules/video/PatientCall.ts`):

* The doctor sends the offer when the patient is admitted, and again whenever either side reconnects. The doctor is
  the polite peer. The patient never offers; it answers every offer.
* Offers and answers carry raw SDP text. ICE candidates carry JSON of `RTCIceCandidateInit`.
* An offer from a new doctor connection, or after this side joined again, gets a fresh `RTCPeerConnection`.
* After a hub reconnect the page joins again, and the doctor then sends a fresh offer.

Before joining, a device check covers camera and microphone permission, choice and preview. It explains every failure
in plain words: permission denied, in use, missing, an insecure page, or an in-app browser. The patient can join
audio-only, switch cameras, or drop to audio when the connection is poor. The screen stays awake during the call.

## Payments

* **Bills.** `POST …/pay` creates a gateway order, and the page opens that gateway's checkout.
  * **Razorpay:** Standard Checkout, loaded only when the patient taps Pay.
  * **Hosted payment link:** the page opens the link and remembers the order in this tab.
  * **Training gateway (simulator):** the order is already captured.

  After any of these, `POST …/payments/{orderRowId}/verify` asks the server to confirm with the gateway server to
  server. The check is safe to repeat ("Check payment status"). The portal never handles card numbers or UPI PINs.
* **Appointments.** Paying online uses `…/payment`, then opens the gateway the same three ways as a bill:
  * **Razorpay:** Checkout, then `…/payment/confirm` with Razorpay's order id, payment id and signature.
  * **Hosted payment link:** the page opens the link and remembers the appointment in this tab. Back on the
    appointment's page, `…/payment/verify` confirms it with the gateway, server to server. While the gateway has not
    reported it yet, the page says so and offers "Check payment status"; the Pay button stays hidden meanwhile.
  * **Training gateway (simulator):** confirmed with `…/payment/verify`.

  A payment problem never loses the booked slot.
* **When the page is closed after paying.** The patient is still credited. The gateway's webhook, or the server's
  re-check every few minutes, records the bill's receipt or confirms the booking and receipts its advance; the bill
  or appointment shows it the next time the patient looks. Receipts are dated when the gateway took the money, not
  when HC360 heard of it.
* **Refunds** of online payments go back to the card or UPI account the patient paid from. The hospital follows each
  one until the bank confirms it (usually 5 to 7 working days) and can give the patient the bank's reference.

## Printing and downloads

* **Download PDF.** Reports, prescriptions, discharge summaries and bills come with the hospital's official PDF. It is
  the same English document the hospital prints and sends, with the letterhead, signatures and QR check. The page
  fetches it from the document's `…/pdf` endpoint and saves it under the file name the server gives
  (Content-Disposition). This works on Android Chrome and on iPhone Safari, which asks first. The button says
  "Preparing your PDF…" while the server makes the PDF, and takes one tap at a time. When the server cannot make PDFs
  right now (503, code `pdf.unavailable`), the page says so in the patient's language and offers Print instead. Other
  failures show the usual error message. An answer that is not a PDF is never saved.
* **Print.** Every document can also be printed from the page itself, in the language the patient is reading. Reports
  and prescriptions carry their verification QR code. Next to the buttons, one line explains the difference; in Hindi
  it also says that the PDF is in English.
* **Documents without a server PDF.** Receipts and the privacy data summary are printed from the page, and "Save as PDF"
  in the print dialog gives a PDF. The data summary can also be downloaded as a self-contained HTML file, marked with
  the language it is in.

Every value in a page template is escaped. The QR code SVG is the only markup accepted, and it is checked first.

## Security and privacy

* **Tokens.** The access token lives in memory. The refresh cookie is HttpOnly. Personal link tokens are taken out of
  the address bar at once.
* **Ids in addresses.** Addresses carry only opaque ids. Ids from the address are checked as GUIDs before any request.
* **Logs.** Nothing about patients or tokens goes to the console or to analytics (the portal has no analytics).
* **Redirects.** `?redirect=` accepts same-app paths only.
* **Payment pages.** Only `https` payment pages are opened.
* **After sign-out.** The query cache is cleared on sign-out and on a family switch. After 20 minutes without activity
  the patient is signed out.
* **Family codes.** The code goes only to the relative's own registered number, and the first answer is the same
  whether or not the UHID and the number match, so the page cannot be used to find out whose number is whose. The code
  field has no one-time-code autofill, because the code arrives on the relative's phone. The server stores codes
  hashed, allows 5 tries per code and 10 minutes, and limits codes per account (5 an hour) and per number (3 in 15
  minutes). Tries of one code, and code requests for one account or number, take turns (a PostgreSQL advisory lock
  held until each is saved), so requests sent together cannot get past these limits.
* **Privacy notice.** `/privacy` covers the Digital Personal Data Protection Act 2023: what is used and why, who sees
  it, how long it is kept, and the rights to access, correct, withdraw consent and complain.

## Tests

`vitest` covers:

* **The dictionary:** both languages complete, no empty text, the same placeholders, no key in two sets.
* **Formatting:** IST dates and Indian digit grouping.
* **OTP helpers**, **safe redirects**, **family resolution**, **link tokens** and **payment helpers**.
* **Slots and date strip**, **lab ranges and flags**, **camera errors and join problems**.
* **Document-check input parsing** and **feedback rules**.
* **Print escaping:** bill, receipt, discharge summary and the privacy data summary.
* **Downloads:** the PDF helper (the server's file name from Content-Disposition, safe names, "cannot make PDFs right
  now" without retries, other failures, never saving a web page as a PDF); Download PDF end to end in jsdom on the lab,
  scan and heart test reports, the prescription, the discharge summary and the bill (the busy button, the 503 message,
  the usual error message, Hindi, printing the page's copy); and the downloaded HTML copy (its font rule and language).
* **Family on other numbers:** the add form's checks and the request it sends, how long the code works on a phone with
  a wrong clock, the server's refusals in the patient's language, the add sheet in jsdom (the details step, the code
  step, a wrong code, a used-up code back to the details, throttling), and the page end to end in jsdom (the link from
  the profile, both lists, removing a relative).
* **The privacy centre:** request-form checks and the body it sends, server errors mapped to fields, retention in the
  patient's language from the server's periods, and the pages end to end in jsdom (hub, data summary, a new request,
  a request's detail and withdrawal, the nominee, a breach notice).
* **WebRTC codecs and quality.**
* **The feedback form in jsdom:** stars, the 0–10 score, the comment, and the call-back number check.
* **Unsaved changes:** the add-family sheet asks before *Not now* or Esc throw typed details away, and the leave
  question stays or leaves.
* **Public pages end to end in jsdom** (router, layouts and queries against a fake server). Feedback and unsubscribe
  links open without sign-in and their tokens leave the address bar. The prescription and report checks, the privacy
  notice, and the redirect of signed-out visitors to sign-in are covered too.

## Known gaps (server follow-ups)

* **Refund status in the portal.** Patients do not see where a refund stands; the hospital tells them.
* **English-only server text.** Business-rule messages (409 and 422 details, validation messages) arrive in English
  only and are shown as sent.
* **Official PDFs in English.** The hospital's PDF of a report, prescription, discharge summary or bill is the English
  document it prints and sends. Patients reading in Hindi are told so next to the button, and Print gives them the
  page's copy in Hindi. Receipts and the privacy data summary have no server PDF; they are printed from the page.
