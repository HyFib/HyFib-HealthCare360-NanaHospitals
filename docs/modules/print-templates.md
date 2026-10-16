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

## The patient's language (PP-06)

The server's documents are made in one of the languages HC360 writes for patients (`PatientLanguages`: English and
Hindi). Only the document's own words change (titles, labels, headings, notes, "Page 1 of 2"); names, medicines,
doses, test names and values, units, codes (UHID, SAC, ICD-10, GSTIN), dates, amounts and whatever staff typed print
as they are. The amount in words on a bill stays in English.

| Copy | Language |
|---|---|
| Printed or downloaded by staff (`GET …/document`, `GET …/pdf`) | English |
| Downloaded from the portal (`GET /api/v1/portal/…/pdf`) | The request's: the language chosen in the portal (`X-HC360-Language`); without one, the preferred language on the signed-in patient's record when it is English or Hindi, then Accept-Language, then English |
| Sent with a message (`INotificationDocumentSource`) | The preferred language on the document's patient's record, else English |
| The prescription, wherever it goes | English with the Hindi after every label (ERX-04), and the Hindi directions under each medicine |

Each document takes a `DocumentText` (`Platform/Documents/DocumentText.cs`) and reads its words from the patient text
catalogue (`Platform/Localization/Resources/patient-texts-{en,hi}.json`, keys `doc.*`). The page's `lang` is the
document's language (the bilingual prescription is `en`, its Hindi marked `lang="hi"`). A bill's legal title keeps its
English in brackets ("कर बीजक (Tax invoice)").

**Hindi font.** A document with any Devanagari in it (a Hindi document, the prescription, a name typed in Devanagari)
carries Noto Sans Devanagari, regular and bold, as `@font-face` rules with `data:` URIs (about 135 KB; the page's
content security policy already allows `font-src data:`), limited by `unicode-range` to the Devanagari blocks, so Latin
text keeps Segoe UI. The family is `HC360 Devanagari`, listed after Segoe UI and before Nirmala UI in the house style.
An English document carries no font. The browser (Edge for the PDF, the workstation's for a print) lays the text out
with HarfBuzz, which forms conjuncts and places the vowel signs; the PDF embeds the font's subset.

The font files are in `src/server/HyFib.HealthCare360.Platform/Documents/Fonts`: Noto Sans Devanagari 2.007 by the Noto
Project Authors under the SIL Open Font License 1.1 (`OFL.txt` beside them; no Reserved Font Name). They were made
from the variable font in `google/fonts` (`ofl/notosansdevanagari/NotoSansDevanagari[wdth,wght].ttf`) with fontTools:
instanced at weight 400 and 700 and width 100 (names updated), cut down to U+0900–097F, U+1CD0–1CFF, U+200C–200D, U+20B9, U+25CC,
U+A830–A839 and U+A8E0–A8FF with every OpenType layout feature kept, and saved as WOFF2. The OFL allows this; the
font may be redistributed with HC360 and the licence must go with it.

The Hindi text is listed for the hospital's review in [../requirements/hindi-review.md](../requirements/hindi-review.md).

Tests: `PrintTemplateTests` (Platform.Tests and IntegrationTests), the template tests in `BillDocumentTests`,
`PrescriptionDocumentTests` and `LabReportDocumentTests`; in the web apps, `packages/core/src/print/letterhead.test.ts`,
`modules/print-templates/*.test.*` and the print tests of billing, pharmacy, canteen and EMR. The patient's language:
`PatientLocalizationTests` (Platform.Tests: every text in every language, the font, the page numbers), the Hindi tests
in `BillDocumentTests`, `PrescriptionDocumentTests` (bilingual labels), `DischargeSummaryDocumentTests`,
`LabReportDocumentTests` and `RadiologyReportDocumentTests`.
