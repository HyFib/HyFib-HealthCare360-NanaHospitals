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

## Screens: Administration › Hospital setup

| Screen | Route | What it holds |
|---|---|---|
| Facilities and letterhead | `/setup/facilities` | Name, legal name, address, contact, GSTIN, PAN, registration, NABH and HFR ids, the logo and the letterhead line. *Preview letterhead* shows the report letterhead the server prints, from the details being typed |
| Departments | `/setup/departments` | Kind, speciality, head of department, location, queue token prefix and position in lists, for the signed-in facility |
| Specialities | `/setup/specialties` | Shared by every facility; patients choose one when booking online |
| Doctors and clinical staff | `/setup/practitioners` | Name and role, council registration (required for doctors), HPR ID (ABDM's Healthcare Professionals Registry, which identifies the doctor in ABDM health records and NHCX claims), department, speciality, facilities, fees and follow-up days, online and video booking, languages, profile, photo and signature |
| Services and prices | `/setup/services` | Category, department, base price, GST, SAC, whether discounts and price changes are allowed |
| Number formats | `/setup/numbering` | The format of every number the hospital issues, set before the first one if needed (`admin.numbering.manage`; see `docs/modules/numbering.md`) |
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
| GET | `/practitioners?page&pageSize&search&departmentId&specialtyId&kind&active` · `/practitioners/{id}` | masters.read | → `PagedResult<PractitionerDto>` (search: name, code, registration number) · `PractitionerDto` |
| POST · PUT | `/practitioners` · `/practitioners/{id}` | masters.manage | `SavePractitionerRequest` (+`version`) → 201 · 204 |
| GET | `/services?page&pageSize&search&sort&desc&category&departmentId&active` · `/services/{id}` | masters.read | → `PagedResult<ServiceItemDto>` (sort: code, name, price, category) · `ServiceItemDto` |
| POST · PUT | `/services` · `/services/{id}` | masters.manage | `SaveServiceItemRequest` (+`version`) → 201 · 204 |
| GET | `/lookups/departments?kind` · `/lookups/specialties` · `/lookups/practitioners?departmentId&search` · `/lookups/services?category&search` | masters.read | → active items for pickers (at most 50 doctors or services) |

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
- **References in use.** A newly chosen speciality, department, head of department or facility must be in use. One chosen
  earlier and since taken out of use doesn't block other edits. A practitioner's or service's department may belong to
  any facility.
- **Images.** A new logo, photo or signature must be a PNG, JPEG or WebP of at most 300 KB uploaded for the purpose, not
  a patient's document. Before, a bigger image was saved and then silently left off documents.
- **Facilities in use.** Staff sign in only to a facility in use, so the facility the user is signed in to (422
  `facility.current`) and the last facility in use (422 `facility.last_active`) can't be taken out of use.
- **Out of use, not deleted.** A department out of use takes no new visits; a doctor out of use can't be booked or chosen
  for new visits (appointments already booked stay); a service out of use can't be added to new bills or orders.
- **Audit.** Every save writes a data audit event with each changed value (from/to). Addresses are recorded part by
  part (`address.city`), as are the other compound values in the system: patients', employees' and donors' addresses,
  cardiology findings and TTI results.

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
- Nothing warns, before a doctor is taken out of use, how many future appointments they have.
- Taking a service out of use is not checked against the wards, lab tests and packages that charge it.
