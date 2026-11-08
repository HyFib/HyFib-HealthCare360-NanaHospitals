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
  name and addresses; the certificate is re-issued automatically when the address changes, and 30 days before it
  expires: the running server checks every six hours, so one left running for a year still serves a valid certificate.
- **HC360 Desktop and the HC360 lab agent pin the hospital CA**:
  - The server's certificate must chain to exactly that CA (through the intermediates the server sends, if any), be
    issued for a TLS server (serverAuth), and name the host being contacted.
  - The public Windows trust store is not consulted.
  - The CA fingerprint is confirmed by a person, or pre-set by IT policy, when a workstation is set up. A certificate
    that contradicts IT's fingerprint, or the one the server announced on the network, is refused outright.
  - The lab agent runs unattended, so its fingerprint is always given by IT at installation; enrolment stops if the
    server's CA does not match it. Every call the agent makes, its analyser relays included, is pinned the same way.
  - A CA rollover keeps the pins, in two steps (docs/deployment/CA-ROLLOVER.md). `certificates prepare-ca` makes the
    next CA (the current one's name, a new key) and a certificate the current CA signs for that key; the server keeps
    serving with the current CA and hands both out. HC360 Desktop and the lab agent hold the next CA as a second pin
    only on the current CA's word (`WindowsInterop/Trust/HospitalCaSuccession`): over a connection already pinned to
    the current CA they fetch what the server hands out, and take a CA only when it is in the pinned CA's name, with
    another key, and comes with a CA certificate for that name and key that the pinned CA itself signed, valid now. A
    CA in the hospital's name signed by its own key, by another hospital's CA, or not a CA, is refused. Each reports in
    its heartbeat the CAs it holds. `certificates switch-ca` is refused while any device seen in the last 30 days, and
    not revoked, does not hold the next CA, as the database says; it then makes the next CA the server's. Devices pin it
    within six hours, and keep the CA it replaced as a second pin until it expires (`Trust/HospitalCaPins`, one copy of
    the check for both apps), so a standby not switched yet still serves them. Nothing else is trusted more: a device
    trusts the CA it pins, the next CA its pinned CA vouched for, and the CA its pinned CA replaced, and a device that
    pins another CA still refuses the server. A CA whose key may have been seen is never rolled over, since whoever
    holds the key could sign a "next" CA of their own: it is replaced (`certificates new-ca`), and every device set up
    again.
  - The server only ever sends its own certificate, and nothing is put into the Server Node's certificate stores: no
    rollover depends on what Windows' TLS would send with it.
  - A device builds the chain from what the server sent in the handshake and its pinned CAs only. Windows also takes
    certificates from the computer's own stores to build a chain; whether the server is trusted does not depend on what
    happens to be there. The certificate probe checks the same way.
  - An IT policy fingerprint (HC360 Desktop) is compared with the certificate the workstation pins, not with the
    fingerprint written beside it in the user's settings file, which the user can edit. A workstation that switched in
    a rollover keeps a policy that still names the CA it replaced only when that CA's signature on the new key, kept
    since, shows it (`HospitalCaSuccession.Succeeded`). A policy that names the next CA a workstation holds has it pin
    that CA alone, from the policy's own fingerprint: nothing the policy names vouches for the CA it pinned until then.
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
- **Temporary passwords** (account recovery: there is no self-service reset) are 12 random characters, or the
  administrator's choice when creating an account. They must be replaced at first sign-in, and stop working after 72
  hours (`TemporaryPasswordHours`); a new reset then gives out another. The first administrator's one-time password is
  written to a file only administrators can read, and the file is deleted once the password is changed.
  - **A reset goes to the account's holder, not the administrator** (MAKER-CHECKER.md, slice 4): an administrator shown
    a colleague's temporary password could sign in as them, and doctors and nurses need no second step by default. The
    reset sends it to the account's own mobile number by SMS and to its e-mail address (`auth.temporary_password`; the
    outbox drops a message not sent before the password expires), and tells the administrator only where it went,
    masked ("mobile +91******3210").
  - An account with neither waits for a second administrator who holds `admin.grants.approve`
    (`identity.password_reset`); nothing changes until then. Approving makes the password and shows it to that
    administrator once, in the answer to the approval, to give in person (`ApprovalHandover`): it is kept nowhere, not
    in the request, its outcome, the audit trail or the notice to the one who asked. The audit names both.
  - Either way the sessions end, the PIN and the quick-unlock devices go, and the password must be changed at the next
    sign-in. Enter a mobile number for every member of staff, so a reset does not wait.
- **Notices** (ASVS V6.4.3): when a password is changed or reset, or two-step verification is turned on, off or reset,
  the account's holder is told in the app and, when the account has an e-mail address, by e-mail
  (`auth.credentials_changed`). When the account's mobile number or e-mail address changes, the number and address it
  had before are told, by SMS and e-mail, naming the new ones masked (`auth.contact_changed`). Someone who did not make
  the change can tell IT at once.
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
- **Administration needs an enrolled device** (SRS SEC-08). The administrative permissions (`admin.*` and all-access:
  users and roles, settings, devices, backups, integrations, the audit log, the server's own credentials) work only in
  HC360 Desktop on a workstation or tablet IT enrolled; a kiosk or queue display signs staff in, but administers
  nothing, like a browser. A browser keeps everything else its user holds:
  - The server refuses the request (403 `auth.device_required`, saying where administration works) when only an
    administrative permission allows it and the session was not signed in with an enrolled workstation's or tablet's
    credential. Any
    other permission the endpoint takes is enough anywhere. The app is given the permissions the session can use, so it
    shows no administration there, and says where it is.
  - Revoking a desktop ends its sessions at once; its credential then signs in as a browser does, without administration.
  - While no workstation or tablet is enrolled (a new installation, or one whose desktops are kiosks and queue displays
    only), a browser may still list the devices and make an enrolment code, and nothing else, so the first workstation
    can be enrolled.
  - **Another administration desktop is enrolled by two people** (MAKER-CHECKER.md, slice 4): whoever enrols a
    workstation or tablet decides where the hospital can be administered from. Once one is enrolled, a code for another
    waits for a second administrator who holds `admin.grants.approve` (`devices.enrollment`, 202). Approved, the code is
    made for the administrator who asked, who is at the computer: Administration › Devices shows it to them once
    (`POST /api/v1/admin/devices/enrollment-codes/requests/{id}/code`), it works once, and it expires the minutes asked
    after the approval (30 by default). It is chosen only when shown, so the approver never sees it and the server keeps
    only its hash. The first enrolment, at installation, and codes for kiosks, queue displays and lab agents, which
    administer nothing, are made at once as before.
  - The `security.administrationRequiresDesktop` setting (Administration › Settings, for the whole hospital) turns it
    off; without it, `HealthCare360:Security:AdministrationRequiresDesktop` in the server's settings file decides, on by
    default. Turned off, an administrator's password alone opens administration from any computer on the network.
- Selected roles can also be limited to **enrolled devices** for signing in at all:
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
  An IPv6 address from outside the hospital counts by its /64 (Rate limits, below).
- Every pathway but the analyser and modality connections is HTTPS only.

| Pathway | Proof, and its strength | What stops guessing |
|---|---|---|
| **Staff sign-in** (staff app, HC360 Desktop) | Username and password: at least 12 characters, none from breach lists. The SuperAdmin, HospitalAdmin, Accountant and HrManager roles, and anyone holding an administrative permission, also give a 6-digit authenticator code (TOTP): two factors (NIST AAL2) for them, one (AAL1) for other staff. Administration works only in HC360 Desktop on an enrolled computer. A role can be limited to enrolled HC360 Desktops for every sign-in; none is by default | The account locks after 5 wrong passwords or codes in a row, for 15 minutes, doubling up to 24 hours |
| **Renewing a session** | The refresh cookie: 256 random bits, replaced on every use, sent with the `X-HC360-Client` header. A spent one presented again, past a minute's grace for a lost response, ends the session. Sessions end 12 hours after sign-in, or after 2 hours unused (portal: 20 minutes) | Too large to guess |
| **Lock screen** | The PIN (4 to 6 digits, not all the same or in sequence), the password, or, where the hospital turned it on, a device set up for it: a WebAuthn signature of a one-time challenge by the device's Windows Hello or fingerprint reader, which verifies the person first. It opens only the session it locked | 5 wrong ones (PINs, passwords and refused device answers together) end the session |
| **Password prompts** before sensitive actions | The password again, from a signed-in staff member | Wrong ones count toward the account lock |
| **Patient portal sign-in** | A 6-digit code sent by SMS to the registered number, valid 5 minutes: one factor (AAL1), as strong as the phone number | 5 tries per code; 3 codes per number in 15 minutes (the refusal, `auth.otp_throttled`, gives `retryAfterSeconds` until the next is allowed, and the portal counts down to it); a proof of work for asking for a code and for giving it (Anti-automation, below) |
| **Link to the portal** (staff send it to the patient's registered number) | Not a credential. A 128-bit token, stored as its SHA-256, valid 7 days, until a newer link is sent, and only while the record still has the number it went to (and is in use and in no protected category). It lets whoever opens it see the hospital and the number masked, and send the portal sign-in's code to that number; signing in then takes the code, as above | Too large to guess; any token that does not work gets one 404; asking for a code takes a proof of work and the sign-in's limits (5 code requests per address and 3 codes per number in 15 minutes); staff may send 3 links per patient in 24 hours |
| **Changing a patient's registered number** (staff, at the desk) | Not a sign-in, but it decides where the portal's sign-in code goes. A new number becomes the patient's only with a 6-digit code sent by SMS to that number and entered by staff who edit patients (`patients.register` and `patients.contact.read`), as the patient reads it out; valid 10 minutes, stored salted and hashed. Nothing goes to the old number | 5 tries per code, judged one at a time; 3 codes per number in 15 minutes (`patient.phone_code_throttled` with `retryAfterSeconds`); made only while the record still has the number it had when the code was sent |
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
- **Access is given by two people**. What widens someone's access waits for a second person who holds
  `admin.grants.approve` (hospital administrators by default; the Super Administrator holds everything):
  - roles or facilities added to a user, a new user's included, and a user reactivated (`identity.user_grant`);
  - permissions added to a role, a new role's included (`identity.role_grant`).

  What narrows it applies at once: roles, facilities or permissions taken away, a user deactivated, a role deleted.
  - Only someone who holds an administrative permission approves a grant of it, as only they may give it.
  - Nobody approves their own request (`approval.self`), nor access given to themselves (`access.self_grant`). A grant to
    yourself, your own account or a role you hold, waits for someone else, and is refused when nobody else may approve it.
  - The approver gives what was asked, if it may still be given: the user still active (unless the grant reactivates
    them), the roles and facilities still there, and the approver allowed to manage the user and to give those roles.
  - The user and role pages show a grant that waits, with a link to its request; the one who asked can withdraw it there.
  - A hospital setting up has one person who may approve grants: until it has had two, grants are given at once and
    audited as `access.granted_alone`. From the first time it has two (`access.two_approvers` in the audit trail, never
    undone), a grant nobody else may approve is refused (`access.no_second_approver`). The exception is a Super
    Administrator's, given at once as a break-glass: audited as critical, and System health is critical for a day after.
    A live server with fewer than two people who may approve grants says so (`access.one_approver`).
- **Nobody takes over a colleague's account alone** (MAKER-CHECKER.md, slice 4; `AccountApprovals`). One holder of
  `admin.users.manage` could change a nurse's e-mail address, reset her password and two-step verification, and sign in
  as her; or link an account to a consultant's practitioner record and sign his notes, prescriptions, discharge
  summaries and ABDM records. Now:
  - a temporary password goes to the account's own mobile number and e-mail address (Temporary passwords, above);
  - a two-step verification reset waits for a second administrator (`identity.mfa_reset`); nothing changes until it is
    approved, and then the sessions end and the holder is told, as before;
  - another user's e-mail address or mobile number waits (`identity.contact_change`), and once it changes the old ones
    are told. A user's own contact details, saved from Administration › Users (there is no self-service screen), change
    at once, and the old ones are told too;
  - linking or re-linking an account to a practitioner waits (`identity.practitioner_link`), the new account's link
    included; taking a link away applies at once. The approver reads the practitioner linked now and the one asked, by
    name, code, kind and registration, and any other account linked to it;
  - a linked practitioner's registration number, HPR ID or signature image waits (`masters.practitioner_identity`,
    masters.md); a practitioner with no account is edited at once.

  The checker holds `admin.grants.approve` (hospital administrators by default, and the Super Administrator) and, for
  an account with administrative permissions, all of them. Whether a change waits is decided as for access given: a
  hospital that has never had two people who may approve makes it at once, audited as `access.granted_alone`; once it
  has had two, a change nobody else may approve is refused, but for a Super Administrator's break-glass. A change to
  one's own account waits for someone else, and nobody approves a change to their own account (`approval.own_account`).
  A change is made only over the account as it was asked for (`approval.stale`): over the fields it is about (the
  password and contact details, the two-step verification, the contact details, the link), not the row's version, which
  a locked-out user's sign-in attempts change too. One request of each kind waits per account (409
  `approval.already_requested`). Each is audited with both names (`user.password_reset`, `user.mfa_reset`,
  `user.contact_changed`, `user.practitioner_linked`). The user's page lists what waits, with links to the requests.
- **Restricted records** (VIP, staff, high-profile patients):
  - They need an extra permission or **break-the-glass** access.
  - Break-the-glass requires a written reason, expires, and is flagged for review. The grant is a row in the database
    (`patients.break_glass_grants`), so it holds, and expires, on every server alike.
  - Lists don't name them unless the user may open restricted records, or has break-the-glass access to that one. Patient
    search and the board of patients in the hospital show the initials, "R. K. S. (restricted)", so the desk can tell two
    results apart. The bed board, the doctor's worklist and the ward round show "Restricted patient". The worklist also
    leaves out the complaint, booking reason, note title, result name and deficiency, and the round leaves out the
    diagnosis and vital-sign details. The token, bed, UHID and safety flags stay, so the patient is still seen to.
  - Their stored files (documents, photos, reports) follow the same rules: opening a file, or its details, by its id
    is refused (403 `patient.restricted`) until the user may open the record, so knowing a file's id is not a way in.
- **Sensitive categories** (HIV, mental health, termination of pregnancy, sexual violence; HIV/AIDS Act 2017, Mental
  Healthcare Act 2017, MTP Act s.5A, Bharatiya Nyaya Sanhita 2023 s.72 and POCSO Act 2012 s.23):
  - An HIV (B20–B24, Z21), mental-illness (F20–F69), medical-abortion (O04) or sexual-abuse (T74.2, T76.2, Y05, Z04.4)
    diagnosis puts the record in the category, and so does a released HIV test result outside its normal answers
    (reactive or indeterminate), or one corrected to it (a result reported under an HIV panel, such as a band of an HIV
    confirmation, is one), a sexual assault entered in the MLC register, and a procedure that ends a pregnancy
    (ICD-10-PCS 10A) or is electroconvulsive therapy (GZB), recorded on the chart or completed in theatre;
    a doctor (`patients.sensitive.manage`) can add or remove one with a reason, audited as a security event.
  - The clinical record (notes, diagnoses, encounters, prescriptions written, nursing and MAR, OT, dialysis, teleconsults,
    emergency cases with their MLC, files) then opens only to staff holding the category: `patients.sensitive.hiv`, `.mental_health` and
    `.sexual_violence` (doctors, nurses, medical records), `patients.sensitive.mtp` (medical records; grant it to
    gynaecologists), to those who may open restricted records, or through break-the-glass, which is audited as for VIP
    records.
  - The patient's details, bills, pharmacy, lab and radiology orders, appointments, allergies and alerts are not closed,
    so the desk and the departments keep serving the patient; the categories themselves show only to doctors. The
    patient's journey (where they are in their visit, stay and lab orders) stays open too, without their operations.
  - The FHIR API does not list these patients to clients that may not open restricted records.
  - **A shared number.** Families often share one mobile number, and whoever holds it signs in to the patient portal
    and reads what is sent to it. So a record in a category is reached through a number only when the number is the
    patient's alone (`SharedNumbers`): when another active patient is registered with the same number, the portal leaves
    the record out for that number, and the report-ready, prescription and bill messages are not sent to it by SMS or
    WhatsApp (a report still goes by e-mail to the patient's own address). A family member's account on another number
    never sees it. The patient gets these at the hospital; on a number that is theirs alone nothing changes. The billing
    desk is told only that the bill can't be sent to that number, not why.
  - The upgrade puts records that already carry such a diagnosis into its category; a merge carries the duplicate's
    categories to the surviving record. A doctor manages the categories without being able to open the chart.
- **Terminations of pregnancy** (MTP Act s.5A): an MTP case opens only to `mtp.read` and `mtp.certify` (medical records
  reads; certifying is granted to the gynaecologists), the register and the monthly report only to `mtp.register`.
  Opening a case puts the record in the MTP category; every view of a case, the list and the register, every print and
  every export is audited. A restricted (VIP) patient's row in the list and the register shows her initials only to
  staff who may not open her record, and the data summary does not count MTP cases.
- **Who may look patients up.** Patient search and visits are for staff who see patients: the canteen, CSSD,
  biomedical, HR, store, purchase and MIS roles do not get `patients.read` or `visits.read` by default. An upgrade takes
  a default grant back from a role that no longer has it (`identity.seeded_grants` records what the seeder gave), and
  leaves alone any grant an administrator gave by hand.
- **Maker-checker.** Some changes are made only when a second person approves them (`ApprovalRequest`,
  `/api/v1/approvals`): what is still done on one person's word, and the order it is being closed in, is in
  [MAKER-CHECKER.md](MAKER-CHECKER.md). They are:
  - credit notes and write-offs on a finalized bill (audit finding S6): a cashier or the billing desk asks
    (`billing.refunds.request`), an approver issues (`billing.refunds.approve`);
  - money recorded that did not move: a receipt whose money never arrived (a cheque returned, a payment the bank never
    settled, one a training simulator made up), and a refund recorded as paid out that never reached the patient. The
    same two permissions; the approver sees what changes, and the reversal is made only if it would still do exactly
    that. Cash is never reversed: a shortfall is the shift's (billing.md, Money that did not move);
  - withdrawing a card and UPI settlement report imported in error (`billing.pos_import_withdrawal`): whoever reconciles
    the card and UPI payouts (`billing.gateway.reconcile`) asks with the reason, and another who holds
    `billing.refunds.approve` approves, shown what the report brought and matched. Its transactions are reversed in the
    books of the day it is approved, never removed (billing.md, Card and UPI reconciliation);
  - merging a duplicate patient record into another: one holder of `patients.merge` asks, another approves;
  - a third party paying for a visit or a stay (an insurer, a company, a government scheme): the desk names it, and it
    is accepted only on the patient's policy or membership, in date and verified with the payer by someone other than
    whoever recorded its terms or last changed them (`PayerChoice`; `insurance.policy_self_verify`). Changing what a
    verification vouches for withdraws it (billing.md, A third party pays only on a second person's word);
  - access given to a user or a role (see "Access is given by two people" above): one administrator asks, another who
    holds `admin.grants.approve`, and any administrative permission it gives, approves;
  - changes that would let one administrator take over an account (see "Nobody takes over a colleague's account alone"
    above): a temporary password for an account with nowhere to send it (`identity.password_reset`, shown once to the
    approver), a two-step verification reset (`identity.mfa_reset`), another user's e-mail address or mobile number
    (`identity.contact_change`), an account linked to a practitioner (`identity.practitioner_link`) and a linked
    practitioner's registration number, HPR ID or signature (`masters.practitioner_identity`): one administrator asks,
    another who holds `admin.grants.approve` approves. A temporary password otherwise goes to the account's own mobile
    number and e-mail address, never to the administrator;
  - another workstation or tablet enrolled once one is (`devices.enrollment`, see "Administration needs an enrolled
    device"): one administrator asks, another who holds `admin.grants.approve` approves, and the code is shown once to
    the one who asked;
  - a service's own price and GST rate, which a self-pay patient pays (`masters.service_price`): one person saves the
    service, another who holds `masters.prices.approve` (accountants and hospital administrators by default) approves.
  - a drug handled less strictly in the item master (`inventory.drug_class`): no longer controlled (NDPS), no longer
    high-alert, or on a lower schedule. Whoever keeps the item master (`inventory.items.manage`: store keepers, purchase
    officers) asks with the reason, and someone else who holds `inventory.drug_class.approve` (pharmacists and hospital
    administrators by default) approves. The approver is told what the change switches off: the statutory register, the
    witness at the bedside, the prescription. The rest of the item is saved at once, and the drug keeps its class until
    approved. An approval is refused (`approval.stale`) when the class changed after it was asked for. Made stricter, a
    class applies at once.
    A new price or GST rate, a price the counter may type, discounts allowed and a service put in use wait; what only
    narrows them (no counter price, no discounts, out of use) applies at once. While a change waits, any other change to
    the price is refused (`approval.already_requested`), the price in use put back included. A new service is made out
    of use at ₹0 and goes live at the price approved. The approver sets the terms only over those the request replaced
    (`prices.changed`), so a change made after it was asked for is never overwritten. The service's form shows a price
    that waits, with its request;
  - a tariff plan's prices and terms (`billing.tariff_prices`, `billing.tariff_plan`): the billing desk asks, someone
    else who holds `masters.prices.approve` approves. Every change waits, as for a plan nothing only narrows (taken out of
    use, its patients are charged by another plan); a bulk revision is one request, and a new plan is made out of use
    until its terms are approved. Approving is made only over the very rows and terms it replaces (`prices.changed`);
  - a payer's terms (`insurance.payer_terms`): the insurance desk asks, someone else who holds `masters.prices.approve`
    approves its category, tariff plan, co-pay and putting it in use; taking it out of use is made at once, and a new
    payer is added out of use until its terms are approved. Approving is made only over the terms it replaces;
  - a stay billed on another plan than its payer's (`billing.ip_plan`): the billing desk asks, with the payer and policy
    asked with it, and someone else who holds `billing.discounts.approve` approves; the request reads what the stay's
    unbilled services come to on the payer's plan and on the plan asked. A stay keeps an approved plan while its payer
    stays, and a new payer brings its own plan at once;
  - a billing package's price and cover (`billing.package`): the billing desk asks, someone else who holds
    `masters.prices.approve` approves its price, the service it is billed as, what it covers, the plan it is priced for,
    the bills it may price and putting it in use; the request says how many stays in progress are on it, as their final
    bills take the package as approved. Taking it out of use is made at once, and a new package is made out of use at ₹0
    covering nothing until its terms are approved. Approving is made only over the terms it replaces;
  - a health check-up package's price: its service's price (`masters.service_price`), asked for from the package's form
    as from the services master; a new check-up is made out of use, and is not booked, until its price is approved, and
    the package follows its service's price and use whichever screen set them;
  - an import of AB PM-JAY's HBP master (`billing.hbp_master`): the tariff keeper (`billing.tariffs.manage`) stages NHA's
    file, and another who holds `masters.prices.approve` puts it in force after reading what it changes (the file's
    SHA-256, procedures new, changed and retired, scheme rates, and the hospital's packages mapped to procedures it
    retires, reserves for public hospitals or rates otherwise). The rows staged are applied only while they match their
    SHA-256, and only over the master they were read against; one import waits at a time;
  - a doctor's sharing rule (`doctorfees.rule`): whoever keeps the rules (`doctorfees.rules.manage`) asks, and another
    who holds `masters.prices.approve` approves every change of its terms, taking it out of use included (its lines fall
    back to a broader rule, which may share more); a new rule is made out of use until approved, and a rule in use is not
    deleted. The approver is shown the share before and after, backdating, and what the change would have done to the
    last 90 days' bill lines;
  - the non-payable list (`insurance.non_payable`): adding an item (the patient then pays for it on insured bills) and
    taking one off (the payer is charged again) are both asked for by whoever keeps the payers and approved by another
    who holds `masters.prices.approve`, shown the insured patients' charges not yet billed that the change would move;
  - correcting a released lab report where the lab sets `lab.separateVerifier`: one pathologist asks, another releases
    the correction by approving it (`lab.results.verify`). Until then nothing on the report changes, and every copy says
    a correction is waiting. The amendment names both, and the corrected results are verified by the one who approved.
    A corrected critical value is asked for with the documented call, registered when the correction is made.
  - a death certificate (MCCD, Form 4; `mrd.death_certificate`): the doctor who certifies it asks, and a second doctor
    (`mrd.deaths.confirm`) confirms the certificate as it stands before it is reported to the Registrar. The confirming
    doctor must be able to open the patient's record, and is printed on the certificate with the registration number of
    their practitioner record. A correction takes the confirmation away and asks again; the certificate is not changed
    while a request waits. The death itself (the patient deceased) is recorded at once. On unless the facility sets
    `mrd.deathCertificateConfirmation` to `false`, as one with a single doctor does.
  - a goods receipt posted into stock (`purchase.grn`): the store enters the receipt and sends it for approval, and
    someone who holds `purchase.grn.approve` (purchase officers and hospital administrators by default) checks it against
    the supplier's invoice and approves it, which posts it. The request shows the invoice's total as printed beside the
    total worked out from the lines. Whoever entered the receipt never posts it either (`grn.post_own`), even when
    someone else sent it, and a receipt cancelled after it was sent posts nothing (`approval.stale`).
  - how long the audit trail is kept (`audit.retention`) and a purge of the months it no longer keeps (`audit.purge`):
    one holder of `admin.system.manage` asks, another approves (see Integrity and audit).
  - a setting that loosens a control, or changes where money, patients' data or alerts go (`settings.change`,
    `SettingGuard`): one holder of `admin.settings.manage` asks with the reason (`PUT /api/v1/admin/settings/{key}`
    answers 202, and 400 on `reason` without one), and another approves, shown the setting, what it controls, where it
    applies, the value in force and the one asked ("Second pathologist for lab corrections: on → off"). That is
    switching off the second pathologist (`lab.separateVerifier`), the second doctor on death certificates
    (`mrd.deathCertificateConfirmation`), the second blood bank officer (`bloodbank.separateVerifier`), administration
    only on enrolled desktops (`security.administrationRequiresDesktop`), payment before a lab sample
    (`lab.requirePaymentBeforeCollection`) or the cash counter shift (`billing.shift.required`); switching on Windows
    Hello unlock (`ui.biometricUnlock`) or AI (`ai.enabled`); raising an own-authority discount limit
    (`billing.discount.defaultMaxPercent`, `pharmacy.discount.max_percent`), what the billing desk cancels or a cash
    shortage it closes without a second person (`billing.chargeCancel.approvalAbove`, `billing.shift.shortageTolerance`),
    the canteen's staff credit, the sessions
    per account, the screen-lock or MAR grace time or a delta check's threshold (0 for none counts as the highest), or
    shortening its look-back; clearing any of them, so the default decides; and any change to the portal's address in
    every SMS and QR code (`portal.baseUrl`), the TDS on doctors' fees, the critical alerts' numbers, times and
    heartbeat (`alerts.*`), an integration (`integrations.*`), the ABDM gateway, the AI provider and its addresses, and
    any credential (a `*.secret` key, or a value saved as secret). The stricter direction is saved at once, and every
    other setting as before. A credential asked is held with the request only as the settings store holds it, encrypted
    with the data key, shown as "a new secret value (not shown)", and decrypted only to be saved. Approved, the change
    is saved through the settings store only over the value it was asked against (`approval.stale`), one waits per
    setting and facility (`approval.already_requested`), and it is audited as `setting.changed` at Warning with who
    asked and who approved. The Features screen's switches are not guarded: one switched off stops a service, never a
    control. Lab setup's analyser list (`integrations.lab.instruments`) keeps its own screen and permission: it saves
    only analysers Lab setup has, each bound to its allowed addresses and audited, while the same key through the
    settings API waits.
  - money out of the pharmacy (`pharmacy.sale_cancel`, `pharmacy.sale_return`): a same-day invoice cancelled, and a
    return that pays money out or writes stock off, are asked for by a pharmacist (`pharmacy.returns`) who neither sold
    the invoice nor took its payment, and approved by someone else who approves every other refund
    (`billing.refunds.approve`: accountants and hospital administrators by default), never by whoever sold it either.
    The endpoint answers 202 with the request, and nothing moves until it is approved: no refund, no stock back, no
    register entry. The approver reads the invoice, the patient, the items and quantities, the amount, how the money
    goes back ("₹400.00 in cash, ₹600.00 back to the card (RRN …)"), whether the stock goes back, and the reason. Money
    goes back the way it came: each refund names the payment it pays back (`refund_of_payment_id`), in that payment's
    mode and under its reference, so a card or UPI payment is never paid out of the till and the card and UPI settlement
    matches the refund; a partial return is shared among the payments in proportion. A cancellation is approved only on
    the invoice's own day, and a request is made only over the invoice as it was asked (`approval.stale` after a
    payment, a return, an IRN, a line expiring or midnight). A return not restocked is a write-off: it comes back into
    its batch and goes out again as a write-off, in the stock ledger and, for a controlled drug, the register. A return
    restocked and set wholly against the balance due is made at once (pharmacy.md, Refunds and write-offs wait for a
    second person);
  - a ward return not restocked (`pharmacy.ward_return`): one pharmacist who issues to the wards asks, and another
    (`pharmacy.ip.issue`) approves, after opening the patient's record; nothing is credited or written off until then. A
    controlled drug is entered in the register as returned from the ward and written off, recorded by the approving
    pharmacist with the one who asked. A ward return restocked is made at once.
  - blood released from quarantine (MAKER-CHECKER.md, slice 3; a second officer verifies, with no approval request): a
    donation's grouping and TTI results that clear it leave its units in quarantine, "awaiting verification", not
    reserved or issued. Another blood bank officer who holds `bloodbank.inventory.manage` (the permission that enters
    results and prepares and discards stock: blood bank officers by default) verifies them as they were shown
    (`POST /api/v1/blood-bank/donations/{id}/lab-results/verify`, with the donation's version), and only then do the
    units go to stock. The donation names who entered and who verified, by account, and the verification is audited with
    both names (`bloodbank.results_verified`). Until then only the officer who entered complete results corrects them
    (`bloodbank.results_entered_by_other`), and once verified nobody does (`bloodbank.results_final`). A reactive result
    discards the units and defers the donor at once, as before. A facility with one officer on duty sets
    `bloodbank.separateVerifier` to `false` (on by default: the Drugs and Cosmetics Rules, Schedule F Part XII-B, and
    the NBTC standards expect the medical officer's check), which waits for a second administrator: that officer then
    verifies their own results, audited at Warning, and the donation is flagged and listed for the medical officer's
    review (`GET …/donations?oneOfficer=true`);
  - uncross-matched blood (slice 3): released only on an emergency request a doctor has authorised from their own
    sign-in (`POST /api/v1/blood-bank/requests/{id}/emergency-release`): a holder of `bloodbank.requests.create` whose
    account is linked to a doctor in the practitioner list (consultant, resident or visiting), never a name the blood
    bank types (`bloodbank.doctor_required`, 403). The request keeps the doctor's account, practitioner record and the
    time; the authorisation and each unit issued on it are audited at Warning, naming the doctor and the issuing
    officer. The doctor's authorisation is the second person: no approval inbox stands in the way of blood in an
    emergency.
  - the prescribing-safety tables loosened (MAKER-CHECKER.md, slice 5; `clinical.drug_interaction`,
    `clinical.dose_limit`): a drug interaction switched off, made less severe or moved to another pair of drugs, and a
    maximum daily dose switched off, raised, or moved to another drug or a narrower route, are asked for, with the
    reason, by whoever keeps the tables (`clinical.templates.manage`: doctors and hospital administrators;
    `PUT /api/v1/clinical/drug-interactions/{id}` and `…/dose-limits/{id}` answer 202, and 400 on `reason` without
    one), and approved by someone else who holds `clinical.drug_safety.approve` (the pharmacy and therapeutics committee:
    pharmacists and hospital administrators by default). The approver reads the row now and as asked ("aspirin +
    warfarin: Major, blocks signing" → "Moderate, a warning") and what it switches off for every prescriber in the
    hospital. The row is unchanged until then, the tables show the change waiting, and it is changed only if it is still
    as it was asked against (`approval.stale`), audited at Warning with both names (`clinical.drug_interaction_changed`,
    `clinical.dose_limit_changed`). Adding or strengthening an alert (a new row, a higher severity, a lower maximum, every
    route, switching one on) and changing only its words are saved at once, audited (`clinical.drug_interaction_saved`,
    `clinical.dose_limit_saved`). A row already switched off alerts nobody, so changing it waits for nothing.
  - a ward medication order written in a doctor's name by anyone else (slice 5; the doctor is the second person, with
    no approval inbox): only a prescriber (`clinical.prescriptions.sign`) writing in their own name, signed in as the
    practitioner it names, signs a ward order at once (`POST /api/v1/nursing/admissions/{id}/medication-orders`). A
    nurse, or a doctor writing another doctor's order, records that doctor's verbal or telephone order: the doctor (an
    active doctor of the practitioner list), whether it was given face to face or on the telephone, and that it was read
    back are required (`medication.verbal_order_required`, `medication.read_back_required`). It stays awaiting
    countersignature, and only the doctor named countersigns it, from the account linked to their practitioner record
    and holding `clinical.prescriptions.sign` (`POST …/medication-orders/{id}/countersign`, with the order's version;
    `medication.countersign_not_prescriber`, 403, for anyone else, the nurse who wrote it and another doctor included).
    A high-alert, controlled or chemotherapy medicine ordered so gives no dose until countersigned: nothing goes on the
    round, and no extra dose is recorded against it (`medication.awaiting_countersignature`), unless the nurse wrote it
    as an emergency verbal order, read back, when doses are given, each witnessed as below, and the order stays flagged.
    The doctor has the hospital's time to countersign (`inpatient.verbalOrderCountersignHours`, 24 hours by default, 1 to
    72; lengthening it waits for a second administrator); past it, the order is flagged, listed (`GET
    …/medication-orders/awaiting-countersignature?wardId=&mine=&overdueOnly=`), and the doctor and the facility's nurses
    in charge (`nursing.in_charge`) are told once, audited at Warning (`medication.countersign_overdue`). Each verbal
    order is audited at Warning as written (`medication.verbal_order`), and its countersignature with the doctor's name.
  - stopping a ward medication order (slice 5): a prescriber (`clinical.prescriptions.sign`) stops it at once. Anyone
    else's stop is recorded as asked for, the order stays on the chart with every dose held (no dose given:
    `medication.stop_pending`), and the doctor named on the order (the patient's consultant for one that names none)
    confirms it (`POST …/medication-orders/{id}/stop/countersign`), when the doses held since are cancelled, or declines
    it (`…/stop/decline`, with the reason, told to the nurse), when the doses are given again. Holding the doses is the
    safe direction while it waits; the same time and overdue notice apply.
  - a refill (`POST /api/v1/clinical/prescriptions/{id}/refill`, slice 5): it carries the original doctor's name and
    registration as before, and records who refilled it (`refilledByName`, `signedByUserId`; audited
    `prescription.refilled`), so a pharmacist's refill never reads as the doctor's signature. A prescription with a
    controlled drug (Schedule H1 or X, or NDPS, as the drug master says at refill) is not refilled
    (`prescription.refill_controlled`). A blocking safety alert found at refill (an allergy recorded since, a new
    interacting medicine) is overridden only by the doctor who wrote the prescription, signed in as themselves: anyone
    else's refill is refused (`prescription.refill_needs_prescriber`, nothing used up), the doctor is told, and the
    refusal is audited (`prescription.refill_blocked`). Another doctor repeats the prescription as their own and signs it.
  - the billing desk's money decisions (MAKER-CHECKER.md, slice 6):
    - a discount above the giver's own limit waits for someone else who holds `billing.discounts.approve`, whoever gives
      it: holding the approval permission no longer lets anyone discount without a limit, and an approver's own discount
      is approved by another approver (`billing.discount_self_approval`), who is recorded on the bill as its approver.
      Within their own limit (their `DiscountLimit`, or `billing.discount.defaultMaxPercent`) a discount is made at once,
      and nobody is recorded as approving it. Free treatment by law (BNSS s.397) is unaffected: nothing is discounted;
    - an own-authority discount limit raised (`billing.discount_limit`: a higher percentage, a rupee cap raised or taken
      away, or a limit removed so a higher default applies): one holder of `billing.discounts.approve` asks
      (`PUT /api/v1/billing/discount-limits` and `DELETE …/{id}` answer 202), another approves, never the user whose limit
      it is (`billing.discount_limit_own`, 403, which also refuses asking to raise one's own), shown the limit before and
      after in words. Made only over the user's limit as it was asked against (`approval.stale`), audited
      `billing.discount_limit.raised` at Warning with both names. Lowering a limit is saved at once;
    - an unbilled charge cancelled at the billing desk (`billing.charge_cancel`): an OT (surgery), procedure, implant or
      bed-day charge always, and any other worth more than `billing.chargeCancel.approvalAbove` (₹1,000 by default; 0
      makes every cancellation wait), is asked for by the desk (`billing.charges.manage`;
      `POST /api/v1/billing/charges/{id}/cancel` answers 202) and approved by someone else who holds
      `billing.discounts.approve`, as a cancelled charge is a discount by another name. The charge stays billable
      meanwhile; it is cancelled only while still unbilled at the amount asked (`approval.stale` once it is billed),
      audited `billing.charge.cancelled` with both names. A credit (a return) cancelled makes the patient owe more and
      never waits. Charges a module cancels itself (an order withdrawn, a visit or stay cancelled, a package changed) are
      cancelled at once as before;
    - a cash shortage at a shift's close beyond `billing.shift.shortageTolerance` (₹0 by default: every shortage): the
      shift closes (no money moves in it, and the cashier may open another), with the cashier's reason required, but it
      stays "closed — shortage awaiting supervisor" in the shift list, the day-end report and the Tally records check
      until someone else who holds `billing.refunds.approve` accepts it (`billing.shift_shortage`; the close answers
      202), never the cashier whose shift it is (`billing.shift_shortage_own`) nor whoever asked. The acceptance is kept
      on the shift (who, when) and audited `billing.shift.shortage_accepted`. A rejected or expired request leaves the
      shortage awaiting, and is asked again (`POST /api/v1/billing/shifts/{id}/shortage`). An excess is recorded and
      flagged (`billing.shift.variance` at Warning) and needs no one: no money is missing;
    - a doctor's consultation fee (`masters.practitioner_fee`): it is charged to a self-pay patient instead of the
      consultation service's approved price (a payer's negotiated tariff still wins) and is the fee appointments are
      booked at, so it changes only as a service's price does: whoever saves the practitioner asks, someone else who holds
      `masters.prices.approve` approves, never the doctor whose fee it is (`masters.practitioner_fee_own`), shown the fees
      before and after in words. The rest of the record is saved at once and the fees in force are charged until then; a
      new doctor starts with none of their own (the service price) until theirs is approved. Another fee while one waits
      is refused (`approval.already_requested`), and a fee is set only over the fees it replaces (`approval.stale`),
      audited `practitioner.fee_changed` with both names;
  - The person who asked never approves (`approval.self`, checked by user, however many roles they hold), and the
    approver must hold the kind's approving permission when deciding.
  - The request keeps the change exactly as asked, with its SHA-256; a request changed in the database after it was
    asked is refused (`approval.altered`, audited as critical).
  - The change is made in the approval's own transaction, and only on the record as it was when it was asked for (its
    version): a bill paid or credited since, or a patient record changed since, fails the request (`approval.stale`)
    and the person who asked is told to ask again.
  - A request waits at most 7 days. Only the person who asked withdraws it. Asking, approving, rejecting, withdrawing
    and a failed approval are audited, and the credit note keeps both names.
- **Separation of duties.** No one approves or undoes their own work:
  - whoever finalised a bill cannot cancel it (`billing.cancel_own_bill`), as refunds, credit notes and write-offs
    already need a second person (a discount needs one above the giver's own limit, an approver's included, and is
    never approved by whoever asked: `billing.discount_self_approval`);
  - nobody raises their own discount limit, nor accepts the cash shortage of their own shift, nor approves their own
    consultation fee (`billing.discount_limit_own`, `billing.shift_shortage_own`, `masters.practitioner_fee_own`);
  - whoever raised a purchase requisition or order cannot approve it (`requisition.approve_own`, `po.approve_own`);
  - whoever sold a pharmacy invoice, or took a payment on it, does not ask for its cancellation or take a return of it,
    nor approve either (`pharmacy.cancel_own_sale`, `pharmacy.return_own_sale`, 403 whatever else they may do), and the
    pharmacist who asks for a ward return not restocked does not approve it (`approval.self`);
  - a lab that sets `lab.separateVerifier` (per facility, off by default for a lab with one pathologist) refuses
    verification of a result by whoever entered it (`lab.verify_own_result`), and a correction of a released report
    waits for a second pathologist (see Maker-checker);
  - the doctor who certified a death does not also confirm the certificate (`death.own_certificate`, and
    `approval.self` on the request);
  - a controlled drug given on the MAR is witnessed by a second nurse who proves who they are at the recording nurse's
    screen, never the nurse recording it (`witness.self`). The witness gives their own user name and password there, and
    their authenticator code when their account has one (`POST /api/v1/auth/witness`, ADR-014). It is checked as
    sign-in checks it: wrong ones count towards their lockout, nothing is accepted while the account is locked, and an
    unknown account gets the same answer. Every attempt is audited against the witness's account. Nothing is signed
    in. The confirmation it returns is good once, for that nurse, that patient and five minutes, and the dose names the
    witness's account. A high-alert medicine given is witnessed the same way, and so is every dose with no order behind
    it (nobody prescribed it, so the second nurse is the only other check). A ward order takes both flags from the drug
    master when it names an item of it, whatever the order form sent, and so does the order made for a medicine the
    pharmacy issues without a prescription line; a ward order typed in without an item of the master is always
    high-alert, so typing a name instead of picking the drug clears nothing.
  - in the blood bank, grouping, cross-match and issue are not all one person: grouping and cross-match record the
    officer's account, and a unit is not issued by the officer who both grouped the patient and cross-matched it, nor
    plasma, platelets or cryoprecipitate (issued on the group alone) by the officer who grouped the patient, nor an
    uncross-matched unit by the doctor who released it (`bloodbank.issue_own_checks`, 403). Where
    `bloodbank.separateVerifier` is off, the one officer on duty issues it, audited at Warning
    (`bloodbank.issued_on_own_checks`), and the issue is flagged and listed for the medical officer's review (`GET
    …/requests?oneOfficer=true`). The officer who entered a donation's results does not verify them while the setting is
    on (`bloodbank.verify_own_results`). A patient's grouping is compared with the request's own first sample (a second
    grouping that disagrees is refused, `bloodbank.grouping_discrepancy`, as before) and with the groups confirmed on
    the patient's other requests, in any facility: one that disagrees is recorded but holds the request, at Warning, and
    nothing is cross-matched or issued on it (`bloodbank.grouping_history_discrepancy`) until another officer groups a
    new sample, which must agree with the first sample or the earlier group and replaces the first
    (`bloodbank.regroup_own_sample` for the same officer). The setting does not lift this; in an emergency, the doctor's
    release of O Rh(D)-negative red cells goes on meanwhile;
  - a unit of blood is started on the ward only after a bedside check by two staff, the second proving who they are at
    the first one's screen in the same way (purpose `blood.bedside_check`, `bloodbank.transfuse`). The patient's
    wristband and the bag are read and compared with the issue on the server; another patient's or another unit is
    refused and audited as a near miss. The patient's consent for the stay, the unit's expiry, its cross-match and its 4
    hours out of the blood bank are checked there too.
- **Attendance terminals that push** are registered with the IP address they push from, and punches from any other
  address are refused. A terminal registered before the address was required is held to the first address it calls from.
- **Switched-off controls are shown.** Outside development, System health shows a critical warning while two-step sign-in
  (`security.mfa_off`) or administration only from an enrolled desktop (`security.desktop_admin_off`) is off.
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
- **Links to the portal** (PP-01). Staff who register patients (`patients.register`, and who may open the record) can
  send a link to the portal to the mobile number registered for the patient, by SMS, WhatsApp or both, from the
  patient's record. The link is not a credential: it only starts the portal's sign-in for that number. Opening it shows
  the hospital and the number masked; asking for a code through it sends the code to the registered number with the
  sign-in's own limits, and only the code signs anyone in. So a link forwarded, or read off a shared phone, gets nobody
  in who does not hold the patient's phone.
  - The token is 128 random bits, kept only as its SHA-256 (masked in the audit trail like other secrets), and never
    shown to staff. A new link stops the earlier ones; a change of the patient's number stops them too, and so does the
    record being merged, marked deceased or inactive, or put in a protected category: every use checks the record as it
    is now and that it still has the number the link went to. Unknown, expired and stopped links all get the same 404.
  - No link is sent for a record in a protected category (HIV, mental health, MTP, sexual violence): a message to a
    phone may be read by someone else, so staff give portal access at the desk after checking that the number is the
    patient's own. Nor to a number that replied STOP on WhatsApp by WhatsApp alone (SMS still works).
  - At most 3 links per patient in 24 hours, counted under a lock so sends at once from two desks cannot get past it.
    Each send is on the patient's record history (`portal.link.sent`), and the link's own row is in the data audit.
- **Changing the registered number** (PP-01). The portal signs in with the mobile number on the patient's record, so a
  desk that could change it alone would hand the patient's records to whoever holds the number it typed (a
  social-engineering call, a recycled SIM). The edit (`PUT /api/v1/patients/{id}`) refuses a new number; staff who edit
  patients use Change number, which sends a 6-digit code by SMS to the new number only, and the number changes when they
  enter the code the patient reads out (`PatientPhoneChangeService`). Nothing is sent to the old number: the hospital
  chose a code to the new number over a notice to the old one. Removing the number, or sending it back unchanged, is
  still an edit.
  - Codes are kept only as SHA-256 salted with the change's id, work for 10 minutes and 5 tries, are judged one at a time
    under the change's row lock, and are used once. A number gets 3 codes in 15 minutes, counted under a lock on the
    number, whichever patients they were for; a newer code supersedes one still waiting; and the change is made only
    while the record still has the number it had when the code was sent (409 otherwise: send a new code).
  - What the change ends, in the same transaction: the links to the portal sent to the old number; the family members
    the old number's portal account saw move to the new number when no other patient in use keeps the old number (they
    stay with the account when one does, since it is still that patient's); and when the old number then reaches no
    patient's records at all, its portal sessions end at once, on every server, and the account forgets the patient's
    name and record, so whoever holds the number next signs in to nothing of theirs.
  - Both steps are on the patient's record history (`patient.phone_code_sent`; `patient.phone_changed`, a warning naming
    who asked and who confirmed), with the numbers masked and never the code.
- Every access to a patient record is audited, and so is every way staff see one: a patient search (what was searched
  for, how many matched and which patients were shown, `patient.searched`), the summary the banner shows and a lookup
  by UHID (`PatientAccessAuditTests`).
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
  - Sending it to another provider through ABDM needs the patient's consent artefact (checked against the records it
    names, the dates and its erase date, again just before sending) and the gateway, switched on and set up by an
    administrator; the records go encrypted for the requesting provider alone (Fidelius), to a push URL that must be
    https and is refused when it names this machine or a private address. NHA's client id and secret are a secret
    setting (`abdm.gateway.secret`, encrypted), and the gateway's status says only whether they are set. Its base URL
    must be https without a user name.
  - The gateway's callbacks are anonymous at the HTTP level and accepted only with a bearer token signed RS256 by a key
    in the gateway's published key set; anything else is refused with 401, and a call naming another HIP or HIU is
    ignored.
  - Checking a patient's ABHA sends ABDM only the ABHA number or mobile number already on the record, encrypted with
    ABDM's public key; the code and ABDM's token are not kept. Asking a patient for their records at other providers
    (`abdm.records.request`, doctors) is in the requesting doctor's name and audited; the patient decides in their app.
  - Records received from other providers arrive encrypted for a key pair made for that one request (Fidelius); the
    private key is field-encrypted and dropped when the transfer ends. The data-push endpoint carries no token (as
    ABDM's push has none): it admits a page only for a transaction the consent manager named for a request of the
    hospital's, still awaiting pages, under a consent still granted, and keeps nothing it cannot decrypt with that
    request's key or that names a visit the consent does not cover. Received records are shown as text (never their
    HTML narrative), each opening audited, and deleted outright when the consent is revoked or expires and at its
    erase date.
  - Linking, replacing or removing a patient's ABHA number or address is audited (`patient.abha_changed`), with the
    number masked, including a number a merge of duplicate records links to the surviving one.
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
  - **Contact details by role** (SRS PRV-03, minimum necessary): a patient's phone numbers, email, street address and PIN
    code, and the emergency contact's phone reach in full only roles holding `patients.contact.read`, those who contact
    or bill patients (front office, doctors, nurses, billing, cashier, insurance desk, CRM, medical records and
    administrators by default; Administration › Roles changes it). Everyone else gets them masked, on the patient's
    record and wherever another screen shows the patient's banner: the last four digits of a number, the first letter of
    an email, the town and state of an address. Editing a patient's details needs the permission too, so masked details
    are never saved back. `PatientContactVisibilityTests` calls every GET the API maps with one patient's ids, as a role
    holding every other permission, and fails if any answer carries a detail in full. Kept whole for their purpose: the
    FHIR API and ABDM exports (sharing the record, with the patient's consent), the privacy officer's data-principal
    requests (answering them gives the patient their own data), the controlled-drug register (the law asks for the
    address), and the audit trail (it must show what was changed; reading it is administration).
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
    nothing else in the row changed. Each server, as it starts, puts back any of these triggers it finds dropped,
    turned off or changed, on the log and on each month's partition.
  - Each entry also carries a keyed seal: an HMAC-SHA256 of its hash under the audit seal key (`keys/audit-seal.key`,
    wrapped by Data Protection like the field key, so not in the database). Someone who can write the database and
    turns the triggers off can change an entry and compute the SHA-256 chain again from it, but cannot make the seals;
    once entries carry seals, none after may lack one. Backups escrow the key beside the field keys, and a restore puts
    it back, so a restored server verifies the seals it made.
  - Anchors outside the database: at most every hour (`HealthCare360:Audit:AnchorInterval`) the sealer appends the
    chain's end (position, hash, seal, time) to the anchor file (`audit/anchors.log`, or `HealthCare360:Audit:AnchorFile`:
    point it at storage the database's administrators cannot write) and to the server log. Every anchor's position
    must hold that hash, and the chain must reach the last anchor, so a chain cut short, or made again from an earlier
    point, is found even by someone who holds the key.
  - Administration › Audit log › *Verify integrity* re-checks the chain, the seals and the anchors, and names the position
    of the first entry changed, moved or removed, even by someone who turned the triggers off (ASVS V16.4.2). It also
    fails on any seal outside the chain. The server that runs the jobs makes the same check by itself an hour after it
    starts and once a day after that. Every check, asked for or not, is written to the audit trail itself
    (`audit.verified`, critical when it fails), and while the latest one failed System health shows `audit.chain_broken`
    as critical.
  - Critical System health warnings are not left for someone to open the page: every five minutes the server that runs
    the jobs tells each new one to everyone who may see System health, in the app, and to the IT on-call number by SMS
    (and WhatsApp when switched on), as Wi-Fi alerts are. A warning is told again every day while it lasts, so one put
    off at go-live, such as nobody holding the backup recovery key, is not forgotten, and at once if it clears and comes
    back. Its SMS waits out an internet outage until the next day's is due. The Wi-Fi warnings are left to the Wi-Fi
    monitor, which tells them as they happen.
  - *Export for auditors* writes the sealed entries of a period, contiguous by position, with their hashes, seals and the
    anchors in range, to a JSON file (`audit.exported` is audited). An auditor checks it on any machine with
    `HyFib.HealthCare360.Server.exe audit verify-export --in <file> [--anchors <their copy of the anchor file>]`: the
    chain and the anchors, without HC360's database or key (the seals need the key, which only the server's own
    verification has).
  - Tests, against PostgreSQL: `AuditTrailTests` (the triggers, and each column changed behind them),
    `AuditSealTests` (a chain computed again without the key, seals taken off, a chain cut short past an anchor or made
    again at an anchored position, an export changed after it was made), `AuditTriggerTests`.
  - **A record's History.** A patient's registration record, a visit, a bill, a lab order and its report, and an
    admission have a *History* button: who created the record, changed it or acted on it, when, and each field from
    what to what, newest first, from the audit trail (`GET /api/v1/history/{type}/{id}`).
    - It holds the record's parts too, the rows its own screen shows as its content that the trail records apart from
      it, deleted ones included, each entry named by its part:
      - a lab report: each result on it (entered, by hand or from an analyser; verified; corrected) and each amendment;
      - a lab order: each test on it (ordered, cancelled) and each sample (collected, received, rejected, referred out);
      - a bill: its discount requests, receipts, refunds, credit notes and write-offs;
      - an admission: each bed the patient was put in or moved to.
    - And the approvals asked for on the record (maker-checker): who asked and why, who approved it, who rejected it and
      why, a request withdrawn, and one that failed when it was approved. A rejected request leaves nothing on the
      record itself; its History still has it.
    - It opens only for staff who may read the record (the record's own read permissions), by the rules that open the
      record itself: its facility, and the restricted-record rules of its patient. Patients are refused. A part is one
      the record's own screen shows to the same staff.
    - It keeps from its reader what the record's own screen keeps from them (`HistoryRedaction`):
      - a patient's phone numbers, email and street address, and the emergency contact's number, masked for staff who
        see them masked on the record (PRV-03);
      - the record's sensitive categories, put in or taken out, and their events, left out for staff who may not manage
        them, so a category taken out does not show once the record opens to everyone again;
      - a lab report's results, released or not, left out for staff who see released results only (clinicians, nurses):
        an unverified value must never guide treatment. They keep the report's releases and its amendments, which change
        released values only.
    - Values the trail masks stay masked. Reading a patient's record history is itself in the patient's access log.
    - The module that owns a record type declares it, with its parts (`IRecordHistorySource`).
    - The trail is read by record through the index `ix_audit_events_entity`. It is not in a migration, which would hold
      every audited write while it was built: the server that runs the jobs builds it after start, one monthly partition
      at a time and `CONCURRENTLY` (`AuditEntityIndex`), and later partitions take it from the parent.
  - The log is partitioned by month (SCAL-04). Each month's partition refuses TRUNCATE and DELETE as the table does, and
    the chain-link trigger refuses a seal that takes a chain position another entry has (a unique index would have to
    include the month). Partition upkeep never drops a partition, of the audit log or any other table: other records are
    deleted only by the Privacy retention schedule, row by row, at least as late as the law allows and with every run
    audited, and the audit log only by the purge below.
  - **How long it is kept, and the purge** (ADR-015). Permanently, unless holders of `admin.system.manage` (the Super
    Administrator by default) set a period: one asks, with a reason, and another approves (`audit.retention`); never
    fewer than 8 years, the period of the books of account whose changes the log records too (Companies Act s.128(5)).
    The retention screen and the settings API refuse its setting otherwise. A period removes nothing by itself: a purge,
    asked for by one holder and approved by another (`audit.purge`), removes whole months entirely before the cut-off
    (today less the period, to the month), only those at the start of the chain with every entry sealed, never the
    default partition, and is refused if what it would remove changed after it was asked for. Approved, in one
    transaction: the entries are exported in the auditor's format to the audit archive outside the database
    (`audit/archive`, or `HealthCare360:Audit:ArchiveDirectory`) and the file is checked; a purge record (boundary position
    and hash, months, entries, the export's name and SHA-256, who asked and who approved) is sealed with the audit seal
    key and an anchor of the boundary is appended to the anchor file; the months' partitions are detached and dropped; and
    `audit.purged` is audited as critical. *Verify integrity*, the daily check and System health then check the chain from
    the latest purge, once its seal and its anchor are checked: rows removed behind a purge record someone made up do not
    verify. Purge records are append-only (triggers refuse UPDATE, DELETE and TRUNCATE). The anchor file is not in the
    backups: a server restored after a purge verifies only with it put back. Tests: `AuditRetentionTests`.
- The controlled-drug registers (the pharmacy's of every Schedule H1/X and NDPS movement, and the prescriber's of every
  signed controlled line) are append-only in the database: a trigger refuses a delete, a truncate and any change but the
  patient link a merge of duplicate records moves (`ControlledDrugRegisterGuardTests`). Each line, and each move of its
  link, is in the sealed audit trail (`[Audited]`).
- CERT-In's Directions of 28 April 2022:
  - The server's log files are kept a year (`HospitalLogging.RetentionDays`, 367 days, so the last events of each day's file are a full year old before it goes, in a leap year too), on the Server Node, in India:
    the 180 days the Directions ask for, and the year the DPDP Rules 2025 ask for from 13 May 2027 (r.6).
  - Each server checks its clock against NPL's and NIC's NTP servers (time.nplindia.org, samay1/samay2.nic.in; or the
    hospital's own, `HealthCare360:TimeSync:Servers`) every 15 minutes, and System health warns when it is more than a
    second off (`HealthCare360:TimeSync:MaxOffset`) or none answers. Windows Time keeps the clock; the check shows when
    it does not (`ServerClockTests`).
  - A breach that is also an incident of a kind CERT-In lists (Annexure I) is reported to CERT-In within 6 hours of
    the hospital becoming aware: the breach register shows the deadline beside the Board's 72 hours, says when it has
    passed, and records when CERT-In was told and its reference (`BreachCertInTests`). An incident with no personal
    data is recorded in the same register and closed with a note saying so.
- Records carry who and when on every change, and deletes are soft deletes. Every change to an audited record also
  writes a data audit event with each changed value (from/to); compound values such as addresses, cardiology
  findings and TTI results are recorded part by part. Binary values (a prescription's DSC signature and the bytes it
  signed, which name the patient) are recorded by their length and SHA-256, never copied into the trail. A text longer
  than 500 characters is recorded by its first 500, its length and the SHA-256 of all of it, so an edit further on still
  shows as a change while each row stays small.
- What the trail covers (`UnitOfWorkInterceptor`, in the transaction that makes the change):
  - Rows a record holds as a collection are audited with it (`[AuditedWithParent]`): the record's event lists the
    collection as it was and as it is (or, when it was not read first, the rows added and removed). So a coding's ICD
    codes, a tariff plan's bed multipliers, a package's inclusions, a template's medicines, a health check-up package's
    items, an instrument set's count sheet, a salary structure's pay heads and a user's roles and facilities are in the
    trail when they are replaced, however the change is made.
  - Account changes name the roles and facilities given and taken away (`user.created`, `user.updated`), and the
    permissions start-up gives a role or takes back at an upgrade are audited as an administrator's are
    (`role.permissions_changed`, with no user).
  - A change made in set-based statements, which the data audit does not see, writes its own entry in its transaction,
    naming the rows: a patient merge's moves in OT, dialysis, physiotherapy and blood bank with the bookings it cancelled
    (`specialtycare.records_moved`), each batch of the retention clean-up (`privacy.retention.purged`), an ABDM erase
    (`abdm.records_erased`), and a setting removed (`SystemSetting.delete`, with every value it had).
  - `AuditCoverageTests` (ArchitectureTests) fails for any table that is neither in the trail nor on its reviewed list,
    where each table has its one-line reason (technical tables, logs that are their own record, read models kept from
    audited records, catalogues imported as a whole). Lines written with a document but not yet audited with it (a
    receipt's payment modes, goods receipt and pharmacy sale lines, and the like) are on the list as such, so a new table
    cannot stay out of the trail unnoticed. `AuditedChangeTests` (IntegrationTests) read each of these changes back
    from the audit log.
- **Idempotency keys** on every write:
  - A WiFi retry replays the original result instead of creating a duplicate bill, payment or order.
  - Stored responses are encrypted, because they can contain one-time secrets.
  - The AI endpoints' answers (dictation transcripts, SOAP drafts, interpreted commands) are never stored
    (`.WithoutStoredResponse()`). The key still stops a retry from running again, so there is no second AI call or
    quota charge. The retry gets 409 `idempotency.answer_not_kept`, and the user asks again.
- Optimistic concurrency: a record changed by someone else is never silently overwritten (REL-04). Every edit (PUT,
  PATCH) sends the version (`xmin`) of the record as it was loaded and gets 409 when it has moved on. An edit that
  leaves the version out is refused too: 400 where the record is edited, 409 where the first save creates it (a
  setting, a report, a nominee) and one exists. The few edits that guard another way (number formats, the analyser
  list, roster cells, a patient's consent switches) are named, with how, in the architecture test that fails the build
  for any edit without a version. The screens send the version their form was filled from, not one fetched since.

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
- Rate limits on sign-in, OTP and public endpoints (per address), and on everything else per signed-in user. With
  several servers (below), the sign-in and OTP limits per address are counted for the hospital, in the database. The
  limits are settings (`HealthCare360:RateLimits`, DEPLOYMENT.md), checked at start-up: none can be zero.
  - **What counts as one address** (`ClientAddresses`), for these limits and for the proof of work's failure counts
    (below): an IPv4 address; an IPv4 address carried in an IPv6 one (IPv4-mapped, 6to4, Teredo, and NAT64's well-known
    prefix `64:ff9b::/96`, RFC 6052, through which IPv4 clients reach a server on IPv6 alone) as that IPv4 address; and
    an IPv6 address from outside the hospital by its /64. A home, a phone on a mobile network or a rented server holds
    a whole /64 (RFC 6177) and can send every request from a new address in it (RFC 8981): counting by the /64 stops
    one host from sidestepping the per-address limits and the extra work asked of a failing address that way. It does
    not stop a client that holds more, a /56 or a /48, which has 256 or 65,536 /64s and so as many budgets. A coarser
    key would not do: mobile networks give each subscriber a /64 out of larger prefixes their other subscribers share,
    so counting by /56 or /48 would put unrelated patients on one budget. Against such a client the per-account and
    per-number limits (the account lockout, the codes a number may be sent and the tries each code has) and the proof
    of work remain the defence. The hospital's own network
    counts address by address, as IPv4 does: link-local and unique local addresses, the /64s the server has an address
    in itself (read again every minute), and `HealthCare360:Network:LocalNetworks` for other subnets that reach it over
    internet IPv6 addresses. The workstations on one WiFi share its /64, and do not share one sign-in budget. The
    audit trail, the logs, address allow-lists and the idempotency of anonymous requests keep the client's full address.
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
  - **Raised for failures.** Every 3 failures from an address (an IPv6 /64 from outside the hospital counting as one,
    as for the rate limits), or for a mobile number from any address, ask 2 bits
    more (four times the work), up to 18 bits (5 to 13 seconds on a phone). A failure is a refused proof, a rate-limit
    refusal, or a refused request (a wrong or expired code, too many codes, a dead link). A form that fails validation
    (feedback, or a sign-in code asked for a number that is not a mobile number) is answered before the proof is
    checked, so it neither spends its proof nor counts. The count for an address or a
    number lasts until 15 minutes pass without a failure for it (each failure starts the quarter of an hour again), so a
    quiet quarter of an hour brings it back down and a steady trickle keeps it up. Addresses and numbers are kept only
    as keyed hashes: an HMAC-SHA256 under keys derived from the anti-automation key, since every IPv4 address and every
    one of India's mobile numbers can be hashed in minutes, and a plain hash would give them back. Past 30 failures
    (the most work any setting can ask for), a server counts at most one a minute for an address or number, so a flood
    of rate-limit refusals does not become a flood of writes; a count that cannot be stored is logged, does not change
    the answer, and is tried again a minute later.
    Giving a code names only its challenge: the work asked for it depends on the address, and when the attempt fails
    the server looks the challenge's number up (one lookup by key, on that path only), so wrong and expired codes count
    against the number as well.
  - **Checking.** One HMAC, then the kind of request, the expiry, one hash, and the bits against what the address and
    number need now. A good proof is spent: the challenge is remembered until a minute past its expiry and refused
    again. A single server keeps the spent challenges, the failure counts and the audit throttle (below) in memory. On
    several API servers (SCAL-01) they are in the database's cluster store (`platform.cluster_entries`, one statement
    each): a challenge is spent once for the hospital whichever server it is sent to, failures counted by one server
    raise the work every server asks for, and a refusal is audited once.
  - **Refusals** get one answer, whatever the reason: 403 `request.check_failed`, "We could not complete this request.
    Please try again." The portal then solves a fresh challenge, for the number when there is one, and tries once more.
    Each refusal is counted in the `hc360.anti_automation.rejected` metric (by kind and reason: missing, malformed,
    tampered, wrong action, expired, too easy, unsolved, replayed), and audited as `security.anti_automation_rejected`
    at most once per address and reason in 5 minutes (for the hospital, with several servers), so a flood cannot flood
    the audit trail.
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
- **Several servers behind a reverse proxy** (optional, SCAL-01; DEPLOYMENT.md section 10):
  - The client's address is taken from `X-Forwarded-For`, and the scheme from `X-Forwarded-Proto`, only on a request
    from a proxy the site names (`HealthCare360:Network:KnownProxies` or `KnownNetworks`). Anyone else's headers are
    ignored, so nobody can claim another address to dodge a rate limit, an address allow-list or the audit trail. With
    no proxy named, the headers are never read.
  - Nothing that decides access lives in one server's memory alone: sessions, sign-in challenges, lockouts and
    break-the-glass grants are in the database, and a permission taken away, a session ended or locked, or a setting
    changed on one server expires the others' caches at once (PostgreSQL notifications). A server that cannot hear the
    others does not use those caches.
  - One-time tokens and attempt counters that must hold whichever server answers use the database's cluster store
    (`IClusterStore`): a token can be spent once, and every attempt is counted once, across all servers.
  - The keys are in a shared folder, wrapped by a Data Protection key ring that is protected with a certificate every
    server holds (key table below), never in the database. A server with keys of its own will not start on a shared
    folder that has none.
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
  - Disables DevTools unless IT's machine policy enables them (`"allowDevTools": true` in `policy.json`, which only an
    administrator can write). The user's own settings file cannot.
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
    Until then it waits, wrapped by Data Protection, beside the current key, in the folder the servers share when there
    are several (SCAL-01), so the confirmation may reach any of them; only the administrator it was shown to can confirm
    it, within 30 minutes, and it is never used before. The server deletes the key it replaces. Showing it and putting it to use are
    audited, without the key.
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
    proof-of-work challenges, which live 5 minutes, and keys the hashes of the addresses and numbers that failed lately;
    it protects no stored data, so it is rotated by deleting `keys\anti-automation.key` and restarting the server:
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
| Anti-automation key | `keys\anti-automation.key`, 256 random bits made on first start | Data Protection | HMAC-SHA256 of the portal's proof-of-work challenges (5 minutes; Application hardening, above), and, through keys derived from it, of the addresses and mobile numbers whose failures are counted (in memory, or in the database with several servers) | Delete the file and restart: challenges issued before stop working and browsers ask for new ones; not from System health, an accepted exception (above). Several API servers share it from the shared folder's `keys` |
| Data Protection key ring | `keys\dataprotection` | Machine DPAPI; with several servers, the ring in the shared folder is protected with the cluster key certificate (RSA, in each server's Local Computer store), and each server keeps its own DPAPI ring for its LAN certificates | The keys above and the LAN certificates' private keys | Automatic (library default, 90 days) |
| Cluster key certificate (several servers only) | The Local Computer's Personal store of each server; the PFX and its password offline, in the hospital's password safe | Windows (the private key readable by `NT SERVICE\HC360Server` and Administrators) | The shared Data Protection key ring, hence every key in the shared folder | Not rotated in place; to replace it, copy the documents and restore the latest backup into a new, empty shared folder named with the new certificate (DEPLOYMENT.md section 10, steps 2 to 4) |
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
  - The daily **Security audit** workflow runs both audits on main, so an advisory published while the code is not
    changing still fails a run.
  - An advisory reviewed as not affecting HC360 is accepted explicitly, never by lowering the level. For NuGet, add a
    `NuGetAuditSuppress` item to `Directory.Packages.props`. For npm, add the advisory under `auditConfig` in
    `pnpm-workspace.yaml`. Each entry has a comment giving the reason and the date to look again.
- **Patching security advisories** (SRS SEC-11): an advisory against a package HC360 uses is patched within 7 days
  of the day it was first seen when it is critical, and within 30 days otherwise.
  - `.github/security-advisories.json` records the day each open advisory was first seen. The
    `Security patch windows (SEC-11)` job in CI, the daily Security audit and the release run
    `.github/scripts/patch_windows.py` on what `dotnet list package --vulnerable` and `pnpm audit` report.
  - The check fails when an advisory is past its window, or is reported but not recorded. It warns 3 days before a
    window ends. A release is not made while it fails.
  - When an advisory first appears, record it in the change that deals with it:
    `python3 .github/scripts/patch_windows.py --nuget <listing> --npm <audit> --record`, then commit the file. The
    commands that make the two files are at the top of the script. Never change a `firstSeen` date by hand.
  - Patching is updating the package (or the SDK, for the .NET runtime). Accepting an advisory as not affecting HC360
    (above) also ends its window, because the audits then leave it out. Run `--record` again to drop advisories that
    are gone.
  - The script's tests use saved audit output and dates (`.github/scripts/test_patch_windows.py`), and run in CI.
- **SBOMs**: every release attaches a CycloneDX 1.6 SBOM for each package it ships, listed in `SHA256SUMS.txt`
  with the packages. CI makes the same files on every change and keeps them with the run.
  - `HC360-Server-<version>-win-x64.cdx.json` covers the server and the staff app and portal it serves.
    `HC360-Desktop-…` covers HC360 Desktop and its updater, and `HC360-LabAgent-…` the lab agent. The staff app and
    the portal also get one each (`HC360-Staff-…`, `HC360-Portal-…`).
  - The .NET SBOMs come from the CycloneDX .NET tool (`.config/dotnet-tools.json`), with the .NET runtime packs a
    self-contained package carries added. The web ones come from cdxgen reading `src/web/pnpm-lock.yaml`
    (`.github/sbom/package-lock.json`), without development-only packages. Both tools are pinned, and Dependabot
    updates them. `.github/scripts/sbom.mjs` makes the files and checks each against the CycloneDX schema.
  - HC360 ships no container images, so there is no image to scan. `deploy/docker/compose.dev.yml` is for developers
    only.
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
