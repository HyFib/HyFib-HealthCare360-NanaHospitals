# HyFib HealthCare360 — Software Requirements Specification (SRS)

| Item | Value |
|---|---|
| Product | HyFib HealthCare360 (HC360) — Hospital Information & Management System |
| Customer | Nana Hospitals |
| Document revision | **2.0 — Windows application on a dedicated WiFi-connected machine** |
| Status | Baseline for implementation |
| Owner | HyFib Product Engineering |

---

## 0. Revision 2.0 — what changed and why

The original brief described HC360 as a hospital software suite (core clinical, administrative, specialised/support modules, and ten integrations). Revision 2.0 changes the delivery and runtime model:

1. **HC360 is a native Windows application.** Staff use the *HC360 Desktop* app (installed Windows program with Start-menu entry, taskbar presence, native printing, device access). A browser is not required on staff workstations.
2. **A dedicated machine runs HC360.** A single dedicated Windows machine (the *HC360 Server Node*) hosts the application server, the database, background jobs, and the integration gateway. It runs unattended as Windows services that start at boot, without anyone signing in.
3. **The dedicated machine connects over WiFi.** The Server Node and the workstations sit on the hospital WiFi LAN. Because WiFi is a shared, intermittently-degraded medium, revision 2.0 adds explicit requirements for:
   * zero-configuration discovery of the server on the LAN (no IP typing at each desk),
   * encrypted traffic on the LAN (TLS even inside the hospital; private hospital CA with certificate pinning),
   * tolerance to WiFi drops (safe retries with idempotency keys, auto-reconnect, draft auto-save, offline banners),
   * continuous monitoring of the Server Node's own WiFi link (signal, link speed, disconnects) with alerts,
   * continued hospital operation when the **internet** is down (only outbound integrations such as SMS/WhatsApp/payment gateway are queued and retried),
   * WiFi security posture checks (warn when a node joins an open/WEP/WPA-Personal network).
4. **Same UI everywhere.** The Desktop app renders the HC360 web UI served by the Server Node, so updating the server updates every workstation. Tablets on the staff WiFi may use a browser (reduced device features). Patients use the Patient Portal.
5. **All-in-one mode.** Small facilities may install Server + Desktop on the same dedicated machine.

Anything in the original brief that implied a cloud-hosted SaaS or a browser-only product is superseded by §3 and §4.

---

## 1. Introduction

### 1.1 Purpose
Define the functional and non-functional requirements for HC360, the integrated hospital software for Nana Hospitals covering clinical care, administration, support services and third-party integrations.

### 1.2 Scope
HC360 digitises the full patient journey — enquiry → appointment → registration → consultation → diagnostics → pharmacy → admission → procedures → billing/insurance → discharge → follow-up — plus the hospital back office (inventory, assets, HR, finance hand-off, quality, analytics).

### 1.3 Definitions
| Term | Meaning |
|---|---|
| UHID | Unique Hospital ID (medical record number) assigned at registration |
| OP / IP / ER | Out-patient / In-patient / Emergency |
| EHR / EMR | Electronic Health Record (longitudinal, shareable) / Electronic Medical Record (encounter-level physician record) |
| ABHA | Ayushman Bharat Health Account number (India ABDM) |
| TPA | Third-Party Administrator for insurance claims |
| LIS / RIS / PACS | Laboratory Information System / Radiology Information System / Picture Archiving & Communication System |
| CSSD | Central Sterile Services Department |
| MHC | Master Health Checkup (preventive health packages) |
| IRMS | Incident Reporting & Risk Management System (see §11 A-01) |
| MIS / BI | Management Information System reports / Business Intelligence |
| Server Node | The dedicated WiFi-connected Windows machine that runs HC360 server components |
| Workstation | Any Windows PC running HC360 Desktop |

### 1.4 Standards & references
HL7 FHIR R4 (ABDM/NRCeS profiles), HL7 v2.5.1, ASTM E1381/E1394 (LIS2-A2), DICOM / DICOMweb (QIDO-RS, WADO-RS, STOW-RS), ICD-10 (WHO 2019) and ICD-11 MMS, ICD-10-PCS / ICHI for procedures, SNOMED CT (optional), LOINC (lab), NABH 6th edition (2025), Digital Personal Data Protection Act 2023 (India), IT Act 2000 & SPDI Rules, HIPAA Security Rule (aligned), OWASP ASVS 4.0.3 Level 2, WCAG 2.2 AA, GST (India), TRAI DLT (SMS).

---

## 2. Overall description

### 2.1 Product perspective
HC360 is a client-server product with three runtime surfaces:

| Surface | Users | Technology | Runs on |
|---|---|---|---|
| **HC360 Server** | — | ASP.NET Core 10 (Windows Service), PostgreSQL 17 | Dedicated Server Node (WiFi) |
| **HC360 Desktop** | Hospital staff | .NET 10 WPF shell + WebView2 hosting the React 19 staff UI | Windows 10 22H2+/11 workstations (WiFi/LAN) |
| **HC360 Web (staff)** | Staff on tablets | Same React UI in a browser (PWA) | Staff WiFi devices |
| **Patient Portal** | Patients / families | React 19 mobile-first web app (PWA) | Internet (via hospital's published URL) |

### 2.2 User classes and roles
Super Administrator, Hospital Administrator, Front Office Executive, Appointment Desk, Doctor (Consultant/Resident/Visiting), Nurse, Ward In-charge, Lab Technician, Pathologist, Radiographer, Radiologist, Cardiology Technician, Cardiologist, Pharmacist, Store Keeper, Purchase Officer, Billing Executive, Cashier, Insurance/TPA Desk, Accountant, MRD Officer/Clinical Coder, OT Coordinator, Anaesthetist, Dialysis Technician, Physiotherapist, Blood Bank Officer, Dietitian, Kitchen/Canteen Manager, CSSD Technician, Biomedical/Asset Engineer, HR Manager, CRM Executive, Quality Manager, MIS/BI Analyst, Patient (portal). Roles are configurable; permissions are granular (§7.1).

### 2.3 Operating environment
* **Server Node:** Windows 11 Pro/Enterprise 24H2+ or Windows Server 2022/2025, x64. See §3.2 for sizing.
* **Workstations:** Windows 10 22H2 or Windows 11, 8 GB RAM minimum, Microsoft Edge WebView2 Runtime (pre-installed on Windows 11), 1366×768 minimum resolution (1920×1080 recommended), WiFi 5 or better.
* **Peripherals:** thermal receipt printers (80 mm ESC/POS), laser printers (A4/A5), barcode label printers (ZPL/TSPL), 1D/2D barcode scanners (USB-HID keyboard wedge or serial), fingerprint scanners (UIDAI-certified RD service), card POS terminals, webcams/headsets for telehealth.

### 2.4 Design and implementation constraints
* Runs entirely on-premises; the hospital owns all data. Internet is needed only for outbound integrations, the patient portal and telehealth with remote patients.
* Must operate on a single Server Node and scale out (§7.4) without re-architecture.
* Uses open, licence-cost-free runtime components (no per-seat runtime licences).

### 2.5 Assumptions and dependencies
* Hospital WiFi supports WPA2-Enterprise or WPA3 with client isolation disabled on the staff SSID (workstations must reach the Server Node).
* Multicast DNS (UDP 5353) is permitted on the staff SSID; otherwise the server address is set once per workstation.
* Accounts with external providers (SMS/DLT, WhatsApp Business, payment gateway, POS acquirer, Tally, PACS, analysers) are procured by the hospital.

---

## 3. Deployment, network & dedicated machine requirements (NEW in 2.0)

### 3.1 Deployment topology
```
                         Internet (optional)
                               │  (SMS · WhatsApp · Payment gateway · ICD API · Portal users)
                     ┌─────────┴─────────┐
                     │ Hospital router / │
                     │ firewall          │
                     └─────────┬─────────┘
                               │
            ┌──────────── Hospital WiFi LAN (Staff SSID, WPA3-Enterprise) ─────────────┐
            │                                                                          │
   ┌────────┴────────┐    ┌───────────────┐   ┌───────────────┐   ┌────────────────┐
   │ HC360 SERVER    │    │ Workstation   │   │ Workstation   │   │ Tablet (browser)│
   │ NODE (dedicated)│◄──►│ Front office  │   │ Doctor / Lab  │   │ Ward rounds     │
   │ • HC360 Server  │TLS │ HC360 Desktop │   │ HC360 Desktop │   │ HC360 Web (PWA) │
   │ • PostgreSQL    │    └───────────────┘   └───────────────┘   └────────────────┘
   │ • Jobs/Gateway  │
   │ • mDNS advert   │◄── Lab analysers / PACS / biometric devices (Device VLAN)
   └─────────────────┘
```

### 3.2 Server Node sizing
| Profile | Beds / concurrent users | CPU | RAM | Storage | Network |
|---|---|---|---|---|---|
| Small | ≤ 50 beds / ≤ 25 users | 6 cores (i5/Ryzen 5) | 16 GB | 512 GB NVMe | WiFi 6 (802.11ax) |
| Medium | ≤ 150 beds / ≤ 75 users | 8 cores | 32 GB | 1 TB NVMe (mirrored) | WiFi 6E + Ethernet fallback |
| Large | ≤ 300 beds / ≤ 150 users | 12+ cores | 64 GB | 2 TB NVMe RAID-1 + NAS backup | WiFi 6E/7 + Ethernet |

All profiles: TPM 2.0 with BitLocker, UPS of at least 30 minutes, and an external backup target (USB drive or NAS).

### 3.3 Requirements
| ID | Requirement | Priority |
|---|---|---|
| DED-01 | The server components shall install as Windows Services (auto-start, delayed start, automatic restart on failure) and run without an interactive logon. | Must |
| DED-02 | The Server Node shall advertise itself via mDNS/DNS-SD (`_hc360._tcp.local`) with TXT records for version, facility code and TLS certificate fingerprint. | Must |
| DED-03 | HC360 Desktop shall discover servers automatically (mDNS), fall back to `localhost` (all-in-one) and to a manually configured address, and remember the last good server. | Must |
| DED-04 | All client-server traffic, including on the LAN, shall use TLS 1.2+ (TLS 1.3 preferred). On first start the server shall generate a private hospital CA and a server certificate whose SANs cover the host name, `<host>.local` and current IPs, unless an administrator supplies a certificate. | Must |
| DED-05 | Workstations shall enrol with the server once (administrator enrolment code). After that the Desktop app pins the server certificate fingerprint and refuses unknown certificates. | Must |
| DED-06 | The server shall monitor its own WiFi link (SSID, BSSID, signal quality, receive/transmit rates, authentication type) every 30 seconds, log drops or degraded periods, and alert administrators in-app and by SMS/WhatsApp when the link is lost or signal stays below a threshold. | Must |
| DED-07 | Workstations shall display a connection indicator (WiFi signal, server reachability, real-time channel state) and a non-blocking banner when connectivity is lost or degraded. | Must |
| DED-08 | Every state-changing request shall carry an idempotency key. The server shall return the original result for a retried key, so a WiFi retry never duplicates a bill, payment, order or dispense. | Must |
| DED-09 | The client shall retry transient failures with exponential backoff and jitter; real-time channels shall reconnect automatically and refresh stale data on reconnect. | Must |
| DED-10 | Forms with free text (clinical notes, discharge summaries, incident reports) shall auto-save drafts locally every 5 seconds and restore them after a disconnect or crash; drafts are cleared on sign-out. | Must |
| DED-11 | Loss of internet shall not interrupt in-hospital operation. Outbound messages (SMS, WhatsApp, e-mail, payment status polls, Tally posting) shall queue in a transactional outbox and retry until delivered or expired. | Must |
| DED-12 | Nodes shall warn administrators when connected to open, WEP or WPA-Personal networks, or to a network whose SSID differs from the approved staff SSID list. | Should |
| DED-13 | The server shall serve the staff UI and Patient Portal itself (no separate web server) and compress responses (Brotli/Gzip) to reduce WiFi airtime. | Must |
| DED-14 | The installer shall configure Windows Firewall to allow HC360 only on Private/Domain profiles and only on the configured port (default 7360), plus mDNS. | Must |
| DED-15 | The installer shall disable WiFi adapter power saving and system sleep on the Server Node and set Windows Update active hours so updates do not reboot the node during clinical hours. | Should |
| DED-16 | Automated encrypted database backups shall run nightly (and on demand) to a local path and a secondary target, with retention policy and restore verification. | Must |
| DED-17 | A health dashboard shall show server uptime, WiFi status, database size and connections, disk space, backup status, outbox backlog, integration status, UPS/battery status (when exposed by Windows), and certificate expiry. | Must |
| DED-18 | The Desktop app shall update itself from the Server Node (signed package) so workstations do not need internet access. | Should |
| DED-19 | Support an all-in-one installation (Server + Desktop on one machine) for small facilities. | Must |
| DED-20 | Optional secondary Server Node (warm standby) using PostgreSQL streaming replication, with documented manual failover. | Could |

---

## 4. Functional requirements

Priority uses MoSCoW. "Must" items make up the release-1 scope.

### 4.1 Core clinical software

#### 4.1.1 Electronic Health Records (EHR)
| ID | Requirement | Priority |
|---|---|---|
| EHR-01 | Keep a longitudinal record per patient (UHID): demographics, encounters, vitals, allergies, problems/diagnoses (ICD-10/11), medications, orders and results, procedures, immunisations, documents and discharge summaries. | Must |
| EHR-02 | Show a patient timeline across OP, IP, ER, telehealth and health-checkup encounters, filterable by type, department and date. | Must |
| EHR-03 | Show allergy, critical-alert and infection-control flags in a persistent patient banner on every clinical screen. | Must |
| EHR-04 | Attach documents (scans, external reports, consent forms) with type, date and source, stored encrypted-at-rest. | Must |
| EHR-05 | Share records with other providers through HL7 FHIR R4 read APIs (Patient, Encounter, Observation, Condition, AllergyIntolerance, MedicationRequest, DiagnosticReport, DocumentReference), subject to consent. | Must |
| EHR-06 | Record patient consent (treatment, data sharing, telehealth, research) with version, timestamp, witness and revocation. | Must |
| EHR-07 | Provide break-the-glass access to restricted records, with a mandatory reason, time-limited access and a flagged audit entry. | Must |
| EHR-08 | Link ABHA numbers and export discharge summaries/reports as ABDM-compliant FHIR bundles (behind a feature flag). | Should |

#### 4.1.2 Telehealth / Telemedicine
| ID | Requirement | Priority |
|---|---|---|
| TEL-01 | Book telehealth appointments; the patient receives a secure, time-limited join link by SMS/WhatsApp/portal. | Must |
| TEL-02 | Run 1:1 video consultations (WebRTC) between the doctor (Desktop) and the patient (portal), with audio-only fallback and in-session chat. | Must |
| TEL-03 | Provide a virtual waiting room; the doctor admits the patient. | Must |
| TEL-04 | The doctor documents the EMR and e-prescribes during the call; the prescription is shared with the patient afterwards. | Must |
| TEL-05 | Record session metadata (start, end, duration, participants, connection quality). Media is not recorded unless consent is captured. | Must |
| TEL-06 | Configure ICE servers (STUN/TURN) for patients outside the hospital network. | Must |

#### 4.1.3 Remote Patient Monitoring (RPM)
| ID | Requirement | Priority |
|---|---|---|
| RPM-01 | Enrol patients in RPM programmes (hypertension, diabetes, cardiac, post-discharge) with assigned devices and a care team. | Must |
| RPM-02 | Ingest readings (BP, heart rate, SpO₂, glucose, weight, temperature, ECG summary) through an authenticated device/gateway API and FHIR Observation. | Must |
| RPM-03 | Set per-patient thresholds; when a reading breaches one, raise an alert to the care team (in-app, SMS/WhatsApp) and track acknowledgement. | Must |
| RPM-04 | Show trend charts and adherence (days with readings). | Must |
| RPM-05 | Detect device silence (no readings for N hours) and notify. | Should |

#### 4.1.4 E-Prescribing
| ID | Requirement | Priority |
|---|---|---|
| ERX-01 | Doctors prescribe from the drug master (generic + brand, strength, form, route, frequency, duration, instructions) with favourites and templates. | Must |
| ERX-02 | Check allergies, duplicate therapy, drug–drug interactions (configurable interaction table) and maximum doses before signing. | Must |
| ERX-03 | Signing sends the prescription electronically to the OP or IP pharmacy queue; paper prescriptions are not needed for dispensing. | Must |
| ERX-04 | Print or share the prescription (bilingual, doctor registration number, digital signature, QR code for verification). | Must |
| ERX-05 | Support refill authorisations for chronic medication and repeat prescriptions. | Should |
| ERX-06 | Flag narcotic/psychotropic (Schedule H1/X) drugs and require an extra confirmation and register entry. | Must |

#### 4.1.5 Medical Imaging
| ID | Requirement | Priority |
|---|---|---|
| IMG-01 | Radiology orders (X-ray, CT, MRI, USG, mammography, fluoroscopy) with modality worklist and scheduling. | Must |
| IMG-02 | Integrate with PACS through DICOMweb (QIDO-RS/WADO-RS) and DICOM MWL, and open the viewer for a study from the patient record. | Must |
| IMG-03 | Radiologists report with templates, critical-finding flags and verification/sign-off; reports appear in the EHR. | Must |
| IMG-04 | Track TAT (order → acquisition → report) and repeat/reject reasons. | Should |
| IMG-05 | AI-assisted triage hooks: accept AI findings from external engines as preliminary annotations; they are never auto-verified. | Could |

### 4.2 Administrative & management software

#### 4.2.1 Practice Management (PMS)
| ID | Requirement | Priority |
|---|---|---|
| PMS-01 | Doctor schedules (sessions, slot duration, capacity, leave/blocks) per facility and department. | Must |
| PMS-02 | Appointment booking (walk-in, phone, online, telehealth), rescheduling, cancellation and no-show tracking. | Must |
| PMS-03 | Front-desk check-in creates a visit and queue token; the doctor's queue updates in real time. | Must |
| PMS-04 | Basic billing for consultations and procedures at the front desk (see OP Billing). | Must |

#### 4.2.2 Hospital Management System (HMS)
| ID | Requirement | Priority |
|---|---|---|
| HMS-01 | A single integrated application covering all modules, masters, users and facilities (multi-branch). | Must |
| HMS-02 | Role-based home dashboards (administrator, doctor, nurse, front office, billing, pharmacy, lab). | Must |
| HMS-03 | Masters: facilities, departments, specialities, practitioners, services and tariffs, wards/beds, stores, items, payers. | Must |
| HMS-04 | A system configuration centre (numbering formats, print templates, integrations, notification templates, feature flags). | Must |

#### 4.2.3 Medical Billing
| ID | Requirement | Priority |
|---|---|---|
| MB-01 | Capture charges automatically from orders, dispenses, procedures, bed occupancy and visits (charge capture). | Must |
| MB-02 | Price services by tariff plan (general, corporate, insurance/TPA, government schemes) with package pricing. | Must |
| MB-03 | Assign diagnosis/procedure codes (ICD-10, ICD-10-PCS) to encounters for claims. | Must |
| MB-04 | Record payments in cash, card (POS), UPI, net banking, payment gateway, cheque and insurance, with split payments. | Must |
| MB-05 | Support refunds, credit notes, discounts (with approval limits), write-offs and cancellation with reason and approval. | Must |
| MB-06 | Produce GST-compliant invoices (pharmacy, canteen) and exempt healthcare service receipts. | Must |

#### 4.2.4 Healthcare CRM
| ID | Requirement | Priority |
|---|---|---|
| CRM-01 | Manage leads/enquiries (source, interest, status) and convert them to patients or appointments. | Must |
| CRM-02 | Follow-up reminders (post-consultation, vaccination, chronic review, health checkup due) with task assignment. | Must |
| CRM-03 | Run campaigns (SMS/WhatsApp) to consented segments with DLT templates, delivery tracking and opt-out. | Must |
| CRM-04 | Show a patient 360° view (visits, spend, feedback, communications, pending follow-ups). | Should |
| CRM-05 | Track referring doctors and referral sources. | Should |

### 4.3 Specialised & support software

#### 4.3.1 Laboratory Information System (LIS) / Laboratory Management
| ID | Requirement | Priority |
|---|---|---|
| LIS-01 | Test master (parameters, units, method, specimen, container, reference ranges by age/sex, critical ranges, LOINC), panels and profiles. | Must |
| LIS-02 | Order → sample collection → barcode label → accession → receipt in lab → processing → result entry → verification → report. | Must |
| LIS-03 | Barcode-labelled samples; rejection with reason and recollection workflow. | Must |
| LIS-04 | Receive results from analysers (HL7 v2 ORU / ASTM) with auto-validation rules; delta checks against previous results. | Must |
| LIS-05 | Flag and notify critical values; notification is mandatory and documented. | Must |
| LIS-06 | Quality control: QC lots, Levey-Jennings charts, Westgard rules (1-2s, 1-3s, 2-2s, R-4s), QC failure lockout. | Must |
| LIS-07 | Deliver reports as PDF with QR verification, via portal, WhatsApp and e-mail; track TAT. | Must |
| LIS-08 | Outsourced tests to reference labs, with tracking. | Should |

#### 4.3.2 Pharmacy Management (OP & IP)
| ID | Requirement | Priority |
|---|---|---|
| PH-01 | The OP pharmacy queue receives e-prescriptions; the pharmacist dispenses with batch/expiry selection (FEFO), substitution (with doctor consent) and counselling notes. | Must |
| PH-02 | OP counter sales (with or without prescription as the schedule allows), GST invoice, returns. | Must |
| PH-03 | IP pharmacy: ward indents, patient-wise issue to the IP account, returns from wards, and a medication administration record (MAR) feed. | Must |
| PH-04 | Stock per pharmacy store, reorder levels, near-expiry and expired stock reports, stock transfer. | Must |
| PH-05 | Prescription refills for chronic patients, with reminders. | Should |
| PH-06 | Schedule H/H1/X registers, narcotics balance tracking. | Must |

#### 4.3.3 Patient Portal
| ID | Requirement | Priority |
|---|---|---|
| PP-01 | Patient login by mobile OTP; link family members. | Must |
| PP-02 | View appointments, prescriptions, lab/radiology reports, discharge summaries and bills; download PDFs. | Must |
| PP-03 | Book, reschedule or cancel appointments online and pay online (payment gateway). | Must |
| PP-04 | Join telehealth consultations. | Must |
| PP-05 | Submit feedback, and request records or refills. | Must |
| PP-06 | Bilingual UI (English, Hindi; extensible). | Should |

### 4.4 Additional hospital modules

| ID | Module | Key requirements | Priority |
|---|---|---|---|
| FO-01..06 | **Front Office** | Quick registration (≤ 60 s, duplicate detection by phone/name/DOB/ABHA), full registration, UHID card/wristband printing, visit creation, queue tokens with display-board feed, enquiries, visitor passes, MLC (medico-legal) flag, photo capture. | Must |
| OA-01..05 | **Online Doctor Appointment** | Public doctor directory (speciality, languages, fees, availability), real-time slot booking, payment at booking or at the hospital, SMS/WhatsApp confirmation and reminders (24 h, 2 h), waitlist. | Must |
| OPB-01..07 | **OP Billing** | Bill consultations/investigations/procedures, pull pending orders for a visit, packages, discounts with approval, multi-mode payments, due collection, refunds, day-end cash closing by counter/cashier. | Must |
| IPB-01..08 | **IP Billing** | Admission advance/deposits, running bill, auto bed/nursing/doctor-visit charges, package vs actuals, interim bills, insurance/patient split, discharge billing clearance, final bill, refund of excess deposit. | Must |
| LAB-* | **Laboratory Management** | See LIS-01..08. | Must |
| OPP-* / IPP-* | **OP / IP Pharmacy** | See PH-01..06. | Must |
| WB-01..06 | **Ward & Bed Management** | Ward/room/bed masters by class (general, semi-private, private, ICU, NICU), live bed board (vacant/occupied/reserved/cleaning/blocked), admission bed allocation, transfers, housekeeping turnaround, occupancy statistics. | Must |
| INS-01..07 | **Insurance Management** | Payers, TPAs, corporate tie-ups, policy verification, pre-authorisation (initial, enhancement, final), claim submission with documents, query/deduction/settlement tracking, receivable ageing. | Must |
| RAD-* | **Radiology** | See IMG-01..05. | Must |
| CAR-01..04 | **Cardiology** | ECG, 2D-Echo, TMT, Holter orders; device upload (PDF/DICOM/SCP-ECG), structured reports, cardiologist sign-off. | Must |
| OT-01..07 | **Operation Theatre** | OT and surgeon calendar, surgery booking, pre-op checklist, WHO surgical safety checklist (sign-in/time-out/sign-out), anaesthesia record, intra-op notes, implants and consumables to the IP bill, OT utilisation. | Must |
| INV-01..08 | **Inventory Management** | Item master (drug, consumable, general, implant), stores, purchase requisition → PO → GRN (batch, expiry, MRP), stock ledger, indents and issues, transfers, reorder, stock audit, vendor master, and ABC/VED/FSN analysis. | Must |
| MRD-01..06 | **Medical Records – ICD integration** | ICD-10/ICD-11 search (local cache and WHO ICD-API), coding at discharge, chart deficiency tracking, record requests and issue/return, retention and archival, disease/morbidity indices. | Must |
| MHC-01..05 | **Master Health Checkup** | Package master (tests, consultations, age/sex rules), package booking, itinerary tracking across departments, consolidated report with doctor summary, corporate health checkups. | Must |
| EMR-01..07 | **EMR – Physician Desktop** | Doctor worklist (queue, appointments, IP rounds, results to review), SOAP notes with templates by speciality, vitals, diagnoses (ICD), orders (lab/rad/procedures), e-prescription, follow-up, referral, voice dictation (AI assistant), previous visit summary. | Must |
| MIS-01..05 | **MIS Reports** | Standard reports (census, revenue by department/doctor/service, collections, pending dues, lab TAT, pharmacy sales, bed occupancy, ALOS, discharges, mortality), date filters, export to Excel/CSV/PDF, scheduled e-mail. | Must |
| CSSD-01..05 | **CSSD** | Instrument set master, requests from departments, receiving soiled items, wash/pack/sterilise cycles (autoclave, ETO, plasma) with batch/load and indicator results, issue with expiry, recall by load. | Must |
| DIET-01..05 | **Dietary** | Diet orders from doctors/dietitians (diet type, restrictions, allergies), ward-wise meal planning, kitchen production lists, delivery tracking, and diet charges to the IP bill. | Must |
| CAN-01..04 | **Canteen** | Menu and pricing, POS billing (staff/patient attendants/visitors), staff credit with payroll deduction, and daily sales and stock consumption. | Must |
| AST-01..06 | **Asset Management** | Asset register (tag/barcode, location, custodian), purchase/warranty/AMC/CMC, preventive maintenance schedules, breakdown tickets, calibration records, depreciation, condemnation. | Must |
| PHY-01..04 | **Physiotherapy** | Assessment, treatment plan (modalities, exercises, number of sessions), session scheduling and attendance, progress notes, charges. | Must |
| HR-01..08 | **HRMS** | Employee master, departments/designations, shift rostering, biometric attendance, leave management, payroll (earnings, deductions, PF/ESI/PT/TDS inputs), payslips, credential/licence expiry tracking. | Must |
| BB-01..07 | **Blood Bank** | Donor registration and screening, donation, component separation, TTI testing, inventory by group/component with expiry, cross-match, issue/return, transfusion reaction reporting. | Must |
| DIA-01..05 | **Dialysis** | Dialysis patient enrolment, machine and station master (with sero-status segregation), session scheduling, pre/intra/post parameters, dialyser reuse tracking, and session charges. | Must |
| FB-01..04 | **Feedback** | OP/IP/portal feedback forms (ratings, NPS, comments), QR code at points of care, low-score alerts with complaint escalation and closure, and analytics. | Must |
| DFA-01..05 | **Doctor Fee Accounting** | Revenue-sharing rules per doctor/service/payer (percentage or fixed), fee accrual from bills, deductions, payout statements, TDS, and period close and payout. | Must |
| IRMS-01..06 | **IRMS** | Incident reporting (clinical, medication error, fall, needle-stick, equipment, security), severity grading, investigation/RCA, CAPA tracking, risk register with likelihood × impact scoring, and NABH quality indicators. | Must |
| AIV-01..06 | **AI Voice Assistant** | Push-to-talk voice commands ("open patient UHID 1234", "today's appointments", "book follow-up in 2 weeks"), clinical dictation → structured SOAP note draft + ICD suggestions. The clinician must review everything; nothing is auto-signed. Provider-pluggable, off by default, audited. | Must |
| BI-01..05 | **Managed BI Services** | KPI dashboards (operational, financial, clinical quality), drill-down, KPI definitions catalogue, a read-only reporting replica/extract, scheduled data extracts for external BI tools (Power BI), and data-refresh monitoring. | Must |

---

## 5. Integration requirements

| ID | Integration | Requirement | Priority |
|---|---|---|---|
| INT-SMS | **SMS** | Pluggable providers (MSG91, Twilio, generic HTTP). DLT entity/template IDs per template, OTP, appointment/billing/report notifications, delivery receipts, and outbox retries. | Must |
| INT-TALLY | **Tally** | Push day-book vouchers (sales, receipts, payments, journals for doctor fees) to Tally Prime over its XML HTTP interface (port 9000), with ledger mapping, retry, reconciliation and an export-file fallback. | Must |
| INT-BAR | **Barcode** | Code-128/QR generation for UHID cards, wristbands, sample labels, pharmacy and asset tags. ZPL/TSPL raw printing and scanner input (HID/serial) through Desktop. | Must |
| INT-ICD | **ICD/ICP** | A local ICD-10 catalogue (seeded, or imported from the hospital's licensed WHO ClaML file) plus online lookup through the WHO ICD-API (ICD-11 MMS), and procedure coding (ICD-10-PCS/ICHI) with a local procedure catalogue (seeded, or imported from the CMS ICD-10-PCS order file). | Must |
| INT-BIO | **Biometric** | Staff attendance from biometric terminals (ZKTeco-style push/pull), fingerprint login/verification at workstations through the RD service, and patient identity verification (optional). | Must |
| INT-PACS | **PACS** | DICOMweb QIDO/WADO/STOW, modality worklist, viewer launch URL templates, and study-to-order reconciliation by accession number. | Must |
| INT-WA | **WhatsApp** | WhatsApp Business Cloud API with template messages, document messages (reports, bills), webhook delivery status and opt-in tracking. | Must |
| INT-LAB | **Laboratory equipment** | TCP/IP and serial (via Desktop) interfaces for HL7 v2.x and ASTM E1381/E1394 analysers, uni-/bi-directional (host query), instrument-to-test mapping, and a raw message log. | Must |
| INT-POS | **POS** | Card/UPI POS terminals through a provider adapter (cloud-POS API or local device API): push the amount to the terminal, poll status and reconcile. | Must |
| INT-PG | **Payment gateway** | Razorpay/PayU-style adapter: order creation, hosted checkout, webhook signature verification, payment links over SMS/WhatsApp, refunds and reconciliation. | Must |

Every integration: configurable per facility, credentials encrypted at rest, a "simulator" provider for training/testing, message logs with PHI masking, health status on the dashboard, and outbox-based retries.

---

## 6. User experience requirements

| ID | Requirement |
|---|---|
| UX-01 | Fluent 2 design language (the Windows 11 look), light/dark/high-contrast themes following Windows settings. |
| UX-02 | Keyboard-first operation: global command palette (Ctrl+K), module shortcuts, Enter-to-save, Esc-to-close, and full tab order. |
| UX-03 | Global patient search (UHID, name, phone, ABHA) from anywhere in ≤ 2 keystrokes. |
| UX-04 | A persistent patient context banner on patient screens (name, UHID, age/sex, allergies, alerts, payer). |
| UX-05 | Role-based navigation: users see only the modules they are permitted to use. |
| UX-06 | The common journeys (register → token; book appointment; OP bill + payment; dispense prescription; admit to bed) take at most 3 screens. |
| UX-07 | Consistent page anatomy: header (title, breadcrumbs, primary actions), filters, data table, detail drawer. |
| UX-08 | Inline validation with clear messages. Unsaved-change guards. Destructive actions need confirmation. |
| UX-09 | Print-ready documents (receipts 80 mm/A5, prescriptions, reports A4, labels) with facility letterhead. |
| UX-10 | WCAG 2.2 AA: contrast, focus visibility, screen-reader labels, and target sizes ≥ 24 px. |
| UX-11 | Screen lock after idle (configurable, default 10 minutes) with quick unlock (PIN/biometric), without losing context. |
| UX-12 | Every list has loading skeletons, empty states with a next action, and error states with a retry. |

---

## 7. Non-functional requirements

### 7.1 Security
| ID | Requirement |
|---|---|
| SEC-01 | Authentication: username + password (min 12 chars, breached/common-password deny list, PBKDF2-SHA512 hashing), optional/enforced TOTP MFA per role, and lockout after 5 failures (15-minute lock with exponential backoff). |
| SEC-02 | Short-lived access tokens (≤ 15 min) with rotating refresh tokens (reuse detection revokes the token family) held in `HttpOnly; Secure; SameSite=Strict` cookies. Absolute session limit of 12 hours (one shift). |
| SEC-03 | Granular permission-based authorisation enforced server-side on every endpoint, scoped by facility. Deny by default. |
| SEC-04 | A tamper-evident audit trail (hash-chained) of logins, PHI reads (chart open, report view, print, export), data changes (before/after for sensitive fields), permission changes and break-glass access. It is append-only, retained for 10 years, and cannot be edited through the application. |
| SEC-05 | Encryption in transit (TLS 1.2+ everywhere, HSTS, certificate pinning in Desktop) and at rest (BitLocker on the Server Node, AES-256-GCM field encryption with a blind index for identifiers such as Aadhaar/ABHA/policy numbers, encrypted backups, encrypted document store). |
| SEC-06 | OWASP ASVS L2: validate all input server-side, use parameterised queries only, output encoding, strict CSP, security headers, rate limiting (login/OTP/public APIs), anti-automation on the portal, and generic error messages. |
| SEC-07 | Secrets (DB password, integration keys, signing keys) are never in source control. They are stored in DPAPI-protected configuration or environment and rotated through the admin UI. |
| SEC-08 | Only enrolled devices may use privileged staff features. Each device can be revoked. |
| SEC-09 | Desktop hardening: WebView2 limited to the enrolled server origin, context isolation, no arbitrary navigation, native bridge methods allow-listed per origin, and downloads restricted. |
| SEC-10 | Logs never contain PHI or secrets. Structured logging uses redaction. |
| SEC-11 | Dependency and container scanning in CI. Security patches are applied within 30 days (critical: 7 days). |
| SEC-12 | AI features are disabled by default. When enabled they minimise the data sent to the provider (no identifiers), are logged, and require a clinician to review the output. |

### 7.2 Privacy & compliance
PRV-01 The system follows DPDP Act 2023 principles: purpose limitation, consent records, data-principal rights (access, correction and erasure requests where law allows), breach notification support. PRV-02 Retention: medical records at least 3 years for OP and 5 years for IP (or as state rules and NABH require; configurable), MLC records permanently, audit logs 10 years. PRV-03 Minimum necessary: role-scoped field visibility; masked identifiers in lists. PRV-04 Export or transfer of records is itself audited.

### 7.3 Performance (Medium profile, Server Node on WiFi 6)
PERF-01 95th percentile API latency ≤ 300 ms for reads and ≤ 600 ms for writes at 75 concurrent users. PERF-02 Patient search ≤ 500 ms over 1 million patients. PERF-03 Initial UI load ≤ 3 s on first run and ≤ 1 s when cached. PERF-04 Real-time updates (queue, bed board, critical alerts) reach clients within 2 s. PERF-05 Typical list payloads are paginated and compressed to under 100 KB.

### 7.4 Scalability
SCAL-01 Stateless API nodes (horizontal scale behind a reverse proxy). SCAL-02 Real-time scale-out through a Redis backplane (optional). SCAL-03 A read-replica connection for MIS/BI queries. SCAL-04 Time-partitioned high-volume tables (audit, RPM readings, messages). SCAL-05 Background workers run separately (`--role worker`). SCAL-06 Multi-facility data model (single database, facility-scoped) for hospital chains. SCAL-07 A modular monolith with enforced module boundaries, so modules can be extracted into services later.

### 7.5 Availability & reliability
REL-01 Target 99.5 % availability during hospital operation. Services restart automatically on failure. REL-02 RPO ≤ 24 h with nightly backups (≤ 15 min with WAL archiving enabled), RTO ≤ 4 h on the same hardware. REL-03 Graceful degradation: integration failures never block clinical workflows. REL-04 Optimistic concurrency on all editable records. Conflicts are shown to the user and never silently overwritten.

### 7.6 Maintainability
MNT-01 Every module has a clear owner folder in the server and UI code bases, with automated tests. MNT-02 Code style is enforced (analyzers, ESLint, Prettier) and CI gates on build, tests, lint and vulnerability scan. MNT-03 Database changes are delivered as versioned migrations and applied automatically at start-up (with backup first). MNT-04 Configuration lives in files plus the admin UI, never hard-coded. MNT-05 Architecture Decision Records document the key choices.

### 7.7 Observability
OBS-01 Structured logs (rolling files and Windows Event Log for errors). OBS-02 OpenTelemetry metrics and traces (OTLP export optional). OBS-03 Health endpoints (`/health/live`, `/health/ready`). OBS-04 A correlation ID across client, server and integration logs.

---

## 8. Data requirements (key entities)
Patient, PatientIdentifier, Visit, QueueToken, Appointment, Schedule, Encounter, Vital, Diagnosis, Allergy, ClinicalNote, Order/OrderItem, Prescription/PrescriptionItem, LabSample, LabResult, QcRun, ImagingStudy, RadiologyReport, CardiologyReport, Admission, Ward/Room/Bed, BedTransfer, Surgery, DialysisSession, PhysioPlan/Session, BloodDonor/BloodUnit/CrossMatch, DietOrder, Item/Batch/Stock/StockLedger, PurchaseOrder/GRN, Indent, Asset/MaintenanceTicket, SterilizationLoad, ServiceItem, Tariff, Charge, Bill/BillItem, Payment/Receipt, Refund, Deposit, Payer/TPA/Policy, PreAuthorization, Claim, DoctorShareRule/DoctorPayout, Employee/Attendance/Leave/PayrollRun, Lead/FollowUp/Campaign, Feedback, Incident/Capa/Risk, KPI, AuditEvent, User/Role/Permission/Device, OutboxMessage, IntegrationLog, StoredFile.

---

## 9. Acceptance & traceability
Each requirement ID maps to a module folder in `src/server/Modules/*`, `src/web/apps/staff/src/modules/*` and to automated tests ([TRACEABILITY.md](TRACEABILITY.md) records the mapping and each requirement's status). Release-1 acceptance requires: all "Must" items implemented, and end-to-end journeys passing (register → appointment → consult → lab → pharmacy → bill; admit → bed → charges → discharge bill → insurance claim), with a security checklist (ASVS L2) signed off.

---

## 10. Out of scope for release 1
Native mobile apps (the Portal is a PWA), full ERP accounting (Tally remains the book of accounts), PACS image storage (a third-party PACS is integrated), and fully automated insurance adjudication.

## 11. Assumptions & open questions
| # | Topic | Assumption made | Needs confirmation |
|---|---|---|---|
| A-01 | IRMS | "Incident Reporting & Risk Management System" (NABH quality/safety). | Confirm this is the intended meaning, not e.g. a referral-management system. |
| A-02 | SMS provider | MSG91 adapter + generic HTTP adapter; the DLT registration is the hospital's. | Provider and DLT entity ID. |
| A-03 | Payment gateway | Razorpay-compatible adapter first. | Gateway choice and merchant account. |
| A-04 | POS | Cloud-POS style adapter (push-to-terminal); the vendor-specific SDK is added during onboarding. | Acquirer/terminal model. |
| A-05 | PACS | A DICOMweb-capable PACS is available (e.g. Orthanc, dcm4chee or a vendor PACS). | PACS vendor and endpoints. |
| A-06 | Analysers | HL7 v2.3.1/2.5.1 or ASTM over TCP; serial analysers go through a workstation's Desktop bridge. | Analyser list and protocols. |
| A-07 | Biometric | ZKTeco-compatible attendance terminals; UIDAI RD-service fingerprint readers at desks. | Device models. |
| A-08 | AI provider | Anthropic Claude through the API for text structuring; speech-to-text is pluggable (Windows speech on Desktop, or a cloud/on-prem STT). | Data-processing agreement, enable/disable policy. |
| A-09 | Server connectivity | The Server Node's primary link is WiFi (per this revision). An Ethernet fallback is recommended where cabling exists. | Confirm the WiFi controller/SSID policy allows client-to-server traffic and mDNS. |
