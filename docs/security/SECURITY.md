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
- The firewall opens only the HTTPS ports of the staff app (7360) and the patient portal (7361), and mDNS, and only on
  Private/Domain networks. PostgreSQL listens on localhost only. Network lab analysers need their own ports, which the
  installer opens only when asked (`-LabPorts`), limited to the analysers' addresses.
- **A warm standby** (SRS DED-20, optional) is the one machine let in to PostgreSQL from the network, and only once
  `Enable-HC360Replication.ps1` names its address: `pg_hba.conf` and the firewall take the account `hc360_replicator`
  from that address alone, over TLS (`hostssl`), with a SCRAM password; the account can stream the database and open
  none of it. The standby trusts exactly the primary's own PostgreSQL certificate (`sslmode=verify-ca`). Its PostgreSQL
  listens on localhost only, and HC360 there is disabled and refuses to start on a database in recovery. The standby
  holds every patient record, so it needs the primary's physical security and BitLocker.
- **Network lab analysers** cannot sign in: HL7 (MLLP) and ASTM have no authentication. Each one's address is
  therefore its check (ASVS V13.2.1): its connection must list its fixed IP address, and its port closes a connection
  from any other address before reading a byte. A connection set up before the address was required takes any address:
  the server logs a warning when it starts listening on it, and Lab setup › Analysers shows it as *Open to any device*
  until its address is added.
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
  - Refused when they are on public breach lists, on the common-password list, or hold the user's own name, a
    facility's name or HC360's (SRS SEC-01). The breach set is built into the server, so nothing about a password
    leaves the hospital: about 800,000 passwords from the 10 million most common in public breach compilations
    (SecLists, MIT licence), rebuilt with `dotnet run tools/build-breached-passwords.cs`.
  - A staff sign-in with a password found there, set before the check existed, must change it before anything else.
    The sign-in is audited as `auth.password_breached`, and the screen says why.
  - Stored with PBKDF2-SHA512 (210 000 iterations).
- **Wrong passwords and codes** (ASVS V6.1.1, V6.3.1):
  - Each address may try to sign in, or give an authenticator code, 10 times a minute. A phone number may be sent 3
    portal codes in 15 minutes, and an address may ask for 5.
  - An account locks for 15 minutes after 5 wrong passwords or authenticator codes in a row. Each further 5 double the
    lock, up to 24 hours. A wrong code counts like a wrong password, whichever sign-in attempt it comes in, because
    whoever is guessing codes knows the password. So does a wrong password given again to confirm an action (changing
    the password, setting a PIN, two-step verification, signing out sessions, key and backup actions), because whoever
    guesses there holds a session, say on a workstation left signed in, but not the password. While the account is
    locked, no password is accepted there either.
  - Attempts sent at once, from many machines, are judged one at a time under a lock on the account, so each guess
    counts and none gets in once the account is locked. The same holds for each portal code's five tries, a session's
    five PIN tries, the three portal codes a number may be sent in 15 minutes, and the single use of an enrolment
    code.
  - Failures are forgotten after 24 hours without another, so the occasional mistyped password does not add up to ever
    longer locks.
  - A locked account gets the same answer, after the same work, as a wrong password, so a response never confirms that
    an account exists or is locked. A lock only delays its owner: it ends by itself, and an administrator can unlock
    the account from Administration › Users.
  - Settings: `HealthCare360:Security:MaxFailedLogins`, `LockoutMinutes` and `FailedLoginResetHours`.
- **Two-step verification (TOTP)** (ASVS V6.3.3):
  - Required of the SuperAdmin, HospitalAdmin, Accountant and HrManager roles, who run the hospital, its accounts and
    its payroll. Roles listed in `HealthCare360:Security:MfaRequiredRoles` are required as well; the setting adds to
    these four and cannot remove them.
  - Required of anyone holding an administrative permission (all-access, or any `admin.` permission), whatever their
    roles are called (`MfaForAdministration`), so a custom role with administrative rights needs it too.
  - Until it is set up, such an account can do nothing else after signing in, and it cannot be turned off.
- **Temporary passwords** from an administrator are 12 random characters (or the administrator's choice when creating
  an account). They must be replaced at first sign-in, and stop working after 72 hours (`TemporaryPasswordHours`);
  the administrator then gives out a new one. The first administrator's one-time password is written to a file only
  administrators can read, and the file is deleted once the password is changed.
- **Notices** (ASVS V6.4.3): when a password is changed or reset, or two-step verification is turned on, off or reset,
  the account's holder is told in the app and, when the account has an e-mail address, by e-mail
  (`auth.credentials_changed`). Someone who did not make the change can tell IT at once.
- **No fixed first password outside development** (ASVS V6.3.2): `HealthCare360:Bootstrap:AdminPassword` and
  `RequirePasswordChange`, and the demonstration accounts that share that password, work on development machines only.
  Elsewhere they are ignored and the server logs a warning.
- Access tokens:
  - ES256-signed, valid for 10 minutes, held only in memory.
  - Refresh tokens are opaque and rotate on every use, in an HttpOnly, Secure, SameSite=Strict cookie.
  - The staff app and the patient portal have addresses of their own ("Separate addresses" below) and separate refresh
    cookies (`hc360_rt` and `hc360_prt`), so signing in to one never replaces the other's session. Browsers send a
    host's cookies to every port of it, so each address reads only its own app's cookie: the portal's address renews
    only portal sessions, and nothing there can renew a staff session.
  - A refresh token presented after it was spent ends the whole session, and is audited as critical. A retry within
    60 seconds (a response lost on the WiFi link) is re-issued a token once. The token it replaces is retired, so if a
    copy was being replayed, the real client's next refresh ends the session for both. The app's windows on one
    computer share the cookie, so they take turns to refresh (a Web Lock only that address's windows share): two
    refreshing at once, as all do when the screen is unlocked in one, would present the same token, and the browser
    could keep the retired one and sign everyone out.
- **Sessions** (ASVS V7):
  - **How long:** a session ends 12 hours after sign-in (one clinical shift), or after 2 hours nobody has used it. A
    patient portal session ends after 20 minutes unused, because portals run on shared and family phones. Together
    with the lock below, these meet NIST SP 800-63B at AAL2: sign in again within 12 hours, and give a secret again
    after 30 minutes unused. The server will not start with `HealthCare360:Security:SessionHours` above 12 or
    `AccessTokenMinutes` above 15 (SRS SEC-02); it names the setting to change (`PlatformOptionsValidator`).
  - **What counts as use:** a key press, click, touch or scroll in the app, which the app reports with each token
    refresh. The refresh itself does not count, since the app refreshes every few minutes in the background. A video
    consultation in progress counts as use on the portal.
  - **Idle lock:** staff screens lock after 10 minutes unused (the `ui.idleLockMinutes` setting; 0 turns it off), and
    staff unlock with a PIN or their password. The server keeps the lock: until the session is unlocked, its requests
    are refused (423) and live updates stop, so hiding the lock screen opens nothing. The server also locks a session
    the app did not lock, at the next refresh that reports 10 minutes unused. Five wrong PINs or passwords end the
    session. A PIN is 4 to 6 digits, not all the same or in sequence, and setting one needs the password; an
    administrator's password reset clears it, since whoever knew the old password may know it. Locking and unlocking
    are audited. Each facility can have its own lock time (`ui.idleLockMinutes` set for the facility). The screens
    stay as they were behind the lock, with whatever was typed and not yet saved: what they ask the server for while
    locked waits, and is answered once unlocked. The app's windows on one computer (browser tabs, HC360 Desktop's
    second windows) share the session: use in any of them keeps all unlocked, and locking or unlocking one does the
    same to the others.
  - **Quick unlock with Windows Hello or a fingerprint** (SRS UX-11): where the hospital has turned it on, staff can
    set up a device under My account › Lock screen and then unlock with its Windows Hello (face, fingerprint or the
    Windows Hello PIN), or Touch ID and the like elsewhere. It is WebAuthn with the device's built-in authenticator, checked by fido2-net-lib (MIT); no
    vendor or service is involved, and nothing about the face or finger leaves the device. The decisions:
    - It only resumes a session that is locked: it needs that session's token, and it never signs anyone in. A
      session that ended, or that nobody used for 2 hours, is not resumed by it, nor by the PIN or password.
    - The device must verify the person (user verification required), not only notice a touch.
    - Setting a device up needs the password, and is audited; so are removing one and each unlock. Staff can remove
      their devices; an administrator's password reset removes them all, with the PIN. An account may have 10.
    - A key works only for its owner, and only where the app is opened by the server name it was set up under (the
      WebAuthn relying party id: the host name in the address, one of the names on the server's certificate, the
      computer's name or `localhost`). WebAuthn cannot use an IP address, so opened by one the lock screen offers the
      PIN and password only. Pages must be on HTTPS (or `localhost`).
    - Each key's signature counter is kept: an answer whose counter goes back means a copied key, and is refused and
      audited as critical. Failed answers count with wrong PINs: five end the session.
    - It is off until an administrator sets `ui.biometricUnlock` to `true`, for the hospital or for one facility
      (`false` for a facility keeps it off there). The PIN and the password always work. Turn it on only where every
      staff member signs in to Windows with their own account: Windows Hello proves who owns the Windows login, not
      who owns the HC360 account. On a computer where staff share one Windows login, as at many nursing stations and
      counters, anyone who knows that login's Windows Hello PIN, or whose finger or face is set up on it, could unlock
      a colleague's locked screen. Administration › Settings says so beside the setting, and so does setting up a
      device.
    - Computers enrolled as kiosks or display boards, which everyone uses, never use it: setting up and unlocking
      there are refused (`auth.biometric_unlock_shared_device`). Where HC360 Desktop runs as a kiosk (its policy),
      setting up a device warns against it as well.
    - A known limitation: HC360 cannot tell a shared Windows login from a personal one. Tying each key to the Windows
      user name, which HC360 Desktop could report, would not help: colleagues on a shared login have the same user
      name, Windows Hello already keeps each Windows account's keys apart (a key set up under one account cannot
      answer under another), and the name would come from the page, not from the device's signature, so it would
      prove nothing. Where logins are shared, leave it off and use the PIN.
    - Refused answers are audited at once, in a transaction of their own, so the entry stays (a copied key's as
      critical) even when the unlock then finds the session ended.
  - **How many at once:** a staff account may have 3 sessions at once (the `security.maxSessionsPerUser` setting; 0
    for no limit). Signing in once more signs out the session used least recently, audited as `auth.session_limit`.
    Patient portal accounts have no limit.
  - **Other devices:** under My account › Signed-in devices, staff see their sessions, when each was last used and
    whether it is locked. They can sign out one of the others, or all of them, after giving their password again.
  - **Everybody:** an administrator with `admin.system.manage` can sign out every session but their own, staff and
    portal, from Administration › Users, after giving their password. Use it for a suspected break-in or a leaked
    password list. It is audited as critical.
  - **Credential changes:** changing the password, or turning two-step verification on or off, signs out the
    account's other sessions. Setting up two-step verification asks for the password first.
- Selected roles can be limited to **enrolled devices**:
  - Each HC360 Desktop has a device secret, protected with Windows DPAPI. It gets it by enrolling with a code an
    administrator makes, which is good for one device and for 30 minutes unless the administrator chose otherwise.
  - Administrators can revoke a device at any time.
  - Only HC360 Desktop kinds count (workstation, kiosk, queue display, tablet). An HC360 lab agent is also an enrolled
    device, but its secret sits on an unattended lab PC (protected with machine DPAPI, in a folder only SYSTEM,
    Administrators and the agent's service account can read), so it is refused for sign-in and for the desktop
    heartbeat. It can only relay the analysers assigned to it, which the server checks on every call and again every
    minute while a relay is open. Revoking it ends its relays within a minute.

#### Sign-in pathways
Every way in, for people, devices, providers and other systems, with what it takes and what stops guessing (ASVS
V6.1.3, V6.3.4). Every endpoint open to callers who have not signed in is also on the reviewed list in
`EndpointAuthorizationTests`, which fails when one is added, so a new pathway is a reviewed change to both.

What they all share:
- The keys, tokens and codes HC360 makes come from the system's secure random generator.
- What HC360 only has to check is stored as a hash: PBKDF2-SHA512 for passwords and PINs, SHA-256 for device keys,
  tokens and codes. What it must read back is encrypted with the data encryption key (Keys and certificates, below):
  authenticator (TOTP) seeds, and the webhook secrets and tokens the providers issue. A secret is compared in constant
  time, or found by its hash. The exceptions are codes printed or posted for anyone holding the paper to use, which are
  stored as they are: feedback form tokens, and report and prescription verification codes.
- An unknown account, device or code gets the same answer as a wrong one.
- Each secret allows a set number of tries, and tries sent at once are judged one at a time, so the limit holds
  however they arrive. Requests are limited per address as well: 10 a minute for sign-in, codes, PINs and password
  prompts, 5 portal code requests in 15 minutes, 60 a minute for patient links, and an allowance per device for devices.
- Every pathway but the analyser and modality connections is HTTPS only.

| Pathway | Proof, and its strength | What stops guessing |
|---|---|---|
| **Staff sign-in** (staff app, HC360 Desktop) | Username and password: at least 12 characters, none from breach lists. The SuperAdmin, HospitalAdmin, Accountant and HrManager roles, and anyone holding an administrative permission, also give a 6-digit authenticator code (TOTP): two factors (NIST AAL2) for them, one (AAL1) for other staff. A role can be limited to enrolled HC360 Desktops; none is by default | The account locks after 5 wrong passwords or codes in a row, for 15 minutes, doubling up to 24 hours |
| **Renewing a session** | The refresh cookie: 256 random bits, replaced on every use, sent with the `X-HC360-Client` header. A spent one presented again, past a minute's grace for a lost response, ends the session. Sessions end 12 hours after sign-in, or after 2 hours unused (portal: 20 minutes) | Too large to guess |
| **Lock screen** | The PIN (4 to 6 digits, not all the same or in sequence), the password, or, where the hospital turned it on, a device set up for it: a WebAuthn signature of a one-time challenge by the device's Windows Hello or fingerprint reader, which verifies the person first. It opens only the session it locked | 5 wrong ones (PINs, passwords and refused device answers together) end the session |
| **Password prompts** before sensitive actions | The password again, from a signed-in staff member | Wrong ones count toward the account lock |
| **Patient portal sign-in** | A 6-digit code sent by SMS to the registered number, valid 5 minutes: one factor (AAL1), as strong as the phone number | 5 tries per code; 3 codes per number in 15 minutes; a proof of work for asking for a code and for giving it (Anti-automation, below) |
| **Video consultation guests** | The 256-bit token in the link sent to the patient, valid until 2 hours after the consultation's scheduled end. A guest waits until the doctor admits them, and the link reveals only the patient's first name | Too large to guess; 5 failed joins per connection |
| **Enrolling a workstation or lab PC** | A code an administrator makes: 8 characters from 31 (about 40 bits), good for one device, for 30 minutes by default (5 minutes to 24 hours) | Too many to try in its lifetime at 60 a minute per address |
| **HC360 Desktop and lab agents** | The device's id and its 256-bit secret, kept with DPAPI. Lab agents never sign anyone in, and relay only their own analysers. HC360 Desktop asks for and downloads its update with the same credential; what it gets is HyFib's signed package, which it checks itself (Release signing key, below). Revoking the device stops it | Too large to guess; the update allows 30 requests in 15 minutes per device |
| **Attendance terminals, JSON punch API** | The serial number and a 256-bit device key | Too large to guess |
| **Attendance terminals, ADMS** (ZKTeco, eSSL) | The serial number, which is printed on the terminal, and its fixed IP address when one is set: the ADMS protocol carries no key. This is the weakest pathway. Without an address, anyone on the network who knows a serial number can add punches in its name, so set the address of every terminal that pushes over ADMS in HR › Terminals (ASVS V6.3.4) | Only registered, active serials; refusals are logged |
| **Patient monitors and their gateways** | A device key of 256 random bits that names its device | Too large to guess |
| **Network lab analysers** | The analyser's fixed IP address: HL7 (MLLP) and ASTM have no authentication. A connection set up before the address was required accepts any address until one is added | Other addresses are closed before a byte is read |
| **Modality worklist** (DICOM) | The modality's AE title and IP address together, from the allow-list | Other addresses are closed before a byte is read |
| **Payment gateway callbacks** | An HMAC-SHA256 signature of the body with the webhook secret | Too large to guess; refusals are logged |
| **SMS delivery receipts** | MSG91 and generic gateways: a shared token in the address (`?token=`), and the gateway's addresses if an allow-list is set. Twilio: its `X-Twilio-Signature` (HMAC-SHA1, Twilio's scheme) | As strong as the token; refusals are logged |
| **WhatsApp callbacks** (Meta) | An `X-Hub-Signature-256` (HMAC-SHA256 of the body with the app secret); the verify token for Meta's subscription check | Too large to guess; refusals are logged |
| **FHIR API** | A staff sign-in whose roles hold `fhir.read` (HospitalAdmin by default): no separate client credentials or SMART on FHIR. `fhir.read` alone does not require two-step verification | As staff sign-in |
| **Patient links** | Feedback forms: a 128-bit token on the hospital's QR poster, which any patient may answer. Feedback invitations: 128 bits, 14 days, one answer. Unsubscribe links: 128 bits; they only stop campaign messages. Prescription checks: 144 bits; the doctor, facility, date and prescription number, and the doctor's DSC signature (signer, certificate, SHA-256 of the signed bytes, re-checked for each answer, and its .p7s), no patient details. Report checks: 100 bits; the patient's initials and the test names, never results | Too large to guess; 60 a minute per address; sending feedback also takes a proof of work (Anti-automation, below) |
| **Server command line and database** | A Windows administrator's sign-in on the Server Node: the data folder is readable only by them, the service and SYSTEM. PostgreSQL accepts only the service's Windows account, from that machine, unless it runs elsewhere (Keys and certificates, below) | Windows |

The webhook secrets and tokens are made at each provider and saved in Administration › Integrations. HC360 does not check
their length, so use the long random values the providers generate. The rest of what anyone may call (the server's
name and certificate, the public doctor directory, the privacy notice and health checks) reveals nothing about a person
and grants nothing.

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
  - Their stored files (documents, photos, reports) follow the same rules: opening a file, or its details, by its id
    is refused (403 `patient.restricted`) until the user may open the record, so knowing a file's id is not a way in.
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
- **ABDM health records** (EHR-08) are off until an administrator turns on `features.abdm`:
  - A record leaves as an ABDM FHIR document only through `/api/v1/abdm/bundles/*`, for holders of `abdm.export`
    (HospitalAdmin and MrdOfficer by default): one signed or released record at a time, from the caller's facility,
    under the restricted-record rules. Every export is audited as an access to the patient's record, and the document
    is never cached.
  - Sending it to ABDM needs the patient's ABDM consent and the gateway, which is not live: the registered client sends
    nothing. NHA's client id and secret are a secret setting (`abdm.gateway.secret`, encrypted), and the gateway's
    status says only whether they are set. Its base URL must be https without a user name.
  - Linking, replacing or removing a patient's ABHA number or address is audited (`patient.abha_changed`), with the
    number masked.
- Only what is needed is stored:
  - **Aadhaar**: the last 4 digits plus a keyed blind index, used for duplicate detection. The full number is never stored.
    Checking a patient's identity with their Aadhaar fingerprint (INT-BIO) keeps the outcome, the last 4 digits and
    UIDAI's references, never the number or the fingerprint: a certified reader encrypts each capture for UIDAI before
    it leaves the device, HC360 Desktop and the server pass it on without logging it, and the request is signed with
    the AUA's certificate, whose key is an encrypted secret. Only enrolled HC360 Desktops can send a check, only with
    the patient's consent, and UIDAI's answer counts only for its own transaction (and with UIDAI's signature once its
    certificate is set). See [integrations.md](../modules/integrations.md#aadhaar-authentication-int-bio-patient-identity).
  - **ABHA numbers**: encrypted with AES-256-GCM. Their blind index, like the number, is left out of the audit trail's
    record of changes. A number must be 14 digits before it is stored; it has no published check digit, so a mistyped
    number cannot be caught until it is checked with ABDM, which needs the gateway (not live).
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
  - Each entry is hash-chained to the previous one, over every column: what happened, to what and whom, who did it
    (user, name, device, session, address), how severe it was and the correlation ID. Entries sealed before the chain
    covered every column are still checked against what was hashed then.
  - Database triggers block deletes and truncates, rows inserted pre-sealed, and every update except a complete seal:
    hash, chain position and previous hash together, linked to the sealed entry before it (checked at commit), with
    nothing else in the row changed.
  - Administration › Audit log › *Verify integrity* re-checks the chain, and names the position of the first entry
    changed, moved or removed, even by someone who turned the triggers off. The chain has no key, so someone who also
    sealed every later entry again would not be found (ASVS V16.4.2). It also fails on any seal outside the chain.
    Tests: `AuditTrailTests` (against PostgreSQL) and `AuditSealTests`.
- Records carry who and when on every change, and deletes are soft deletes. Every change to an audited record also
  writes a data audit event with each changed value (from/to); compound values such as addresses, cardiology
  findings and TTI results are recorded part by part. Binary values (a prescription's DSC signature and the bytes it
  signed, which name the patient) are recorded by their length and SHA-256, never copied into the trail.
- **Idempotency keys** on every write:
  - A WiFi retry replays the original result instead of creating a duplicate bill, payment or order.
  - Stored responses are encrypted, because they can contain one-time secrets.
  - The AI endpoints' answers (dictation transcripts, SOAP drafts, interpreted commands) are never stored
    (`.WithoutStoredResponse()`). The key still stops a retry from running again, so there is no second AI call or
    quota charge. The retry gets 409 `idempotency.answer_not_kept`, and the user asks again.
- Optimistic concurrency: a record changed by someone else is never silently overwritten. Edits send the version
  (`xmin`) they loaded and get 409 when it has moved on; settings, print templates and feature switches do too
  (REL-04). A change sent without a version, as older clients send it, still saves.

### Application hardening
- **Separate addresses** (ASVS V3.5.4). The staff app, the patient portal and the API reference each have an origin of
  their own, so script running on one cannot read the others' pages or storage, or use their sessions:
  - The staff app, HC360 Desktop and the integrations: port 7360 (`HealthCare360:HttpsPort`).
  - The patient portal: port 7361 (`HealthCare360:PortalHttpsPort`). The portal runs third-party script, the payment
    checkout. Its address serves the portal's pages and API and the few endpoints it shares with the staff app
    (renewing and ending a session, public links, report and prescription checks, the server's name and switches,
    video consultations, health probes and provider webhooks). Nothing else answers there: no staff sign-in, page or
    endpoint. An architecture test fails if an endpoint needing a staff permission ever does.
  - The staff app's address sends `/portal/...` to the same page on the portal's address, so links in messages sent,
    and QR codes printed, before the portal moved keep working. It does not serve the portal's own API or sign-in.
  - The API reference, while it is on: port 7362 (`HealthCare360:ApiReferencePort`), listening on the server machine
    only. It runs third-party script too, so its address takes no cookies and sets none.
  - Each address is told by the port a request arrived on, never by what the request says (its Host or
    `X-HC360-Client` header).
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
- **Anti-automation on the portal** (SRS SEC-06, ASVS V2.4.1; V11.1.4 in ASVS 4.0). The portal's anonymous requests
  that a script could abuse ask the browser for a proof of work first: asking for a sign-in code (which also signs a
  new number up, and sends an SMS), giving the code, and sending feedback from a QR poster, a kiosk or a personal link.
  It is self-hosted Hashcash, in the manner of ALTCHA: no third-party CAPTCHA, so hospitals on a LAN keep working and no
  patient's traffic goes to Google or Cloudflare. There is no puzzle to see or solve, so it works with a screen reader.
  - **Challenge.** `POST /api/v1/public/challenge {action, subject?}` returns `1.{action}.{bits}.{expires}.{salt}.{signature}`:
    16 random bytes of salt, an expiry 5 minutes ahead, and an HMAC-SHA256 of the rest under the anti-automation key
    (`keys\anti-automation.key`, Keys and certificates, below). It names the kind of request it is good for (`otp`,
    `feedback`).
  - **Work.** The browser finds a decimal nonce for which SHA-256 of `{challenge}{nonce}` starts with `bits` zero bits,
    in a Web Worker with WebCrypto, and sends `{challenge}:{nonce}` in `X-HC360-Proof`. The portal solves one in the
    background when a form appears, so the patient seldom waits; it says "Checking your browser…" only after 1.5
    seconds.
  - **Difficulty.** 14 bits by default: 2^14 (16,384) hashes on average. WebCrypto in a worker does some 20,000 to
    50,000 a second on a mid-range phone, so 0.3 to 0.8 seconds, a cost a person does not notice.
  - **What it is worth.** It is no wall: a native SHA-256 solver does 2^14 to 2^18 hashes in milliseconds. The proof
    raises the cost of cheap, high-volume scripting (every request needs a fresh challenge from this server and its own
    work, and a script has to run a solver at all). The rate limits stay the primary control: per address (above), and
    per number for sign-in codes (3 in 15 minutes, 5 tries each).
  - **Raised for failures.** Every 3 failures from an address, or for a mobile number from any address, ask 2 bits
    more (four times the work), up to 18 bits (5 to 13 seconds on a phone). A failure is a refused proof, a rate-limit
    refusal, or a refused request (a wrong or expired code, too many codes, a dead link). A form that fails validation
    (feedback, or a sign-in code asked for a number that is not a mobile number) is answered before the proof is
    checked, so it neither spends its proof nor counts. The count for an address or a
    number lasts until 15 minutes pass without a failure for it (each failure starts the quarter of an hour again), so a
    quiet quarter of an hour brings it back down and a steady trickle keeps it up. Numbers are kept only as keyed
    hashes, in memory: an HMAC-SHA256 under a key derived from the anti-automation key, since India has few enough
    mobile numbers to hash every one, and a plain hash would give the number back.
    Giving a code names only its challenge: the work asked for it depends on the address, and when the attempt fails
    the server looks the challenge's number up (one lookup by key, on that path only), so wrong and expired codes count
    against the number as well.
  - **Checking.** Stateless: one HMAC, then the kind of request, the expiry, one hash, and the bits against what the
    address and number need now, with no database. A good proof is spent: the challenge is remembered until it expires and
    refused again. On several API servers (SCAL-01) the spent challenges and the failure counts are each server's own,
    so a proof could be used once on each within its 5 minutes; the rate limits still bound that.
  - **Refusals** get one answer, whatever the reason: 403 `request.check_failed`, "We could not complete this request.
    Please try again." The portal then solves a fresh challenge, for the number when there is one, and tries once more.
    Each refusal is counted in the `hc360.anti_automation.rejected` metric (by kind and reason: missing, malformed,
    tampered, wrong action, expired, too easy, unsolved, replayed), and audited as `security.anti_automation_rejected`
    at most once per address and reason in 5 minutes, so a flood cannot flood the audit trail.
  - **A too-easy proof is not a failure.** It was solved, for a challenge issued before the work asked of the address or
    number went up. The portal solves its sign-in challenge as the form appears, before the number is typed, so a
    patient whose number failed lately (they mistyped the code, or someone tried their number) meets this on every
    attempt. Such a refusal is counted in the metric but neither raises the work nor is audited, and the retry, solved
    for the number, gets in. A proof that does not solve its own challenge is unsolved, which is a failure.
  - **Accepted: the challenge shows recent failures for a number.** The challenge endpoint is anonymous and answers with
    the bits a given number needs, so asking for one shows whether the number has failed in the last quarter of an hour.
    It says nothing about whether the number has an account or who failed, anyone who typed the number can cause such
    failures, and the sign-in itself never says whether a number is registered.
  - **Settings** (Administration › Settings): `security.antiAutomation.enabled` (on unless set to `false`),
    `security.antiAutomation.bits` (14) and `security.antiAutomation.maxBits` (18); neither above 20, and a value
    outside 0 to 20 (or not true or false) is refused when it is saved. 2^20 hashes take a mid-range phone 20 to 50
    seconds on average, and an unlucky one in twenty three times that, still within the 5 minutes a challenge lives; at
    22 bits the average alone would be 1.5 to 3.5 minutes. Turning it off lets every request through without a proof.
  - Unsubscribing does not ask for one: its link is personal, and doing it twice changes nothing. Neither do the
    read-only public pages (the doctor directory, document checks, the privacy notice) or signed-in requests, which the
    per-user limits cover.
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
- The API reference is off by default. If IT turns it on, it answers only on its own address on the server machine
  (port 7362), which takes and sets no cookies.
- The embedded browser in HC360 Desktop:
  - Opens only the hospital server; other sites open in the default browser.
  - Signs prescriptions with the doctor's DSC (`dsc.signPrescription`): the doctor picks the certificate in the
    Windows dialog, which shows what the bytes contain as the desktop reads them (never text the page sends), and the
    token asks for its PIN. The private key never leaves the token; one dialog at a time; nothing is stored. The
    server checks the signature again before it keeps it, and accepts only a certificate that chains to a CCA India
    root IT installed in its DSC trust folder — never one the operating system trusts, such as a domain's user
    certificate or a self-signed one (docs/modules/clinical.md, *Signing with the doctor's DSC*).
  - Captures fingerprints for Aadhaar identity checks (`fingerprint.capture`) only from a UIDAI-certified reader's RD
    service on `127.0.0.1` (ports 11100–11120, paths checked, no proxy, no redirects). The reader encrypts the capture
    for UIDAI; the desktop passes it to the page as it came, one capture at a time, and never logs or keeps it.
  - Answers bridge calls (printing, device identity, notifications, DSC signing, fingerprint capture) only from the staff app's pages. The patient
    portal and the API reference, which run third-party script, are on addresses of their own, so it opens them in the
    default browser.
  - Saves only document types HC360 produces (PDF, CSV, XLSX, XML, JSON, text, images, ECG/DICOM files, recordings,
    the hospital CA's .crt and a prescription's DSC signature as .p7s), and only from the server.
  - Denies permissions other than camera, microphone, clipboard and notifications.
  - Disables password autosave and autofill.
  - Disables DevTools unless IT enables them.
- The server runs as a dedicated virtual service account, and its data folder is readable only by that account,
  Administrators and SYSTEM.

### Backups and keys
- Nightly backups are **encrypted** (AES-256-GCM, chunked, every chunk authenticated) and optionally copied to a second
  disk or NAS.
- Each backup is one archive holding the database, the stored documents (already encrypted) and escrow copies of the
  field encryption keys. So one backup file and its recovery key restore everything on replacement hardware, including
  identifiers such as Aadhaar and ABHA, which are unreadable without that key.
- Every backup is read back in full and its database dump listed by pg_restore before it counts. A backup that fails
  this check is discarded and reported.
- **The recovery key opens the backups**, and the hospital keeps it **offline** (ASVS V11.1.1, V13.3.2).
  - It is 256 random bits of its own, not derived from the data keys, kept on the Server Node with Data Protection
    (machine DPAPI) to encrypt new backups.
  - A holder of `admin.system.manage` makes a new one from Administration › Backups, after giving their password
    again. It is shown once, to them, as 14 groups of 4 letters and digits whose last group checks the others. Backups
    use it only once they type it back from their copy, so a key nobody wrote down never becomes the one backups need.
    The server deletes the key it replaces. Showing it and putting it to use are audited, without the key.
  - Every backup file names its key in a header that each chunk authenticates. The Backups list names each backup's
    key, and the restore tool says which key a backup needs when it is given another.
  - Until the first new key, backups use the original recovery key, derived from the original data key, which any
    holder of the backups permission could display before. System health warns while they do, and says so as critical
    when that key was never displayed, because then nobody holds it.
  - A restore on a machine without a recovery key of its own, such as new hardware, keeps the key it was given for new
    backups. A machine that has one keeps it.
- Signing and encryption keys are generated on the Server Node and protected with Windows Data Protection (DPAPI).
  A restore installs the field encryption keys protected for the new machine, and a different key already there is
  kept under a new name, never deleted. The keys are installed last, after the backup's database is in place. If an
  earlier step fails, the previous database is put back and the machine keeps its own keys.
- **Keys are rotated from System health › Encryption keys** (SRS SEC-07), by holders of `admin.system.manage`; each
  rotation is audited.
  - *Data encryption key.* A new key becomes current and encrypts new data at once. Every value and document names
    the key it was encrypted with, and a background job moves what was encrypted before to the new key, a little at a
    time: the protected columns of every module, secret settings, scheduled report files, and stored documents, which
    it rewrites only while no backup is copying them. The screen shows how much is left. An earlier key stays until
    nothing is encrypted with it, so anything not yet moved stays readable, and every backup carries every key the
    server has when the backup is made.
  - *Search indexes* (ASVS V11.2.2). The keyed hashes that find a patient by ABHA number, a policy by its number and an
    employee by PAN or bank account come from the current data key too, and name it. The same background job computes
    each one again with the new key, from the encrypted number beside it, once the rotation is a minute old (a search
    that began just before it still looks for the earlier index). Until then, and while it works, a search tries every
    key, so nothing goes missing.
  - *Retiring an earlier data key* (ASVS V11.1.1). The screen lists each earlier key with what is still encrypted with
    it. Once nothing is, and the current key has been current for a day (so anything still being encrypted with the
    old key as it changed has been written, and moved), it offers to retire the key. The administrator gives their
    password again, the server deletes the key, and the audit log records it. Stored answers to repeated requests are
    not moved: they are kept a day, so a key waits for them to expire. Backups made before keep their own copy of the
    key, so they still restore, and a restore of one puts the key back. When it also makes that key current again (the
    backup was made while it was), rotate the data key again.
  - *What does not rotate.* The Aadhaar index stays on the original key: it cannot be computed again, because only the
    last four digits are kept. This is an accepted risk: keeping the full number to compute the index again would
    break the rule that HC360 never stores it. The original recovery key, which older backups open with, comes from the
    original key too; the recovery key is replaced on its own (above). The original key is therefore never retired.
  - *Sign-in token key.* New access tokens are signed with a new key, and the previous one is still accepted for the
    access-token lifetime plus a minute, so nobody is signed out. Another rotation waits until then.
  - *Anti-automation key* (an accepted exception to rotating from System health). It signs only the portal's
    proof-of-work challenges, which live 5 minutes, and keys the in-memory hashes of the numbers that failed lately; it
    protects no stored data, so it is rotated by deleting `keys\anti-automation.key` and restarting the server:
    challenges issued before stop working and browsers ask for new ones, and the failures counted so far are forgotten. Rotate it with the sign-in token key when the Server Node may have been exposed (Key policy, below).
- **Key policy.** Rotate the sign-in token key every year and the data encryption key every two years; System health
  says when either is due. Rotate both at once, and change the database password if the server signs in with one, when
  the Server Node, its disks or a backup may have been exposed, and when someone with administrator access to the
  Server Node leaves. Retire an earlier data key once System health shows nothing is encrypted with it; the backups
  made before keep their copy of it until they are deleted. Make a new backup recovery key when someone who has seen
  the current one leaves or a copy may have been exposed, and keep each one offline as long as backups made with it are
  kept.

#### Keys and certificates

| Key or secret | Where | Protected by | Protects | Rotation |
|---|---|---|---|---|
| Data encryption keys (field master key ring) | `keys\field-master.key` (original) and `keys\field-keys\` (one per rotation, and which is current) | Data Protection (machine DPAPI) | AES-256-GCM, a key per purpose (HKDF): ABHA and policy numbers, staff PAN and bank details, two-step verification secrets, secret settings, stored documents, scheduled report files, stored responses | System health, every 2 years or on exposure; an earlier key retired there once nothing is encrypted with it, the original never |
| Blind-index keys | Derived from each data key | As above | HMAC-SHA256 lookups of ABHA, policy, PAN and bank account numbers (the current data key's), and of Aadhaar numbers (the original key's) | With the data key, indexes computed again in the background; the Aadhaar index stays on the original key (it cannot be rebuilt) |
| Backup recovery key | `keys\backup-recovery.key` (until the first new key, derived from the original data key); shown once, when it is made | Data Protection on the Server Node; the hospital keeps the copy offline | Every backup file (chunked AES-256-GCM), which carries all the data keys and names its recovery key | Administration › Backups, by holders of `admin.system.manage` who give their password again, when someone who saw it leaves or a copy may be exposed; each key kept as long as backups made with it |
| Token signing key | `keys\jwt-signing.key`, and the previous one for minutes after a rotation | Data Protection | ES256 access tokens (10 minutes) | System health, every year or on exposure |
| Anti-automation key | `keys\anti-automation.key`, 256 random bits made on first start | Data Protection | HMAC-SHA256 of the portal's proof-of-work challenges (5 minutes; Application hardening, above), and, through a key derived from it, of the mobile numbers whose failures are counted in memory | Delete the file and restart: challenges issued before stop working and browsers ask for new ones; not from System health, an accepted exception (above). Several API servers need the same file |
| Data Protection key ring | `keys\dataprotection` | Machine DPAPI | The keys above and the LAN certificates' private keys | Automatic (library default, 90 days) |
| Hospital CA and server certificate | `certs\*.pfx.protected` | Data Protection | HTTPS on the hospital LAN; HC360 Desktop pins the CA | Server certificate re-issued 30 days before expiry; CA valid 10 years, never re-made by itself. System health warns 30 days before either expires |
| Exported hospital CA | A `.pfx` file made by `certificates export-ca`, for a warm standby or replacement hardware; kept offline, or deleted once imported | A password of 16 characters or more (AES-256-CBC, PBKDF2-SHA256, 200,000 rounds), kept apart from the file | Issuing certificates that workstations trust | With the CA. A file and password that may have been exposed are a CA exposed: make a new CA (every workstation set up again) |
| PostgreSQL TLS certificate (warm standby) | `hc360-server.key` in PostgreSQL's data folder, copied to the standby with the database | File permissions: PostgreSQL's service account and Administrators | The replication stream between the Server Nodes | Made by `Enable-HC360Replication.ps1`, valid 10 years; made again when it is within 30 days of expiry (copy the new `hc360-server.crt` to the standby) |
| Replication password (warm standby) | On the primary, only its SCRAM verifier, in PostgreSQL; on the standby, `hc360-standby\replication.pgpass` beside the data folder; the hospital's password safe | File permissions on the standby: PostgreSQL's service account and Administrators | Streaming the database to the standby's address | Run `Enable-HC360Replication.ps1` with a new password, then `New-HC360Standby.ps1` with it |
| Database sign-in | None: the service's Windows account (`NT SERVICE\HC360Server`, SSPI), which `pg_hba.conf` and `pg_ident.conf` allow from this machine only | Windows | The server's PostgreSQL account | Nothing to rotate. Where the server signs in with a password instead (PostgreSQL on another machine), the password is in `config\hc360.settings.json` under machine DPAPI, and changed from System health every year or when someone who knew it leaves |
| Integration credentials (SMS, WhatsApp, e-mail, payment, AI) | Secret settings in the database | The data encryption key | Each provider's API | At the provider, then saved in Administration › Settings |
| Desktop and lab agent device secrets | Each workstation | DPAPI (user or machine) | A device's sign-in | Revoke and enrol the device again |
| Release signing key (HyFib's, ECDSA P-256) | The `HC360_RELEASE_SIGNING_KEY` secret of the release workflow, and one copy offline at HyFib; the public keys are built into HC360 (`ReleaseKeys.cs`) | GitHub's encrypted secrets: only the release job reads it, from its environment, never on a command line or in a log | The manifest of each HC360 Desktop package, which the Server Node, HC360 Desktop and its updater check before they take, offer or install it (SRS DED-18; Release signing key, below) | A new pair every few years, at once on exposure or when someone who held it leaves: release with both public keys, then sign with the new one |
| Doctors' Digital Signature Certificates (DSC) | The doctor's own USB token, issued by a licensed Indian CA; never on the server or in HC360 | The token and its PIN (the vendor's CSP/KSP); HC360 Desktop only asks Windows to sign | Their detached CMS signatures over prescriptions (SHA-256; ERX-04), checked by the server and anyone with OpenSSL | By the CA (one to three years); a new certificate signs new prescriptions, earlier signatures stay checkable with the certificate inside them |
| DSC trust anchors (the CCA India root certificates) | The server's DSC trust folder (`HealthCare360:Clinical:DscTrustDirectory`, by default `certs\dsc-trust` in the data folder): public certificates only, which IT downloads from cca.gov.in and checks against the thumbprints the CCA publishes | File permissions of the data folder: whoever can write there decides which certificates count as DSCs | Accepting a DSC signature: the certificate must chain to one of them, offline, at the signing time (no revocation check), and be issued in the prescribing doctor's name; every printed copy re-checks it and states the DSC only while it is Valid | When the CCA issues a new root, add it; a removed root turns the signatures under it to "not confirmed as a DSC" (`Untrusted`: printed as "Signed electronically", and not issued at all where the DSC is required). The folder is not in the backups or streamed to the standby: IT copies the roots onto new hardware and the standby (DEPLOYMENT.md, sections 4, 8 and 9), and System health warns while it is empty, or the server cannot read it (which counts as empty), and DSC signing is on |
| Passwords, refresh tokens, one-time codes | The database | PBKDF2-SHA512 (passwords), SHA-256 (tokens and codes) | Sign-in | Refresh tokens on every use; passwords rehashed when the work factor rises |
- Before start-up changes the database schema, it takes a backup and checks it. When that fails, the database is not
  changed.
- **Secrets in the settings file** (`config\hc360.settings.json`) are protected with Windows DPAPI for the machine:
  every connection string, which carries the database password where the server signs in with one, and every setting
  whose name ends in Password, Secret or ApiKey, such as an administrator-supplied certificate's password (SRS SEC-07).
  The server opens them as it loads the file, so a copy of the file is no use on another computer, and the file itself
  can be read only by SYSTEM, Administrators and the service. The installer protects them on every install and
  upgrade, and System health warns while one is in plain text. Integration keys are stored encrypted in the database,
  as before.
- **The server signs in to PostgreSQL with its Windows account** (ASVS V13.2.1), `NT SERVICE\HC360Server`, through
  SSPI, and not with a password: there is none in the settings file to copy, leak with a backup of it, or change.
  `pg_ident.conf` maps that Windows account to the database account, and `pg_hba.conf` takes the database account only
  that way and only from this machine, ahead of every other rule. Backups (pg_dump) sign in the same way. The installer
  sets this up once the server has started as it was, checks that the server then reaches the database, and puts the
  settings file, `pg_hba.conf` and `pg_ident.conf` back if it does not. With the superuser's password it also removes
  the account's old password; without it, PostgreSQL refuses that password anyway. The Windows sign-in needs PostgreSQL
  on the same machine: elsewhere, or after `-DatabaseSignIn Password`, the server signs in with a password, as below.
- **Where the server signs in with a password, it is changed from System health** (SRS SEC-07), by holders of
  `admin.system.manage`, which only SuperAdmin has by default. HC360 makes a new random password that nobody sees and
  sends PostgreSQL only its SCRAM-SHA-256 verifier, so the password never appears in a statement or the database's
  logs. The settings file is written before the change is committed and put back if the commit fails, so the two agree.
  When a failed commit cannot be checked because the database is out of reach, the old password is kept and the server
  log says how to recover. New connections use the new password at once, and each change is audited.
- **BitLocker** must protect every fixed disk of the Server Node (SRS SEC-05). Reading BitLocker's state needs
  administrator rights, which the service does not have, so a scheduled task checks it as SYSTEM every day and at
  start-up, and the installer checks it too. System health shows the result, and warns when a disk is not encrypted,
  when protection is suspended or unfinished, and when the check has not run for three days.

#### Release signing key
HC360 Desktop updates itself from the Server Node (SRS DED-18), so workstations need no internet access. Each release's
HC360 Desktop package comes with a manifest (product, version, file name, size, SHA-256, release date and the oldest
server it works with), signed with HyFib's release signing key: ECDSA on the P-256 curve with SHA-256, HyFib's own key
rather than a commercial code-signing certificate. The public keys HC360 trusts are built into its code
(`src/shared/HyFib.HealthCare360.WindowsInterop/Updates/ReleaseKeys.cs`), so nothing on a workstation or the server can
add one, and every copy of HC360 runs the same checks (`ReleasePackages`):
- The **Server Node** takes a package in (`desktop publish`, which Install-HC360Server.ps1 runs for the package beside
  the server package) only when its manifest is signed by a trusted key, is for HC360 Desktop, matches the package, is
  not older than the one offered and fits this server's version. It checks the package again before offering it, so
  one changed on disk afterwards is withdrawn.
- **HC360 Desktop** asks for it with its device credential, over the connection pinned to the hospital CA, checks the
  signature and that it is newer than itself before it downloads, and the size and SHA-256 after.
- The **updater**, run as SYSTEM by the "HC360 Desktop update" task, trusts nothing in the staging folder, which every
  user may write to: it checks the signature, the product, the version and the package itself, holds the package open
  so nobody can change it until it is unpacked, unpacks only what it checked, and never writes or deletes there. It
  installs only while HC360 Desktop is closed, never goes back a version, and puts the previous version back when an
  installation fails. Users may start the task ("Restart to update") but not change it.

An unsigned package is never installed by itself. Until HyFib has set up its key, releases are built unsigned (the
release job's summary says so) and workstations are upgraded with `Install-HC360Desktop.ps1`, as before. IT can turn
self-update off on a computer with `"autoUpdate": false` in its policy (`Install-HC360Desktop.ps1 -NoAutoUpdate`).

**Setting up the key, once (HyFib):**
1. On a computer HyFib trusts with the key, run `.\tools\release\Sign-HC360Release.ps1 -NewKey -KeyPath <file>`, with
   the file on an encrypted drive. It writes the private key there, readable only by you, and prints the public key.
2. Store the private key as the release workflow's secret, then delete the file:
   `Get-Content -Raw <file> | gh secret set HC360_RELEASE_SIGNING_KEY --repo HyFib/HyFib-HealthCare360-NanaHospitals`
   (or Settings › Secrets and variables › Actions). Keep one copy offline: an encrypted USB drive in the safe, or the
   company password manager.
3. Add the printed public key to `ReleaseKeys.Trusted` and merge that before tagging the first release to be signed.
   The release workflow checks each signature against the keys built into that same release, and fails when the key is
   not among them, rather than publish a package no workstation would take.

**Rotating it** (every few years, at once if it may be exposed, or when someone who held it leaves): make a new pair as
above, add the new public key beside the old one and release that version signed with the old key; then replace the
secret with the new key, and remove the old public key a release or two later, once every workstation runs a version
that trusts the new one. A key that was exposed is removed from `ReleaseKeys.Trusted` in the next release; until a
workstation has that release it still trusts the old key, so tell hospitals to upgrade.

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
  - A finding of high or critical severity fails the pull request. On a pull request, CodeQL reports only findings in
    the lines it changes, as code scanning does; the runs on main and every Monday report everything. Each run checks
    whether GitHub Code Security is on for the repository, and acts on the answer:
    - **On:** the results go to Security › Code scanning, and its check fails a pull request that adds such an alert.
    - **Off:** GitHub refuses the results, so they are not uploaded. Instead, each `CodeQL` job checks them itself
      (`.github/codeql/check-results.ps1`). It lists every finding in the job summary and on the line it is about, and
      fails for one of high or critical severity.
  - CodeQL's default setup should stay off, because this workflow is the advanced setup. While both are on, GitHub
    refuses this workflow's results; each `CodeQL` job then warns about it and checks the results itself, as while
    Code Security is off. Switch back with Settings › Code security › Code scanning › CodeQL analysis › Switch to
    advanced.
  - Test code and generated route files are left out. For TypeScript and the workflows the filter is
    `.github/codeql/codeql-config.yml`; for C#, which CodeQL analyses as it is compiled, the workflow builds only the
    shipped projects, so the tests are left out but the database migrations, part of the server, are analysed.
  - A finding is fixed in the code, never hidden by turning a query off. One of high or critical severity reviewed as
    a false positive or an accepted risk is marked where it is: a `// codeql[<query id>]` comment on the line just
    before it, with the reason beside it. While Code Security is off, that comment is what keeps it from failing the
    check. With Code Security on, also dismiss the alert on GitHub with the same reason. Medium and low findings do
    not fail the check. The first run's alerts and how each was handled are in the table below.

| CodeQL alert | Where | How it was handled |
|---|---|---|
| Clear-text storage of sensitive information (high) | `IdentitySeeder.CreateInitialAdministratorAsync` | False positive: what it flags is the log line giving the path of `initial-admin-password.txt`, which CodeQL takes for a password because the file's name contains the word. Marked with a `codeql[...]` comment there; with Code Security on, also dismiss as "false positive". The file itself does hold the first administrator's one-time password, so the installer can show it. That is an accepted risk: the data directory is open only to administrators and the service, and HC360 deletes the file as soon as `admin` sets a new password (and at start-up if that already happened) |
| Clear-text storage of sensitive information (high) | Portal `lib/payments.ts` `rememberPendingPayment` | False positive: before the gateway's own payment page opens, the tab keeps which bill or appointment it pays, so the page can confirm the payment when the patient comes back. That is a random id and an order row id, no personal or clinical data, in `sessionStorage`, which ends with the tab; CodeQL takes the word "appointment" for health data. Marked with a `codeql[...]` comment there; with Code Security on, also dismiss as "false positive" |
| User-controlled bypass of sensitive method (high) | `AadhaarAuthRouter.AuthenticateAsync` (Integrations) | Fixed in the code. The router decided whether to ask UIDAI on a consent flag taken from the request. The flag is the staff member's statement that the patient agreed, which UIDAI's request carries the same way (`rc="Y"`), so the router's check was no credential. Consent is now part of the check itself: `AadhaarFingerprintCheck` cannot be made without it, and the endpoint refuses a request without it (400) |
| Missing XML validation (medium) | `RdService.Load` (WindowsInterop, HC360 Desktop's fingerprint reader) | False positive: UIDAI publishes no schema for an RD service's answers, so there is none to validate against. The parser refuses DTDs and external entities, caps the size, and checks the root element. Numbers are parsed and range-checked, the paths the service names must be plain local paths, and the rest is carried as text. With Code Security on, dismiss as "false positive" |
| Exposure of private information (medium) | `IntegrationSettingsReader`, `IntegrationLog`, `ReportDeliveryService` | False positive: the "private data" is the constant name of the e-mail integration (`email`), not an address. With Code Security on, dismiss as "false positive" |
| Log entries created from user input (medium) | `ErrorHandling` (request path, correlation id), `FhirEndpoints`, `MealPlanService`, `GatewayWebhookLog` | False positive: each value is cleaned or checked before it is logged (masked path, correlation id and event id checked against a pattern, route template and printable resource type, internal error codes). With Code Security on, dismiss as "false positive" |

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
11. Give every attendance terminal that pushes over ADMS a fixed IP address, and enter it as *Only accept from this IP
    address* on the terminal in HR › Terminals. ADMS carries no key, so without the address a device that knows the
    terminal's serial number can add punches in its name.
12. Keep Microsoft Edge updating on the Server Node: it makes the PDFs, and HC360 restarts it regularly so updates apply.
    Decide whether report PDFs may go by e-mail and WhatsApp (`diagnostics.reports.sendPdf`, on by default), and say so
    in the privacy notice.
13. Before checking identities with Aadhaar fingerprints, have the AUA (or the hospital as one) approve the consent
    words staff read out, the privacy notice's paragraph on it, and what its authentication logs must hold: HC360 keeps
    each check without the Aadhaar number, and keeping the number would need an Aadhaar Data Vault. Install only
    UIDAI-certified readers and their makers' RD services, keep the RD service subscriptions current, and keep the AUA
    signing certificate's .pfx and password out of e-mail and shared folders.

## 4. Reporting a vulnerability

Email **security@hyfib.com** with the steps to reproduce. Please do not include real patient data. We acknowledge
reports within 2 working days and keep reporters informed until a fix is released.
