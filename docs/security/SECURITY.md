# HyFib HealthCare360 – security overview

HC360 holds the most sensitive data a hospital has. This document explains how it is protected, what the hospital must
do, and how to report a problem. Requirement IDs refer to `docs/requirements/SRS.md` (SEC-*, PRV-*, DED-*).
[ASVS-L2.md](ASVS-L2.md) checks HC360 against every OWASP ASVS 5.0 Level 2 requirement and plans the open gaps.

## 1. Threats we design for

- **Someone else on the WiFi**, e.g. a visitor's laptop that has the password, or a compromised device. They could
  sniff traffic or impersonate the server.
- **Shared workstations**: a nurses' station left signed in, or one staff member using another's session.
- **Insiders** browsing records they have no reason to see, e.g. VIP or staff patients.
- **Stolen or lost devices**: laptops, tablets, a backup disk.
- **Ransomware or disk failure** on the Server Node.
- **Retries on flaky WiFi** creating duplicate bills, payments or orders (a data-integrity risk, not only a security one).

## 2. Controls

### Network and transport
- HTTPS only (TLS 1.2/1.3). The Server Node creates a **private hospital CA** and a server certificate naming its host
  name and addresses; the certificate is re-issued automatically when the address changes.
- **HC360 Desktop and the HC360 lab agent pin the hospital CA**:
  - The server's certificate must chain to exactly that CA (through the intermediates the server sends, if any), be
    issued for a TLS server (serverAuth), and name the host being contacted.
  - The public Windows trust store is not consulted.
  - The CA fingerprint is confirmed by a person, or pre-set by IT policy, when a workstation is set up. A certificate
    that contradicts IT's fingerprint, or the one the server announced on the network, is refused outright.
  - The lab agent runs unattended, so its fingerprint is always given by IT at installation; enrolment stops if the
    server's CA does not match it. Every call the agent makes, its analyser relays included, is pinned the same way.
  - WebView2, which shows the staff app in HC360 Desktop, hands the Desktop a server certificate only when Windows
    rejects it. A certificate Windows trusts would pass unchecked: one from a company's TLS-inspection proxy, say.
    So the Desktop checks the connection with its own pinned client before it loads the staff app, and every 10
    seconds after. When it is offered a certificate that is not from the hospital CA, it unloads the staff app and
    shows "HC360 stopped: the server could not be verified" until a check passes. Between two checks (10 seconds at
    most) the staff app could still use such a connection.
- Browsers keep to HTTPS for a year after their first visit (HSTS).
- The firewall opens only port 7360 (HTTPS) and mDNS, and only on Private/Domain networks. PostgreSQL listens on
  localhost only. Network lab analysers need their own ports, which the installer opens only when asked
  (`-LabPorts`), limited to the analysers' addresses; each analyser's port also accepts only its listed IP addresses.
- **The DICOM modality worklist** (off by default) listens on its own TCP port, 11112 unless changed, for the CT, MR,
  X-ray and ultrasound consoles. DICOM there has no encryption or passwords, as on most modality networks, so other
  controls apply:
  - Only modalities on the allow-list may speak, identified by their AE title and their IP address together. A
    connection from any other address is closed before a byte is read; a wrong AE title is refused.
  - Each modality sees only its own facility's studies, of the kinds it does, within a few days of today. It is checked
    again against the settings on every query, so removing it takes effect at once, even on an open connection.
  - It answers only the worklist query and the echo test: no images, no retrieval, no storage.
  - Connections, their idle time and the size of an answer are limited.
  - Every answer writes one PHI access audit entry (which modality, from where, how many patients) before anything is
    sent; if the audit cannot be written, nothing is sent. The integration log masks the patient keys of each query,
    and DICOM data is never written to the service log.
  - Refused connections are counted, and logged at most once per ten minutes per network, so a scan cannot flood the
    logs.

  The installer does not open this port. The hospital opens it for the modalities' addresses only (DEPLOYMENT.md §6),
  and should keep it on the imaging network.

### Identity and sessions
- Passwords:
  - At least 12 characters.
  - Checked against a list of common passwords and the user's own name.
  - Stored with PBKDF2-SHA512 (210 000 iterations).
  - Accounts lock after 5 failures for 15 minutes. A locked account gets the same answer as a wrong password, so a
    response never confirms that an account exists.
- **Two-step verification (TOTP)**, required for administrator roles.
- Access tokens:
  - ES256-signed, valid for 10 minutes, held only in memory.
  - Refresh tokens are opaque and rotate on every use, in an HttpOnly, Secure, SameSite=Strict cookie.
  - The staff app and the patient portal share one origin but keep separate refresh cookies (`hc360_rt` and
    `hc360_prt`), so signing in to one never replaces the other's session. A cookie renews only its own app's
    sessions.
  - A refresh token presented after it was spent ends the whole session, and is audited as critical. A retry within
    60 seconds (a response lost on the WiFi link) is re-issued a token once. The token it replaces is retired, so if a
    copy was being replayed, the real client's next refresh ends the session for both.
- Sessions:
  - End after 12 hours, or after 2 hours idle.
  - Workstations **lock after 10 minutes** of inactivity; staff unlock with a PIN.
  - Too many wrong PINs end the session.
- Selected roles can be limited to **enrolled devices**:
  - Each HC360 Desktop has a device secret, protected with Windows DPAPI.
  - Administrators can revoke a device at any time.
  - Only HC360 Desktop kinds count (workstation, kiosk, queue display, tablet). An HC360 lab agent is also an enrolled
    device, but its secret sits on an unattended lab PC (protected with machine DPAPI, in a folder only SYSTEM,
    Administrators and the agent's service account can read), so it is refused for sign-in and for the desktop
    heartbeat. It can only relay the analysers assigned to it, which the server checks on every call and again every
    minute while a relay is open. Revoking it ends its relays within a minute.

### Authorisation and privacy
- Permission-based access: every API endpoint requires a named permission, and screens hide what a role cannot use.
- **Access is denied by default.** Every endpoint states who may call it. The exceptions to a named permission are:
  - a short list open to any signed-in user, for their own account and notifications only;
  - a short list open to anyone: sign-in, patient links, and provider and device callbacks, each checked by its own
    token, signature or key.

  An architecture test checks every endpoint and holds both lists, so a new exception is a reviewed change. A request
  whose endpoint states nothing still requires sign-in.
- **Administrators cannot hand themselves more power.** Administrative permissions (`admin.*`, and the Super
  Administrator's all-access) are:
  - given only by someone who holds them;
  - protected on accounts that hold them: only an administrator with the same administrative permissions may edit such
    an account, reset its password or two-step verification, unlock it or end its sessions. A hospital administrator
    cannot take over a Super Administrator.

  The Patient role cannot be edited, so no staff permission can reach every portal account.
- **Restricted records** (VIP, staff, high-profile patients):
  - They need an extra permission or **break-the-glass** access.
  - Break-the-glass requires a written reason, expires, and is flagged for review.
  - Lists don't name them unless the user may open restricted records. The bed board, the doctor's worklist and the
    ward round show "Restricted patient". The worklist also leaves out the complaint, booking reason, note title,
    result name and deficiency, and the round leaves out the diagnosis and vital-sign details. The token, bed, UHID
    and safety flags stay, so the patient is still seen to.
- **Patient portal scope.** A portal account sees the patients registered with the mobile number it signed in with,
  and family members on other numbers only through a family link in force (PP-01):
  - A link is made at the front desk, after staff check both people's identity documents and record who agreed, or in
    the portal with a 6-digit code sent to the relative's own registered number.
  - Codes are stored hashed and work for 10 minutes and 5 tries. Each account may ask for 5 an hour and each number may
    receive 3 in 15 minutes. Tries and requests sent at the same time take turns, so they cannot get past these limits.
    Asking gives the same answer whether or not the UHID and the number match.
  - The patient, the account holder or staff can end a link, and it stops on the next request. Making and ending links
    are audited, and a patient merge or the erasure that closes a portal account carries them along or ends them.
  - Signing in counts as proof of identity (for a privacy request, say) only for patients on the account's own number.
- Every access to a patient record is audited.
- **Radiology images** from PACS reach the browser only through the server, as JPEG or PNG within a size limit, for
  people with `radiology.images.view` (doctors, radiographers, radiologists). PACS credentials stay on the server. A
  study's images are found by the study UID in HC360's database, and only series and objects PACS lists for that study
  are served, so naming another study's UIDs reaches nothing. Responses are never cached, opening a study's images is
  audited, and each person has a limited number of images in flight. A study is linked with PACS, or opened in the
  PACS viewer before linking, only when PACS holds exactly one study under its accession number with the patient's
  UHID as patient ID, so a mistyped accession at a modality never shows another patient's images.
- Only what is needed is stored:
  - **Aadhaar**: the last 4 digits plus a keyed blind index, used for duplicate detection. The full number is never stored.
  - **ABHA numbers**: encrypted with AES-256-GCM.
  - Uploaded documents: encrypted at rest.
  - **Online payments**: the gateway's settlement data is kept as ids, amounts, the UTR and the payment method only;
    the card network, issuer and type, descriptions and notes are dropped. Webhook events are kept by event id with
    the gateway's ids, never the body, which carries the patient's contact and card or bank details.
- Masking:
  - Lists show masked phone numbers.
  - Every server log line is masked as it is written, exception text included: phone, Aadhaar and ABHA numbers keep only
    their last digits, e-mail and ABHA addresses only their domain, and tokens and passwords are removed.
  - Provider error text is masked the same way before it is stored, and integration logs strip PHI and secrets from
    payloads.
  - PostgreSQL logs statements slower than two seconds for tuning, but never their parameter values
    (`log_parameter_max_length = 0`, set by `Install-PostgreSQL.ps1`): those can be patient details.
- Patient links carry their secret in the address (feedback and unsubscribe tokens, verification codes). Request
  logs, error logs, traces and idempotency records hold the route template for those endpoints and for portal
  pages, for example `/api/v1/public/unsubscribe/{token}`, never the token itself (`LoggedPath`,
  `.WithSensitivePath()` on the endpoint).
- Consent capture (treatment, data sharing, SMS/WhatsApp, telehealth, research) supports the **DPDP Act 2023** notice
  and consent requirements. Withdrawing is as easy as giving (s6(4)): staff withdraw a consent from the patient profile
  or the chart with a reason, and it takes effect at once where HC360 acts on it (record sharing, campaigns, feedback
  requests and reminders). Withdrawing a standing permission ends every grant of the type, so no older copy keeps it in
  force. The consent stays as history with when, by whom and why. Consents follow the restricted-record rules, and
  every listing is audited.
- **Messaging opt-outs.**
  - After a number replies STOP to the hospital's WhatsApp messages, it gets only sign-in codes on WhatsApp, whichever
    module queued the message. The check runs when each message is delivered, and the notification log records it
    as suppressed.
  - The same reply withdraws marketing consent for the patients registered with that number. Each patient's consent
    history records the change and where it came from.
  - START resumes WhatsApp messages, and gives back only the marketing consent that a STOP withdrew.
  - Each reply is acted on once, even when the webhook is delivered again, and the latest reply wins.
  - A WhatsApp reply never changes SMS. Campaign SMS carry their own opt-out link.
- **Live updates** (SignalR) follow the same rules as the API. Every topic a screen subscribes to is checked, and
  anything else is refused:
  - Staff need the module's permission and may follow only their own facility's topics (lab, pharmacy, bed board, OT)
    and records (wards, department queues, telehealth sessions).
  - Restricted patients' topics need restricted-record access.
  - Portal users may follow only their own record and their own consultations. They never join a facility's channel,
    and cannot switch into a facility.
  - Tokens limited to a mandatory step (MFA enrolment, password change) may follow nothing and join no consultation.
  - An events connection lasts only as long as the token it opened with; a signed-out or revoked session cannot
    reconnect. (Video-consultation connections stay until the call ends.)

### Personal data (DPDP Act 2023)
The Privacy module ([privacy.md](../modules/privacy.md)) carries the hospital's duties under the Digital Personal Data
Protection Act 2023 and the DPDP Rules 2025 (in force for hospitals from May 2027):
- **Rights.**
  - Patients see a summary of the data held about them in the portal at any time: what is held, why and on what
    basis, how long it is kept, and who received it.
  - They ask for corrections, erasure or a grievance to be answered, and name a nominee.
  - The front desk records the same requests for patients without the portal.
- **Answering requests.**
  - The grievance officer's queue shows each reply-by date (the hospital's published period, 30 days by default and
    never more than 90).
  - Data is given out, changed or erased only after the requester's identity, and their authority to act for the
    patient, has been checked.
  - Every reply names whom to contact.
- **Erasure stops at the law.**
  - What can go goes at once: optional messages, the patient's name on feedback, complaints and enquiries, the
    nominee, and the portal account.
  - Medical records, bills and the audit trail are kept for their legal period (medico-legal records permanently).
  - The request records until when.
- **Breaches.**
  - The breach register starts the Board's 72-hour clock when the hospital becomes aware of a breach.
  - It records the intimation, any extension and the detailed report.
  - Every person affected is told by SMS and e-mail, and in the portal: what happened, what it may mean, what the
    hospital is doing, what they can do, and whom to contact.
- **Retention.**
  - A nightly clean-up deletes the outbox (the full text of each message, sign-in codes included) after 30 days.
  - It deletes the message, integration and sign-in logs after a year (the Rules' minimum).
  - Medical records past their 3- or 5-year period are listed for the medical records team, never deleted
    automatically.
  - Every period is on one screen, and none can be set below its legal minimum.
- **Completeness.** An architecture test fails when a table that holds a patient id is missing from the personal
  data catalogue, so the data summary cannot fall behind the database.
- **Reports sent to patients.** The "report ready" e-mail carries the report's PDF, and so does the WhatsApp message
  when its template has a document header (LIS-07). E-mail is not end-to-end encrypted. Hospitals that do not want
  reports to travel that way set `diagnostics.reports.sendPdf` to `false`, with a WhatsApp template that has no
  document header: patients are then told the report is ready and open it on the portal, after signing in with a
  one-time code. The PDFs are made when the message is sent
  and are never stored, so they add no copy of the data to keep track of.

### Integrity and audit
- **Tamper-evident audit log**:
  - Each entry is hash-chained to the previous one.
  - Database triggers block deletes, rows inserted pre-sealed, and every update except a complete seal: hash, chain
    position and previous hash together, linked to the sealed entry before it (checked at commit).
  - Administration › Audit log › *Verify integrity* re-checks the chain. It also fails on any seal outside the chain.
- Records carry who and when on every change, and deletes are soft deletes. Every change to an audited record also
  writes a data audit event with each changed value (from/to); compound values such as addresses, cardiology
  findings and TTI results are recorded part by part.
- **Idempotency keys** on every write:
  - A WiFi retry replays the original result instead of creating a duplicate bill, payment or order.
  - Stored responses are encrypted, because they can contain one-time secrets.
  - The AI endpoints' answers (dictation transcripts, SOAP drafts, interpreted commands) are never stored
    (`.WithoutStoredResponse()`). The key still stops a retry from running again, so there is no second AI call or
    quota charge. The retry gets 409 `idempotency.answer_not_kept`, and the user asks again.
- Optimistic concurrency: a record changed by someone else is never silently overwritten.

### Application hardening
- Security headers:
  - Content-Security-Policy:
    - Scripts come only from the server, and pages connect only to the server, so injected script has nowhere to send
      what it reads.
    - The one exception is patient-portal pages, which may load Razorpay Standard Checkout: the `checkout.razorpay.com`
      and `cdn.razorpay.com` scripts, the `api.razorpay.com` payment frame, and the checkout's calls to
      `api.razorpay.com` and `lumberjack.razorpay.com`. Their opener policy allows the pop-ups some payment methods open.
  - X-Frame-Options through `frame-ancestors 'none'`.
  - `nosniff`, a strict referrer policy, and a Permissions-Policy that allows only camera/microphone for video consults.
  - `no-store` on API responses.
  - They apply to every response, including the staff and portal pages and their static assets, not just API calls.
- Rate limits on sign-in, OTP and public endpoints (per address), and on everything else per signed-in user.
- Input validation on every request, with parameterised SQL through EF Core only. An endpoint that asks for validation
  never runs without it.
- Uploaded files are accepted by their content (PDF, PNG, JPEG, WebP, DICOM, SCP-ECG), and their stored name takes the
  matching extension, so a download can never produce a program or a web page.
- **Server documents** (reports, prescriptions, discharge summaries, bills, MIS reports):
  - Templates build their HTML only through an encoder that escapes every value unless it is already safe HTML, so
    data in a document (a patient's name, a doctor's note) can never become markup. Images can only be embedded as
    checked `data:` URIs.
  - Each document carries its own content security policy: no scripts, no network access.
  - The staff app gets a document as JSON and prints it, so the API never serves an HTML page.
  - PDFs are downloads (`Content-Disposition: attachment`) with `no-store`. Every print or download of a patient's
    document is written to the PHI access audit like the screen that shows the same record ("Downloaded lab report …
    as a PDF"), after the same permission, restricted-record and portal-scope checks. An MIS report that lists patients
    writes an entry for each patient in the file, as its Excel export does.
- **The PDF browser.** PDFs are printed by headless Microsoft Edge, run as the server's own service account:
  - It is controlled over private anonymous pipes, never a TCP port, so no other program on the machine can take
    control of it.
  - Scripts are switched off, and every http, https, ws, wss, ftp and file request is blocked, so a document can't
    run code or reach anything, local files included.
  - Each PDF is made in a tab of its own browser context. The browser has no extensions, no sync and no background
    networking, and it keeps its sandbox.
  - It runs in a job object that ends it, and every process it started, when the server stops. It starts inside the
    job, so none of its processes begins outside it, and it inherits only its own pipes. Its private profile folder is
    deleted.
- Calls to outside services follow no redirects and need TLS 1.2 or later.
- The API reference is off by default. If IT turns it on, it answers only on the server machine.
- The embedded browser in HC360 Desktop:
  - Opens only the hospital server; other sites open in the default browser.
  - Answers bridge calls (printing, device identity, notifications) only from the staff app's pages, not from the
    patient portal or the API docs, which run third-party script.
  - Saves only document types HC360 produces, and only from the server.
  - Denies permissions other than camera, microphone, clipboard and notifications.
  - Disables password autosave and autofill.
  - Disables DevTools unless IT enables them.
- The server runs as a dedicated virtual service account, and its data folder is readable only by that account,
  Administrators and SYSTEM.

### Backups and keys
- Nightly backups are **encrypted** (AES-256-GCM, chunked, every chunk authenticated) and optionally copied to a second
  disk or NAS.
- Each backup is one archive holding the database, the stored documents (already encrypted) and an escrow copy of the
  field encryption key. So one backup file and the recovery key restore everything on replacement hardware, including
  identifiers such as Aadhaar and ABHA, which are unreadable without that key.
- Every backup is read back in full and its database dump listed by pg_restore before it counts. A backup that fails
  this check is discarded and reported.
- The recovery key opens every backup. It is shown on request, each view is audited, and it must be stored **offline**.
- Signing and encryption keys are generated on the Server Node and protected with Windows Data Protection (DPAPI).
  A restore installs the field encryption key protected for the new machine, and a different key already there is
  kept under a new name, never deleted. The key is installed last, after the backup's database is in place. If an
  earlier step fails, the previous database is put back and the machine keeps its own key.
- Before start-up changes the database schema, it takes a backup and checks it. When that fails, the database is not
  changed.
- **Secrets in the settings file** (`config\hc360.settings.json`) are protected with Windows DPAPI for the machine:
  every connection string, which carries the database password, and every setting whose name ends in Password, Secret
  or ApiKey, such as an administrator-supplied certificate's password (SRS SEC-07). The server opens them as it loads
  the file, so a copy of the file is no use on another computer, and the file itself can be read only by SYSTEM,
  Administrators and the service. The installer protects them on every install and upgrade, and System health warns
  while one is in plain text. Integration keys are stored encrypted in the database, as before.
- **The database password is changed from System health** (SRS SEC-07), by holders of `admin.system.manage`, which
  only SuperAdmin has by default. HC360 makes a new random password that nobody sees and sends PostgreSQL only its SCRAM-SHA-256
  verifier, so the password never appears in a statement or the database's logs. The settings file is written before
  the change is committed and put back if the commit fails, so the two agree. When a failed commit cannot be checked
  because the database is out of reach, the old password is kept and the server log says how to recover. New
  connections use the new password at once, and each change is audited.
- **BitLocker** must protect every fixed disk of the Server Node (SRS SEC-05). Reading BitLocker's state needs
  administrator rights, which the service does not have, so a scheduled task checks it as SYSTEM every day and at
  start-up, and the installer checks it too. System health shows the result, and warns when a disk is not encrypted,
  when protection is suspended or unfinished, and when the check has not run for three days.

### Dependencies, secrets and code scanning
CI runs these checks on every pull request and on main. A pull request cannot merge while one fails.
- **Known-vulnerable packages fail the build**, whatever the severity, and whether the package is a direct or a
  transitive dependency:
  - NuGet audits every restore (`Directory.Build.props`).
  - `pnpm audit` checks the web apps' lockfile.
  - The weekly **Security audit** workflow runs both audits on main, so an advisory published while the code is not
    changing still fails a run.
  - An advisory reviewed as not affecting HC360 is accepted explicitly, never by lowering the level. For NuGet, add a
    `NuGetAuditSuppress` item to `Directory.Packages.props`. For npm, add the advisory under `auditConfig` in
    `pnpm-workspace.yaml`. Each entry has a comment giving the reason and the date to look again.
- **Dependabot** proposes updates every Monday: NuGet packages, the web packages, the .NET SDK (the server and HC360
  Desktop ship the runtime that comes with it) and the CI actions.
  - Minor and patch updates come grouped, and CI tests them like any change.
  - A new release waits 7 days first, long enough for a hijacked release to be caught and pulled. Microsoft's .NET
    packages and the SDK do not wait, because their monthly releases carry the security fixes.
- **CI actions** are pinned to commits, and no job keeps the checkout's token.
- **Secret scan:** gitleaks checks every commit in the history, not only the latest files.
  - Values reviewed as not secret, such as the fixed fake tokens in the tests, are listed in `.gitleaks.toml` with
    where they come from.
  - A real secret found in a commit must be revoked at its provider first. Removing it from the files is not enough,
    because the history keeps it.
- **Static analysis:**
  - **.NET:** every security analyzer rule is an error, including those off by default: native library loading, TLS
    and certificate validation, weak cryptography, insecure randomness and SQL built from text. An accepted exception
    is a `[SuppressMessage]` next to the code, with its justification; each one is listed below. Test projects are
    exempt from the SQL-text and randomness rules, because test code never ships.
  - **Web apps:** ESLint refuses code built from strings (`eval`, `new Function`, `javascript:` links). It also refuses
    HTML written straight into the page (`innerHTML`, `insertAdjacentHTML`, `document.write` and the like), where
    React's escaping does not apply. A reviewed use carries an `eslint-disable` comment saying why its markup is safe.
    The only one is the portal's in-page print, whose templates escape every value.

- **CodeQL code scanning** (`.github/workflows/codeql.yml`) analyses the server, HC360 Desktop and the lab agent (C#),
  the web apps (TypeScript) and the CI workflows, with the `security-extended` queries. It runs on every pull request,
  on main, and every Monday so that new queries reach code nobody is changing.
  - Alerts appear under Security › Code scanning. The `CodeQL` check fails a pull request that adds an alert of high
    or critical severity.
  - It needs GitHub Code Security on the repository, with CodeQL's default setup off (this workflow is the advanced
    setup).
  - Test code and generated route files are left out. For TypeScript and the workflows the filter is
    `.github/codeql/codeql-config.yml`; for C#, which CodeQL analyses as it is compiled, the workflow builds only the
    shipped projects, so the tests are left out but the database migrations, part of the server, are analysed.
  - An alert is fixed in the code. One reviewed as a false positive or an accepted risk is dismissed on GitHub with
    the reason, never hidden by turning a query off. The first run's alerts and how each was handled are in the table
    below.

| CodeQL alert | Where | How it was handled |
|---|---|---|
| Clear-text storage of sensitive information (high) | `IdentitySeeder.CreateInitialAdministratorAsync` | Accepted risk: the first administrator's one-time password goes to `initial-admin-password.txt` so the installer can show it. The data directory is open only to administrators and the service, and HC360 now deletes the file as soon as `admin` sets a new password (and at start-up if that already happened). Dismiss as "won't fix" |
| Exposure of private information (medium) | `IntegrationSettingsReader`, `IntegrationLog`, `ReportDeliveryService` | False positive: the "private data" is the constant name of the e-mail integration (`email`), not an address. Dismiss as "false positive" |
| Log entries created from user input (medium) | `ErrorHandling` (request path, correlation id), `FhirEndpoints`, `MealPlanService`, `GatewayWebhookLog` | False positive: each value is cleaned or checked before it is logged (masked path, correlation id and event id checked against a pattern, route template and printable resource type, internal error codes). Dismiss as "false positive" |

| Rule | Where | Why it is accepted |
|---|---|---|
| CA5359, certificate validation off | `ServerClient.FetchCaCertificateAsync` (Desktop) | Trust on first use: the CA it downloads is only trusted once its fingerprint matches IT policy or an administrator confirms it |
| CA5359, certificate validation off | `ServerApi.FetchCaCertificateAsync` (lab agent) | The same, at enrolment: the CA it downloads is used only if its fingerprint equals the one IT passed to the installer |
| CA5350, HMAC-SHA1 | `Signatures.*HmacSha1Base64` (Integrations) | Twilio signs its webhooks with HMAC-SHA1 |
| CA2100, SQL from text | `MigrationBaseline` (Platform) | Constant catalogue queries with parameters, and the DDL EF Core generates |
| CA5398, fixed TLS versions | `Program.cs` (server), `OutboundHttp` (calls to providers) | TLS 1.2 is the floor on purpose, because older Windows versions still allow TLS 1.0 and 1.1 |

## 3. What the hospital must do

1. Use WPA2/WPA3-Enterprise for the clinical SSID. Keep guests on a separate network that cannot reach the server.
2. Keep the Server Node patched (Windows Update during a maintenance window), physically secured, and on a UPS. Turn on
   BitLocker, with the TPM, for every disk that holds HC360 data, backups or the database: the keys that decrypt the
   stored data live on the same disk (SRS SEC-05). Administration › System health warns until every fixed disk is
   protected.
3. Give each person their own account and the smallest set of roles. Review users and roles every quarter, and
   deactivate leavers the same day.
4. Turn on two-step verification for everyone who handles finance or administration, not only administrators.
5. Store the backup recovery key and the postgres password offline, in the hospital safe. Run a restore test every
   quarter.
6. Review the audit log for break-the-glass and restricted-record access every month (NABH IMS).
7. Record a suspected personal data breach in Privacy › Data breaches as soon as it is known: the Data Protection Board
   must be told without delay, and sent the detailed report within 72 hours. Report it to HyFib as well.
8. Name the grievance officer in Privacy › Privacy settings. Register the privacy SMS templates with DLT and set their
   template ids, as for the other SMS templates (`privacy.request.received`, `privacy.request.closed`,
   `privacy.breach.notice`); without them those SMS are not sent.
9. Confirm the retention periods in Privacy › Retention with the medical records committee and a legal adviser, and
   review the charts it lists as past their period.
10. If the modality worklist is used, open its port in the firewall for the modalities' addresses only, keep it on the
    imaging network, and remove a modality from the worklist settings the day it is retired or moved.
11. Keep Microsoft Edge updating on the Server Node: it makes the PDFs, and HC360 restarts it regularly so updates apply.
    Decide whether report PDFs may go by e-mail and WhatsApp (`diagnostics.reports.sendPdf`, on by default), and say so
    in the privacy notice.

## 4. Reporting a vulnerability

Email **security@hyfib.com** with the steps to reproduce. Please do not include real patient data. We acknowledge
reports within 2 working days and keep reporters informed until a fix is released.
