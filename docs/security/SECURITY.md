# HyFib HealthCare360 – security overview

HC360 holds the most sensitive data a hospital has. This document explains how it is protected, what the hospital must
do, and how to report a problem. Requirement IDs refer to `docs/requirements/SRS.md` (SEC-*, PRV-*, DED-*).

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
  - The CA fingerprint is confirmed by a person, or pre-set by IT policy, when a workstation is set up.
- The firewall opens only port 7360 (HTTPS) and mDNS, and only on Private/Domain networks. PostgreSQL listens on
  localhost only.

### Identity and sessions
- Passwords:
  - At least 12 characters.
  - Checked against a list of common passwords and the user's own name.
  - Stored with PBKDF2-SHA512 (210 000 iterations).
  - Accounts lock after 5 failures for 15 minutes.
- **Two-step verification (TOTP)**, required for administrator roles.
- Access tokens:
  - ES256-signed, valid for 10 minutes, held only in memory.
  - Refresh tokens are opaque and rotate on every use, in an HttpOnly, Secure, SameSite=Strict cookie.
  - The staff app and the patient portal share one origin but keep separate refresh cookies (`hc360_rt` and
    `hc360_prt`), so signing in to one never replaces the other's session. A cookie renews only its own app's
    sessions.
  - Re-using an old refresh token ends the whole session family.
- Sessions:
  - End after 12 hours, or after 2 hours idle.
  - Workstations **lock after 10 minutes** of inactivity; staff unlock with a PIN.
  - Too many wrong PINs end the session.
- Selected roles can be limited to **enrolled devices**:
  - Each HC360 Desktop has a device secret, protected with Windows DPAPI.
  - Administrators can revoke a device at any time.

### Authorisation and privacy
- Permission-based access: every API endpoint requires a named permission, and screens hide what a role cannot use.
- **Restricted records** (VIP, staff, high-profile patients):
  - They need an extra permission or **break-the-glass** access.
  - Break-the-glass requires a written reason, expires, and is flagged for review.
- Every access to a patient record is audited.
- Only what is needed is stored:
  - **Aadhaar**: the last 4 digits plus a keyed blind index, used for duplicate detection. The full number is never stored.
  - **ABHA numbers**: encrypted with AES-256-GCM.
  - Uploaded documents: encrypted at rest.
- Masking: lists show masked phone numbers. Logs contain no patient-identifiable data, and integration logs strip
  PHI and secrets.
- Patient links carry their secret in the address (feedback and unsubscribe tokens, verification codes). Request
  logs, error logs, traces and idempotency records hold the route template for those endpoints and for portal
  pages, for example `/api/v1/public/unsubscribe/{token}`, never the token itself (`LoggedPath`,
  `.WithSensitivePath()` on the endpoint).
- Consent capture (treatment, data sharing, SMS/WhatsApp, telehealth, research) supports the **DPDP Act 2023** notice
  and consent requirements.
- **Live updates** (SignalR) follow the same rules as the API. Every topic a screen subscribes to is checked, and
  anything else is refused:
  - Staff need the module's permission and may follow only their own facility's topics (lab, pharmacy, bed board, OT)
    and records (wards, department queues, telehealth sessions).
  - Restricted patients' topics need restricted-record access.
  - Portal users may follow only their own record and their own consultations.
  - Tokens limited to a mandatory step (MFA enrolment, password change) may follow nothing.

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
  - Content-Security-Policy: scripts only from the server. The one exception is patient-portal pages, which may load
    Razorpay Standard Checkout (`checkout.razorpay.com` and `cdn.razorpay.com` scripts, and the `api.razorpay.com`
    payment frame). Their opener policy allows the pop-ups some payment methods open.
  - X-Frame-Options through `frame-ancestors 'none'`.
  - `nosniff`, a strict referrer policy, and a Permissions-Policy that allows only camera/microphone for video consults.
  - `no-store` on API responses.
  - They apply to every response, including the staff and portal pages and their static assets, not just API calls.
- Rate limits on sign-in, OTP and public endpoints.
- Input validation on every request, with parameterised SQL through EF Core only.
- The embedded browser in HC360 Desktop:
  - Opens only the hospital server; other sites open in the default browser.
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
  kept under a new name, never deleted.
- Before start-up changes the database schema, it takes a backup and checks it. When that fails, the database is not
  changed.

## 3. What the hospital must do

1. Use WPA2/WPA3-Enterprise for the clinical SSID. Keep guests on a separate network that cannot reach the server.
2. Keep the Server Node patched (Windows Update during a maintenance window), physically secured, and on a UPS.
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
