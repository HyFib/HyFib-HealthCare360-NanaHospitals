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
- **HC360 Desktop pins the hospital CA**:
  - The server's certificate must chain to exactly that CA and name the host being contacted.
  - The public Windows trust store is not consulted.
  - The CA fingerprint is confirmed by a person, or pre-set by IT policy, when a workstation is set up. A certificate
    that contradicts IT's fingerprint, or the one the server announced on the network, is refused outright.
- Browsers keep to HTTPS for a year after their first visit (HSTS).
- The firewall opens only port 7360 (HTTPS) and mDNS, and only on Private/Domain networks. PostgreSQL listens on
  localhost only.

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
- Every access to a patient record is audited.
- Only what is needed is stored:
  - **Aadhaar**: the last 4 digits plus a keyed blind index, used for duplicate detection. The full number is never stored.
  - **ABHA numbers**: encrypted with AES-256-GCM.
  - Uploaded documents: encrypted at rest.
- Masking:
  - Lists show masked phone numbers.
  - Every server log line is masked as it is written, exception text included: phone, Aadhaar and ABHA numbers keep only
    their last digits, e-mail and ABHA addresses only their domain, and tokens and passwords are removed.
  - Provider error text is masked the same way before it is stored, and integration logs strip PHI and secrets from
    payloads.
- Patient links carry their secret in the address (feedback and unsubscribe tokens, verification codes). Request
  logs, error logs, traces and idempotency records hold the route template for those endpoints and for portal
  pages, for example `/api/v1/public/unsubscribe/{token}`, never the token itself (`LoggedPath`,
  `.WithSensitivePath()` on the endpoint).
- Consent capture (treatment, data sharing, SMS/WhatsApp, telehealth, research) supports the **DPDP Act 2023** notice
  and consent requirements.
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

### Integrity and audit
- **Tamper-evident audit log**:
  - Each entry is hash-chained to the previous one.
  - Database triggers block deletes, rows inserted pre-sealed, and every update except a complete seal: hash, chain
    position and previous hash together, linked to the sealed entry before it (checked at commit).
  - Administration › Audit log › *Verify integrity* re-checks the chain. It also fails on any seal outside the chain.
- Records carry who and when on every change, and deletes are soft deletes.
- **Idempotency keys** on every write:
  - A WiFi retry replays the original result instead of creating a duplicate bill, payment or order.
  - Stored responses are encrypted, because they can contain one-time secrets.
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
- Uploaded files are accepted by their content (PDF, PNG, JPEG, WebP, DICOM), and their stored name takes the matching
  extension, so a download can never produce a program or a web page.
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

| Rule | Where | Why it is accepted |
|---|---|---|
| CA5359, certificate validation off | `ServerClient.FetchCaCertificateAsync` (Desktop) | Trust on first use: the CA it downloads is only trusted once its fingerprint matches IT policy or an administrator confirms it |
| CA5350, HMAC-SHA1 | `Signatures.*HmacSha1Base64` (Integrations) | Twilio signs its webhooks with HMAC-SHA1 |
| CA2100, SQL from text | `MigrationBaseline` (Platform) | Constant catalogue queries with parameters, and the DDL EF Core generates |
| CA5398, fixed TLS versions | `Program.cs` (server), `OutboundHttp` (calls to providers) | TLS 1.2 is the floor on purpose, because older Windows versions still allow TLS 1.0 and 1.1 |

## 3. What the hospital must do

1. Use WPA2/WPA3-Enterprise for the clinical SSID. Keep guests on a separate network that cannot reach the server.
2. Keep the Server Node patched (Windows Update during a maintenance window), physically secured, and on a UPS. Turn on
   BitLocker, with the TPM, for every disk that holds HC360 data, backups or the database: the keys that decrypt the
   stored data live on the same disk (SRS SEC-05).
3. Give each person their own account and the smallest set of roles. Review users and roles every quarter, and
   deactivate leavers the same day.
4. Turn on two-step verification for everyone who handles finance or administration, not only administrators.
5. Store the backup recovery key and the postgres password offline, in the hospital safe. Run a restore test every
   quarter.
6. Review the audit log for break-the-glass and restricted-record access every month (NABH IMS).
7. Report suspected incidents to HyFib and follow the hospital's breach-notification process under the DPDP Act.

## 4. Reporting a vulnerability

Email **security@hyfib.com** with the steps to reproduce. Please do not include real patient data. We acknowledge
reports within 2 working days and keep reporters informed until a fix is released.
