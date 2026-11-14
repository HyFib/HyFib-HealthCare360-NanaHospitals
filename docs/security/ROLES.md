# Users, roles and responsibilities

Who signs in to HealthCare360, the built-in roles and what each one is for, what each role may do on a new installation,
and how an administrator turns a person, a role or a part of HC360 off and on again.

The tables under [Built-in roles and their defaults](#built-in-roles-and-their-defaults) are written from the code by
`RolesInventoryTests` (ArchitectureTests), which fails when they no longer say what the code does. The same test writes
the same data to [roles.json](roles.json) ([The same data as JSON](#the-same-data-as-json)). What a hospital has
changed since a new installation (roles of its own, permissions given or taken back, roles turned off) is on its own
**Administration › Roles and permissions** screen, and every change is in the audit trail.

## Who signs in

| User type | Who | How they sign in | Roles |
|---|---|---|---|
| Staff | Doctors, nurses, front office, billing, pharmacy, laboratory, administration and every other department; also accounts the hospital makes for other care providers that read records over FHIR, with a role of its own that holds `fhir.read` | User name and password, with two-step verification where the table below requires it; administration only from an enrolled HC360 Desktop (SRS SEC-08) | Any staff role, built-in or the hospital's own |
| Patient | Patients and their families, on the patient portal | Mobile number and a one-time code | The Patient role only, given at the first sign-in |
| Service | Reserved: no code creates such an account. Workstations, kiosks, queue displays and lab agents sign in as enrolled devices, not as users | — | — |

A new installation has one account, `admin`, the first Super Administrator, with a one-time password that must be
changed at the first sign-in. On a development machine only, demonstration accounts (`doctor`, `nurse`, `frontoffice`,
`billing`, `pharmacist`, `lab`, `hospitaladmin`) share the password set there. A training server's load test adds
`loadtest01`, `loadtest02`… with the Front Office role ([LOAD-TESTS.md](../testing/LOAD-TESTS.md)).

## Turning access off and on

| Question | Switch | Who may change it | Turning it off | Turning it back on |
|---|---|---|---|---|
| May this person use HC360? | **Administration › Users**, *Account is active* | `admin.users.manage` (hospital administrators; the Super Administrator holds everything), only on an account whose administrative permissions they hold themselves | Applies at once, and the person's sessions end | Waits for a second administrator who may approve grants (`identity.user_grant`) |
| Does this job function give anything? | **Administration › Roles and permissions**, *In use* | `admin.roles.manage` (the Super Administrator by default) | Applies from each holder's next request. Its holders keep the role but get nothing from it: no permission, no notification or work sent to them, no part in approving grants | Waits for a second administrator who may approve grants and does not hold the role (`identity.role_activation`) |
| What may a role do? | **Administration › Roles and permissions**, the role's permissions | `admin.roles.manage` | A permission taken away applies at once | A permission added waits for a second administrator (`identity.role_grant`) |
| Is a part of HC360 in use at this hospital? | **Hospital setup › Features** | `admin.settings.manage` | Applies at once: its endpoints answer 503 and both apps hide it ([features.md](../modules/features.md)) | Applies at once |

Rules every switch follows:

- What narrows access applies at once; what widens it waits for a second person ([SECURITY.md](SECURITY.md), *Access
  is given by two people*). Nobody approves their own request, nor access given to themselves.
- Never off: the Super Administrator role, which is how the hospital is administered, and the Patient role, which every
  portal account holds (turn the patient portal off under Features instead). Nobody turns off their own account, or a
  role they hold.
- A role turned off still binds its holders. Two-step verification, signing in from an enrolled workstation and the idle
  lock it requires still apply, and so does the protection of the accounts that hold it: turning a role off narrows
  access and never weakens a safeguard.
- Before a role is turned off, the screen says how many people hold it and for how many it is the only role in use:
  they will be able to do nothing until it is turned back on or they are given another role.
- Every change is in the audit trail: `user.updated`, `role.deactivated` (with the reason and how many held it),
  `role.reactivated`, `role.permissions_changed`, `feature.changed`, and the approval that gave the access.

### Departments have no switch yet

Three parts of HC360 have a switch: the patient portal, video consultations and ABDM health records. A switch for each
department (pharmacy, laboratory, billing and the rest) is planned in
[MODULE-ROLLOUT-PLAN.md](../deployment/MODULE-ROLLOUT-PLAN.md). It is safe only when it covers the department's
endpoints, screens and background services, and the work other departments hand it (sections 3.3 and 3.4): a switch that
only hid the screens would leave orders and prescriptions arriving where nobody looks. Until then a department that is
not live is kept closed by its roles, given to nobody or turned off, as the plan's first waves are. That does not stop
other departments handing it work, which is why the plan builds the switches before the wave that needs them.

## Built-in roles and their defaults

What a new installation gives each built-in role. Two-step verification is required of the roles named in the
`HealthCare360:Security:MfaRequiredRoles` setting and of anyone who holds an administrative permission (`admin.*` or
all-access); the table shows the defaults.

<!-- roles-inventory:start: written from the code by RolesInventoryTests; do not edit by hand -->

### The 32 built-in roles

289 permissions are declared. 1755 of the API's endpoints ask for one or more of them; the column *Endpoints* counts those the role's default permissions open.

| Role | Responsibility | Default permissions | Endpoints | Two-step verification | Can be turned off |
|---|---|---:|---:|---|---|
| `SuperAdmin` | Full access to everything, including security settings | all (`*`) | 1755 | Required (role) | No, always in use |
| `HospitalAdmin` | Hospital administration, masters, users and reports | 204 | 1261 | Required (role) | Yes |
| `Doctor` | Consultants and residents: EMR, orders, e-prescriptions | 87 | 522 | If the user sets it up | Yes |
| `Nurse` | Nursing care, vitals, ward operations | 84 | 550 | If the user sets it up | Yes |
| `FrontOffice` | Registration, visits, queue and enquiries | 40 | 313 | If the user sets it up | Yes |
| `Billing` | OP/IP billing and charge management | 25 | 224 | If the user sets it up | Yes |
| `Cashier` | Payments, receipts and day-end closing | 18 | 172 | If the user sets it up | Yes |
| `InsuranceDesk` | Insurance verification, pre-authorisation and claims | 18 | 200 | If the user sets it up | Yes |
| `Pharmacist` | OP/IP pharmacy dispensing and pharmacy stock | 32 | 211 | If the user sets it up | Yes |
| `LabTechnician` | Sample collection, processing and result entry | 17 | 140 | If the user sets it up | Yes |
| `Pathologist` | Laboratory result verification and sign-off | 18 | 151 | If the user sets it up | Yes |
| `Radiographer` | Imaging acquisition and modality worklist | 13 | 111 | If the user sets it up | Yes |
| `Radiologist` | Imaging reporting and sign-off | 17 | 127 | If the user sets it up | Yes |
| `CardiologyTechnician` | ECG/Echo/TMT acquisition | 11 | 98 | If the user sets it up | Yes |
| `Cardiologist` | Cardiology reporting and sign-off (give cardiologists the Doctor role as well) | 11 | 95 | If the user sets it up | Yes |
| `StoreKeeper` | Stores, stock issues and receipts | 18 | 177 | If the user sets it up | Yes |
| `PurchaseOfficer` | Purchase requisitions, orders and vendors | 11 | 146 | If the user sets it up | Yes |
| `MrdOfficer` | Medical records, coding and record requests | 36 | 287 | If the user sets it up | Yes |
| `OtCoordinator` | Operation theatre scheduling and records | 20 | 137 | If the user sets it up | Yes |
| `DialysisTechnician` | Dialysis sessions and machines | 13 | 105 | If the user sets it up | Yes |
| `Physiotherapist` | Physiotherapy assessment and sessions | 12 | 95 | If the user sets it up | Yes |
| `BloodBankOfficer` | Blood donors, inventory and issue | 17 | 115 | If the user sets it up | Yes |
| `Dietitian` | Diet orders and meal planning | 14 | 117 | If the user sets it up | Yes |
| `CanteenManager` | Canteen menu and sales | 14 | 91 | If the user sets it up | Yes |
| `CssdTechnician` | Sterilisation cycles and instrument sets | 13 | 99 | If the user sets it up | Yes |
| `BiomedicalEngineer` | Assets, maintenance and calibration | 9 | 98 | If the user sets it up | Yes |
| `HrManager` | Employees, attendance, leave and payroll | 15 | 170 | Required (role) | Yes |
| `CrmExecutive` | Leads, follow-ups, campaigns and feedback | 23 | 155 | Required (administers) | Yes |
| `QualityManager` | Incidents, risk register and quality indicators | 37 | 286 | Required (administers) | Yes |
| `Accountant` | Accounts hand-off, doctor fees and Tally | 28 | 238 | Required (role) | Yes |
| `MisAnalyst` | MIS reports and BI dashboards | 10 | 90 | If the user sets it up | Yes |
| `Patient` | Patient portal user | 1 | 61 | If the user sets it up | No, always in use |

### What each role may do on a new installation

Each permission is listed with its name, by the area that declares it.

- **`SuperAdmin`**: every permission (`*`), including those added by future updates.

<details>
<summary><strong>HospitalAdmin</strong>: Hospital administration, masters, users and reports (204 permissions)</summary>

| Area | May |
|---|---|
| AI assistant | Use voice or typed commands (open patients, appointments, follow-ups, notes) (`ai.assistant.use`); Dictate clinical notes and get AI-structured SOAP drafts for review (`ai.dictation.use`); View AI provider status and usage (`ai.settings.manage`) |
| Administration | View and verify the audit trail (`admin.audit.read`); Enrol and revoke workstations (`admin.devices.manage`); Approve roles, facilities and permissions another administrator gives (`admin.grants.approve`); Edit SMS/WhatsApp/e-mail templates (`admin.notifications.manage`); Configure number series (UHID, bill numbers) (`admin.numbering.manage`); Set up print templates (letterhead, logo, closing notes) (`admin.print.manage`); Change system settings (`admin.settings.manage`); View server health, WiFi link and backups (`admin.system.read`); Create, edit, lock and reset users (`admin.users.manage`); View users and their roles (`admin.users.read`) |
| Ambulance | Take ambulance calls, send vehicles and record each trip (`ambulance.dispatch`); Keep the ambulance fleet and its certificates (`ambulance.fleet`); Approve the waiver of a completed trip's charge another asked for (`ambulance.waivers.approve`) |
| Analytics | View clinical quality reports, including reports that list individual patients (audited) (`analytics.clinical.read`); Build, share and edit BI dashboards (`analytics.dashboards.manage`); View revenue, collections, dues, discounts, refunds and doctor-fee reports (`analytics.finance.read`); Set each KPI's target and amber and red thresholds for the facility, and who is told when it is missed (`analytics.kpi_targets.manage`); View MIS reports: census, occupancy, diagnostics, pharmacy stock, quality and HR (`analytics.mis.read`); Schedule reports to be e-mailed as Excel, CSV or PDF files, and BI extracts for tools such as Power BI (`analytics.schedules.manage`) |
| Assets | Preventive maintenance, breakdown tickets and calibration (`assets.maintenance`); Register assets and contracts; decide condemnation and disposal (`assets.manage`); View the asset register and report equipment breakdowns (`assets.read`) |
| Beds | Work the housekeeping queue and release cleaned beds (`beds.housekeeping`); Reserve and block beds, send beds for maintenance or cleaning (`beds.manage`) |
| Billing | Approve and perform the cancellation of finalized bills (`billing.bills.cancel`); Finalize bills (allocates the bill number) (`billing.bills.finalize`); Post manual charges and cancel unbilled charges (`billing.charges.manage`); View charges, bills, receipts, IP accounts and billing reports (`billing.charges.read`); Collect in-patient advances (deposits) (`billing.deposits`); Approve discounts above a user's limit, a stay billed on another plan than its payer's, a visit's payer changed to another kind or a lower price, and discharges with dues; set discount limits (`billing.discounts.approve`); Keep the GST e-invoice register: the e-invoice JSON of B2B invoices, their IRNs (asked of the portal or recorded), IRN cancellations and e-way bills (`billing.einvoice`); Reconcile online, card and UPI payments with the gateway's and the acquirer's settlements: record missed receipts, send failed refunds again, resolve exceptions (`billing.gateway.reconcile`); Read the GST returns of the facility's GSTIN: GSTR-1 with its GSTN offline-tool JSON, and GSTR-3B (`billing.gst.returns`); Manage IP accounts: packages, interim and final bills, settlement (`billing.ip.manage`); Create and edit draft OP bills (services, discounts within the user's limit) (`billing.op.create`); Collect payments, POS and payment-link payments, pay out approved refunds (`billing.payments.collect`); Approve refunds (the pharmacy's invoice cancellations and returns paying money out or writing stock off included), credit notes, write-offs, reversals of money that did not move, and card and UPI settlement reports withdrawn as imported in error, someone else asked for (`billing.refunds.approve`); Request refunds, credit notes and write-offs, and ask to reverse money that did not move (a receipt not received, a refund not paid out) (`billing.refunds.request`); Open and close cashier shifts; day-end collection report (`billing.shifts`); Set the Tally ledger names the day book posts to (`billing.tally.configure`); Post and export the Tally day book, mark it imported and reconcile it with Tally (`billing.tally.export`); Maintain tariff plans, prices and packages (`billing.tariffs.manage`) |
| Bio-medical waste | Hand waste over to the treatment facility, the register and the annual report (Form IV) (`bmw.manage`); Weigh and bar-code bags of bio-medical waste where it is generated (`bmw.record`) |
| Blood Bank | Blood bank medical officer: approve a transfusion reaction's severity or imputability lowered (which can take it out of haemovigilance reporting) (`bloodbank.reactions.approve`); View blood stock, blood requests and transfusion records (`bloodbank.read`) |
| CRM | Approve a bulk campaign someone else asked to schedule (never one's own) (`crm.campaigns.approve`); Draft SMS/WhatsApp campaigns, preview consented audiences and view delivery (`crm.campaigns.manage`); Ask to schedule campaigns, hold and cancel their sending (`crm.campaigns.send`); Follow-up call worklist: create, assign, record calls and send reminders (`crm.followups.manage`); Capture and work leads; convert them into patients and appointments (`crm.leads.manage`); Patient 360° view: visits, spend, feedback, complaints and communication consent (`crm.patient360.read`); Referring doctors and referral analytics (`crm.referrals.manage`) |
| CSSD | Maintain instrument sets and count sheets (`cssd.masters.manage`); Receive soiled trays, run wash cycles and sterilizer loads, and issue sterile packs (`cssd.process`); View instrument sets, loads, packs, issues and CSSD reports (`cssd.read`); Receive CSSD recall and count-discrepancy alerts and acknowledge recalled packs (`cssd.recalls`); Release loads and record biological indicator results (triggers recalls) (`cssd.release`); Request sterile sets for a department and record packs used on patients (`cssd.requests`) |
| Canteen | Approve a canteen bill's cancellation another asked for, never one's own sale (the money goes back) (`canteen.cancellations.approve`); Staff credit balances, statements, payroll export and settlement (`canteen.credit.manage`); Maintain the canteen menu, prices and stock (`canteen.menu.manage`); View canteen sales reports (`canteen.reports.read`); Ask for today's canteen bills to be cancelled (`canteen.sales.cancel`) |
| Dialysis | Add and edit dialysis machines and stations (`dialysis.machines.manage`); View the dialysis board, patients, machines and sessions (`dialysis.read`) |
| Dietary | Maintain the diet master (`dietary.masters.manage`); Generate meal plans, view kitchen production lists and record meal deliveries (`dietary.meals.manage`); View diet orders and ward meal plans (`dietary.orders.read`) |
| Doctor fees | View accruals and statements; prepare, approve and pay doctor payouts (`doctorfees.payouts.manage`); Maintain doctor revenue-sharing rules (`doctorfees.rules.manage`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| Downtime | Ask for a late back-entry of paper records older than their window (`patients.back_entry`); Approve someone else's late back-entry of paper records (`patients.back_entry.approve`); Print the pre-downtime pack and read the nightly one (census, medication sheets, pending orders, OT list) (`patients.downtime_pack`) |
| E-Prescribing | Approve a drug interaction switched off or made less severe, or a maximum dose raised or switched off, that someone else asked for (pharmacy and therapeutics committee) (`clinical.drug_safety.approve`) |
| EMR / Physician Desktop | Maintain clinical templates, order sets and the drug-safety tables (`clinical.templates.manage`) |
| Emergency | The ER board: patients in the emergency department, most urgent first, with their waits (`clinical.er.board`) |
| Feedback | Receive the alert when a complaint passes its resolution deadline unresolved (`feedback.complaints.escalations`); Handle patient complaints; receives new-complaint alerts (`feedback.complaints.manage`); Maintain feedback forms and QR codes, record feedback and send feedback links (`feedback.manage`); View feedback responses and analytics (`feedback.read`) |
| Front Office | View the front-office dashboard (`frontoffice.dashboard`); Record and resolve enquiries (`frontoffice.enquiries`); Issue and close visitor passes (`frontoffice.visitors`) |
| HR | Biometric terminals, attendance, holidays and regularisation approvals (`hr.attendance.manage`); Maintain staff licences and certifications; receives expiry alerts (`hr.credentials.manage`); Add and edit employees, bank details, designations, leave policies; separate employees (`hr.employees.manage`); View the employee master and credentials (`hr.employees.read`); Approve leave for any employee; credit and adjust leave balances (`hr.leave.approve`); Approve payroll, export the bank transfer file and edit professional-tax slabs (`hr.payroll.approve`); Prepare payroll: salary structures, inputs and processing (`hr.payroll.process`); Shifts and duty rosters (`hr.roster.manage`); Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Health Checkups | Maintain health packages, book and run health checkups (including corporate batches) (`healthcheck.manage`) |
| Housekeeping | Set up the areas cleaned on a schedule, their frequency and checklists (`housekeeping.manage`); Ask housekeeping for a spill, terminal or routine cleaning (`housekeeping.request`); Verify cleaning someone else did, and see the housekeeping report (`housekeeping.verify`); Do the scheduled and asked-for cleaning against the checklists (`housekeeping.work`) |
| In-patients | Admit patients, edit and cancel admissions (`inpatient.admissions.admit`); Initiate, revoke and complete discharges (`inpatient.admissions.discharge`); View admissions, the bed board, discharges and census statistics (`inpatient.admissions.read`); Transfer patients between beds and wards (`inpatient.admissions.transfer`); Set up wards, rooms and beds (`inpatient.masters.manage`) |
| Insurance | Submit claims, record queries and settlements, view receivable ageing (`insurance.claims.manage`); Approve an insurance write-off someone else asked for: the insurer's deductions, or a rejected claim's unpaid balance (`insurance.claims.writeoff.approve`); Maintain insurers, TPAs, corporates, schemes and non-payable items (`insurance.payers.manage`); Record and verify patient insurance policies (`insurance.policies.manage`); Manage cashless pre-authorisations and enhancements (`insurance.preauth.manage`) |
| Integrations | Set up analyser connections: network ports, allowed addresses, serial ports and lab agents (`integrations.lab.connections`); See whether lab analysers and their agents are connected (`integrations.lab.status`); Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`); Choose the printers a workstation uses for labels, wristbands, receipts and documents, and its serial barcode scanner (`integrations.printers.setup`) |
| Interoperability | Export discharge summaries, lab reports, radiology and cardiology reports, consultation notes, prescriptions and bills as ABDM health records, and link them to the patient's ABHA (every export and link is audited). Staff only: it does not check the record-sharing consent, so never give it to an integration service account (`abdm.export`); Register partner applications that read the FHIR API with SMART on FHIR tokens, and suspend them (a registration waits for a second administrator's approval; every token still reads only what patients' consents allow) (`fhir.clients.manage`); Read and search patient records through the HL7 FHIR R4 API (patients with record-sharing consent only; every access is audited) (`fhir.read`) |
| Inventory | Approve stock adjustments and write-offs (not your own) (`inventory.adjustments.approve`); Set up refrigerators and freezers, their allowed temperatures and their data loggers (`inventory.cold_chain.manage`); Record refrigerator and freezer temperatures and the action taken on an excursion (`inventory.cold_chain.record`); Run physical stock counts and raise stock adjustments (`inventory.counts`); Approve a drug made less strictly handled: no longer controlled or high-alert, or a lower schedule (not your own) (`inventory.drug_class.approve`); Approve, reserve and issue indents from a store (`inventory.indents.issue`); Raise indents (stock requests) to a store (`inventory.indents.raise`); Maintain the item master, categories and stores (`inventory.items.manage`); Approve a recalled batch released for use (not your own request) (`inventory.recall.release`); View stock, batches, the stock ledger and inventory reports (`inventory.stock.read`); Dispatch and receive stock transfers between stores (`inventory.transfers`) |
| Laboratory | Manage the test master, reference ranges, analysers and reference labs (`lab.masters.manage`) |
| Linen | Issue, receive, wash and condemn linen, and set ward par levels (`linen.manage`); Send a ward's soiled linen to the laundry and report what it has lost (`linen.ward`) |
| Masters | Edit facilities, departments, doctors and services (`masters.manage`); Approve the changes another person makes to a service's price, GST and use, to tariff plans, packages and payers' terms, and to the PM-JAY HBP master (`masters.prices.approve`); View facilities, departments, doctors and services (`masters.read`) |
| Medical Records | Import the WHO ICD-10 and CMS ICD-10-PCS catalogue files (changes every user's code lists) (`mrd.catalogue.import`); The medico-legal case register (`mrd.mlc.register`); Chart tracking, deficiencies, record requests, archival and morbidity reports (`mrd.records`); The births and deaths registers, and reporting them to the Registrar within 21 days (`mrd.vital.register`) |
| Mortuary | Set the mortuary's cold-storage slots (`mortuary.configure`); Receive bodies into the mortuary, release them and keep the register (`mortuary.manage`) |
| Nursing | Ward nurse in charge: see the verbal and telephone medication orders and nurses' stops not countersigned in time, and be told of each (`nursing.in_charge`) |
| Operation Theatre | OT in-charge: approve a correction of an implant or consumable line after the operation has started (its charge and stock follow) (`ot.materials.approve`); View theatres, the OT calendar and surgery records (`ot.read`); View OT utilisation reports (`ot.reports.read`); Add and edit operation theatres (`ot.theatres.manage`) |
| Patients | Capture and revoke patient consents (`patients.consents`); See patients' phone numbers, email and address in full (others see them masked); needed to edit a patient (`patients.contact.read`); Attach and view patient documents (`patients.documents`); Issue downtime UHID, visit, bill and receipt blocks for paper work, follow their entry and return them (`patients.downtime`); Approve a change someone else asked for to a patient's name, sex, date of birth, ABHA, Aadhaar or deceased status (`patients.identity.approve`); Check a patient's identity with their Aadhaar fingerprint (UIDAI) or their ABHA (a code from ABDM) (`patients.identity.verify`); Merge duplicate patient records (`patients.merge`); Search and view patient demographics (`patients.read`); Register patients and edit demographics (`patients.register`); Open restricted (VIP/confidential) records without break-the-glass (`patients.restricted.read`) |
| Pharmacy | Pharmacovigilance: assess suspected adverse drug reactions and send them to the ADR monitoring centre (`pharmacy.adr.manage`); Dispense prescriptions (FEFO batches, substitution, counselling) (`pharmacy.dispense`); Raise ward indents for admitted patients (`pharmacy.ip.indent`); Issue medicines to admitted patients, accept ward returns, and approve another pharmacist's ward return not restocked (`pharmacy.ip.issue`); View the e-prescription queue and pharmacy stock (`pharmacy.queue.read`); Authorise, approve and reject prescription refills (`pharmacy.refills`); View and export the Schedule H/H1/X and narcotic registers (`pharmacy.registers`); Sales returns and same-day invoice cancellation (a cancellation, and a return paying money out or writing stock off, waits for someone who approves refunds) (`pharmacy.returns`); Counter sales, GST invoices and collection of dues (`pharmacy.sales`) |
| Physiotherapy | View physiotherapy referrals, plans and sessions (`physio.read`) |
| Privacy | Approve closing a personal data breach and taking patients off its notification list, asked for by someone else (the Data Protection Officer) (`privacy.breaches.approve`); Record personal data breaches, report them to the Board and notify the people affected (`privacy.breaches.manage`); Register the consent managers the hospital accepts patients' consent from (`privacy.consent_managers.manage`); Record privacy requests and nominees for patients at the desk (`privacy.requests.log`); Verify requesters, answer privacy requests, and correct or erase data for them (`privacy.requests.manage`); View patients' privacy requests (access, correction, erasure, grievance) (`privacy.requests.read`); Set how long each kind of data is kept, and run the clean-up (`privacy.retention.manage`); Approve data protection impact assessments written by someone else (the Data Protection Officer) (`privacy.sdf.approve`); Keep the Significant Data Fiduciary records: the Data Protection Officer, impact assessments and data audits (`privacy.sdf.manage`); Set the grievance officer's contact details and the response period (`privacy.settings.manage`) |
| Purchase | Approve purchase requisitions and purchase orders (`purchase.approve`); Check goods receipts against the supplier's invoice and post them into stock (not your own) (`purchase.grn.approve`); Receive goods against vendor invoices (GRN) (`purchase.grn.receive`); Vendors, purchase requisitions and purchase orders (`purchase.manage`) |
| Quality | Keep the NABH audit checklists, schedule internal audits and close them (`quality.audits.manage`); Plan corrective/preventive actions and verify their effectiveness (`quality.capa.manage`); Record downtime of HC360 and drills of the downtime procedure, and sign off the back-entry of paper records (`quality.downtime.manage`); Record central lines, urinary catheters and ventilation put in and taken out (device days for infection surveillance) (`quality.hai.devices`); Review possible hospital-acquired infections against the NHSN definitions and see the HAI rates; receive positive-culture alerts (`quality.hai.manage`); Approve closing a sentinel or never event, and re-grading one out of sentinel, asked for by someone else (never one's own report) (`quality.incidents.approve`); Review, grade and investigate incidents; receive sentinel-event alerts; report device adverse events to MvPI (`quality.incidents.manage`); Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`); Define quality indicators and record their values (`quality.indicators.manage`); Maintain the risk register (`quality.risks.manage`) |
| Radiology | View and export the monthly PCPNDT Form F register for the Appropriate Authority (`radiology.form_f.register`) |
| Remote Monitoring | Enrol patients, set thresholds, register devices and record readings (`rpm.manage`) |
| Scheduling | Book appointments and add patients to a doctor's waitlist (`scheduling.appointments.book`); Confirm, reschedule, cancel, check in and mark no-shows; overbook full slots (`scheduling.appointments.manage`); View appointment calendars, slots and waitlists (`scheduling.appointments.read`); Set up doctor OPD sessions, leave, blocks and extra sessions (`scheduling.schedules.manage`) |
| Telehealth | View video consultations, their participants and chat (`telehealth.sessions.read`) |
| Visits | Show the queue display board (kiosk/TV) (`queue.display`); Call, start and complete queue tokens (`queue.manage`); Approve cancelling a visit the doctor has started, asked for by someone else (a doctor approves only the visits they treat) (`visits.cancel.approve`); Create, cancel and complete visits (`visits.manage`); View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>Doctor</strong>: Consultants and residents: EMR, orders, e-prescriptions (87 permissions)</summary>

| Area | May |
|---|---|
| AI assistant | Use voice or typed commands (open patients, appointments, follow-ups, notes) (`ai.assistant.use`); Dictate clinical notes and get AI-structured SOAP drafts for review (`ai.dictation.use`) |
| Antimicrobial Stewardship | The antimicrobial stewardship queue and report (`clinical.ams.read`) |
| Blood Bank | Report transfusion reactions (`bloodbank.reactions.report`); View blood stock, blood requests and transfusion records (`bloodbank.read`); Request blood for a patient (`bloodbank.requests.create`); Check a unit of blood at the bedside, start and end its transfusion, and witness another's check (`bloodbank.transfuse`) |
| Cardiology | View cardiology studies and released reports (`cardiology.studies.read`) |
| Diagnostics | Receive critical lab value and critical finding alerts (`diagnostics.critical.alerts`) |
| Dialysis | Approve a dialysis patient taken off a positive infection status (HBV, HCV, HIV) another asked for, on the lab result (`dialysis.infection_status.approve`); Enrol dialysis patients and edit their dialysis profile (`dialysis.patients.manage`); View the dialysis board, patients, machines and sessions (`dialysis.read`); Schedule and record dialysis sessions, dialyzers and machine disinfection (`dialysis.sessions.manage`) |
| Dietary | Write, stop and review diet orders (`dietary.orders.manage`); View diet orders and ward meal plans (`dietary.orders.read`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| Downtime | Ask for a late back-entry of paper records older than their window (`patients.back_entry`); Print the pre-downtime pack and read the nightly one (census, medication sheets, pending orders, OT list) (`patients.downtime_pack`) |
| E-Prescribing | Write, sign and amend discharge summaries (`clinical.discharge_summary.write`); Issue an authorised refill of a signed prescription (`clinical.prescriptions.refill`); Sign and cancel e-prescriptions (sends them to the pharmacy) (`clinical.prescriptions.sign`); Draft e-prescriptions (`clinical.prescriptions.write`) |
| EMR / Physician Desktop | Set and clear patient alerts (DNR, fall risk, isolation, pregnancy…) (`clinical.alerts.manage`); Record and update allergies (`clinical.allergies.manage`); View the clinical record: encounters, vitals, allergies, problems, orders, prescriptions and timeline (`clinical.emr.read`); Write encounters (SOAP), diagnoses, clinical notes, procedures, referrals and health-checkup summaries (`clinical.emr.write`); Sign and amend encounters and clinical notes (`clinical.encounters.sign`); See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`); Record immunisations (`clinical.immunizations.record`); Place and cancel lab, imaging, cardiology and procedure orders (`clinical.orders.place`); Maintain clinical templates, order sets and the drug-safety tables (`clinical.templates.manage`); Record vital signs (`clinical.vitals.record`) |
| Emergency | The ER board: patients in the emergency department, most urgent first, with their waits (`clinical.er.board`); See emergency patients and decide where they go: discharge, admit, refer, LAMA (`clinical.er.disposition`); Enter medico-legal cases in the MLC register and record the police intimation, and a sexual-assault victim's examination and its report (`clinical.er.mlc`); Triage emergency patients on the Emergency Severity Index (`clinical.er.triage`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Housekeeping | Ask housekeeping for a spill, terminal or routine cleaning (`housekeeping.request`) |
| In-patients | Admit patients, edit and cancel admissions (`inpatient.admissions.admit`); Initiate, revoke and complete discharges (`inpatient.admissions.discharge`); View admissions, the bed board, discharges and census statistics (`inpatient.admissions.read`); Transfer patients between beds and wards (`inpatient.admissions.transfer`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Interoperability | Ask a patient, through ABDM, to let the hospital see their records at other providers, in their name as the requesting doctor (the patient decides in their ABHA app; every request is audited) (`abdm.records.request`) |
| Laboratory | View lab orders, samples, results and reports (`lab.orders.read`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Medical Records | Record births in the hospital (Form 1, and Form 3 for a still birth) (`mrd.births.record`); Certify the cause of death (MCCD, Form 4) (`mrd.deaths.certify`); Confirm, as a second doctor, a death certificate another doctor certified, before it is reported (`mrd.deaths.confirm`) |
| Nursing | Nursing notes, handovers, intake/output, risk assessments and consultant visits (`nursing.chart`); Medication chart and medication administration record (MAR) (`nursing.mar`); Order, renew and end a patient's restraint (a doctor's order, for at most four hours at a time) (`nursing.restraint.order`) |
| Operation Theatre | Write the anaesthesia record (`ot.anaesthesia.record`); Book, schedule, postpone and cancel surgeries (`ot.bookings.manage`); Record the pre-operative and WHO surgical safety checklists (`ot.checklists.record`); Record implants and consumables used in surgery (billed to the patient) (`ot.materials.record`); View theatres, the OT calendar and surgery records (`ot.read`); Start and complete surgery, write operation notes and post-op orders (`ot.surgery.perform`) |
| Patients | Emergency access to restricted records (audited) (`patients.break_glass`); Capture and revoke patient consents (`patients.consents`); See patients' phone numbers, email and address in full (others see them masked); needed to edit a patient (`patients.contact.read`); Attach and view patient documents (`patients.documents`); Search and view patient demographics (`patients.read`); Open the clinical record of patients in the HIV category (`patients.sensitive.hiv`); Put a patient's record in a sensitive category (HIV, mental health, MTP, sexual violence) or take it out (`patients.sensitive.manage`); Open the clinical record of patients in the mental health category (`patients.sensitive.mental_health`); Open the clinical record of patients in the sexual-violence category (`patients.sensitive.sexual_violence`) |
| Pharmacy | Report suspected adverse drug reactions (PvPI) (`pharmacy.adr.report`) |
| Physiotherapy | View physiotherapy referrals, plans and sessions (`physio.read`); Refer patients to physiotherapy and cancel referrals (`physio.referrals.manage`) |
| Quality | Record central lines, urinary catheters and ventilation put in and taken out (device days for infection surveillance) (`quality.hai.devices`); Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Radiology | View radiology images from PACS, in HC360 and in the PACS viewer (`radiology.images.view`); View imaging studies and released radiology reports (`radiology.studies.read`) |
| Remote Monitoring | Receive, acknowledge and resolve remote monitoring alerts (`rpm.alerts.manage`); Enrol patients, set thresholds, register devices and record readings (`rpm.manage`); View monitored patients, readings, trends and alerts (`rpm.read`) |
| Scheduling | Book appointments and add patients to a doctor's waitlist (`scheduling.appointments.book`); View appointment calendars, slots and waitlists (`scheduling.appointments.read`) |
| Telehealth | Run video consultations (admit patients, start, end, record consent) (`telehealth.consult`); Create and cancel video consultations and resend join links (`telehealth.sessions.manage`); View video consultations, their participants and chat (`telehealth.sessions.read`) |
| Visits | Call, start and complete queue tokens (`queue.manage`); Approve cancelling a visit the doctor has started, asked for by someone else (a doctor approves only the visits they treat) (`visits.cancel.approve`); Create, cancel and complete visits (`visits.manage`); View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>Nurse</strong>: Nursing care, vitals, ward operations (84 permissions)</summary>

| Area | May |
|---|---|
| AI assistant | Use voice or typed commands (open patients, appointments, follow-ups, notes) (`ai.assistant.use`) |
| Ambulance | Take ambulance calls, send vehicles and record each trip (`ambulance.dispatch`) |
| Antimicrobial Stewardship | The antimicrobial stewardship queue and report (`clinical.ams.read`) |
| Assets | View the asset register and report equipment breakdowns (`assets.read`) |
| Beds | Work the housekeeping queue and release cleaned beds (`beds.housekeeping`); Reserve and block beds, send beds for maintenance or cleaning (`beds.manage`) |
| Bio-medical waste | Weigh and bar-code bags of bio-medical waste where it is generated (`bmw.record`) |
| Blood Bank | Report transfusion reactions (`bloodbank.reactions.report`); View blood stock, blood requests and transfusion records (`bloodbank.read`); Request blood for a patient (`bloodbank.requests.create`); Check a unit of blood at the bedside, start and end its transfusion, and witness another's check (`bloodbank.transfuse`) |
| CSSD | View instrument sets, loads, packs, issues and CSSD reports (`cssd.read`); Receive CSSD recall and count-discrepancy alerts and acknowledge recalled packs (`cssd.recalls`); Request sterile sets for a department and record packs used on patients (`cssd.requests`) |
| Cardiology | View cardiology studies and released reports (`cardiology.studies.read`) |
| Diagnostics | Receive critical lab value and critical finding alerts (`diagnostics.critical.alerts`) |
| Dialysis | Enrol dialysis patients and edit their dialysis profile (`dialysis.patients.manage`); View the dialysis board, patients, machines and sessions (`dialysis.read`); Schedule and record dialysis sessions, dialyzers and machine disinfection (`dialysis.sessions.manage`) |
| Dietary | Generate meal plans, view kitchen production lists and record meal deliveries (`dietary.meals.manage`); Write, stop and review diet orders (`dietary.orders.manage`); View diet orders and ward meal plans (`dietary.orders.read`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| Downtime | Ask for a late back-entry of paper records older than their window (`patients.back_entry`); Print the pre-downtime pack and read the nightly one (census, medication sheets, pending orders, OT list) (`patients.downtime_pack`) |
| EMR / Physician Desktop | Set and clear patient alerts (DNR, fall risk, isolation, pregnancy…) (`clinical.alerts.manage`); Record and update allergies (`clinical.allergies.manage`); View the clinical record: encounters, vitals, allergies, problems, orders, prescriptions and timeline (`clinical.emr.read`); See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`); Record immunisations (`clinical.immunizations.record`); Record vital signs (`clinical.vitals.record`) |
| Emergency | The ER board: patients in the emergency department, most urgent first, with their waits (`clinical.er.board`); Triage emergency patients on the Emergency Severity Index (`clinical.er.triage`) |
| Front Office | Issue and close visitor passes (`frontoffice.visitors`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Housekeeping | Ask housekeeping for a spill, terminal or routine cleaning (`housekeeping.request`); Verify cleaning someone else did, and see the housekeeping report (`housekeeping.verify`); Do the scheduled and asked-for cleaning against the checklists (`housekeeping.work`) |
| In-patients | Admit patients, edit and cancel admissions (`inpatient.admissions.admit`); Initiate, revoke and complete discharges (`inpatient.admissions.discharge`); View admissions, the bed board, discharges and census statistics (`inpatient.admissions.read`); Transfer patients between beds and wards (`inpatient.admissions.transfer`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Inventory | Record refrigerator and freezer temperatures and the action taken on an excursion (`inventory.cold_chain.record`); Raise indents (stock requests) to a store (`inventory.indents.raise`) |
| Laboratory | View lab orders, samples, results and reports (`lab.orders.read`); Collect samples and print barcode labels (`lab.samples.collect`) |
| Linen | Send a ward's soiled linen to the laundry and report what it has lost (`linen.ward`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Medical Records | Record births in the hospital (Form 1, and Form 3 for a still birth) (`mrd.births.record`) |
| Mortuary | Receive bodies into the mortuary, release them and keep the register (`mortuary.manage`) |
| Nursing | Nursing notes, handovers, intake/output, risk assessments and consultant visits (`nursing.chart`); Medication chart and medication administration record (MAR) (`nursing.mar`) |
| Operation Theatre | Record the pre-operative and WHO surgical safety checklists (`ot.checklists.record`); Record implants and consumables used in surgery (billed to the patient) (`ot.materials.record`); View theatres, the OT calendar and surgery records (`ot.read`) |
| Patients | Emergency access to restricted records (audited) (`patients.break_glass`); Capture and revoke patient consents (`patients.consents`); See patients' phone numbers, email and address in full (others see them masked); needed to edit a patient (`patients.contact.read`); Attach and view patient documents (`patients.documents`); Search and view patient demographics (`patients.read`); Register patients and edit demographics (`patients.register`); Open the clinical record of patients in the HIV category (`patients.sensitive.hiv`); Open the clinical record of patients in the mental health category (`patients.sensitive.mental_health`); Open the clinical record of patients in the sexual-violence category (`patients.sensitive.sexual_violence`) |
| Pharmacy | Report suspected adverse drug reactions (PvPI) (`pharmacy.adr.report`); Raise ward indents for admitted patients (`pharmacy.ip.indent`); Count the narcotic drugs at a change of shift, and witness another's count (`pharmacy.ndps.count`) |
| Physiotherapy | View physiotherapy referrals, plans and sessions (`physio.read`) |
| Quality | Record central lines, urinary catheters and ventilation put in and taken out (device days for infection surveillance) (`quality.hai.devices`); Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Radiology | View imaging studies and released radiology reports (`radiology.studies.read`) |
| Remote Monitoring | Receive, acknowledge and resolve remote monitoring alerts (`rpm.alerts.manage`); Enrol patients, set thresholds, register devices and record readings (`rpm.manage`); View monitored patients, readings, trends and alerts (`rpm.read`) |
| Scheduling | Book appointments and add patients to a doctor's waitlist (`scheduling.appointments.book`); Confirm, reschedule, cancel, check in and mark no-shows; overbook full slots (`scheduling.appointments.manage`); View appointment calendars, slots and waitlists (`scheduling.appointments.read`) |
| Telehealth | Create and cancel video consultations and resend join links (`telehealth.sessions.manage`); View video consultations, their participants and chat (`telehealth.sessions.read`) |
| Visits | Call, start and complete queue tokens (`queue.manage`); Create, cancel and complete visits (`visits.manage`); View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>FrontOffice</strong>: Registration, visits, queue and enquiries (40 permissions)</summary>

| Area | May |
|---|---|
| AI assistant | Use voice or typed commands (open patients, appointments, follow-ups, notes) (`ai.assistant.use`) |
| Ambulance | Take ambulance calls, send vehicles and record each trip (`ambulance.dispatch`) |
| Beds | Reserve and block beds, send beds for maintenance or cleaning (`beds.manage`) |
| Billing | Create and edit draft OP bills (services, discounts within the user's limit) (`billing.op.create`); Collect payments, POS and payment-link payments, pay out approved refunds (`billing.payments.collect`); Open and close cashier shifts; day-end collection report (`billing.shifts`) |
| CRM | Capture and work leads; convert them into patients and appointments (`crm.leads.manage`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| Downtime | Ask for a late back-entry of paper records older than their window (`patients.back_entry`) |
| Emergency | The ER board: patients in the emergency department, most urgent first, with their waits (`clinical.er.board`) |
| Front Office | View the front-office dashboard (`frontoffice.dashboard`); Record and resolve enquiries (`frontoffice.enquiries`); Issue and close visitor passes (`frontoffice.visitors`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Health Checkups | Maintain health packages, book and run health checkups (including corporate batches) (`healthcheck.manage`) |
| In-patients | Admit patients, edit and cancel admissions (`inpatient.admissions.admit`); View admissions, the bed board, discharges and census statistics (`inpatient.admissions.read`); Transfer patients between beds and wards (`inpatient.admissions.transfer`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Laboratory | Register walk-in and outside-referral lab orders; cancel tests (`lab.orders.create`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | Capture and revoke patient consents (`patients.consents`); See patients' phone numbers, email and address in full (others see them masked); needed to edit a patient (`patients.contact.read`); Attach and view patient documents (`patients.documents`); Check a patient's identity with their Aadhaar fingerprint (UIDAI) or their ABHA (a code from ABDM) (`patients.identity.verify`); Search and view patient demographics (`patients.read`); Register patients and edit demographics (`patients.register`) |
| Privacy | Record privacy requests and nominees for patients at the desk (`privacy.requests.log`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Scheduling | Book appointments and add patients to a doctor's waitlist (`scheduling.appointments.book`); Confirm, reschedule, cancel, check in and mark no-shows; overbook full slots (`scheduling.appointments.manage`); View appointment calendars, slots and waitlists (`scheduling.appointments.read`); Set up doctor OPD sessions, leave, blocks and extra sessions (`scheduling.schedules.manage`) |
| Telehealth | Create and cancel video consultations and resend join links (`telehealth.sessions.manage`); View video consultations, their participants and chat (`telehealth.sessions.read`) |
| Visits | Show the queue display board (kiosk/TV) (`queue.display`); Call, start and complete queue tokens (`queue.manage`); Create, cancel and complete visits (`visits.manage`); View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>Billing</strong>: OP/IP billing and charge management (25 permissions)</summary>

| Area | May |
|---|---|
| Billing | Finalize bills (allocates the bill number) (`billing.bills.finalize`); Post manual charges and cancel unbilled charges (`billing.charges.manage`); View charges, bills, receipts, IP accounts and billing reports (`billing.charges.read`); Collect in-patient advances (deposits) (`billing.deposits`); Keep the GST e-invoice register: the e-invoice JSON of B2B invoices, their IRNs (asked of the portal or recorded), IRN cancellations and e-way bills (`billing.einvoice`); Manage IP accounts: packages, interim and final bills, settlement (`billing.ip.manage`); Create and edit draft OP bills (services, discounts within the user's limit) (`billing.op.create`); Collect payments, POS and payment-link payments, pay out approved refunds (`billing.payments.collect`); Request refunds, credit notes and write-offs, and ask to reverse money that did not move (a receipt not received, a refund not paid out) (`billing.refunds.request`); Open and close cashier shifts; day-end collection report (`billing.shifts`); Maintain tariff plans, prices and packages (`billing.tariffs.manage`) |
| Dietary | View diet orders and ward meal plans (`dietary.orders.read`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| Downtime | Ask for a late back-entry of paper records older than their window (`patients.back_entry`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| In-patients | View admissions, the bed board, discharges and census statistics (`inpatient.admissions.read`) |
| Insurance | Record and verify patient insurance policies (`insurance.policies.manage`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | See patients' phone numbers, email and address in full (others see them masked); needed to edit a patient (`patients.contact.read`); Search and view patient demographics (`patients.read`); Register patients and edit demographics (`patients.register`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>Cashier</strong>: Payments, receipts and day-end closing (18 permissions)</summary>

| Area | May |
|---|---|
| Billing | Finalize bills (allocates the bill number) (`billing.bills.finalize`); View charges, bills, receipts, IP accounts and billing reports (`billing.charges.read`); Collect in-patient advances (deposits) (`billing.deposits`); Create and edit draft OP bills (services, discounts within the user's limit) (`billing.op.create`); Collect payments, POS and payment-link payments, pay out approved refunds (`billing.payments.collect`); Request refunds, credit notes and write-offs, and ask to reverse money that did not move (a receipt not received, a refund not paid out) (`billing.refunds.request`); Open and close cashier shifts; day-end collection report (`billing.shifts`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| Downtime | Ask for a late back-entry of paper records older than their window (`patients.back_entry`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| In-patients | View admissions, the bed board, discharges and census statistics (`inpatient.admissions.read`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | See patients' phone numbers, email and address in full (others see them masked); needed to edit a patient (`patients.contact.read`); Search and view patient demographics (`patients.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>InsuranceDesk</strong>: Insurance verification, pre-authorisation and claims (18 permissions)</summary>

| Area | May |
|---|---|
| Billing | View charges, bills, receipts, IP accounts and billing reports (`billing.charges.read`); Manage IP accounts: packages, interim and final bills, settlement (`billing.ip.manage`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| In-patients | View admissions, the bed board, discharges and census statistics (`inpatient.admissions.read`) |
| Insurance | Submit claims, record queries and settlements, view receivable ageing (`insurance.claims.manage`); Maintain insurers, TPAs, corporates, schemes and non-payable items (`insurance.payers.manage`); Record and verify patient insurance policies (`insurance.policies.manage`); Manage cashless pre-authorisations and enhancements (`insurance.preauth.manage`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | See patients' phone numbers, email and address in full (others see them masked); needed to edit a patient (`patients.contact.read`); Attach and view patient documents (`patients.documents`); Search and view patient demographics (`patients.read`); Register patients and edit demographics (`patients.register`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>Pharmacist</strong>: OP/IP pharmacy dispensing and pharmacy stock (32 permissions)</summary>

| Area | May |
|---|---|
| Antimicrobial Stewardship | The antimicrobial stewardship queue and report (`clinical.ams.read`) |
| Billing | Keep the GST e-invoice register: the e-invoice JSON of B2B invoices, their IRNs (asked of the portal or recorded), IRN cancellations and e-way bills (`billing.einvoice`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| E-Prescribing | Approve a drug interaction switched off or made less severe, or a maximum dose raised or switched off, that someone else asked for (pharmacy and therapeutics committee) (`clinical.drug_safety.approve`); Issue an authorised refill of a signed prescription (`clinical.prescriptions.refill`) |
| EMR / Physician Desktop | See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| In-patients | View admissions, the bed board, discharges and census statistics (`inpatient.admissions.read`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Inventory | Set up refrigerators and freezers, their allowed temperatures and their data loggers (`inventory.cold_chain.manage`); Record refrigerator and freezer temperatures and the action taken on an excursion (`inventory.cold_chain.record`); Approve a drug made less strictly handled: no longer controlled or high-alert, or a lower schedule (not your own) (`inventory.drug_class.approve`); Raise indents (stock requests) to a store (`inventory.indents.raise`); Approve a recalled batch released for use (not your own request) (`inventory.recall.release`); View stock, batches, the stock ledger and inventory reports (`inventory.stock.read`); Dispatch and receive stock transfers between stores (`inventory.transfers`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Pharmacy | Pharmacovigilance: assess suspected adverse drug reactions and send them to the ADR monitoring centre (`pharmacy.adr.manage`); Report suspected adverse drug reactions (PvPI) (`pharmacy.adr.report`); Dispense prescriptions (FEFO batches, substitution, counselling) (`pharmacy.dispense`); Raise ward indents for admitted patients (`pharmacy.ip.indent`); Issue medicines to admitted patients, accept ward returns, and approve another pharmacist's ward return not restocked (`pharmacy.ip.issue`); Count the narcotic drugs at a change of shift, and witness another's count (`pharmacy.ndps.count`); View the e-prescription queue and pharmacy stock (`pharmacy.queue.read`); Authorise, approve and reject prescription refills (`pharmacy.refills`); View and export the Schedule H/H1/X and narcotic registers (`pharmacy.registers`); Sales returns and same-day invoice cancellation (a cancellation, and a return paying money out or writing stock off, waits for someone who approves refunds) (`pharmacy.returns`); Counter sales, GST invoices and collection of dues (`pharmacy.sales`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>LabTechnician</strong>: Sample collection, processing and result entry (17 permissions)</summary>

| Area | May |
|---|---|
| Bio-medical waste | Weigh and bar-code bags of bio-medical waste where it is generated (`bmw.record`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| EMR / Physician Desktop | See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | See whether lab analysers and their agents are connected (`integrations.lab.status`); Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Laboratory | Register walk-in and outside-referral lab orders; cancel tests (`lab.orders.create`); View lab orders, samples, results and reports (`lab.orders.read`); Manage QC lots, record and review QC runs (`lab.qc.manage`); Enter results and record external reports (`lab.results.enter`); Collect samples and print barcode labels (`lab.samples.collect`); Receive samples in the lab, reject them and send tests to reference labs (`lab.samples.receive`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>Pathologist</strong>: Laboratory result verification and sign-off (18 permissions)</summary>

| Area | May |
|---|---|
| Antimicrobial Stewardship | Approve or reject restricted antibiotics and review antibiotic courses (`clinical.ams.approve`); The antimicrobial stewardship queue and report (`clinical.ams.read`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| EMR / Physician Desktop | See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | See whether lab analysers and their agents are connected (`integrations.lab.status`); Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Laboratory | Manage the test master, reference ranges, analysers and reference labs (`lab.masters.manage`); View lab orders, samples, results and reports (`lab.orders.read`); Manage QC lots, record and review QC runs (`lab.qc.manage`); Enter results and record external reports (`lab.results.enter`); Verify, release and amend lab reports; document critical-value calls; override QC lockouts (`lab.results.verify`); Receive samples in the lab, reject them and send tests to reference labs (`lab.samples.receive`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>Radiographer</strong>: Imaging acquisition and modality worklist (13 permissions)</summary>

| Area | May |
|---|---|
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| EMR / Physician Desktop | See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Radiology | Record the woman's PCPNDT Form F details and declaration before an ultrasound in pregnancy (`radiology.form_f.record`); View radiology images from PACS, in HC360 and in the PACS viewer (`radiology.images.view`); View imaging studies and released radiology reports (`radiology.studies.read`); Schedule and perform imaging studies; modality worklist and PACS reconciliation (`radiology.worklist`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>Radiologist</strong>: Imaging reporting and sign-off (17 permissions)</summary>

| Area | May |
|---|---|
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| EMR / Physician Desktop | See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Radiology | Record the woman's PCPNDT Form F details and declaration before an ultrasound in pregnancy (`radiology.form_f.record`); View and export the monthly PCPNDT Form F register for the Appropriate Authority (`radiology.form_f.register`); Complete PCPNDT Form F with the result and the performing doctor's declaration (`radiology.form_f.sign`); View radiology images from PACS, in HC360 and in the PACS viewer (`radiology.images.view`); Sign off, release and add addenda to radiology reports; manage templates (`radiology.reports.verify`); Write radiology reports (`radiology.reports.write`); View imaging studies and released radiology reports (`radiology.studies.read`); Schedule and perform imaging studies; modality worklist and PACS reconciliation (`radiology.worklist`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>CardiologyTechnician</strong>: ECG/Echo/TMT acquisition (11 permissions)</summary>

| Area | May |
|---|---|
| Cardiology | Perform ECG/Echo/TMT/Holter/ABPM studies and upload device output (`cardiology.studies.manage`); View cardiology studies and released reports (`cardiology.studies.read`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| EMR / Physician Desktop | See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>Cardiologist</strong>: Cardiology reporting and sign-off (give cardiologists the Doctor role as well) (11 permissions)</summary>

| Area | May |
|---|---|
| Cardiology | Report and sign off cardiology studies (`cardiology.reports.verify`); View cardiology studies and released reports (`cardiology.studies.read`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| EMR / Physician Desktop | See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>StoreKeeper</strong>: Stores, stock issues and receipts (18 permissions)</summary>

| Area | May |
|---|---|
| Assets | View the asset register and report equipment breakdowns (`assets.read`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Inventory | Set up refrigerators and freezers, their allowed temperatures and their data loggers (`inventory.cold_chain.manage`); Record refrigerator and freezer temperatures and the action taken on an excursion (`inventory.cold_chain.record`); Run physical stock counts and raise stock adjustments (`inventory.counts`); Approve, reserve and issue indents from a store (`inventory.indents.issue`); Raise indents (stock requests) to a store (`inventory.indents.raise`); Maintain the item master, categories and stores (`inventory.items.manage`); View stock, batches, the stock ledger and inventory reports (`inventory.stock.read`); Dispatch and receive stock transfers between stores (`inventory.transfers`) |
| Linen | Issue, receive, wash and condemn linen, and set ward par levels (`linen.manage`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Purchase | Receive goods against vendor invoices (GRN) (`purchase.grn.receive`); Vendors, purchase requisitions and purchase orders (`purchase.manage`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |

</details>

<details>
<summary><strong>PurchaseOfficer</strong>: Purchase requisitions, orders and vendors (11 permissions)</summary>

| Area | May |
|---|---|
| Assets | View the asset register and report equipment breakdowns (`assets.read`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Inventory | Maintain the item master, categories and stores (`inventory.items.manage`); View stock, batches, the stock ledger and inventory reports (`inventory.stock.read`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Purchase | Check goods receipts against the supplier's invoice and post them into stock (not your own) (`purchase.grn.approve`); Vendors, purchase requisitions and purchase orders (`purchase.manage`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |

</details>

<details>
<summary><strong>MrdOfficer</strong>: Medical records, coding and record requests (36 permissions)</summary>

| Area | May |
|---|---|
| Analytics | View clinical quality reports, including reports that list individual patients (audited) (`analytics.clinical.read`) |
| Disease Surveillance | Notifiable diseases (IDSP): the cases, the weekly P and L forms to IHIP, and the outbreak alerts (`mrd.idsp.report`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| Downtime | Ask for a late back-entry of paper records older than their window (`patients.back_entry`); Approve someone else's late back-entry of paper records (`patients.back_entry.approve`); Print the pre-downtime pack and read the nightly one (census, medication sheets, pending orders, OT list) (`patients.downtime_pack`) |
| EMR / Physician Desktop | View the clinical record: encounters, vitals, allergies, problems, orders, prescriptions and timeline (`clinical.emr.read`); See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| In-patients | View admissions, the bed board, discharges and census statistics (`inpatient.admissions.read`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Interoperability | Export discharge summaries, lab reports, radiology and cardiology reports, consultation notes, prescriptions and bills as ABDM health records, and link them to the patient's ABHA (every export and link is audited). Staff only: it does not check the record-sharing consent, so never give it to an integration service account (`abdm.export`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Medical Records | Record births in the hospital (Form 1, and Form 3 for a still birth) (`mrd.births.record`); Clinical coding (ICD-10 / ICD-10-PCS) and verification (`mrd.coding`); The medico-legal case register (`mrd.mlc.register`); Chart tracking, deficiencies, record requests, archival and morbidity reports (`mrd.records`); The births and deaths registers, and reporting them to the Registrar within 21 days (`mrd.vital.register`) |
| Patients | See patients' phone numbers, email and address in full (others see them masked); needed to edit a patient (`patients.contact.read`); Attach and view patient documents (`patients.documents`); Issue downtime UHID, visit, bill and receipt blocks for paper work, follow their entry and return them (`patients.downtime`); Approve a change someone else asked for to a patient's name, sex, date of birth, ABHA, Aadhaar or deceased status (`patients.identity.approve`); Merge duplicate patient records (`patients.merge`); Search and view patient demographics (`patients.read`); Open the clinical record of patients in the HIV category (`patients.sensitive.hiv`); Open the clinical record of patients in the mental health category (`patients.sensitive.mental_health`); Open the clinical record of patients in the termination-of-pregnancy (MTP) category (`patients.sensitive.mtp`); Open the clinical record of patients in the sexual-violence category (`patients.sensitive.sexual_violence`) |
| Privacy | Record privacy requests and nominees for patients at the desk (`privacy.requests.log`); Verify requesters, answer privacy requests, and correct or erase data for them (`privacy.requests.manage`); View patients' privacy requests (access, correction, erasure, grievance) (`privacy.requests.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Termination of pregnancy | View termination of pregnancy cases (confidential under the MTP Act) (`mtp.read`); View and export the MTP admission register (Form III) and the monthly report (Form II) (`mtp.register`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>OtCoordinator</strong>: Operation theatre scheduling and records (20 permissions)</summary>

| Area | May |
|---|---|
| Bio-medical waste | Weigh and bar-code bags of bio-medical waste where it is generated (`bmw.record`) |
| CSSD | View instrument sets, loads, packs, issues and CSSD reports (`cssd.read`); Receive CSSD recall and count-discrepancy alerts and acknowledge recalled packs (`cssd.recalls`); Request sterile sets for a department and record packs used on patients (`cssd.requests`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| EMR / Physician Desktop | See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Operation Theatre | Book, schedule, postpone and cancel surgeries (`ot.bookings.manage`); Record the pre-operative and WHO surgical safety checklists (`ot.checklists.record`); OT in-charge: approve a correction of an implant or consumable line after the operation has started (its charge and stock follow) (`ot.materials.approve`); Record implants and consumables used in surgery (billed to the patient) (`ot.materials.record`); View theatres, the OT calendar and surgery records (`ot.read`); View OT utilisation reports (`ot.reports.read`); Add and edit operation theatres (`ot.theatres.manage`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>DialysisTechnician</strong>: Dialysis sessions and machines (13 permissions)</summary>

| Area | May |
|---|---|
| Dialysis | Add and edit dialysis machines and stations (`dialysis.machines.manage`); Enrol dialysis patients and edit their dialysis profile (`dialysis.patients.manage`); View the dialysis board, patients, machines and sessions (`dialysis.read`); Schedule and record dialysis sessions, dialyzers and machine disinfection (`dialysis.sessions.manage`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| EMR / Physician Desktop | See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>Physiotherapist</strong>: Physiotherapy assessment and sessions (12 permissions)</summary>

| Area | May |
|---|---|
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| EMR / Physician Desktop | See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Physiotherapy | View physiotherapy referrals, plans and sessions (`physio.read`); Refer patients to physiotherapy and cancel referrals (`physio.referrals.manage`); Assess patients, plan treatment and record sessions (`physio.treatment.manage`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>BloodBankOfficer</strong>: Blood donors, inventory and issue (17 permissions)</summary>

| Area | May |
|---|---|
| Blood Bank | Group patient samples and record cross-matches (`bloodbank.crossmatch.perform`); Register and screen blood donors and record donations (donor data is confidential) (`bloodbank.donors.manage`); Prepare components, record grouping/TTI results and discard units (`bloodbank.inventory.manage`); Investigate and close transfusion reaction reports (`bloodbank.reactions.investigate`); Report transfusion reactions (`bloodbank.reactions.report`); View blood stock, blood requests and transfusion records (`bloodbank.read`); Request blood for a patient (`bloodbank.requests.create`); Issue blood units and accept returns (`bloodbank.units.issue`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| EMR / Physician Desktop | See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>Dietitian</strong>: Diet orders and meal planning (14 permissions)</summary>

| Area | May |
|---|---|
| Dietary | Maintain the diet master (`dietary.masters.manage`); Generate meal plans, view kitchen production lists and record meal deliveries (`dietary.meals.manage`); Write, stop and review diet orders (`dietary.orders.manage`); View diet orders and ward meal plans (`dietary.orders.read`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| EMR / Physician Desktop | See the patient's allergies and alerts (DNR, isolation, infection control…) on the patient banner (`clinical.flags.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| In-patients | View admissions, the bed board, discharges and census statistics (`inpatient.admissions.read`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>CanteenManager</strong>: Canteen menu and sales (14 permissions)</summary>

| Area | May |
|---|---|
| Canteen | Approve a canteen bill's cancellation another asked for, never one's own sale (the money goes back) (`canteen.cancellations.approve`); Staff credit balances, statements, payroll export and settlement (`canteen.credit.manage`); Maintain the canteen menu, prices and stock (`canteen.menu.manage`); View canteen sales reports (`canteen.reports.read`); Ask for today's canteen bills to be cancelled (`canteen.sales.cancel`); Bill canteen sales (POS) and take staff credit repayments (`canteen.sales.create`) |
| Dietary | Generate meal plans, view kitchen production lists and record meal deliveries (`dietary.meals.manage`); View diet orders and ward meal plans (`dietary.orders.read`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |

</details>

<details>
<summary><strong>CssdTechnician</strong>: Sterilisation cycles and instrument sets (13 permissions)</summary>

| Area | May |
|---|---|
| Bio-medical waste | Weigh and bar-code bags of bio-medical waste where it is generated (`bmw.record`) |
| CSSD | Maintain instrument sets and count sheets (`cssd.masters.manage`); Receive soiled trays, run wash cycles and sterilizer loads, and issue sterile packs (`cssd.process`); View instrument sets, loads, packs, issues and CSSD reports (`cssd.read`); Receive CSSD recall and count-discrepancy alerts and acknowledge recalled packs (`cssd.recalls`); Release loads and record biological indicator results (triggers recalls) (`cssd.release`); Request sterile sets for a department and record packs used on patients (`cssd.requests`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |

</details>

<details>
<summary><strong>BiomedicalEngineer</strong>: Assets, maintenance and calibration (9 permissions)</summary>

| Area | May |
|---|---|
| Assets | Preventive maintenance, breakdown tickets and calibration (`assets.maintenance`); Register assets and contracts; decide condemnation and disposal (`assets.manage`); View the asset register and report equipment breakdowns (`assets.read`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |

</details>

<details>
<summary><strong>HrManager</strong>: Employees, attendance, leave and payroll (15 permissions)</summary>

| Area | May |
|---|---|
| Canteen | Staff credit balances, statements, payroll export and settlement (`canteen.credit.manage`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| HR | Biometric terminals, attendance, holidays and regularisation approvals (`hr.attendance.manage`); Maintain staff licences and certifications; receives expiry alerts (`hr.credentials.manage`); Add and edit employees, bank details, designations, leave policies; separate employees (`hr.employees.manage`); View the employee master and credentials (`hr.employees.read`); Approve leave for any employee; credit and adjust leave balances (`hr.leave.approve`); Approve payroll, export the bank transfer file and edit professional-tax slabs (`hr.payroll.approve`); Prepare payroll: salary structures, inputs and processing (`hr.payroll.process`); Shifts and duty rosters (`hr.roster.manage`); Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |

</details>

<details>
<summary><strong>CrmExecutive</strong>: Leads, follow-ups, campaigns and feedback (23 permissions)</summary>

| Area | May |
|---|---|
| Administration | Edit SMS/WhatsApp/e-mail templates (`admin.notifications.manage`) |
| CRM | Draft SMS/WhatsApp campaigns, preview consented audiences and view delivery (`crm.campaigns.manage`); Ask to schedule campaigns, hold and cancel their sending (`crm.campaigns.send`); Follow-up call worklist: create, assign, record calls and send reminders (`crm.followups.manage`); Capture and work leads; convert them into patients and appointments (`crm.leads.manage`); Patient 360° view: visits, spend, feedback, complaints and communication consent (`crm.patient360.read`); Referring doctors and referral analytics (`crm.referrals.manage`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| Feedback | Handle patient complaints; receives new-complaint alerts (`feedback.complaints.manage`); Maintain feedback forms and QR codes, record feedback and send feedback links (`feedback.manage`); View feedback responses and analytics (`feedback.read`) |
| Front Office | Record and resolve enquiries (`frontoffice.enquiries`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | See patients' phone numbers, email and address in full (others see them masked); needed to edit a patient (`patients.contact.read`); Search and view patient demographics (`patients.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Scheduling | Book appointments and add patients to a doctor's waitlist (`scheduling.appointments.book`); Confirm, reschedule, cancel, check in and mark no-shows; overbook full slots (`scheduling.appointments.manage`); View appointment calendars, slots and waitlists (`scheduling.appointments.read`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>QualityManager</strong>: Incidents, risk register and quality indicators (37 permissions)</summary>

| Area | May |
|---|---|
| Administration | View and verify the audit trail (`admin.audit.read`) |
| Analytics | View clinical quality reports, including reports that list individual patients (audited) (`analytics.clinical.read`) |
| Antimicrobial Stewardship | The antimicrobial stewardship queue and report (`clinical.ams.read`) |
| Bio-medical waste | Hand waste over to the treatment facility, the register and the annual report (Form IV) (`bmw.manage`); Weigh and bar-code bags of bio-medical waste where it is generated (`bmw.record`) |
| CSSD | View instrument sets, loads, packs, issues and CSSD reports (`cssd.read`); Receive CSSD recall and count-discrepancy alerts and acknowledge recalled packs (`cssd.recalls`) |
| Disease Surveillance | Notifiable diseases (IDSP): the cases, the weekly P and L forms to IHIP, and the outbreak alerts (`mrd.idsp.report`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| Feedback | Receive the alert when a complaint passes its resolution deadline unresolved (`feedback.complaints.escalations`); Handle patient complaints; receives new-complaint alerts (`feedback.complaints.manage`); Maintain feedback forms and QR codes, record feedback and send feedback links (`feedback.manage`); View feedback responses and analytics (`feedback.read`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Housekeeping | Set up the areas cleaned on a schedule, their frequency and checklists (`housekeeping.manage`); Verify cleaning someone else did, and see the housekeeping report (`housekeeping.verify`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Pharmacy | Pharmacovigilance: assess suspected adverse drug reactions and send them to the ADR monitoring centre (`pharmacy.adr.manage`) |
| Privacy | Record personal data breaches, report them to the Board and notify the people affected (`privacy.breaches.manage`); Record privacy requests and nominees for patients at the desk (`privacy.requests.log`); Verify requesters, answer privacy requests, and correct or erase data for them (`privacy.requests.manage`); View patients' privacy requests (access, correction, erasure, grievance) (`privacy.requests.read`); Keep the Significant Data Fiduciary records: the Data Protection Officer, impact assessments and data audits (`privacy.sdf.manage`) |
| Quality | Keep the NABH audit checklists, schedule internal audits and close them (`quality.audits.manage`); Plan corrective/preventive actions and verify their effectiveness (`quality.capa.manage`); Record downtime of HC360 and drills of the downtime procedure, and sign off the back-entry of paper records (`quality.downtime.manage`); Record central lines, urinary catheters and ventilation put in and taken out (device days for infection surveillance) (`quality.hai.devices`); Review possible hospital-acquired infections against the NHSN definitions and see the HAI rates; receive positive-culture alerts (`quality.hai.manage`); Approve closing a sentinel or never event, and re-grading one out of sentinel, asked for by someone else (never one's own report) (`quality.incidents.approve`); Review, grade and investigate incidents; receive sentinel-event alerts; report device adverse events to MvPI (`quality.incidents.manage`); Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`); Define quality indicators and record their values (`quality.indicators.manage`); Maintain the risk register (`quality.risks.manage`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>Accountant</strong>: Accounts hand-off, doctor fees and Tally (28 permissions)</summary>

| Area | May |
|---|---|
| Ambulance | Approve the waiver of a completed trip's charge another asked for (`ambulance.waivers.approve`) |
| Analytics | View revenue, collections, dues, discounts, refunds and doctor-fee reports (`analytics.finance.read`); View MIS reports: census, occupancy, diagnostics, pharmacy stock, quality and HR (`analytics.mis.read`) |
| Billing | Approve and perform the cancellation of finalized bills (`billing.bills.cancel`); View charges, bills, receipts, IP accounts and billing reports (`billing.charges.read`); Approve discounts above a user's limit, a stay billed on another plan than its payer's, a visit's payer changed to another kind or a lower price, and discharges with dues; set discount limits (`billing.discounts.approve`); Keep the GST e-invoice register: the e-invoice JSON of B2B invoices, their IRNs (asked of the portal or recorded), IRN cancellations and e-way bills (`billing.einvoice`); Reconcile online, card and UPI payments with the gateway's and the acquirer's settlements: record missed receipts, send failed refunds again, resolve exceptions (`billing.gateway.reconcile`); Read the GST returns of the facility's GSTIN: GSTR-1 with its GSTN offline-tool JSON, and GSTR-3B (`billing.gst.returns`); Approve refunds (the pharmacy's invoice cancellations and returns paying money out or writing stock off included), credit notes, write-offs, reversals of money that did not move, and card and UPI settlement reports withdrawn as imported in error, someone else asked for (`billing.refunds.approve`); Open and close cashier shifts; day-end collection report (`billing.shifts`); Set the Tally ledger names the day book posts to (`billing.tally.configure`); Post and export the Tally day book, mark it imported and reconcile it with Tally (`billing.tally.export`) |
| Canteen | Approve a canteen bill's cancellation another asked for, never one's own sale (the money goes back) (`canteen.cancellations.approve`); View canteen sales reports (`canteen.reports.read`) |
| Doctor fees | View accruals and statements; prepare, approve and pay doctor payouts (`doctorfees.payouts.manage`); Maintain doctor revenue-sharing rules (`doctorfees.rules.manage`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Insurance | Submit claims, record queries and settlements, view receivable ageing (`insurance.claims.manage`); Approve an insurance write-off someone else asked for: the insurer's deductions, or a rejected claim's unpaid balance (`insurance.claims.writeoff.approve`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | Approve the changes another person makes to a service's price, GST and use, to tariff plans, packages and payers' terms, and to the PM-JAY HBP master (`masters.prices.approve`); View facilities, departments, doctors and services (`masters.read`) |
| Patients | Search and view patient demographics (`patients.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |
| Visits | View visits and queues (`visits.read`) |

</details>

<details>
<summary><strong>MisAnalyst</strong>: MIS reports and BI dashboards (10 permissions)</summary>

| Area | May |
|---|---|
| Analytics | Build, share and edit BI dashboards (`analytics.dashboards.manage`); View MIS reports: census, occupancy, diagnostics, pharmacy stock, quality and HR (`analytics.mis.read`); Schedule reports to be e-mailed as Excel, CSV or PDF files, and BI extracts for tools such as Power BI (`analytics.schedules.manage`) |
| Documents | Download stored documents (`files.read`); Upload documents (`files.write`) |
| HR | Employee self-service: own profile, attendance, leave and payslips (`hr.self`) |
| Integrations | Print barcode/QR labels (UHID cards, wristbands, sample, pharmacy and asset tags) (`integrations.labels.print`) |
| Masters | View facilities, departments, doctors and services (`masters.read`) |
| Operation Theatre | View OT utilisation reports (`ot.reports.read`) |
| Quality | Report incidents and near misses (optionally anonymously); work on own corrective actions (`quality.incidents.report`) |

</details>

<details>
<summary><strong>Patient</strong>: Patient portal user (1 permissions)</summary>

| Area | May |
|---|---|
| Patient Portal | Use the patient portal (own and family records only) (`portal.access`) |

</details>

### The 5 permissions no built-in role holds by default

Only the Super Administrator has them on a new installation, until an administrator who holds them gives them to a role.

| Permission | Area | What it allows | Endpoints |
|---|---|---|---:|
| `admin.backups.manage` | Administration | Run backups and see which recovery key opens each | 6 |
| `admin.integrations.manage` | Administration | Configure integrations and credentials | 8 |
| `admin.roles.manage` | Administration | Create roles and assign permissions | 4 |
| `admin.system.manage` | Administration | Change the server's database password and keys, make backup recovery keys, sign everybody out, and set how long the audit trail is kept and purge it (with a second person) | 15 |
| `mtp.certify` | Termination of pregnancy | Record the woman's consent (Form C), the doctors' opinion (Form I) and the termination (Form III) | 9 |

<!-- roles-inventory:end -->

## The same data as JSON

[roles.json](roles.json), beside this page, holds the same inventory as data, for tools and for the shareable roles page.
`RolesInventoryTests` writes it from the same reading of the code as the tables above, and fails when it no longer
matches the code, so never edit it by hand. It lists no routes. It has no date or commit in it, so it changes only when
what a role may do changes: a pull request that changes default access shows that change as a short diff of this file.

| Field | What it holds |
|---|---|
| `version` | 1. It goes up when a field is removed or changes meaning |
| `totals` | How many `roles`, `permissions` and `approvalKinds` there are, and `guardedEndpoints`: the API endpoints that ask for a permission |
| `security` | The defaults of the `HealthCare360:Security` settings that decide two-step verification: `enforceMfa`, `mfaForAdministration` and `mfaRequiredRoles` |
| `roles` | Each built-in role, in HC360's order: `name`, `description`, `defaultPermissions` (`*` is every permission), `endpoints` (the API endpoints those permissions open), `mfa` (`required-by-role`, `required-to-administer`, `optional` or `not-enforced`) and `canBeTurnedOff` |
| `permissions` | Each permission, by name: `area`, `description`, `defaultRoles`, `administrative` (an `admin.` permission: only those who hold it may give it out, and its holders must use two-step verification) and `endpoints` (the API endpoints whose rule names it) |
| `approvals` | Each kind of change a second person approves, by `kind`: its `title`, and `approvePermission`, the permission its approver must hold. The test fails when a kind is registered twice with different titles or approvers, or names a permission the code does not declare |

The shareable roles page is built from this file by `dotnet run tools/build-roles-page.cs`, run from the repository
root. It writes `artifacts/roles.html`, one file with the data inside it, which names the commit that last changed
roles.json; `-- <file> --fragment` writes the page content alone, for a publisher that adds its own document around it.
The template is `tools/roles-page.html` and reads version 1 of this file. The build refuses a roles.json with
uncommitted changes, and a version the template does not read.
