# Patient Portal (web app)

Project: `src/web/apps/portal` · package `@hc360/portal` · served at `/portal/` by the HC360 server, on an address of its
own: port 7361 (`HealthCare360:PortalHttpsPort`), where the staff app's is 7360. That address serves the portal and its
API only, and the staff app's address sends `/portal/...` there (SECURITY.md, "Separate addresses"). The build goes to
`src/server/HyFib.HealthCare360.Server/wwwroot/portal`, and the server's fallback `/portal/{**path}` returns its
`index.html`. It covers the SRS Patient Portal requirements **PP-01..06**, patient-side telehealth (**TEL-01..05**)
and the public checks of printed documents (**ERX-04**, **LIS-07**).

It is mobile-first, in English, Hindi and Tamil (switchable at any time) and built to WCAG 2.2 AA: 44 px or larger
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
| `src/i18n` | `messages/<feature>.ts` (English, Hindi and Tamil side by side), formatters in hospital time (IST) with Indian digit grouping |
| `src/lib` | API client (`clientType: 'portal'`), session, errors, payments, printing, PDF downloads, link tokens |

Commands (run from `src/web`): `pnpm --filter @hc360/portal dev` (port 5361, proxies `/api`, `/hubs` and `/health` to
`HC360_PORTAL_SERVER` or `https://localhost:7361`, the portal's address of the local server), `build`, `typecheck`, `test`,
and `npx eslint apps/portal`.

## Sign-in, session and family members

* **Anti-automation (SRS SEC-06).** Asking for a code, giving it, and sending feedback from a poster, kiosk or personal
  link need a proof of work (`src/lib/proofOfWork`). The page asks `POST /api/v1/public/challenge` for a challenge as
  soon as the form appears, solves it in a Web Worker with WebCrypto (`solver.worker.ts`; SHA-256 with 14 leading zero
  bits by default, well under a second on a mid-range phone), and sends the answer in `X-HC360-Proof` with the request.
  Nothing shows unless it takes longer than 1.5 seconds; then a polite status says "Checking your browser…". When the
  server refuses a proof (403 `request.check_failed`, for instance because it asks more work of the number after
  failures), the page solves a fresh challenge, for the mobile number when it asks for a code, and tries once more. The
  challenge solved when the sign-in form appeared cannot name the number, so for a number that failed lately it is too
  easy: the server refuses it without counting that against the patient's address or number, and the retry gets in.
  Giving the code sends no number: its work depends on the address only, and the server finds the number from the
  challenge when a code is wrong. There is no puzzle and no third party. The server side, its settings and the
  reasoning behind the difficulty are in SECURITY.md, "Anti-automation on the portal".

* **OTP sign-in.** `auth.requestOtp` → `POST /api/v1/auth/otp/request`, then `auth.verifyOtp` →
  `POST /api/v1/auth/otp/verify`. The page never says whether a number is registered. The resend button backs off
  (30 s, 60 s, 2 min, then 5 min), so patients do not hit the server limit. A code that seems to expire implausibly (a
  wrong phone clock) falls back to a five-minute countdown.
* **A link sent to the patient's number (PP-01).** Staff send a link to the portal from the patient's record (the staff
  console's "Portal access" tab, "Send portal link") by SMS, WhatsApp or both, to the mobile number registered for the
  patient: `{portal.baseUrl}/portal/link/{token}`. The page (`/link/{token}`) shows the hospital and the number masked
  and changes nothing when it opens. "Send code" asks for a code through the link (`POST
  /api/v1/public/portal-links/{token}/otp`, with the same proof of work as the sign-in page's request and its limit of
  three codes per number in 15 minutes); the code goes to the same registered number, and the patient enters it in the
  sign-in page's own code step (`modules/signIn/CodeStep`, shared with `/sign-in`), checked by
  `POST /api/v1/auth/otp/verify`. Once signed in, the patient is where the sign-in goes (home) and the page forgets the
  token. The link itself signs nobody in: whoever holds it can only send a code to the patient's own number. A link
  works for 7 days, until a newer one is sent, and only while the record still has the number it went to and is in no
  protected category; anything else shows "This link no longer works" with a button to the normal sign-in. Someone
  already signed in on the phone goes straight to home.
* **A change of the registered number (PP-01).** The portal signs in with the mobile number on the patient's record, so
  staff cannot change it alone: the edit (`PUT /api/v1/patients/{id}`) refuses a new number (400 on `phone`,
  `patient.phone_needs_code`). The staff console's edit screen shows the number read-only with "Change number", which
  sends a 6-digit code by SMS to the new number (`POST /api/v1/patients/{id}/phone-change`, template
  `patients.phone_code`); nothing goes to the old number. The number changes when staff enter the code the patient reads
  out (`POST …/phone-change/{changeId}/confirm`). In the same transaction the links sent to the old number stop; the
  family members the old number's account saw move to the new number's account when no other patient in use keeps the
  old number (a relative the new number sees already, or the patient themself, ends instead); and when the old number
  then reaches no patient at all, its portal account is signed out everywhere and forgets the patient's name, so whoever
  gets the number next (a recycled SIM) sees nothing of the patient.
* **Tokens.** The access token stays in memory (core session). The refresh token is the server's HttpOnly cookie. The
  portal and the staff console have addresses of their own, but browsers send a host's cookies to all its ports, so each
  keeps its own refresh cookie and each address reads only its own app's: signing in to one never signs the other out,
  and nothing on the portal's address can renew a staff session.
* **Family.** `GET /api/v1/portal/profiles` returns everyone registered with the verified number, then the family
  members on other numbers that the account sees through a family link (below). A date of birth worked out from the
  age given at registration (`isDateOfBirthEstimated`) is shown as estimated, not as the patient's birthday. The patient chooses whose records to
  see, and the switcher is always in the header. Every query key starts `['portal', patientId, …]`, and family-wide
  lists are filtered to the person being viewed. A record that belongs to another family member shows "This belongs
  to …" with a switch button.
* **Protected records on a shared number.** A record in a protected category (HIV, mental health, MTP, sexual violence)
  is left out of the profiles, and of every portal endpoint (they all go through `PortalPatientScope`), when another
  patient is registered with the same number, and always for a family member on another number. The patient sees it
  when signed in with a number that is theirs alone; otherwise the hospital gives it in person (`SharedNumberTests`).
* **Family on other numbers (PP-01).** A family member with their own mobile number joins the account in one of two
  ways. In the portal (`/profile/family`), the account holder enters the relative's UHID, mobile number and relation;
  the server sends a 6-digit code to that number when the UHID and the number belong to the same active patient, and
  the relative agrees by passing the code on. At the front desk, staff check both people's identity documents, record
  who agreed (the patient or their guardian), and ask to share the records with the family member's number (the staff
  console's "Portal access" tab on the patient's profile): a 6-digit code goes by SMS to the patient's own registered
  number (`portal.family_code_desk`), and the link is made only when staff enter it as the patient reads it out
  (MAKER-CHECKER.md, slice 14). A patient with no mobile number of their own cannot share this way. Either side can end a link: the account holder removes the
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
| `/link/{token}` | no | A link to the portal sent to the patient's registered number: the hospital and the number masked, "Send code", then the sign-in page's code step |
| `/home` | yes | Greeting; video consultation ready to join; quick actions; next appointment; amount due; recent reports and prescriptions |
| `/doctors`, `/doctors/{practitionerId}` | no | Find a doctor and book. The list takes `?q=`, `?specialtyId=`, `?facilityId=`, `?language=` and `?video=true`. The doctor page takes `?mode=video`, `?branch=`, `?reschedule={appointmentId}` and `?slot=`. Choosing a time while signed out goes to sign-in and returns to the same time |
| `/appointments`, `/appointments/{id}` | yes | Upcoming and past, waiting list, pay, reschedule, cancel, join video |
| `/video`, `/video/{sessionId}` | yes | Video consultations: device check, waiting room, call, chat, audio-only |
| `/telehealth/{sessionId}?t={joinToken}` | no | The SMS or WhatsApp join link (guest join) |
| `/reports`, `/reports/{lab\|radiology\|cardiology}/{id}` | yes | Lab values with ranges and flags in plain words, and radiology and cardiology text. Download PDF (the hospital's official copy) or print |
| `/prescriptions`, `/prescriptions/{id}` | yes | Prescriptions with verification QR code; Download PDF or print; refill requests. Where the hospital requires the doctor's DSC (ERX-04) and the prescription has no valid one yet, it shows no print, PDF or QR code and says, in English and Hindi, that the doctor has not digitally signed it yet; a PDF the server refuses for that reason is worded by its code in the patient's language |
| `/bills`, `/bills/{id}` | yes | Bills, amount due, online payment; Download PDF or print a bill; receipts (print); refunds, where each stands and the reference to trace it by |
| `/records`, `/records/discharge/{id}`, `/records/request` | yes | Health summary, discharge summaries (Download PDF or print), request a copy of records |
| `/my-feedback`, `/my-feedback/{formId}` | yes | Feedback forms and the patient's feedback with complaint status |
| `/profile`, `/profile/preferences` | yes | Details, family, language, SMS and WhatsApp choices, sign out. "Messages on WhatsApp" records the number's WhatsApp opt-in (or opt-out, as a STOP would), and lifts a STOP the number sent; it can be changed only when signed in with the patient's own number, and is shown read-only, with the reason, to a family member on another number |
| `/profile/family` | yes | Family on other numbers: the relatives the account sees (Remove), add a relative with a code sent to their number, and the accounts that see the records of someone on this number (Stop sharing) |
| `/feedback/{token}` | no | Personal feedback link (SMS after a visit or stay) |
| `/feedback/form/{token}` | no | QR poster form. Add `?kiosk=true` on a kiosk tablet, which then resets itself after each response |
| `/unsubscribe/{token}` | no | Opt-out link in campaign messages. Opening it changes nothing; the patient confirms with one tap |
| `/verify`, `/verify/prescription/{token}`, `/verify/{lab\|radiology\|cardiology}/{code}` | no | Check that a printed prescription or report is genuine. Shows authenticity details only, never results. A prescription signed with the doctor's DSC also shows the signer, whether the certificate chains to the CCA India root (a licensed CA's DSC; "the signature checks out" only then, else a warning not to rely on it as a digital signature), the certificate serial and issuer, the SHA-256 of the signed prescription (as the server re-checked them), that revocation is not checked, and a .p7s download |
| `/privacy` | no | Privacy notice (DPDP Act 2023) in plain language, with the grievance officer and the reply period from the server |
| `/rates` | no | The rate list each facility displays at its counter (Clinical Establishments Rules 2012, r.9(i)): what a patient who pays for themselves is charged, with each service's name in the facility's language too (marked with `lang`). Takes `?facilityId=` and `?q=` (a search). Download PDF (the list the counter prints) or print. Linked from sign-in and *Bills*; see billing.md, "On the portal" |
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
| `{portal.baseUrl}/portal/link/{token}` | `/link/{token}` | Same as the personal feedback link, and forgotten once the patient has signed in |
| `{portal.baseUrl}/portal/doctors/{practitionerId}` | `/doctors/{practitionerId}` | No token |
| `{portal.baseUrl}/portal/telehealth/{sessionId}?t={token}` | `/telehealth/{sessionId}` | Moved from `?t=` to this tab's session storage. The address bar keeps the session id only |
| `{portal.baseUrl}/portal/verify/prescription/{token}` | `/verify/prescription/{token}` | Stays: a printed QR code is meant to be shared, and the answer holds no personal data |
| `{portal.baseUrl}/portal/verify/{kind}/{code}` | `/verify/{kind}/{code}` | Same as the prescription check |

## APIs used

| Area | Endpoints |
|---|---|
| System | `GET /api/v1/system/info` (hospital name, and whether the portal and video consultations are switched on), `GET /health/live` (connection check) |
| Anti-automation | `POST /api/v1/public/challenge {action: otp\|feedback, subject?}` → `{required, challenge, bits, expiresAt, algorithm}`; the proof goes in `X-HC360-Proof: {challenge}:{nonce}` |
| Link to the portal (public) | `GET /api/v1/public/portal-links/{token}` → `{hospital, maskedPhone, expiresAt}`; `POST …/{token}/otp` (proof of work, action `otp`, no subject) → `{challengeId, expiresAt, maskedPhone}` as the sign-in's code request answers (422 `auth.otp_throttled` after three codes to the number in 15 minutes); 404 "This link no longer works." for any token that does not work now. Then `POST /api/v1/auth/otp/verify` |
| Profiles | `GET /api/v1/portal/profiles` |
| Family on other numbers | `GET /api/v1/portal/family/` → `{linked[{linkId, patientId, fullName, uhid, relation, source, linkedAt}], sharedWith[{linkId, patientId, patientName, accountPhoneMasked, relation, linkedAt}]}`; `POST /api/v1/portal/family/codes {uhid, phone, relation}` → `{codeId, expiresAt}` (the same answer whether or not they match; 422 `portal.family_code_throttled`, `portal.family_own_number`; 409 `portal.family_already_linked`); `POST …/codes/{codeId}/verify {code}` → the relative (400 on a wrong code, 422 `portal.family_code_expired` once it is used up); `POST …/links/{linkId}/end` → 204 |
| Doctors (public) | `GET /api/v1/public/doctors/filters`, `GET /api/v1/public/doctors`, `GET /api/v1/public/doctors/{id}`, `GET /api/v1/public/doctors/{id}/slots` |
| Rate list (public) | `GET /api/v1/public/rate-list` (the facilities that publish it), `GET /api/v1/public/rate-list/{facilityId}`, `GET …/{facilityId}/pdf` |
| Appointments | `GET /api/v1/portal/appointments?scope=upcoming\|past`, `GET …/{id}`, `POST /api/v1/portal/appointments`, `POST …/{id}/cancel`, `POST …/{id}/reschedule`, `POST …/{id}/payment`, `POST …/{id}/payment/confirm` |
| Waiting list | `GET /api/v1/portal/waitlist`, `POST /api/v1/portal/waitlist`, `POST /api/v1/portal/waitlist/{id}/cancel` |
| Video | `GET /api/v1/portal/telehealth/sessions`, `GET …/sessions/{id}/join`, and hub `/hubs/telehealth` |
| Reports | `GET /api/v1/portal/reports?patientId=`, `GET /api/v1/portal/reports/{lab\|radiology\|cardiology}/{id}`, `GET …/{id}/pdf` (the id is the lab report id, or the scan or heart test's study id) |
| Prescriptions | `GET /api/v1/portal/prescriptions?patientId=`, `GET …/{id}`, `GET …/{id}/pdf`, `GET /api/v1/portal/refills`, `POST /api/v1/portal/refills` |
| Bills | `GET /api/v1/portal/bills?patientId=`, `GET …/{id}` (with the bill's own `refunds`), `GET …/{id}/pdf`, `POST …/{id}/pay`, `POST …/{id}/payments/{orderRowId}/verify` |
| Refunds | `GET /api/v1/portal/refunds?patientId=` → `PortalRefund[]`, newest first: `refundNumber`, `billNumber`, `purpose` (`Bill`, `Deposit`, `Advance`), `amount`, `stage`, `requestedAt`, `sentAt`, `mode`, `reference`, `creditedAt`; without `patientId`, every patient the account sees; 403 `portal.patient_denied` for anyone else |
| Records | `GET /api/v1/portal/health-records?patientId=`, `GET /api/v1/portal/discharge-summaries/{id}`, `GET …/{id}/pdf`, `POST /api/v1/portal/record-requests` |
| Feedback | `GET /api/v1/portal/feedback/forms`, `GET /api/v1/portal/feedback?patientId=`, `POST /api/v1/portal/feedback` |
| Preferences | `GET` and `PUT /api/v1/portal/communication-preferences` |
| Privacy | `GET /api/v1/portal/privacy/status?patientId=`, `GET …/summary?patientId=`, `GET …/requests?patientId=`, `GET …/requests/{id}`, `POST …/requests`, `POST …/requests/{id}/withdraw`, `GET`, `PUT` and `DELETE …/nominee`, `GET …/notices?patientId=`, `GET …/notices/{id}`; public `GET /api/v1/public/privacy` (see [privacy.md](privacy.md)) |
| Public links | `GET` and `POST /api/v1/public/feedback/invitations/{token}`, `GET` and `POST /api/v1/public/feedback/forms/{token}[?kiosk=true]`, `GET` and `POST /api/v1/public/unsubscribe/{token}` |
| Document checks | `GET /api/v1/public/prescriptions/verify/{token}` (and `…/signature.p7s`), `GET /api/v1/{lab\|radiology\|cardiology}/reports/verify/{code}` |

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
* After a hub reconnect the page joins again, and the doctor then sends a fresh offer. Told to catch up without
  reconnecting (`resync`: with several servers, Redis was away), it calls `Refresh` instead: it goes into the call if
  the doctor admitted the patient meanwhile, ends it if the consultation ended, and shows the messages it missed.

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
* **Appointments.** Paying online uses `…/payment`, then:
  * **Razorpay:** Checkout, then `…/payment/confirm` with Razorpay's order id, payment id and signature.
  * **Gateway's own page, when Checkout cannot load here** (its script is blocked by the network or the browser):
    the page asks for `…/payment?hosted=true`, a payment link, opens it and remembers the appointment in this tab.
    The gateway sends the patient back to `/portal/payment-return`, which opens the appointment, and its page confirms
    the payment with `…/payment/verify`, server to server. Until the outcome is known (not reported yet, gateway
    unreachable, or offline) the page says so and offers "Check payment status", and Pay stays hidden.
  * **Training gateway (simulator):** confirmed with `…/payment/verify`.

  Before the server replaces an appointment's order with a new one, it asks the gateway what became of the old one:
  a payment taken after all is recorded (the portal is told "already paid"), and while one is still being processed,
  or the gateway cannot say, no new order is made. The order replaced is remembered, so a patient who pays it after all
  (the first checkout left open on another tab) is credited: the booking's own payment when nothing else paid it,
  otherwise a second payment that the hospital refunds once a second person approves. A payment problem never loses the
  booked slot.
* **Return page.** `/portal/payment-return` is where the gateway sends patients back from its own page: set
  `integrations.payment.paymentLinkCallbackUrl` to `https://<portal address>/portal/payment-return`. It opens the
  bill or appointment this tab was paying for; anyone else (for example a patient who paid a link the hospital sent
  by SMS) is thanked, as the gateway's webhook records their payment anyway.
* **When the page is closed after paying.** The patient is still credited. The gateway's webhook, or the server's
  re-check every few minutes, records the bill's receipt or confirms the booking and receipts its advance; the bill
  or appointment shows it the next time the patient looks. Receipts are dated when the gateway took the money, not
  when HC360 heard of it.
* **Refunds.** The Bills page lists the patient's refunds, newest first, and a bill's page lists that bill's own. Each
  shows the amount, what it is for (a bill, an IP deposit or an unused advance), its number, where it stands in words,
  and the reference to trace it by. The stages are:

  | Stage | What the patient reads | Reference shown |
  |---|---|---|
  | Asked for | the date, and that a second person checks every refund before it is paid | none |
  | Approved | that the hospital will pay it soon | none |
  | Not approved | to ask at the billing counter (the reason is a staff note and is not sent) | none |
  | Paid | paid in cash at the counter, or by bank transfer, card reversal, UPI, cheque or wallet, with the date | the counter's (transfer, card-reversal, UPI or cheque reference); none for cash |
  | Kept as advance | kept as an advance on the account, with the date | none |
  | With your bank | sent back through the payment gateway to the card or account that paid, with the date; banks usually take 5 to 7 working days | the bank's (ARN, RRN or UTR) once the gateway gives it |
  | Credited | the date the bank credited it | the bank's, "your bank can trace the refund with it" |
  | Bank could not credit | that the hospital will send it again or pay it another way (also when sending it again stopped part way); a refund sent again shows as *With your bank* | none |
  | Paid again | that the payment did not reach the patient and a new refund pays it; the new refund is listed on its own | none |

  Staff names, the approver's remarks, the gateway's own refund id and its error text are never sent to the portal.

## Printing and downloads

* **Download PDF.** Reports, prescriptions, discharge summaries and bills come with the hospital's official PDF. It is
  the same document the hospital prints and sends, with the letterhead, signatures and QR check, and its labels in the
  patient's language (below); the prescription is in English with the patient's Tamil, or Hindi for everyone else. The page fetches it from the
  document's `…/pdf` endpoint and saves it under the file name the server gives (Content-Disposition). This works on
  Android Chrome and on iPhone Safari, which asks first. The button says "Preparing your PDF…" while the server makes
  the PDF, and takes one tap at a time. When the server cannot make PDFs
  right now (503, code `pdf.unavailable`), the page says so in the patient's language and offers Print instead. Other
  failures show the usual error message. An answer that is not a PDF is never saved.
* **Print.** Every document can also be printed from the page itself, in the language the patient is reading. Reports
  and prescriptions carry their verification QR code. Next to the buttons, one line explains the difference.
* **Documents without a server PDF.** Receipts and the privacy data summary are printed from the page, and "Save as PDF"
  in the print dialog gives a PDF. The data summary can also be downloaded as a self-contained HTML file, marked with
  the language it is in.

Every value in a page template is escaped. The QR code SVG is the only markup accepted, and it is checked first.

## The server's text in the patient's language (PP-06)

The portal sends the language the patient chose in it with every call, the OTP sign-in included, as
`X-HC360-Language: en|hi|ta` (`getLanguage` of `createApiClient`, the `language` argument of `auth.requestOtp` and
`auth.verifyOtp`). For portal, public and OTP requests, and any request with a patient's sign-in, the server writes
its own text in this order:

1. the language chosen in the portal (`X-HC360-Language`): what the patient sees on screen wins, so a patient whose
   record says Hindi but who reads the portal in English gets English refusals and PDFs;
2. without a choice in the request (a call with a patient's token from elsewhere), the preferred language on the
   signed-in patient's record when it is English, Hindi or Tamil ("Hindi", "hi", "हिन्दी", "Tamil", "தமிழ்"), read once
   per patient every five minutes;
3. Accept-Language;
4. English.

What the server sends on its own has no request: a document attached to a WhatsApp, SMS or e-mail message follows
the record's preferred language, else English.

* **Refusals.** The Problem Details `title`, `detail` and field messages of a refusal (400, 409, 422 and the others)
  are in that language, and the answer says so (`Content-Language: hi`). The `code` and the field names never change,
  so the portal's own handling of codes is unaffected. A message without a translation would be sent in English; a
  test keeps that from happening (below). The portal shows business-rule messages as sent, so a Hindi or Tamil reader
  sees the server's reason in their language; transport and status problems (no connection, 401, 403, 404, 429, 5xx)
  are still worded by the portal itself.
* **Field checks.** A field left empty, too long or holding a value not offered gets the validators' own message,
  worded in Hindi and Tamil without the field's name, because the portal shows it next to the field: "यह जानकारी
  ज़रूरी है।", where the library's own Hindi would name the field in English ('Patient Id'). These are the
  `validator.*` texts, which `PatientValidatorMessages` gives the FluentValidation library for those languages. Staff,
  and English readers, keep the library's own English.
* **PDFs.** `…/pdf` answers in the same language as the request, so in the portal's (see print-templates.md, *The
  patient's language*).
* **Texts.** One catalogue per language in `src/server/HyFib.HealthCare360.Platform/Localization/Resources`
  (`patient-texts-en.json`, `patient-texts-hi.json`, `patient-texts-ta.json`); a server message is matched on its
  English text. The Hindi and Tamil are listed for review by native speakers in
  [../requirements/hindi-review.md](../requirements/hindi-review.md) and
  [../requirements/tamil-review.md](../requirements/tamil-review.md).
* **Kept complete by a test.** `PortalMessageTests` (architecture tests) finds the portal's endpoints from the server
  itself (every route under `/api/v1/portal`, `/api/v1/public` and `/api/v1/auth/otp`, and every endpoint the patients'
  portal permission opens) and fails when anything they can answer with has no Hindi or Tamil entry:
  * every rule of the requests they check, in each patient language;
  * every message their code can return: it follows the compiled code from each endpoint through every call that can
    carry a refusal back (an `Error`, a `Result` or an HTTP result), and reads each message at its source line, every
    branch of it included;
  * the title of each kind of refusal, "not found", "changed by someone else", and the answers of a switched-off
    feature.

  A message the code reaches but that is never sent to a patient (the portal pays a bill by its id, so the
  IP-advance branches of the same method never run for it) is on a short reviewed list in the test, with its reason.
  The test's output lists the few messages built at run time that it cannot read.

## Security and privacy

* **Tokens.** The access token lives in memory. The refresh cookie is HttpOnly. Personal link tokens are taken out of
  the address bar at once.
* **Ids in addresses.** Addresses carry only opaque ids. Ids from the address are checked as GUIDs before any request.
* **Logs.** Nothing about patients or tokens goes to the console or to analytics (the portal has no analytics).
* **Redirects.** `?redirect=` accepts same-app paths only.
* **Payment pages.** Only `https` payment pages are opened.
* **After sign-out.** The query cache is cleared on sign-out and on a family switch. After 20 minutes without activity
  the patient is signed out.
* **Links to the portal.** A link is not a credential: it only lets whoever opens it send a sign-in code to the
  number registered for the patient, which signs them in only if they hold that phone. The server keeps only the
  SHA-256 of the token (128 random bits), and its answers carry the hospital and the masked number only. Unknown,
  expired, replaced and changed-record links all get the same 404. Asking for a code through a link spends a proof of
  work and counts against the sign-in's limits per address and per number, and a failed request (an unknown token)
  raises the work asked of the address. The token is taken out of the address bar at once and forgotten after sign-in.
* **Changing the registered number.** A new number becomes the patient's only with a code sent to it and entered at the
  desk. Codes are stored hashed (salted with the change), work for 10 minutes and 5 tries, are judged one at a time
  under the change's row lock and used once; a number gets 3 in 15 minutes, counted under a lock on it (422
  `patient.phone_code_throttled` with `retryAfterSeconds`); a newer code supersedes a waiting one; and the change is
  made only while the record still has the number it had when the code was sent (409 `patient.phone_changed_meanwhile`
  otherwise). Both steps are on the record's history (`patient.phone_code_sent`, `patient.phone_changed`), the numbers
  masked and never the code.
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
  the usual error message, Hindi, printing the page's copy, the PDF asked for in Hindi with `X-HC360-Language`); and the downloaded HTML copy (its font rule and language).
* **Family on other numbers:** the add form's checks and the request it sends, how long the code works on a phone with
  a wrong clock, the server's refusals in the patient's language, the add sheet in jsdom (the details step, the code
  step, a wrong code, a used-up code back to the details, throttling), and the page end to end in jsdom (the link from
  the profile, both lists, removing a relative).
* **The privacy centre:** request-form checks and the body it sends, server errors mapped to fields, retention in the
  patient's language from the server's periods, and the pages end to end in jsdom (hub, data summary, a new request,
  a request's detail and withdrawal, the nominee, a breach notice).
* **Refunds:** each stage in words with its date, the bank's reference for a gateway refund and the counter's for a
  transfer (none for cash), the list with its bill on the Bills page and without it on the bill's own page, and Hindi.
* **WebRTC codecs and quality.**
* **The feedback form in jsdom:** stars, the 0–10 score, the comment, and the call-back number check.
* **Unsaved changes:** the add-family sheet asks before *Not now* or Esc throw typed details away, and the leave
  question stays or leaves.
* **A link to the portal end to end in jsdom** (`modules/signIn/linkPage.test.tsx`): the page shows the hospital and the
  masked number and asks for nothing more on opening, the token leaves the address bar, "Send code" sends a proof with
  the request, the code step signs the patient in and the token is forgotten, Hindi, and a link that no longer works
  (unknown, malformed, or stopped after the page opened) offers the normal sign-in.
* **Public pages end to end in jsdom** (router, layouts and queries against a fake server). Feedback and unsubscribe
  links open without sign-in and their tokens leave the address bar. The prescription and report checks, the privacy
  notice, and the redirect of signed-out visitors to sign-in are covered too.

## Known gaps (server follow-ups)

* **No server PDF for receipts or the privacy data summary.** They are printed from the page (*Documents without a
  server PDF*, above).
* **Hindi and Tamil review.** The server's Hindi and Tamil were written by the development team and await native
  speakers' review ([../requirements/hindi-review.md](../requirements/hindi-review.md),
  [../requirements/tamil-review.md](../requirements/tamil-review.md)).
