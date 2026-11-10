# Going live one department at a time

**Status: proposed, not decided.** Of the work in section 9, items 7 and 8 are built: the start-up guard against a
newer database, and the way back from an upgrade. The switches, the profile and their tests are not. This plan is
written from the code as it stands on 10 November 2026 (commit `ad5acb37`). Every figure in section 2 was
counted from the source, and the appendix gives the command for each. What could not be checked is marked
**to confirm**.

The request: release HC360 to the hospital module by module, not all at once, and make the application configurable
for it.

## 1. The decision in one page

**Install everything once. Turn it on one department at a time.**

- **One package.** Every release holds every module, as it does today. Nothing is installed "per module".
- **One switch per department.** A department's part of HC360 is live when its switch is on for the hospital. While
  it is off the server refuses it, the screens hide it, its background work stays still, and no other department
  hands it work.
- **One wave at a time.** A wave is a few switches turned on together, with the masters, the roles, the training and
  the department head's signature that go with them. The next wave starts when the last one is signed, not on a date.
- **Two different days.** Installing a new version (an upgrade) and starting a department on it (a go-live) never
  happen on the same day. A go-live that goes wrong is undone with a switch, in minutes. An upgrade that goes wrong
  needs a restore.

Why not a separate installer for each module: the code is one system, and cutting it would create combinations that
nobody tests.

| What the code shows | Counted | What separate installers would mean |
|---|---|---|
| One server process composes every module | 18 modules and the integrations, in one list (`ServerModules.All`) | A second way to build the server, for each combination shipped |
| One database model and one chain of changes | 439 tables in 23 schemas; 140 migrations, each built on the last | The chain cannot be cut by module: a later change assumes every earlier one |
| Tables point across modules | 113 foreign keys cross a schema, 92 of them to the patient tables | A module's tables cannot be made without its neighbours' |
| Modules react to each other inside one save | 41 kinds of event have a handler in a module | A missing module becomes a missing step in someone else's save |
| One staff app | 43 screen groups, built into one bundle | A bundle for each combination |

An empty table costs nothing, and a switched-off department runs no code. So the whole product is installed, and
only the switches differ from one week to the next. The switch sets are few and have names: the waves.

**What is missing today** is the switch. HC360 has three (the patient portal, video consultations, ABDM health
records) and a sound mechanism behind them. It has none for pharmacy, the laboratory, billing or any other
department. Section 3 extends that mechanism. It does not add a second one.

## 2. What the code gives today, and what it does not

### Already there

| What | Where | Used here for |
|---|---|---|
| Three feature switches. Hospital-wide, with a per-branch "off". The server enforces them (503 `feature.disabled`), both apps hide what is off, administrators change them under Hospital setup › Features, and each change is audited (`feature.changed`) | `Platform/Features`, [features.md](../modules/features.md) | The mechanism to extend |
| A test that fails the build when a switched endpoint lacks its switch | `FeatureSwitchTests` (ArchitectureTests) | Extended to every endpoint |
| Module layering, held by project references and a test | `LayeringTests`, [ARCHITECTURE.md](../architecture/ARCHITECTURE.md) section 4 | The order departments can go live in |
| Stand-ins for a module that is absent, for 10 of the 28 contracts modules call each other through | `PlatformServiceCollectionExtensions.cs` | A start on "what a caller gets while the other side is off" (section 3.4) |
| 32 built-in roles. Default grants are applied once, so an administrator's changes survive upgrades | `IdentitySeeder.cs` | Who may use a live department; pilots |
| Settings for the hospital and for each branch, number formats, print templates, masters | [masters.md](../modules/masters.md), [numbering.md](../modules/numbering.md), [print-templates.md](../modules/print-templates.md) | How a live department behaves |
| A checked backup before any change to the database. If it fails, nothing is changed | `DatabaseInitializer.cs` | Upgrades |
| One command to upgrade. Configuration and data are kept | [DEPLOYMENT.md](DEPLOYMENT.md) section 7 | Upgrades |
| Checksummed packages, a signed HC360 Desktop that updates itself from the server, SBOMs, a gate on unpatched advisories | `.github/workflows/release.yml`, `Publish-HC360.ps1` | The release train |
| A preflight of the server machine, a load test, restore, the downtime kit, a sweep for simulator records | [NANA-THENI-READINESS.md](NANA-THENI-READINESS.md), [LOAD-TESTS.md](../testing/LOAD-TESTS.md), [downtime.md](../modules/downtime.md) | Wave 0, and the checks of every wave |
| Six end-to-end journeys (out-patient, in-patient, laboratory, pharmacy, teleconsultation, remote monitoring) | `tests/HyFib.HealthCare360.IntegrationTests` | Run again for each wave's switches |

### Not there

1. **No switch for a department.** Of the 43 screen groups in the staff app, 2 name a switch. Nothing turns pharmacy,
   the laboratory or billing off.
2. **A live module hands work to one that is not live.** A doctor's order makes the lab's accession item in the same
   save (`ServiceOrderPlacedHandler`, Diagnostics). A signed prescription lands in the pharmacy queue. A check-in
   charges the consultation. With the laboratory still on paper, HC360 would fill a worklist nobody opens.
3. **Background work runs whatever is in use.** The modules register 29 background services, none of them tied to
   a switch: appointment and refill reminders, campaigns, bed-day charges, attendance.
4. **The state of a rollout is written nowhere.** "Wave 2" would be a set of clicks on one screen. It could not be
   reviewed, applied to the training server, or tested.
5. **Only "everything on" is tested**, apart from the three switches' own tests. A department that is off is a
   product configuration of its own, and it has no test.
6. **An older version started on a newer database.** Start-up applied the changes that were pending and returned
   when there were none, without looking for changes the build did not know (`DatabaseInitializer.MigrateAsync`).
   *Built since (section 9, item 7):* start-up now stops on such a database, names the changes it does not know, and
   changes nothing. The way back from an upgrade is the previous package **and** the "before an update" backup, never
   the package alone, and DEPLOYMENT.md section 7 now says how (*Going back*).

## 3. The configuration model

### 3.1 Four questions, four layers

| Question | Layer | Today | Changed by |
|---|---|---|---|
| Is this department on HC360 yet, at this hospital or this branch? | **Department switch** | Three switches | The hospital's decision at each wave; then rarely |
| Who may use it? | Roles and permissions | Built | Hospital administrator, any day |
| How does it behave here? | Settings, masters, number formats, print templates | Built | The department, during set-up |
| What is it connected to? | Integrations: SMS, WhatsApp, payment gateway, POS, Tally, PACS, analysers | Built; a live server refuses the simulators | IT, once per device or provider |

Two rules keep the layers apart:

- A switch says a department has gone live. It is not used to keep one person out: that is a permission.
- A pilot needs no third state. The switch is on, and only the pilot users hold the department's roles.

### 3.2 The switches

One switch for each part of HC360 that a department head can accept on its own. The list is the staff app's own, so
nobody learns a second one. The server's 18 modules are too coarse: Diagnostics holds the laboratory, radiology and
cardiology, and SupportServices holds eight unrelated departments.

| Staff app section | Switches | Server module |
|---|---|---|
| Patient services | Front office (visits, queue, enquiries, visitors) · Appointments · Health check-ups | Patients, Scheduling, Clinical |
| Pharmacy and stores | Stores (inventory and purchase) · Pharmacy · Assets | SupplyChain, Pharmacy |
| Billing and finance | Billing · Insurance and TPA · Doctor fees | Billing |
| Clinical | EMR and e-prescribing · Emergency · Medical records (MRD) · Antimicrobial stewardship | Clinical |
| Diagnostics | Laboratory · Radiology · Cardiology · Blood bank | Diagnostics, SpecialtyCare |
| In-patient | Wards, beds and nursing | Inpatient |
| Theatre and therapies | Operation theatre · Dialysis · Physiotherapy · MTP register | SpecialtyCare |
| Support services | CSSD · Dietary · Canteen · Linen · Housekeeping · Bio-medical waste · Mortuary · Ambulance | SupportServices |
| Engagement and quality | CRM · Feedback · Quality (IRMS) | Engagement, Quality |
| People | HR, attendance and payroll | HR |
| Insights | MIS and BI · AI assistant | Analytics, AI |
| Outside the hospital | Patient portal (exists) · Video consultations (exists) · ABDM health records (exists) · Remote monitoring · FHIR API for other providers | Patients, VirtualCare, Interop |

**Always on, with no switch:** sign-in, users and roles, masters, number formats, print templates, printers,
approvals, the audit trail, backups, System health, the patient register (UHID, search, merge, downtime UHIDs) and
privacy (notices, consent and requests under the DPDP Act apply from the first patient record).

Each switch declares, in the catalogue that exists (`FeatureCatalog`):

- its name, what stops while it is off, and the words of its refusal (as the three have today);
- **what it needs**: the switches that must be on first. Turning it on is refused while one of them is off. Turning
  one off is refused while something that needs it is on;
- its default. An installation that is upgraded keeps everything it had (the rule features.md already states). A new
  hospital database is created with a rollout profile (section 3.5), so it never starts with more than its first wave.

A department the hospital does not have (dialysis, a blood bank) simply stays off.

### 3.3 What "off" must cover

| Surface | The three switches today | To build |
|---|---|---|
| API | The endpoint answers 503 before its handler runs | Every route group under its switch: 225 groups, 1,661 endpoints |
| Screens | Menu, quick actions, home widgets, Ctrl+K and the voice assistant hide it; an open screen explains | The 43 manifests name their switch; the portal likewise |
| Background services | Not covered | Each of the 29 asks before a run, per branch |
| Work handed over by another module | Not covered | Section 3.4 |
| Messages to patients | Not covered | Follows from the two rows above; tested on its own, because a wrong SMS cannot be recalled |

**What "off" never touches:**

- the tables, which are always there and always upgraded;
- the reference data a module seeds;
- its permissions, so that roles can be prepared before the wave;
- records already made. Turning a department off after it was used (a back-out) stops new work. What was recorded
  stays, and shows again when the switch is turned back on.

### 3.4 Seams: a live department next to one that is not

The rule: **nothing goes where nobody is looking.** There are two kinds of hand-over in the code, and each needs its
answer.

**Events** (41 kinds). What the handlers do today, and what they should do while their own department is off:

| Live | Not live yet | Today, in the sender's own save | While the receiver is off |
|---|---|---|---|
| EMR | Laboratory, radiology, cardiology | The order becomes an accession item or a study | Skip. The order prints as a requisition and the department works on paper |
| EMR | Pharmacy | A signed prescription lands in the pharmacy queue | Skip. The printed prescription is the hand-over |
| EMR | Operation theatre, physiotherapy, blood bank, dietary | The order becomes a request on that department's list | Skip, as for the laboratory |
| Front office, EMR | Billing | Check-in charges the consultation; an ordered procedure is priced | Skip. Billing must not open on its first day with weeks of unbilled charges |
| IP pharmacy | Wards | An issue is scheduled on the medication chart | Skip |
| Any | CRM, feedback | A discharge queues a follow-up call and sends the patient a feedback link | Skip |
| Wards | Billing | Admission opens the IP billing account; discharge drafts the final bill | Not allowed: the wards need Billing (a prerequisite, section 3.2) |
| Wards | Theatre, blood bank, Billing | Each keeps its own copy of who is admitted | **Always runs.** Nobody has to act on it, and a copy with holes cannot be mended later |

So every handler is declared as one of three kinds, and a test checks the declaration:

- **work**: makes something a person must act on, or money. Skipped while its department is off;
- **book-keeping**: keeps a copy in step. Always runs;
- neither, because a **prerequisite** forbids the combination.

**Direct calls** (28 contracts). Six modules post charges through `IChargePoster`. The stand-in that exists answers
with an error ("Billing module is not installed"): it was written for a module that is absent, not for one that is
installed and off. What each caller does with that answer decides whether the laboratory can work before Billing
does. That is a test to write for each caller, not something to assume. 18 contracts have no stand-in. Some of those
are lists that several modules add to (GST returns, cash receipts): they need a filter, not a stand-in.

**What was begun before the go-live.** The system fills nothing in afterwards. On the morning of a go-live the
department takes on what is open: the patients in their beds, the stock on the shelves, the bills not yet paid. Each
wave's sheet names its take-on list (section 6).

### 3.5 The rollout profile

The state of a rollout is a small file, kept in the repository and changed by pull request:

```json
{
  "site": "nana-theni",
  "wave": 1,
  "needsVersion": "1.1.0",
  "on": ["stores", "pharmacy"]
}
```

Whatever it does not list is off. It names switches only: no secret and no personal data.

- `HyFib.HealthCare360.Server.exe features apply --profile <file>` sets every switch to match, refuses a set that
  breaks a prerequisite, and audits each change as today.
- `features show` prints what is on, with a checksum to compare with the file.
- `Install-HC360Server.ps1 -CreateDatabase -RolloutProfile <file>` creates a new hospital with its first wave only.

The same file is used in three places: by CI, to run the journeys under it; on the training server; and on the live
server on the day. The Features screen still shows every switch, and an administrator can still turn one off there
in an emergency.

## 4. Releases: one package, a regular train

- **Version numbers** are `MAJOR.MINOR.PATCH`. The build is 1.0.0, and a tag `v1.2.3` makes the release
  (`release.yml`). That stays.
- **Every release is the whole product.** Its notes carry one table: for each switch, whether the release changed
  it; the database changes; and whether workstations need their installer run again.
- **Unfinished departments ship switched off.** This is safe only because each wave's profile is tested (section 8).
  A department that is off still shares tables and events with those that are on.
- **Database changes go in two steps** for anything a live department uses: add the new shape in one release, and
  remove the old one in a later release. The way back from an upgrade is a restore, so an upgrade must never need one
  for a reason that could be seen coming.
- **Rehearse every upgrade on a copy.** Restore last night's backup on the spare PC, install the new package over it,
  and work through the smoke list of the departments that are live. This is the quarterly restore test of
  DEPLOYMENT.md section 8, done for every release.
- **Upgrade at the quietest hour, with the downtime kit out.** Decide within 30 minutes whether it stands, before
  staff start again. The way back is the previous package and the "before an update" backup, and it loses whatever
  was entered after the upgrade.
- **An upgrade lands at least a week before the go-live that needs it.** By the go-live, the version has run a week
  with the departments already live.
- **The guard is built**: start-up stops on a database that holds changes it does not know (section 9, item 7), and
  DEPLOYMENT.md section 7, *Going back*, gives the steps.

## 5. The waves proposed for Nana Hospitals, Theni

The order is a proposal. Three rules hold for any order:

1. What a wave needs is live before it, or goes live with it.
2. Money and the patient's identity each have one owner at a time.
3. What faces patients, or leaves the hospital, goes last.

The "needs" of each wave are a hypothesis until its journeys pass in CI with everything else off (section 8).

| Wave | Turns on | Why here | Until the next wave | Take-on | Signed by, on what evidence |
|---|---|---|---|---|---|
| 0 Foundation | Nothing clinical | The machine, the backups and the masters come before any patient | — | Departments, doctors, service catalogue and prices, number formats, letterhead, users and roles | IT officer: the readiness document's steps 1 to 8 (preflight, UPS, backup disks, load test at 75 users, restore drill). This also closes the PERF-01 to PERF-04 rows of [TRACEABILITY.md](../requirements/TRACEABILITY.md), which wait on a run at the hospital |
| 1 Pharmacy and stores | Stores, Pharmacy | The readiness document already plans the pharmacy's workstations first, and Pharmacy's code references only Patients and SupplyChain | Prescriptions arrive on paper. A sale is to a registered patient or to a walk-in customer | Item master built afresh (the hospital's answer of 10 November); opening stock from a physical count, by batch | Pharmacist-in-charge: the H and H1 registers reconciled for three working days (readiness document, section 2); counter cash against the day's receipts; the stock count |
| 2 Front office and OP billing | Front office, Appointments, Billing, Doctor fees | From here one desk issues every UHID and one system holds the money | Doctors and the laboratory work on paper; bills are made from their slips | Patients registered at the pharmacy in wave 1 (the duplicate check and the merge exist) | Front office in-charge and accountant: each day's collection against cash, card and UPI settlements; bill numbers without a gap; a registration timed at the desk, which closes the FO-01..06 row |
| 3 Doctors | EMR and e-prescribing, then Emergency | Prescriptions reach a pharmacy that is already live | Lab and imaging orders print as requisitions | Nothing: a record starts with the patient's next visit | Each doctor, after their own sessions; a sample of prescriptions against what the pharmacy dispensed |
| 4 Diagnostics | Laboratory, then Radiology, then Cardiology | Orders now arrive from the EMR, and results return to it | — | Tests and prices; one analyser at a time | Pathologist: the same samples through the present method and HC360; a critical value timed to the doctor's list; the simulator sweep clean |
| 5 In-patient | Wards and nursing, Insurance, then theatre, blood bank, dialysis and physiotherapy where the hospital has them | Needs the EMR, the pharmacy and Billing | — | Every patient in a bed that morning is admitted in HC360 | Nursing in-charge and accountant: the midnight census against the bed board; the first discharge bills against a hand calculation |
| 6 Back office | HR, the support services, Quality, MRD, CRM and feedback, Assets | Nothing clinical waits on them | — | Staff list, leave balances | HR manager: one month's payroll run beside the present one |
| 7 Outside and insights | Patient portal, video consultations, remote monitoring, ABDM, MIS and BI, the AI assistant | They show patients, or send out, records the hospital must already trust | — | — | Hospital administrator. ABDM waits on outside parties (TRACEABILITY.md) |

If the hospital would rather start at the front desk, waves 1 and 2 change places. The rules allow either.

**Gates, not dates.** A wave is signed only when both of these are true:

- its parallel run is reconciled (three working days at the least, as the readiness document sets for the registers);
- the first period-end it touches has closed in HC360: a day-end for a counter, a month-end for GST and payroll.

Training for the next wave can start before that. Its go-live cannot.

## 6. One wave, step by step

Each step ends with a check. The next step starts only once it passes.

1. **Write the wave sheet**, one page: the switches; what they need; each seam and the paper procedure that covers it;
   the take-on list; who signs; what triggers a back-out.
   *Check:* the department head has read it and agrees.
2. **Add the profile** by pull request.
   *Check:* CI passes the journeys under it.
3. **Upgrade, if the wave needs a newer version**: rehearse on the copy, then upgrade the live server (section 4).
   *Check:* a week of normal running with the departments already live.
4. **Load the masters and set the number formats** of the wave's departments, before the first number. A later
   change applies only to numbers issued from then on ([numbering.md](../modules/numbering.md)).
   *Check:* the department head reviews the list on screen.
5. **Create the accounts.** Give the department's roles to the pilot users only.
6. **Set up the workstations, printers and devices** (DEPLOYMENT.md sections 5 and 6).
   *Check:* a test page from each printer; each device answers.
7. **Train on the training server**, under the same profile. The live server stays on a database of its own
   (DEPLOYMENT.md, "Going live on a database used for training").
   *Check:* each user finishes the department's day script unaided.
8. **Dress rehearsal.** The department enters yesterday's real work on the training server, and it is timed.
   *Check:* nothing is left that would stop a working day.
9. **Go or no-go.** The hospital's named decision-maker signs the checklist. HyFib advises and does not decide.
10. **Go live.** Apply the profile at the quiet hour. Do the take-on. The pilot users start.
    *Check:* `features show` matches the file, and the audit trail shows who turned what on.
11. **Run in parallel and reconcile every day.** Keep one list of issues, each with a severity and an owner.
    *Check:* every difference has a written reason by the next morning.
12. **Widen.** Give the roles to the rest of the department.
13. **Sign off.** The present method stops for new entries. It stays readable for as long as its records must be
    kept. File the wave sheet with its signatures.

**Backing out.** Until step 13 it is always possible:

- *Triggers:* a risk to a patient; a difference that is not explained within a day; PERF-01 missed on the live server.
- *Action:* apply the previous wave's profile, and the department returns to its present method within the hour.
  Print what it entered in HC360 that day from its own registers, and carry it over by hand.
- *After step 13* there is no way back to the old method. The way through a failure is the downtime procedure.

Rehearse a back-out once, on the training server, before wave 1.

## 7. Hazards of the in-between state

A partial go-live creates risks that neither the old way nor the finished system has. They belong in a hazard log
that the hospital owns and reviews at each go or no-go. England's NHS asks this of hospitals that deploy health IT
(standard DCB0160). It is borrowed here as a method, not as a rule that applies in India.

| Hazard | Why a partial go-live causes it | Control | Checked by |
|---|---|---|---|
| Two answers to one question about a patient | An allergy is in the EMR, and the ward still works from paper | The wave sheet names one owner for each fact, per wave | Department heads, at go or no-go |
| A drug-safety check that looks complete and is not | The check sees only the medicines recorded in HC360 | Doctors record current medicines at the first visit on HC360; training says what the check cannot see | Pharmacist-in-charge |
| An order nobody receives | A live sender, and a receiver that is off | The seam rules (section 3.4), and a printed requisition | The profile tests; the lab in-charge |
| A charge missed, or made twice | Billing live for some departments and manual for others | Daily reconciliation; one system owns the money from wave 2 | Accountant |
| Two records for one patient | Two desks register before the front office owns the UHID | One desk issues UHIDs from wave 2; the duplicate check; merge | Front office in-charge |
| A break in a legal register | The H1 and NDPS registers move systems part-way through | The closing balance of the old register is the opening balance of the new, signed on the same day | Pharmacist-in-charge, then the Drugs Inspector |
| Tired staff entering everything twice | A parallel run that goes on too long | Days, not weeks; a wave is not started during another's parallel run | Hospital administrator |
| No plan for a failure in wave 1 | The downtime kit was meant for "later" | The kit and one drill are part of wave 0 | Quality team |

## 8. What would show it is right, other than the people who built it

| Claim | Shown by |
|---|---|
| Every endpoint, screen group, background service and handler belongs to one switch, or is on the reviewed always-on list | An architecture test that reads them from the code and fails the build. No hand-kept list |
| A department that is off is really off | CI, for each wave's profile: every endpoint of every switch that is off answers 503; no background service of theirs ran; no row of work and no message was made for them |
| A wave's departments work without the later ones | The same run: the journeys of what is on pass with everything else off |
| The live server runs the profile that was reviewed | `features show` and its checksum against the file in the repository; the `feature.changed` entries in the audit trail |
| An upgrade is safe | The rehearsal on a restored copy; the "before an update" backup, checked by the server itself |
| The way back works | A back-out drill on the training server before wave 1; a restore drill every quarter |
| The machine carries the load | The load test on the hospital's machine (PERF-01); System health |
| A department's work is right | Its own reconciliation against the present method, signed by its head; then the outsider who inspects it: the Drugs Inspector for the registers, the auditor for the books |

## 9. What to build, in order

| # | Work | Size, counted from the code | Done when |
|---|---|---|---|
| 1 | The switch catalogue: needs, sections, defaults; the Features screen in sections; `features apply` and `features show`; `-RolloutProfile` | About 38 new switches beside the three | Turning one on without what it needs is refused; a new database has only its profile |
| 2 | Every route group under its switch | 225 groups, 1,661 endpoints | The architecture test names no endpoint without a switch or a reviewed reason |
| 3 | Staff app and portal | 43 manifests | A user with every permission sees only what is on |
| 4 | Background services | 29 | None runs for a department that is off |
| 5 | Seams: events and direct calls | 41 kinds of event; 28 contracts, 18 with no stand-in | Each handler is declared work or book-keeping; each caller is tested against a receiver that is off |
| 6 | Profiles in CI | One run for each wave | Section 8, rows 2 and 3 |
| 7 | The start-up guard against a newer database | One check | An older build stops, and names the changes it does not know. **Built**: `DatabaseInitializer.RefuseNewerDatabaseAsync`, proved by `NewerDatabaseTests` and `UnknownMigrationsTests` |
| 8 | The way back, written into DEPLOYMENT.md section 7 | One section; one drill | A person who has not done it before completes the drill from the page. **Written** (*Going back*); the drill is still to run |

Items 7 and 8 come before the first upgrade, 1.0.1, and are built. Wave 1 needs none of items 1 to 6: no live
department hands work to one that is not live, and roles keep the other departments closed
([RELEASE-1.0.0-READINESS.md](RELEASE-1.0.0-READINESS.md), section 5). Wave 2 has one hand-over: an emergency visit
registered at the front office opens a case on the ER board, which goes live in wave 3. A procedure covers it until
then (close those cases at wave 3's take-on). From wave 3 the hand-overs are many, the EMR's orders first, so items 1
to 6 must be built by then. Item 5 is the part most likely to hold a surprise, so start it early.

## 10. What only the hospital can answer

1. Which department goes first: the pharmacy, as the readiness document assumes, or the front desk?
2. Who decides go or no-go, and who signs for each department?
3. Which departments does the hospital have? Dialysis, a blood bank, a theatre, a canteen: the switches of those it
   lacks stay off.
4. In wave 1, does the pharmacy register the patients it supplies, or does it sell to walk-in customers until the
   front office goes live? The H1 register must carry the patient's name and address either way.
5. Is there a spare PC for the training server and the rehearsals? It is the same question as the dedicated server
   in the readiness document, section 6.
6. For how long must the present pharmacy software stay readable after the pharmacy's sign-off?
7. Which hour is quietest in each department?
8. Is there one branch, or will there be more? The switches already work per branch.

## 11. Risks

| Risk | Answer |
|---|---|
| Too many switches, and switches that outlive their use | These are permanent product settings, not temporary ones: a hospital without dialysis never turns it on. No switch is added for half-built work inside a department that is live |
| Every combination of switches would need a test | Only the named profiles are supported and tested. The prerequisites refuse most other combinations |
| Code that is off breaks code that is on, through a shared table or event | The profile runs in CI (section 8); database changes in two steps (section 4) |
| Two systems side by side for months | Gates in section 5; one wave at a time; a parallel run counted in days |
| The server is a desk PC without a UPS | Wave 0 does not end until the readiness document's conditions are met |
| The seams hold more than this plan found | Item 5 starts early; the handler table in section 3.4 was read from the code, and the test must then enumerate all 41 |

## Appendix: how the figures were counted

Run from the repository root at commit `ad5acb37`.

```bash
grep -c 'new [A-Za-z]*Module()' src/server/HyFib.HealthCare360.Server/ServerModules.cs                 # 19: 18 modules and the integrations
ls src/server/HyFib.HealthCare360.Server/Persistence/Migrations | grep -v Designer | grep -vc Snapshot # 140 migrations
grep -rhoE '\.Map(Get|Post|Put|Patch|Delete)\(' src/server/Modules --include=*.cs | wc -l              # 1661 endpoints
grep -rho 'MapGroup("' src/server/Modules --include=*.cs | wc -l                                       # 225 route groups
grep -rh 'AddHostedService<' src/server/Modules --include=*.cs | wc -l                                 # 29 background services
grep -rho 'IDomainEventHandler<[A-Za-z]*>' src/server/Modules --include=*.cs | sort -u | wc -l         # 41 kinds of event
grep -rho 'public interface I[A-Za-z0-9]*' src/server/HyFib.HealthCare360.Platform/Contracts | sort -u | wc -l  # 28 contracts
grep -c 'TryAddScoped<I[A-Za-z]*, Null' src/server/HyFib.HealthCare360.Platform/PlatformServiceCollectionExtensions.cs  # 11 stand-ins: 10 contracts, and the patient's language
ls src/web/apps/staff/src/modules/*/manifest.ts* | wc -l                                               # 43 screen groups
grep -l 'feature:' src/web/apps/staff/src/modules/*/manifest.ts* | wc -l                               # 2 name a switch
```

Tables, schemas and the foreign keys that cross a schema, from the model EF Core keeps of the database:

```bash
python3 - <<'EOF'
import collections, re
entities, current = collections.defaultdict(lambda: {"to": []}), None
path = "src/server/HyFib.HealthCare360.Server/Persistence/Migrations/HospitalDbContextModelSnapshot.cs"
for line in open(path, encoding="utf-8"):
    if m := re.search(r'modelBuilder\.Entity\("([^"]+)"', line): current = m.group(1)
    elif m := re.search(r'b\.ToTable\("[^"]+", "([^"]+)"', line): entities[current]["schema"] = m.group(1)
    elif m := re.search(r'b\.HasOne\("([^"]+)"', line): entities[current]["to"].append(m.group(1))
schemas = collections.Counter(e["schema"] for e in entities.values() if "schema" in e)
crossing = collections.Counter()
for e in entities.values():
    for other in e["to"]:
        a, b = e.get("schema"), entities.get(other, {}).get("schema")
        if a and b and a != b: crossing[b] += 1
print(sum(schemas.values()), "tables in", len(schemas), "schemas")      # 439 tables in 23 schemas
print(sum(crossing.values()), "foreign keys cross a schema;", dict(crossing))  # 113; patients 92
EOF
```

The handler table in section 3.4 was read from the summary comment of each `IDomainEventHandler<…>` class in
`src/server/Modules`. The stand-in's answer is `NullChargePoster` in `Platform/Contracts/BillingContracts.cs`.
