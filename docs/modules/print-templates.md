# Print templates (how each kind of document prints)

Platform feature: `src/server/HyFib.HealthCare360.Platform/Documents/PrintTemplates.cs` and
`PrintTemplateEndpoints.cs` · settings `print.<key>` (`platform.system_settings`) · screen **Hospital setup → Print
templates** (`/setup/print-templates` in the staff app) · permission `admin.print.manage` (HospitalAdmin by default).
SRS **HMS-04** (print templates in the configuration centre) and **UX-09** (print-ready documents with the facility
letterhead).

An administrator sets, for each kind of document:

- **Top of the page**: HC360 prints the letterhead, or the paper is the hospital's own **pre-printed stationery**. For
  stationery, HC360 leaves the letterhead off and keeps blank space at the top (and, for a printed footer, at the foot)
  of every A4 and A5 page, 0 to 80 mm. Tax invoices, receipts and vouchers still print the GSTIN and PAN, and clinical
  documents still print the doctor's block, since the stationery does not have them.
- **The logo**: printed beside the hospital name, or left off. On pre-printed stationery the logo is printed only on
  80 mm slips.
- **A closing note**: plain text printed at the end of each document, such as terms ("Subject to Pune jurisdiction")
  or a thank-you line. Up to 500 characters; line breaks are kept; nothing in it can become markup.
- **The paper**, where the document offers a choice: prescriptions print on A5 (the usual) or A4.

The layout of each document stays as it is (ADR-012): a template cannot add markup or styles, so every document stays
the one the hospital checked. What the letterhead prints (name, address, logo, GSTIN, PAN, NABH number, the letterhead
line) is set in **Hospital setup → Facilities and letterhead**.

## The kinds of document

| Key | Name | Covers | Paper | Pre-printed |
|---|---|---|---|---|
| `bill` | Bills | Tax invoices and bills of supply (A4, made by the server), the 80 mm counter bill and the IP running bill | fixed | yes (A4) |
| `receipt` | Receipts and refund vouchers | Payment and advance receipts, on 80 mm or A4 as each counter is set up, and refund vouchers | per counter | yes (A4) |
| `sales-invoice` | Pharmacy and canteen bills | The 80 mm bills of the pharmacy and canteen counters | 80 mm | no |
| `report` | Reports | Laboratory, radiology, cardiology and health check-up reports (made by the server) | A4 | yes |
| `prescription` | Prescriptions | Prescriptions (made by the server) | A5 or A4 | yes |
| `clinical-note` | Clinical notes | Discharge summaries (made by the server), consultation summaries and operation notes | A4 | yes |
| `purchase` | Purchase orders and goods receipts | Purchase orders sent to suppliers, and goods receipt notes | A4 | yes |
| `other` | Other documents | Everything else the staff app prints: day-end and other reports, ward, store and CSSD slips, admission and appointment slips, visitor passes, lists and statements | as printed | yes (A4, A5) |

80 mm rolls are never pre-printed: a document printed on a roll always prints the letterhead, and follows the logo and
closing note of its template.

Not covered by a template: labels (medicine, sample, pack and asset labels), wristbands, the UHID card and the token
slip, which are too small for a letterhead; the landscape quality scorecard; payslips and the DPDP data summary, which
name the facility the record belongs to; and the MIS report exports.

## Hospital-wide or one facility

A template is saved hospital-wide or for the signed-in user's facility. A facility prints with its own template when it
has one, else with the hospital-wide one, else with the defaults: the letterhead printed with the logo, no closing note,
the usual paper. *Use the hospital-wide template here* removes the facility's own. With one facility, the screen offers
no choice and saves hospital-wide.

## When a change applies

The server's documents (A4 bills, reports, prescriptions, discharge summaries, their PDFs and the copies sent to
patients) follow a change from the next document made: settings are cached for a minute, and a save clears this
server's cache at once. The staff app's own prints read the templates with the letterhead
(`GET /api/v1/print/letterhead`), kept for 10 minutes: the administrator's own screens follow at once, other
workstations within 10 minutes or on signing in again.

## Rules (`PrintTemplateRules`)

| Field | Rule | Error (field) |
|---|---|---|
| `letterhead` | `Printed` or `Preprinted`; `Preprinted` only for documents that allow it | `letterhead` |
| `topSpaceMm`, `bottomSpaceMm` | Whole millimetres, 0 to 80. Kept only for pre-printed stationery (0 otherwise) | `topSpaceMm`, `bottomSpaceMm` |
| `closingNote` | At most 500 characters after trimming; text and line breaks only (no tabs or other control characters). Blank means none | `closingNote` |
| `paper` | One of the document's papers (`A5`, `A4` for prescriptions); null for its usual one | `paper` |

A value set by hand in Administration › Settings that breaks a rule prints as near to it as the rules allow (spaces
clamped, a note trimmed, an unknown paper dropped); a value that is not a template prints the defaults. A document is
never refused because of its template.

## Endpoints

| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/api/v1/admin/print-templates` | admin.print.manage | → `PrintTemplateList {facilityId, facilityName, facilityCount, templates: PrintTemplateItem[]}`; each item has `key`, `name`, `area`, `covers`, `papers`, `allowsPreprinted`, `hospitalWide` and `facility` (null without its own) |
| PUT | `/api/v1/admin/print-templates/{key}` | admin.print.manage | `SavePrintTemplateRequest {template, forThisFacility}` → 204; 400 with the field errors above; 404 for an unknown key; 422 `facility.required` for a facility template without a facility |
| DELETE | `/api/v1/admin/print-templates/{key}/facility` | admin.print.manage | → 204; the facility prints with the hospital-wide template again |
| GET | `/api/v1/print/letterhead` | any staff member | → `PrintLetterheadDto` (the signed-in user's facility's letterhead as documents print it, the logo as a data: URI, and every template by key); 403 `print.staff_only` for patients |

A template is stored as JSON in words (`{"letterhead":"Preprinted","topSpaceMm":38,"bottomSpaceMm":0,"showLogo":false,
"closingNote":"Thank you.","paper":null}`). Saving or removing one writes the admin audit event `print.template.changed`
with the template saved and the facility.

## How documents use it

- **Server** (`Platform/Documents`): `DocumentAssets.LetterheadAsync(facilityId, templateKey, ct)` returns the letterhead
  with its template (the logo left out when the template hides it). `PrintableDocument.Template` adds the blank space to
  the page margins (A4: 12 mm plus the top space, 12 mm at the sides, 16 mm plus the foot space; A5: 10, 10 and 14 mm)
  and the closing note after the body; the running footer (patient, number, page X of Y) stays above the space kept
  for the stationery's footer. `DocumentParts.Letterhead` prints only the aside (a doctor's block) on stationery; the bill's header keeps
  its GSTIN and PAN line and title.
- **Staff app** (`@hc360/core`): `useLetterhead()` (`src/lib/letterhead.ts`) reads `/api/v1/print/letterhead`; a print
  starts with `letterheadHtml(letterhead, templateFor(letterhead, key), { compact, taxIds, registration, aside })` and
  passes `template: templateFor(letterhead, key)` to `printDocument`, which adds the blank space to A4 and A5 pages and
  ends the page with the closing note. Until the letterhead arrives, prints show the facility's name with the defaults.

Tests: `PrintTemplateTests` (Platform.Tests and IntegrationTests), the template tests in `BillDocumentTests`,
`PrescriptionDocumentTests` and `LabReportDocumentTests`; in the web apps, `packages/core/src/print/letterhead.test.ts`,
`modules/print-templates/*.test.*` and the print tests of billing, pharmacy, canteen and EMR.
