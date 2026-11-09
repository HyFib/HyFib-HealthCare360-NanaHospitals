# Organisation masters and hospital setup

The records every module builds on: facilities (with the letterhead documents print), departments, specialities,
practitioners (doctors and other clinicians) and the service catalogue. SRS **HMS-03**, with the configuration centre of
**HMS-04** (number formats, print templates, features). Tariff plans, prices and packages are in Billing (`docs/modules/billing.md`); wards, rooms and beds in
Inpatient.

Code: `src/server/HyFib.HealthCare360.Platform/Organization` (schema `org`). Screens:
`src/web/apps/staff/src/modules/masters` and `routes/_app/setup`. Tests: `OrganizationMasterValidationTests` and
`AuditChangesTests` (Platform.Tests), `OrganizationMasterTests` (IntegrationTests), and the staff app's
`modules/masters/*.test.*`.

## Permissions

| Permission | Purpose | Default roles |
|---|---|---|
| `masters.read` | Read facilities, departments, doctors and services (pickers, prints) | All staff |
| `masters.manage` | Add and change them, preview the letterhead | HospitalAdmin |
| `masters.prices.approve` | Approve the price changes another person makes (maker-checker; see *A service's price*) | Accountant, HospitalAdmin |

## Screens: Administration › Hospital setup

| Screen | Route | What it holds |
|---|---|---|
| Facilities and letterhead | `/setup/facilities` | Name, legal name, address, contact, GSTIN, PAN, registration, NABH and HFR ids, the logo and the letterhead line. *Preview letterhead* shows the report letterhead the server prints, from the details being typed |
| Departments | `/setup/departments` | Kind, speciality, head of department, location, queue token prefix and position in lists, for the signed-in facility |
| Specialities | `/setup/specialties` | Shared by every facility; patients choose one when booking online |
| Doctors and clinical staff | `/setup/practitioners` | Name and role, council registration (required for doctors), HPR ID (ABDM's Healthcare Professionals Registry, which identifies the doctor in ABDM health records and NHCX claims), department, speciality, facilities, fees (approved by someone who approves prices) and follow-up days, online and video booking, languages, profile, photo and signature |
| Services and prices | `/setup/services` | Category, department, base price, GST, SAC, whether discounts and price changes are allowed, and a name in each facility's local language, which that facility's rate list prints under the English name (billing.md, *The rate list*) |
| Number formats | `/setup/numbering` | The format of every number the hospital issues, set before the first one if needed (`admin.numbering.manage`; see `docs/modules/numbering.md`). Invoice, bill of supply, credit and debit note series are held to the 16 characters of GST rule 46, measured with the longest facility code in use, so a longer facility code can make a saved format refuse |
| Print templates | `/setup/print-templates` | How each kind of document prints: the letterhead or pre-printed stationery, the logo, a closing note and the paper, hospital-wide or for one facility (`admin.print.manage`; see `docs/modules/print-templates.md`) |
| Features | `/setup/features` | Switch the patient portal and video consultations on or off (`admin.settings.manage`; see `docs/modules/features.md`) |

The five masters need `masters.manage`. Each list keeps its search and filters in the address, and opens a record with
`?edit=<id>` (or `?edit=new`), so a link opens the same record. Records are taken out of use ("In use" off), never
deleted; the lists hide them unless *Show those not in use* is on.

The editors:
- read the record afresh when opened and send its `version` back, so a change made meanwhile by someone else is reported
  instead of overwritten;
- ask before discarding unsaved changes (SRS UX-08): on closing the editor, leaving the screen, pressing Back or closing
  the window (the app-wide guard, see *Unsaved changes* in `docs/architecture/FRONTEND-GUIDE.md`);
- save with Ctrl+S as well as the Save button;
- show the server's field messages beside their fields;
- mark every cached query stale after a save, so pickers, billing and prints use the new details.

**Images.** Logos, doctors' photos and signatures are shrunk in the browser until they fit 300 KB (the most a document
embeds), keeping PNG transparency for logos and signatures, then stored in the file store with the category `logo`,
`staff-photo` or `signature`.

## Endpoints — `/api/v1/masters`

| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/facilities` · `/facilities/{id}` | masters.read | → `FacilityDto[]` · `FacilityDto` |
| POST · PUT | `/facilities` · `/facilities/{id}` | masters.manage | `SaveFacilityRequest` (+`version`) → 201 `{ id }` · 204 |
| POST | `/facilities/letterhead-preview` | masters.manage | `LetterheadPreviewRequest` (the facility's printable details and `logoFileId`, not yet saved) → `PrintableDocumentDto` |
| GET | `/departments?kind&active&search` · `/departments/{id}` | masters.read | → `DepartmentDto[]` (signed-in facility, by position then name) · `DepartmentDto` |
| POST · PUT | `/departments` · `/departments/{id}` | masters.manage | `SaveDepartmentRequest` (+`version`) → 201 · 204; 422 `facility.required` without a facility |
| GET | `/specialties` · `/specialties/{id}` | masters.read | → `SpecialtyDto[]` · `SpecialtyDto` |
| POST · PUT | `/specialties` · `/specialties/{id}` | masters.manage | `SaveSpecialtyRequest` (+`version`) → 201 · 204 |
| GET | `/practitioners?page&pageSize&search&departmentId&specialtyId&kind&active` · `/practitioners/{id}` | masters.read | → `PagedResult<PractitionerDto>` (search: name, code, registration number) · `PractitionerDto` (one read also gives `linkedAccounts`, the accounts that sign as them, `pendingIdentity` and `pendingFee`) |
| POST · PUT | `/practitioners` · `/practitioners/{id}` | masters.manage | `SavePractitionerRequest` (+`version`; `takeOutOfUseAnyway` after a 409 `practitioner.work_ahead`) → 201 `{ id, feeApprovalId }` · 204, or 202 with the `ApprovalRequestDto` when a linked doctor's registration number, HPR ID or signature, or the consultation fees, wait for approval (both in one save: the identity's request answers, the read shows both); 409 `approval.already_requested` when another one waits |
| GET | `/services?page&pageSize&search&sort&desc&category&departmentId&active` · `/services/{id}` | masters.read | → `PagedResult<ServiceItemDto>` (sort: code, name, price, category) · `ServiceItemDto` |
| POST · PUT | `/services` · `/services/{id}` | masters.manage | `SaveServiceItemRequest` (+`version`, `reason` for the approver) → 201 `{ id, approvalId }` · 204, or 202 with the `ApprovalRequestDto` when the price waits for approval; 409 `approval.already_requested` when a change to the price waits and this save does not fit it |
| GET | `/lookups/departments?kind` · `/lookups/specialties` · `/lookups/practitioners?departmentId&search&kind` (`kind` repeated to narrow to those kinds, e.g. `kind=Nurse&kind=Technician` for the OT nurse picker) · `/lookups/services?category&search` | masters.read | → active items for pickers (at most 50 doctors or services) |
| GET | `/lookups/local-languages` | masters.read | → `LocalLanguageItem[]`: the languages a service may be named in (`code`, `name`), `atFacility` when one is an active facility's local language (billing.md, *The rate list*) |

A save replaces every field, so screens send the whole record.

## Rules

- **Codes are permanent.** A changed code on update is a field error on `code`. Billing, Inpatient and Clinical find
  services by code (`REG-NEW`, `CONS-*`, `NURS-DAY`, `VISIT-DR*`), Diagnostics finds the `LAB`, `RAD` and `CDX`
  departments, and the facility code starts every UHID and document number. A duplicate code is 409
  `<master>.duplicate_code`, also against records deleted in the past.
- **Fields within their columns.** Every request is validated against the column it fills (length, control characters,
  enum, GSTIN, PAN, PIN code, web address, SAC/HSN code, fees up to ₹10 lakh and prices up to ₹1 crore with two
  decimals, 12 languages), so a long value is a field error rather than a server error. Doctors (consultant, visiting,
  resident) need their council registration number. An HPR ID is 14 digits, typed in groups or run together, and kept
  written `xx-xxxx-xxxx-xxxx`.
- **A facility's state is one of India's.** In an Indian address it is read however it is commonly written ("TN",
  "Tamilnadu", "Orissa", ISO and vehicle codes; `SharedKernel/IndianStates.cs`) and saved as GST writes it ("Tamil
  Nadu"); one that names no state or union territory is a field error on `address.state`. The rate list's local
  language is read from it.
- **A service's names in other languages** are keyed by language code (`ta`, `hi`, `kn`…; `LocalLanguages.All`: the
  Eighth Schedule's 22 and the official languages of Mizoram, Tripura and Meghalaya); a blank name is no name.
- **References in use.** A newly chosen speciality, department, head of department or facility must be in use. One chosen
  earlier and since taken out of use doesn't block other edits. A practitioner's or service's department may belong to
  any facility.
- **Images.** A new logo, photo or signature must be a PNG, JPEG or WebP of at most 300 KB uploaded for the purpose, not
  a patient's document. Before, a bigger image was saved and then silently left off documents.
- **A doctor someone signs in as keeps their identity until a second administrator agrees** (MAKER-CHECKER.md, slice 4;
  `PractitionerIdentityApprovals`). The registration number, HPR ID and signature image print on the prescriptions,
  certificates and discharge summaries the linked account signs, and identify the doctor in ABDM records. For a
  practitioner linked to a staff account, a change to any of them waits for another administrator who holds
  `admin.grants.approve` (`masters.practitioner_identity`): the rest of the save is made at once (the fees wait on their
  own, below), the form says it was
  asked for, and the approver reads the before and after ("Registration number: 45120 → 45210", "a signature image
  uploaded now"). It is made only over the identity it replaces (`approval.stale`; a fee changed meanwhile does not
  stand in the way), and audited with both names (`practitioner.identity_changed`). A practitioner with no account is
  edited at once, as before. Linking an account to a practitioner waits too (`identity.practitioner_link`, SECURITY.md).
- **A doctor's consultation fee changes only as a service's price does** (MAKER-CHECKER.md, slice 6;
  `PractitionerFeeApprovals`). The fee for a new visit and for a follow-up is charged to a self-pay patient instead of the
  consultation service's approved price (billing's `PriceResolver.ConsultationOverride`; a payer's negotiated tariff
  still wins, and 0 means the service price), and is the fee appointments are booked at and the rate list shows. A
  change, lower or higher, waits for someone else who holds `masters.prices.approve` (`masters.practitioner_fee`;
  accountants and hospital administrators by default), never the doctor whose fee it is
  (`masters.practitioner_fee_own`); the rest of the save is made at once, and the fee in force is charged until then.
  The approver reads "Consultation fee, new visit: ₹500.00 → ₹700.00". A new practitioner is created with no fee of
  their own (the service price applies) and their fee asked for (`feeApprovalId`). Saving the record again with the fee
  in force, or with the fee that waits, asks nothing twice; another fee while one waits is refused (409
  `approval.already_requested`). It is set only over the fees it replaces (`approval.stale`; an identity or a phone
  number changed meanwhile does not stand in the way), audited with both names (`practitioner.fee_changed`). The form
  shows the fee that waits, with its request.
- **Facilities in use.** Staff sign in only to a facility in use, so the facility the user is signed in to (422
  `facility.current`) and the last facility in use (422 `facility.last_active`) can't be taken out of use.
- **Out of use, not deleted.** A department out of use takes no new visits; a doctor out of use can't be booked or chosen
  for new visits (appointments already booked stay); a service out of use can't be added to new bills or orders.
- **A service charged by itself stays in use while it is** (`IServiceChargeSource`, each module's own): a ward's beds,
  a stay in progress in one of them (it keeps the bed charge its bed was taken with until it moves bed or ends, whatever
  its ward is re-pointed to) and a diet are charged day after day by a service, and billing refuses every charge of a
  service out of use, so the days would go unbilled. Taking it out of use is refused (409 `service.charged_by_itself`),
  naming each ward, stay and diet; point them to another service first, or wait for the stays to end. The nursing day and
  the consultant's visit are looked up by code and charged only while in use: taking them out of use is how a hospital
  stops charging them, and is not refused.
- **A doctor with work ahead goes out of use only once that work is named** (`IPractitionerWorkSource`, each module's
  own): OPD appointments booked or confirmed from now on, patients checked in to see them now, patients on the waiting
  list for a slot with them (Scheduling); video consultations no appointment stands for (Virtual care); patients admitted
  under them as consultant, discharge started too (Inpatient); surgeries asked for, scheduled, in pre-op, in theatre or
  postponed with them as surgeon, anaesthetist or assisting nurse / OT in-charge (OT). Taking the doctor out of use while any is ahead is refused (409
  `practitioner.work_ahead`), naming each kind with how many, when the first is and the first five by number, in the
  problem's `data.workAhead` for the screen; saving again with `takeOutOfUseAnyway` goes ahead, and the audit trail keeps
  what was ahead and who went ahead (`practitioner.out_of_use_with_work_ahead`). Nothing booked is cancelled: the desk
  moves the patients to another doctor or cancels with them. A doctor with nothing ahead, or put back in use, is saved
  without a question.
- **A service's price is approved by a second person** (maker-checker, `ServicePrices`). A self-pay patient pays the
  service's own price and GST, so a save that changes either, lets the counter type a price, allows discounts or puts
  the service in use saves everything else and asks for that (`masters.service_price`); someone else who holds
  `masters.prices.approve` decides it in Approvals. What only narrows the terms (no counter price, no discounts, out of
  use) is made at once, also while a price waits. A new service is made out of use at ₹0 and goes live at the price
  approved. `GET /services/{id}` returns the terms waiting (`pendingPrice`), the editor shows them with a notice and a
  link to the request, so saving again asks for nothing twice; any other change to the terms while they wait, the price
  in use put back included, is refused (409 `approval.already_requested`) until the request is withdrawn or decided.
  The approver sets them only over the terms the request replaced; a change made meanwhile fails it (422
  `prices.changed`) and the person who asked asks again. Billing quotes and charges the terms in use until then. Each
  approved change is audited as `service.price_changed` (before and after, who asked, who approved). The SAC code
  changes at once: it classifies the service for GST returns, not what a patient pays.
- **Audit.** Every save writes a data audit event with each changed value (from/to). Addresses are recorded part by
  part (`address.city`), as are the other compound values in the system: patients', employees' and donors' addresses,
  cardiology findings and TTI results.

## HPR verification

A practitioner's HPR ID is checked against ABDM's Healthcare Professionals Registry from the practitioner's editor
(**Check with the HPR registry**, `POST /api/v1/masters/practitioners/{id}/hpr-check` with the version last read,
`masters.manage`). The registry's name, council and registration number are compared with the master's: names without
titles, case, accents, punctuation or word order; councils without case, punctuation or the words council, medical,
of and the (abbreviations are not guessed); registration numbers without case, punctuation or leading zeros; and the
registration must be active. The result (verified, mismatch with what differs, or not in the registry), when, by whom
and against what source is shown on the master, and kept as history (`org.practitioner_hpr_checks`). Once the HPR ID,
council, registration number or name changes, the check is shown as no longer current. The registry is reached through
`IHprRegistry`; until the hospital is connected to ABDM's HPR service it is the simulator, and every check says so.

## The letterhead

Documents print the facility's name, address and contact, NABH number, letterhead line and logo; bills add the legal
name, GSTIN, PAN and registration number. They read them from the facility record each time, so a saved change shows on
the next document. Lab, radiology, cardiology and health check-up reports and MIS exports use the platform letterhead
(`DocumentParts.Letterhead`); prescriptions and discharge summaries put the same details beside the doctor's block; the
A4 bill uses its own header.

The staff app's own prints (80 mm bills and receipts, pharmacy, canteen, store and purchase documents, slips and lists)
print the same letterhead with the logo and the letterhead line: `letterheadHtml` in `@hc360/core`, from
`GET /api/v1/print/letterhead`, which every staff member may read, whatever their masters permission. Each kind of
document follows its print template: the letterhead or pre-printed stationery, the logo and a closing note (see
`docs/modules/print-templates.md`).

## Not covered yet

- The facility's time zone and currency can't be edited (India only).
- Taking a doctor out of use names the work ahead but doesn't move it: their booked patients are moved or cancelled
  at the desk, appointment by appointment.
- Taking a service out of use is checked against what charges it by itself (wards, stays in progress, diets), not
  against the lab tests, procedures and packages that charge it when ordered: those are refused when ordered, where the
  person ordering sees it.
- Tariff plans' prices and terms, payers' terms, a stay's plan other than its payer's, billing packages' price and
  cover, doctor-fee rules and the non-payable list wait for a second person (billing.md), and a health check-up
  package's price is its service's, asked for from the package's form (clinical.md).
