# Tamil text for review (SRS PP-06)

**Status: needs review by a native Tamil speaker before go-live.** The Tamil below was written by the development team.
It has not yet been checked by a native speaker or by the hospital's clinical staff. Please check each line for meaning,
tone and the words your patients in Tamil Nadu use, mark the last column, and send corrections to the HC360 team.

This list covers the text the **server** writes for patients:

* the labels of the documents patients receive: bills, lab, radiology and heart test reports, discharge summaries, and
  the prescription. The prescription is always printed in English plus the patient's language (ERX-04): English and
  Tamil for a patient who reads Tamil, English and Hindi otherwise;
* the messages the patient portal shows when the server refuses something (a slot that was just taken, a wrong code,
  a payment that did not go through).

The portal's own screens are translated separately (with the same words for *report* and *appointment*), in `src/web/apps/portal/src/i18n/messages`.

What is never translated: medicine names, doses, test names and values, units, codes (UHID, SAC, ICD-10, GSTIN, NABH,
OTP, QR, PDF, UPI), numbers, dates and amounts, and anything a doctor or staff member typed. Words in `{braces}` are
filled in by HC360 (a doctor's name, a date, a number) and must stay as they are. A few legal titles keep their English
in brackets ("வரி விலைப்பட்டியல் (Tax invoice)") so the document can still be recognised for GST.

Where the text lives: `src/server/HyFib.HealthCare360.Platform/Localization/Resources/patient-texts-en.json` (English)
and `patient-texts-ta.json` (Tamil). A server message is matched on its English text, so a correction to the Tamil is
made in the Tamil file only. The tests check that every English text has a Tamil one with the same `{placeholders}`
(a server message may leave one out, as `message.problem.not_found_detail` does with an English record name, as in
Hindi), and that every Tamil text contains Tamil script (except `doc.bill.mode.upi`, which is "UPI" in every language).

Not in this list: the validators' built-in messages for an empty or too-long field ("'Reason' must not be empty"),
which come from the FluentValidation library's own translations, with the field's English name in them.

Points we would most like checked:

* "நோயாளி" for *patient* throughout, and "மருத்துவர்" for *doctor* (rather than "டாக்டர்").
* "அறிக்கை" for *report* throughout, on the documents and in the portal (lab, radiology, heart test reports); patients often say "ரிப்போர்ட்".
  Please tell us which one your hospital prints.
* "சந்திப்பு" for an *appointment* and "முன்பதிவு" for *booking* one, in the server messages and the portal (some patients may know it better as "அப்பாயின்ட்மென்ட்").
* "குறியீடு" for the one-time *code* sent by SMS (some patients may say "OTP" or "கோடு").
* Bill terms: "வரி விலைப்பட்டியல்", "வழங்கல் பில்", "கட்டண அட்டவணை" (tariff), "இணைக் கட்டணம்" (co-pay),
  "முழுமையாக்கல்" (round off), "கணக்கிலிருந்து நீக்கியது (write-off)", "முன்பணத்திலிருந்து சரிசெய்யப்பட்டது"
  (advance adjusted), "தொண்டு" (charity), "நோயாளி / உடனிருப்பவர்" (patient / attendant).
* Report terms: "இயல்பு வரம்பு" (reference interval; "குறிப்பு வரம்பு" is the literal word), "முடிவுரை (இம்ப்ரெஷன்)",
  "கண்டறிதல்கள்" (findings), "தீவிரமான / தீவிரமாக" for *critical*, "குறிப்பு" for the *flag* column,
  "உணர்திறன் உள்ளது / இடைநிலை / எதிர்ப்புத் திறன் உள்ளது" (S / I / R).
* "பிரித்தறியப்பட்ட நுண்ணுயிரி" for *organism isolated* on culture reports: a neutral word (microorganism) chosen on
  purpose, because a culture can grow a fungus or a yeast as well as a bacterium; "பாக்டீரியா" would be wrong for
  those. Please confirm the word your microbiologists and patients use.
* "டிஸ்சார்ஜ்" for discharge, "டிஸ்சார்ஜ் சுருக்கம்" for *discharge summary*, "கன்சல்டன்ட் மருத்துவர்" for
  *consultant*, "செய்யப்பட்ட சிகிச்சை முறைகள்" for *procedures*, and "மீண்டும் வருகை (ஃபாலோ-அப்)" for *follow-up*.
* "சுயமாக" for *Self* (in "Referred by: Self") and "பரிசோதனைக்கு எழுதப்பட்டது" for *Ordered* on reports.
* "ரேடியாலஜி அறிக்கை" and "இதய பரிசோதனை அறிக்கை" as report titles, and "அக்செஷன் எண்" for *accession no.*

## Document labels (bills, prescriptions, reports, discharge summaries)

| # | Key | English | Tamil | OK? |
|---|---|---|---|---|
| 1 | `doc.patient` | Patient | நோயாளி | |
| 2 | `doc.age_sex` | Age / sex | வயது / பாலினம் | |
| 3 | `doc.date` | Date | தேதி | |
| 4 | `doc.page_of` | Page {page} of {pages} | பக்கம் {page} / {pages} | |
| 5 | `doc.phone` | Phone {phone} | தொலைபேசி {phone} | |
| 6 | `doc.nabh_accreditation` | NABH accreditation {number} | NABH அங்கீகாரம் {number} | |
| 7 | `doc.registration_number` | Reg. no. {number} | பதிவு எண் {number} | |
| 8 | `doc.self` | Self | சுயமாக | |
| 9 | `doc.referred_by` | Referred by | பரிந்துரைத்தவர் | |
| 10 | `doc.ordered` | Ordered | பரிசோதனைக்கு எழுதப்பட்டது | |
| 11 | `doc.reported` | Reported | அறிக்கை வழங்கப்பட்டது | |
| 12 | `doc.not_signed` | Not signed | கையொப்பமிடப்படவில்லை | |
| 13 | `doc.report_number` | Report no. | அறிக்கை எண் | |
| 14 | `doc.report_revision` | {number} (revision {revision}) | {number} (திருத்தம் {revision}) | |
| 15 | `doc.accession_number` | Accession no. | அக்செஷன் எண் | |
| 16 | `doc.verify.report` | Scan to verify this report | இந்த அறிக்கையைச் சரிபார்க்க ஸ்கேன் செய்யுங்கள் | |
| 17 | `doc.verify.code` | Verification code {code} | சரிபார்ப்புக் குறியீடு {code} | |
| 18 | `doc.verify.code_label` | Verification code | சரிபார்ப்புக் குறியீடு | |
| 19 | `doc.verify.qr_alt` | QR code to verify this document | இந்த ஆவணத்தைச் சரிபார்க்க QR குறியீடு | |
| 20 | `doc.report.end` | — End of report — | — அறிக்கை முடிவு — | |
| 21 | `doc.report.signed_not_released` | Signed, not yet released | கையொப்பமிடப்பட்டது, இன்னும் வெளியிடப்படவில்லை | |
| 22 | `doc.report.critical_note` | Critical finding: communicated to the treating doctor. | தீவிரமான கண்டறிதல்: சிகிச்சை அளிக்கும் மருத்துவருக்குத் தெரிவிக்கப்பட்டது. | |
| 23 | `doc.bill.tax_invoice` | Tax invoice | வரி விலைப்பட்டியல் (Tax invoice) | |
| 24 | `doc.bill.bill_of_supply` | Bill of supply | வழங்கல் பில் (Bill of supply) | |
| 25 | `doc.bill.estimate` | Estimate (draft bill) | மதிப்பீடு (வரைவு பில்) | |
| 26 | `doc.bill.ip_bill` | IP bill | உள்நோயாளி (IP) பில் | |
| 27 | `doc.bill.ip_final_bill` | IP final bill | உள்நோயாளி (IP) இறுதி பில் | |
| 28 | `doc.bill.stamp_cancelled` | CANCELLED | ரத்து செய்யப்பட்டது | |
| 29 | `doc.bill.stamp_draft` | DRAFT | வரைவு | |
| 30 | `doc.bill.computer_generated` | This is a computer-generated {title}. | இது கணினியால் உருவாக்கப்பட்ட {title} ஆகும். | |
| 31 | `doc.bill.footer_number` | Bill {number} | பில் {number} | |
| 32 | `doc.bill.number` | Bill no. | பில் எண் | |
| 33 | `doc.bill.draft` | Draft | வரைவு | |
| 34 | `doc.bill.registration` | Registration {number} | பதிவு {number} | |
| 35 | `doc.bill.status` | Status | நிலை | |
| 36 | `doc.bill.status.draft` | Draft | வரைவு | |
| 37 | `doc.bill.status.unpaid` | Unpaid | செலுத்தப்படவில்லை | |
| 38 | `doc.bill.status.partly_paid` | Partly paid | பகுதியளவு செலுத்தப்பட்டது | |
| 39 | `doc.bill.status.paid` | Paid | செலுத்தப்பட்டது | |
| 40 | `doc.bill.status.cancelled` | Cancelled | ரத்து செய்யப்பட்டது | |
| 41 | `doc.bill.status.refunded` | Cancelled, refunded | ரத்து செய்யப்பட்டது, பணம் திருப்பித் தரப்பட்டது | |
| 42 | `doc.bill.payer` | Payer | செலுத்துபவர் | |
| 43 | `doc.bill.payer.self` | Self pay | சுயமாகச் செலுத்துதல் | |
| 44 | `doc.bill.payer.insurance` | Insurance | காப்பீடு | |
| 45 | `doc.bill.payer.corporate` | Corporate | கார்ப்பரேட் | |
| 46 | `doc.bill.payer.government_scheme` | Government scheme | அரசுத் திட்டம் | |
| 47 | `doc.bill.payer.staff` | Staff | ஊழியர் | |
| 48 | `doc.bill.payer.charity` | Charity | தொண்டு | |
| 49 | `doc.bill.tariff` | Tariff | கட்டண அட்டவணை | |
| 50 | `doc.bill.package` | Package | பேக்கேஜ் | |
| 51 | `doc.bill.co_pay` | Co-pay | இணைக் கட்டணம் (Co-pay) | |
| 52 | `doc.bill.cancelled` | Cancelled | ரத்து செய்யப்பட்டது | |
| 53 | `doc.bill.cancelled_on` | on {at} | {at} அன்று | |
| 54 | `doc.bill.service` | Service | சேவை | |
| 55 | `doc.bill.qty` | Qty | அளவு | |
| 56 | `doc.bill.rate` | Rate | விலை | |
| 57 | `doc.bill.amount` | Amount | தொகை | |
| 58 | `doc.bill.discount` | Discount | தள்ளுபடி | |
| 59 | `doc.bill.taxable` | Taxable | வரிக்குட்பட்டது | |
| 60 | `doc.bill.net` | Net | நிகரம் | |
| 61 | `doc.bill.return` | Return | திருப்பியளிப்பு | |
| 62 | `doc.bill.exempt` | Exempt | வரிவிலக்கு | |
| 63 | `doc.bill.gross_amount` | Gross amount | மொத்தத் தொகை | |
| 64 | `doc.bill.less_package` | Less: covered by package | கழிவு: பேக்கேஜில் அடங்கியது | |
| 65 | `doc.bill.less_tariff` | Less: tariff concession | கழிவு: கட்டணச் சலுகை | |
| 66 | `doc.bill.less_discount` | Less: discount | கழிவு: தள்ளுபடி | |
| 67 | `doc.bill.less_discount_reason` | Less: discount ({reason}) | கழிவு: தள்ளுபடி ({reason}) | |
| 68 | `doc.bill.taxable_value` | Taxable value | வரிக்குட்பட்ட மதிப்பு | |
| 69 | `doc.bill.round_off` | Round off | முழுமையாக்கல் (Round off) | |
| 70 | `doc.bill.net_amount` | Net amount | நிகரத் தொகை | |
| 71 | `doc.bill.payable_by` | Payable by {payer} | {payer} செலுத்த வேண்டியது | |
| 72 | `doc.bill.insurer` | insurer | காப்பீட்டு நிறுவனம் | |
| 73 | `doc.bill.payable_by_patient` | Payable by patient | நோயாளி செலுத்த வேண்டியது | |
| 74 | `doc.bill.payable_by_patient_words` | Payable by patient: {words} | நோயாளி செலுத்த வேண்டியது (எழுத்தில்): {words} | |
| 75 | `doc.bill.credit_notes` | Credit notes / write-off | கிரெடிட் நோட் / கணக்கிலிருந்து நீக்கியது (write-off) | |
| 76 | `doc.bill.paid` | Paid | செலுத்தப்பட்டது | |
| 77 | `doc.bill.refunded` | Refunded | திருப்பித் தரப்பட்டது | |
| 78 | `doc.bill.refundable` | Refundable to patient | நோயாளிக்குத் திருப்பித் தர வேண்டியது | |
| 79 | `doc.bill.balance_due` | Balance due | செலுத்த வேண்டிய மீதித் தொகை | |
| 80 | `doc.bill.gst_summary` | GST summary | GST சுருக்கம் | |
| 81 | `doc.bill.cgst_rate` | CGST rate | CGST விகிதம் | |
| 82 | `doc.bill.sgst_rate` | SGST rate | SGST விகிதம் | |
| 83 | `doc.bill.payments_received` | Payments received | பெறப்பட்ட தொகைகள் | |
| 84 | `doc.bill.receipt` | Receipt | ரசீது | |
| 85 | `doc.bill.mode` | Mode | செலுத்தும் முறை | |
| 86 | `doc.bill.mode.cash` | Cash | ரொக்கம் | |
| 87 | `doc.bill.mode.card` | Card | கார்டு | |
| 88 | `doc.bill.mode.upi` | UPI | UPI | |
| 89 | `doc.bill.mode.bank_transfer` | Bank transfer | வங்கிப் பரிமாற்றம் | |
| 90 | `doc.bill.mode.cheque` | Cheque | காசோலை | |
| 91 | `doc.bill.mode.online` | Online payment | ஆன்லைன் கட்டணம் | |
| 92 | `doc.bill.mode.insurance` | Insurance settlement | காப்பீட்டுத் தொகை (செட்டில்மென்ட்) | |
| 93 | `doc.bill.mode.wallet` | Wallet | வாலெட் | |
| 94 | `doc.bill.mode.advance` | Advance adjusted | முன்பணத்திலிருந்து சரிசெய்யப்பட்டது | |
| 95 | `doc.bill.sign_patient` | Patient / attendant | நோயாளி / உடனிருப்பவர் | |
| 96 | `doc.bill.sign_authorised` | Authorised signatory | அங்கீகரிக்கப்பட்ட கையொப்பதாரர் | |
| 97 | `doc.clinical.facility_registration` | Reg. {number} | பதிவு {number} | |
| 98 | `doc.clinical.doctor_registration` | Reg. No. {number} | பதிவு எண் {number} | |
| 99 | `doc.clinical.doctor_registration_council` | Reg. No. {number} ({council}) | பதிவு எண் {number} ({council}) | |
| 100 | `doc.clinical.known_allergies` | Known allergies | அறியப்பட்ட ஒவ்வாமைகள் | |
| 101 | `doc.clinical.none_recorded` | none recorded | எதுவும் பதிவு செய்யப்படவில்லை | |
| 102 | `doc.clinical.diagnosis` | Diagnosis | நோயறிதல் | |
| 103 | `doc.clinical.advice` | Advice | அறிவுரை | |
| 104 | `doc.clinical.follow_up` | Follow-up | மீண்டும் வருகை (ஃபாலோ-அப்) | |
| 105 | `doc.clinical.review_on` | Review on {date}. | {date} அன்று மீண்டும் பரிசோதனைக்கு வாருங்கள். | |
| 106 | `doc.clinical.signature_of` | Signature of {name} | {name} அவர்களின் கையொப்பம் | |
| 107 | `doc.rx.medicine` | Medicine | மருந்து | |
| 108 | `doc.rx.dose_directions` | Dose and directions | அளவும் உட்கொள்ளும் முறையும் | |
| 109 | `doc.rx.duration` | Duration | கால அளவு | |
| 110 | `doc.rx.quantity` | Qty | எண்ணிக்கை | |
| 111 | `doc.rx.once` | Once | ஒரு முறை | |
| 112 | `doc.rx.one_day` | 1 day | 1 நாள் | |
| 113 | `doc.rx.days` | {count} days | {count} நாட்கள் | |
| 114 | `doc.rx.weeks` | {count} weeks | {count} வாரங்கள் | |
| 115 | `doc.rx.refills_allowed` | Refills allowed: {count} | மீண்டும் மருந்து பெற அனுமதி: {count} | |
| 116 | `doc.rx.signed_on` | Signed electronically on {at} | {at} அன்று மின்னணு முறையில் கையொப்பமிடப்பட்டது | |
| 117 | `doc.rx.dsc_signed_by` | Digitally signed by {signer} — certificate {serial}, {at} | {signer} அவர்களால் டிஜிட்டல் கையொப்பமிடப்பட்டது — சான்றிதழ் {serial}, {at} | |
| 118 | `doc.rx.scan_to_verify` | Scan to verify this prescription | இந்த மருந்துச் சீட்டைச் சரிபார்க்க ஸ்கேன் செய்யுங்கள் | |
| 119 | `doc.rx.qr_alt` | QR code to verify this prescription | இந்த மருந்துச் சீட்டைச் சரிபார்க்க QR குறியீடு | |
| 120 | `doc.rx.cancelled` | CANCELLED — do not dispense | ரத்து செய்யப்பட்டது — மருந்து வழங்க வேண்டாம் | |
| 121 | `doc.rx.draft` | DRAFT — not valid for dispensing | வரைவு — மருந்து வழங்கச் செல்லாது | |
| 122 | `doc.discharge.title` | Discharge summary | டிஸ்சார்ஜ் சுருக்கம் | |
| 123 | `doc.discharge.document_title` | Discharge summary {uhid} | டிஸ்சார்ஜ் சுருக்கம் {uhid} | |
| 124 | `doc.discharge.discharged_on` | Discharged {date} | {date} அன்று டிஸ்சார்ஜ் செய்யப்பட்டது | |
| 125 | `doc.discharge.draft` | DRAFT — not signed | வரைவு — கையொப்பமிடப்படவில்லை | |
| 126 | `doc.discharge.signed` | Signed {at} | கையொப்பமிடப்பட்டது {at} | |
| 127 | `doc.discharge.amended` |  · amended {at} ({reason}) |  · திருத்தப்பட்டது {at} ({reason}) | |
| 128 | `doc.discharge.stay` | Admitted / discharged | அனுமதி / டிஸ்சார்ஜ் | |
| 129 | `doc.discharge.presenting_complaints` | Presenting complaints | வந்தபோது இருந்த அறிகுறிகள் | |
| 130 | `doc.discharge.course` | Course in hospital | மருத்துவமனையில் சிகிச்சை விவரம் | |
| 131 | `doc.discharge.procedures` | Procedures | செய்யப்பட்ட சிகிச்சை முறைகள் | |
| 132 | `doc.discharge.investigations` | Investigations | பரிசோதனைகள் | |
| 133 | `doc.discharge.condition` | Condition at discharge | டிஸ்சார்ஜின்போது உடல்நிலை | |
| 134 | `doc.discharge.medicines` | Medicines on discharge | டிஸ்சார்ஜின்போது பரிந்துரைக்கப்பட்ட மருந்துகள் | |
| 135 | `doc.discharge.emergency` | In an emergency, come to the hospital casualty or call the hospital. | அவசர நிலையில், மருத்துவமனையின் அவசர சிகிச்சைப் பிரிவுக்கு வாருங்கள் அல்லது மருத்துவமனையை அழையுங்கள். | |
| 136 | `doc.discharge.consultant` | Consultant | கன்சல்டன்ட் மருத்துவர் | |
| 137 | `doc.lab.title` | Laboratory report | ஆய்வக அறிக்கை | |
| 138 | `doc.lab.document_title` | Lab report {number} | ஆய்வக அறிக்கை {number} | |
| 139 | `doc.lab.preliminary` | Preliminary copy for the laboratory: not released | ஆய்வகத்திற்கான முதற்கட்ட நகல்: வெளியிடப்படவில்லை | |
| 140 | `doc.lab.collected` | Collected | மாதிரி சேகரிக்கப்பட்டது | |
| 141 | `doc.lab.received` | Received | பெறப்பட்டது | |
| 142 | `doc.lab.released` | Released | வெளியிடப்பட்டது | |
| 143 | `doc.lab.not_released` | Not released | வெளியிடப்படவில்லை | |
| 144 | `doc.lab.amendments` | Amendments | திருத்தங்கள் | |
| 145 | `doc.lab.revision` | Revision | திருத்தம் | |
| 146 | `doc.lab.when` | When | எப்போது | |
| 147 | `doc.lab.by` | By | யாரால் | |
| 148 | `doc.lab.reason_changes` | Reason and changes | காரணமும் மாற்றங்களும் | |
| 149 | `doc.lab.note.partial` | Partial report: some tests are still in progress and will follow in an updated report. | பகுதி அறிக்கை: சில பரிசோதனைகள் இன்னும் நடைபெற்று வருகின்றன; அவற்றின் முடிவுகள் புதுப்பிக்கப்பட்ட அறிக்கையில் வழங்கப்படும். | |
| 150 | `doc.lab.note.auto_validated` | * Verified automatically under the laboratory auto-validation rules (in range, QC accepted, delta check passed). | * ஆய்வகத்தின் தானியங்கு சரிபார்ப்பு விதிகளின்படி தானாகச் சரிபார்க்கப்பட்டது (வரம்பிற்குள் உள்ளது, QC ஏற்கப்பட்டது, டெல்டா சோதனை வெற்றி). | |
| 151 | `doc.lab.note.amended` | This report was amended after release. The amendments and their reasons are listed above. | இந்த அறிக்கை வெளியிடப்பட்ட பிறகு திருத்தப்பட்டது. திருத்தங்களும் அவற்றின் காரணங்களும் மேலே கொடுக்கப்பட்டுள்ளன. | |
| 152 | `doc.lab.method` | Method: {method} | முறை: {method} | |
| 153 | `doc.lab.tested_at` | Tested at {lab} | பரிசோதிக்கப்பட்ட இடம்: {lab} | |
| 154 | `doc.lab.verified_not_released` | (verified, not yet released) | (சரிபார்க்கப்பட்டது, இன்னும் வெளியிடப்படவில்லை) | |
| 155 | `doc.lab.in_progress` | (in progress) | (நடைபெறுகிறது) | |
| 156 | `doc.lab.investigation` | Investigation | பரிசோதனை | |
| 157 | `doc.lab.result` | Result | முடிவு | |
| 158 | `doc.lab.flag` | Flag | குறிப்பு | |
| 159 | `doc.lab.unit` | Unit | அலகு | |
| 160 | `doc.lab.reference_interval` | Reference interval | இயல்பு வரம்பு | |
| 161 | `doc.lab.interpretation` | Interpretation | விளக்கம் | |
| 162 | `doc.lab.awaited` | Awaited | எதிர்பார்க்கப்படுகிறது | |
| 163 | `doc.lab.not_verified` | (not verified) | (சரிபார்க்கப்படவில்லை) | |
| 164 | `doc.lab.organism` | Organism isolated | பிரித்தறியப்பட்ட நுண்ணுயிரி | |
| 165 | `doc.lab.colony_count` | Colony count {count} | காலனி எண்ணிக்கை {count} | |
| 166 | `doc.lab.antibiotic` | Antibiotic | ஆன்டிபயாடிக் | |
| 167 | `doc.lab.sensitive` | Sensitive (S) | உணர்திறன் உள்ளது (S) | |
| 168 | `doc.lab.intermediate` | Intermediate (I) | இடைநிலை (I) | |
| 169 | `doc.lab.resistant` | Resistant (R) | எதிர்ப்புத் திறன் உள்ளது (R) | |
| 170 | `doc.lab.flag.low` | Low | குறைவு | |
| 171 | `doc.lab.flag.high` | High | அதிகம் | |
| 172 | `doc.lab.flag.critically_low` | Critically low | தீவிரமாகக் குறைவு | |
| 173 | `doc.lab.flag.critically_high` | Critically high | தீவிரமாக அதிகம் | |
| 174 | `doc.lab.flag.abnormal` | Abnormal | இயல்புக்கு மாறானது | |
| 175 | `doc.lab.flag.critical` | Critical | தீவிரம் | |
| 176 | `doc.radiology.title` | Radiology report | ரேடியாலஜி அறிக்கை | |
| 177 | `doc.radiology.document_title` | Radiology report {number} | ரேடியாலஜி அறிக்கை {number} | |
| 178 | `doc.radiology.note` | This report is a professional opinion and should be correlated clinically. | இந்த அறிக்கை ஒரு நிபுணரின் கருத்து மட்டுமே; இதை நோயாளியின் மருத்துவ நிலையுடன் ஒப்பிட்டுப் பார்க்க வேண்டும். | |
| 179 | `doc.radiology.draft` | Draft: not signed by the radiologist | வரைவு: ரேடியாலஜிஸ்ட் இன்னும் கையொப்பமிடவில்லை | |
| 180 | `doc.radiology.scanned` | Scanned | ஸ்கேன் செய்யப்பட்டது | |
| 181 | `doc.radiology.clinical_history` | Clinical history | மருத்துவ வரலாறு | |
| 182 | `doc.radiology.technique` | Technique | ஸ்கேன் முறை | |
| 183 | `doc.radiology.comparison` | Comparison | ஒப்பீடு | |
| 184 | `doc.radiology.findings` | Findings | கண்டறிதல்கள் | |
| 185 | `doc.radiology.impression` | Impression | முடிவுரை (இம்ப்ரெஷன்) | |
| 186 | `doc.radiology.addenda` | Addenda | கூடுதல் குறிப்புகள் | |
| 187 | `doc.radiology.critical_finding` | critical finding | தீவிரமான கண்டறிதல் | |
| 188 | `doc.cardiology.document_title` | Cardiology report {number} | இதய பரிசோதனை அறிக்கை {number} | |
| 189 | `doc.cardiology.title.ecg` | ECG report | ECG அறிக்கை | |
| 190 | `doc.cardiology.title.echo` | Echo report | எக்கோ அறிக்கை | |
| 191 | `doc.cardiology.title.tmt` | TMT report | TMT அறிக்கை | |
| 192 | `doc.cardiology.title.holter` | Holter report | ஹோல்டர் அறிக்கை | |
| 193 | `doc.cardiology.title.abpm` | ABPM report | ABPM அறிக்கை | |
| 194 | `doc.cardiology.title.other` | Cardiology report | இதய பரிசோதனை அறிக்கை | |
| 195 | `doc.cardiology.recorded` | Recorded | பதிவு செய்யப்பட்டது | |
| 196 | `doc.cardiology.draft` | Draft: not signed by the cardiologist | வரைவு: இதய நிபுணர் இன்னும் கையொப்பமிடவில்லை | |
| 197 | `doc.cardiology.conclusion` | Conclusion | முடிவு | |
| 198 | `doc.cardiology.recommendations` | Recommendations | பரிந்துரைகள் | |
| 199 | `doc.cardiology.one_recording` | 1 device recording is stored with this report. | இந்த அறிக்கையுடன் கருவியின் 1 பதிவு சேமிக்கப்பட்டுள்ளது. | |
| 200 | `doc.cardiology.recordings` | {count} device recordings are stored with this report. | இந்த அறிக்கையுடன் கருவியின் {count} பதிவுகள் சேமிக்கப்பட்டுள்ளன. | |

## Server messages shown on the patient portal

| # | Key | English | Tamil | OK? |
|---|---|---|---|---|
| 1 | `message.problem.validation` | Please correct the highlighted fields. | தயவுசெய்து குறிக்கப்பட்ட விவரங்களைச் சரிசெய்யுங்கள். | |
| 2 | `message.problem.not_found` | Not found | கிடைக்கவில்லை | |
| 3 | `message.problem.conflict` | Conflict | இந்தக் கோரிக்கையை முடிக்க முடியவில்லை | |
| 4 | `message.problem.not_allowed` | Not allowed | அனுமதி இல்லை | |
| 5 | `message.problem.sign_in_required` | Sign-in required | உள்நுழைவு தேவை | |
| 6 | `message.problem.cannot_complete` | Cannot complete this action | இந்தச் செயலை முடிக்க முடியவில்லை | |
| 7 | `message.problem.unexpected` | Unexpected error | எதிர்பாராத பிழை | |
| 8 | `message.problem.something_went_wrong` | Something went wrong | ஏதோ தவறு நடந்துவிட்டது | |
| 9 | `message.problem.server_error` | The server could not complete the request. Quote the reference ID if you contact support. | சர்வரால் இந்தக் கோரிக்கையை முடிக்க முடியவில்லை. உதவிக்குத் தொடர்பு கொள்ளும்போது குறிப்பு எண்ணை (reference ID) தெரிவியுங்கள். | |
| 10 | `message.problem.bad_request` | Bad request | தவறான கோரிக்கை | |
| 11 | `message.problem.body_required` | A request body is required. | கோரிக்கையில் விவரங்கள் இருக்க வேண்டும். | |
| 12 | `message.problem.concurrency` | This record was changed by someone else. Reload it and try again. | இந்தப் பதிவை வேறொருவர் மாற்றியுள்ளார். பக்கத்தை மீண்டும் திறந்து, மறுபடி முயற்சி செய்யுங்கள். | |
| 13 | `message.problem.duplicate` | A record with the same unique value already exists. | இதே தனித்துவமான மதிப்புடன் ஒரு பதிவு ஏற்கனவே உள்ளது. | |
| 14 | `message.problem.reference` | This record is linked to other records and cannot be changed this way. | இந்தப் பதிவு மற்ற பதிவுகளுடன் இணைக்கப்பட்டுள்ளதால், இதை இவ்வாறு மாற்ற முடியாது. | |
| 15 | `message.problem.not_found_detail` | {entity} was not found. | நீங்கள் கேட்ட தகவல் கிடைக்கவில்லை. | |
| 16 | `message.request.check_failed` | We could not complete this request. Please try again. | இந்தக் கோரிக்கையை எங்களால் முடிக்க முடியவில்லை. தயவுசெய்து மீண்டும் முயற்சி செய்யுங்கள். | |
| 17 | `message.pdf.title` | The PDF could not be made | PDF-ஐ உருவாக்க முடியவில்லை | |
| 18 | `message.pdf.failed` | The PDF could not be made. | PDF-ஐ உருவாக்க முடியவில்லை. | |
| 19 | `message.pdf.not_set_up` | PDF generation is not set up on this server. | இந்த சர்வரில் PDF உருவாக்கும் வசதி அமைக்கப்படவில்லை. | |
| 20 | `message.pdf.too_long` | Making the PDF took too long. | PDF-ஐ உருவாக்க அதிக நேரம் ஆனது. | |
| 21 | `message.auth.otp_throttled` | Too many codes requested for this number. Please wait a few minutes. | இந்த எண்ணுக்கு அதிகமான குறியீடுகள் கோரப்பட்டுள்ளன. தயவுசெய்து சில நிமிடங்கள் காத்திருங்கள். | |
| 22 | `message.auth.inactive` | This portal account is disabled. Please contact the hospital. | இந்தப் போர்ட்டல் கணக்கு முடக்கப்பட்டுள்ளது. தயவுசெய்து மருத்துவமனையைத் தொடர்பு கொள்ளுங்கள். | |
| 23 | `message.auth.code_incorrect_or_expired` | The code is incorrect or has expired. | குறியீடு தவறானது அல்லது காலாவதியாகிவிட்டது. | |
| 24 | `message.auth.code_incorrect` | The code is incorrect. | குறியீடு தவறானது. | |
| 25 | `message.auth.code_incorrect_check_time` | The code is incorrect. Check the time on your phone and try again. | குறியீடு தவறானது. உங்கள் போனில் உள்ள நேரத்தைச் சரிபார்த்து மீண்டும் முயற்சி செய்யுங்கள். | |
| 26 | `message.appointment.already_paid` | This appointment is already paid. | இந்தச் சந்திப்புக்கு ஏற்கனவே பணம் செலுத்தப்பட்டுவிட்டது. | |
| 27 | `message.appointment.duplicate_day` | {patient} already has an appointment with {doctor} on this day. | {patient} அவர்களுக்கு இந்த நாளில் {doctor} அவர்களிடம் ஏற்கனவே ஒரு சந்திப்பு உள்ளது. | |
| 28 | `message.appointment.not_open_payment` | Only upcoming appointments can be paid online. | வரவிருக்கும் சந்திப்புகளுக்கு மட்டுமே ஆன்லைனில் பணம் செலுத்த முடியும். | |
| 29 | `message.appointment.not_open_reschedule` | Only booked or confirmed appointments can be rescheduled. | முன்பதிவு செய்யப்பட்ட அல்லது உறுதிசெய்யப்பட்ட சந்திப்புகளின் நேரத்தை மட்டுமே மாற்ற முடியும். | |
| 30 | `message.appointment.online_unavailable` | {doctor} is not available for online booking. Please call the hospital. | {doctor} அவர்களிடம் ஆன்லைனில் முன்பதிவு செய்ய முடியாது. தயவுசெய்து மருத்துவமனையை அழையுங்கள். | |
| 31 | `message.appointment.past` | This time has already passed. Choose a later slot. | இந்த நேரம் ஏற்கனவே கடந்துவிட்டது. பிந்தைய நேரத்தைத் தேர்ந்தெடுங்கள். | |
| 32 | `message.appointment.same_slot` | Choose a different time to reschedule. | நேரத்தை மாற்ற, வேறொரு நேரத்தைத் தேர்ந்தெடுங்கள். | |
| 33 | `message.appointment.slot_full` | This slot has just been booked. Choose another slot. | இந்த நேரம் இப்போதுதான் முன்பதிவு செய்யப்பட்டுவிட்டது. வேறொரு நேரத்தைத் தேர்ந்தெடுங்கள். | |
| 34 | `message.appointment.slot_unavailable` | This time is not available for booking. | இந்த நேரம் முன்பதிவுக்குக் கிடைக்கவில்லை. | |
| 35 | `message.appointment.telehealth_unavailable` | This doctor does not offer video consultations. | இந்த மருத்துவர் வீடியோ ஆலோசனை வழங்குவதில்லை. | |
| 36 | `message.appointment.telehealth_unavailable_named` | {doctor} does not offer video consultations. | {doctor} வீடியோ ஆலோசனை வழங்குவதில்லை. | |
| 37 | `message.appointment.choose_patient` | Choose who the appointment is for. | சந்திப்பு யாருக்கு என்பதைத் தேர்ந்தெடுங்கள். | |
| 38 | `message.appointment.choose_doctor` | Choose a doctor. | ஒரு மருத்துவரைத் தேர்ந்தெடுங்கள். | |
| 39 | `message.appointment.choose_slot` | Choose a time slot. | ஒரு நேரத்தைத் தேர்ந்தெடுங்கள். | |
| 40 | `message.appointment.choose_new_slot` | Choose the new time slot. | புதிய நேரத்தைத் தேர்ந்தெடுங்கள். | |
| 41 | `message.patient.inactive_booking` | Appointments cannot be booked for inactive or deceased patients. | செயலில் இல்லாத அல்லது காலமான நோயாளிகளுக்கு முன்பதிவு செய்ய முடியாது. | |
| 42 | `message.patient.inactive` | This patient record is not active. | இந்த நோயாளிப் பதிவு செயலில் இல்லை. | |
| 43 | `message.patient.merged_booking` | This record was merged into another UHID. Book with the surviving record. | இந்தப் பதிவு வேறொரு UHID உடன் இணைக்கப்பட்டுவிட்டது. தற்போது பயன்பாட்டில் உள்ள பதிவைக் கொண்டு முன்பதிவு செய்யுங்கள். | |
| 44 | `message.waitlist.duplicate` | This patient is already on the waitlist for this doctor. | இந்த நோயாளி ஏற்கனவே இந்த மருத்துவருக்கான காத்திருப்புப் பட்டியலில் உள்ளார். | |
| 45 | `message.waitlist.no_schedule` | {doctor} has no OPD sessions to wait for. | {doctor} அவர்களுக்குக் காத்திருக்கக்கூடிய OPD நேரங்கள் எதுவும் இல்லை. | |
| 46 | `message.waitlist.dates` | The last date must be on or after the first date. | கடைசித் தேதி முதல் தேதியாகவோ அதற்குப் பிறகோ இருக்க வேண்டும். | |
| 47 | `message.payment.gateway_not_configured` | No payment gateway is configured. | பணம் செலுத்தும் கேட்வே எதுவும் அமைக்கப்படவில்லை. | |
| 48 | `message.payment.gateway_unavailable` | The payment gateway could not be reached. Try again shortly. | பணம் செலுத்தும் கேட்வேயை அணுக முடியவில்லை. சிறிது நேரத்தில் மீண்டும் முயற்சி செய்யுங்கள். | |
| 49 | `message.payment.in_progress` | A payment for this appointment is still being processed. Check its status again in a few minutes. | இந்தச் சந்திப்புக்கான பணம் செலுத்துதல் இன்னும் செயலாக்கத்தில் உள்ளது. சில நிமிடங்களில் அதன் நிலையை மீண்டும் சரிபாருங்கள். | |
| 50 | `message.payment.invalid_signature` | The payment could not be verified. If money was deducted, it will be refunded automatically. | பணம் செலுத்தியதைச் சரிபார்க்க முடியவில்லை. உங்கள் கணக்கிலிருந்து பணம் கழிக்கப்பட்டிருந்தால், அது தானாகவே திருப்பித் தரப்படும். | |
| 51 | `message.payment.not_captured` | The payment has not been completed. Please try again or pay at the hospital. | பணம் செலுத்துதல் முழுமையடையவில்லை. தயவுசெய்து மீண்டும் முயற்சி செய்யுங்கள் அல்லது மருத்துவமனையில் பணம் செலுத்துங்கள். | |
| 52 | `message.payment.not_started` | No online payment was started for this appointment. | இந்தச் சந்திப்புக்கு ஆன்லைன் பணம் செலுத்துதல் எதுவும் தொடங்கப்படவில்லை. | |
| 53 | `message.payment.order_mismatch` | This payment does not belong to this appointment. | இந்தப் பணம் செலுத்துதல் இந்தச் சந்திப்புக்கு உரியது அல்ல. | |
| 54 | `message.payment.unavailable` | Online payment is not available right now. Please pay at the hospital. | ஆன்லைனில் பணம் செலுத்தும் வசதி இப்போது கிடைக்கவில்லை. தயவுசெய்து மருத்துவமனையில் பணம் செலுத்துங்கள். | |
| 55 | `message.payment.amount` | Enter an amount greater than zero. | பூஜ்ஜியத்தை விட அதிகமான தொகையை உள்ளிடுங்கள். | |
| 56 | `message.portal.account_required` | Sign in to the patient portal first. | முதலில் நோயாளி போர்ட்டலில் உள்நுழையுங்கள். | |
| 57 | `message.portal.patient_denied` | This record is not linked to your mobile number. | இந்தப் பதிவு உங்கள் மொபைல் எண்ணுடன் இணைக்கப்படவில்லை. | |
| 58 | `message.portal.whatsapp_not_holder` | WhatsApp messages can be switched on or off only by signing in with the patient's own mobile number. | நோயாளியின் சொந்த மொபைல் எண் மூலம் உள்நுழைந்தால் மட்டுமே WhatsApp செய்திகளை இயக்கவோ நிறுத்தவோ முடியும். | |
| 59 | `message.portal.family_already_linked` | This family member is already in your family. | இந்தக் குடும்ப உறுப்பினர் ஏற்கனவே உங்கள் குடும்பத்தில் உள்ளார். | |
| 60 | `message.portal.family_already_sees` | This mobile number can already see the record. | இந்த மொபைல் எண் மூலம் ஏற்கனவே இந்தப் பதிவைப் பார்க்க முடியும். | |
| 61 | `message.portal.family_code_expired` | This code can no longer be used. Ask for a new one. | இந்தக் குறியீட்டை இனி பயன்படுத்த முடியாது. புதிய குறியீட்டைக் கேளுங்கள். | |
| 62 | `message.portal.family_code_throttled_account` | You have asked for several codes in the last hour. Please try again later. | கடந்த ஒரு மணி நேரத்தில் நீங்கள் பல குறியீடுகளைக் கேட்டுள்ளீர்கள். தயவுசெய்து பிறகு மீண்டும் முயற்சி செய்யுங்கள். | |
| 63 | `message.portal.family_code_throttled_number` | Several codes have just been sent to this number. Please wait a few minutes. | இந்த எண்ணுக்கு இப்போதுதான் பல குறியீடுகள் அனுப்பப்பட்டுள்ளன. தயவுசெய்து சில நிமிடங்கள் காத்திருங்கள். | |
| 64 | `message.portal.family_own_number` | Everyone registered with your own number is already in your family. | உங்கள் சொந்த எண்ணில் பதிவு செய்யப்பட்ட அனைவரும் ஏற்கனவே உங்கள் குடும்பத்தில் உள்ளனர். | |
| 65 | `message.portal.family_patient_inactive` | This record is no longer in use, so it cannot be shared. | இந்தப் பதிவு இப்போது பயன்பாட்டில் இல்லை, எனவே இதைப் பகிர முடியாது. | |
| 66 | `message.portal.family_same_number` | The patient is registered with this mobile number, so its portal account sees the record already. | நோயாளி இந்த மொபைல் எண்ணில் பதிவு செய்யப்பட்டுள்ளார், எனவே இந்த எண்ணின் போர்ட்டல் கணக்கில் ஏற்கனவே இந்தப் பதிவைப் பார்க்க முடியும். | |
| 67 | `message.portal.family_uhid` | Enter the UHID printed on the hospital card or a bill. | மருத்துவமனை அட்டையில் அல்லது பில்லில் அச்சிடப்பட்டுள்ள UHID-ஐ உள்ளிடுங்கள். | |
| 68 | `message.prescription.not_printable` | Only signed prescriptions can be printed or shared. | கையொப்பமிடப்பட்ட மருந்துச் சீட்டுகளை மட்டுமே அச்சிடவோ பகிரவோ முடியும். | |
| 69 | `message.prescription.dsc_not_signed` | Your doctor has not digitally signed this prescription yet. | உங்கள் மருத்துவர் இந்த மருந்துச் சீட்டில் இன்னும் டிஜிட்டல் கையொப்பமிடவில்லை. | |
| 70 | `message.prescription.cannot_issue` | This prescription cannot be issued right now. Please contact the hospital. | இந்த மருந்துச் சீட்டை இப்போது வழங்க முடியாது. தயவுசெய்து மருத்துவமனையைத் தொடர்பு கொள்ளுங்கள். | |
| 71 | `message.refill.already_requested` | A refill for this prescription is already waiting for the pharmacy. | இந்த மருந்துச் சீட்டுக்கு மீண்டும் மருந்து பெறும் கோரிக்கை ஏற்கனவே மருந்தகத்தில் காத்திருக்கிறது. | |
| 72 | `message.refill.pickup_date` | Choose a pickup date within the next 30 days. | அடுத்த 30 நாட்களுக்குள் மருந்தைப் பெற்றுக்கொள்ளும் தேதியைத் தேர்ந்தெடுங்கள். | |
| 73 | `message.feedback.link_expired` | This feedback link has expired. | இந்தக் கருத்து இணைப்பு காலாவதியாகிவிட்டது. | |
| 74 | `message.feedback.link_used` | Thank you — feedback has already been received for this link. | நன்றி — இந்த இணைப்புக்கான கருத்து ஏற்கனவே பெறப்பட்டுவிட்டது. | |
| 75 | `message.feedback.form` | Choose an available feedback form. | கிடைக்கக்கூடிய ஒரு கருத்துப் படிவத்தைத் தேர்ந்தெடுங்கள். | |
| 76 | `message.feedback.no_comments` | This form does not take comments. | இந்தப் படிவத்தில் கருத்துகளை எழுத முடியாது. | |
| 77 | `message.feedback.contact_phone` | Leave a phone number so that we can call you back. | நாங்கள் உங்களைத் திரும்ப அழைக்க, ஒரு தொலைபேசி எண்ணைக் கொடுங்கள். | |
| 78 | `message.feedback.nps_range` | The recommendation score is from 0 to 10. | பரிந்துரை மதிப்பெண் 0 முதல் 10 வரை. | |
| 79 | `message.feedback.no_nps` | This form does not ask for a recommendation score. | இந்தப் படிவம் பரிந்துரை மதிப்பெண்ணைக் கேட்பதில்லை. | |
| 80 | `message.feedback.ratings_on_form` | Rate only the aspects on this form. | இந்தப் படிவத்தில் உள்ள அம்சங்களை மட்டும் மதிப்பிடுங்கள். | |
| 81 | `message.feedback.ratings_range` | Ratings are from 1 to 5. | மதிப்பீடுகள் 1 முதல் 5 வரை. | |
| 82 | `message.feedback.ratings_required` | Answer at least one question. | குறைந்தது ஒரு கேள்விக்குப் பதிலளியுங்கள். | |
| 83 | `message.feedback.visit` | Choose one of your visits at this hospital. | இந்த மருத்துவமனையில் நீங்கள் வந்த வருகைகளில் ஒன்றைத் தேர்ந்தெடுங்கள். | |
| 84 | `message.privacy.too_many_open` | There are already {count} open requests for this patient. Please wait for our replies. | இந்த நோயாளிக்கு ஏற்கனவே {count} கோரிக்கைகள் நிலுவையில் உள்ளன. தயவுசெய்து எங்கள் பதில்களுக்காகக் காத்திருங்கள். | |
| 85 | `message.privacy.request_closed` | This request is already closed. | இந்தக் கோரிக்கை ஏற்கனவே முடிக்கப்பட்டுவிட்டது. | |
| 86 | `message.privacy.request_type` | Choose a correction, an erasure or a concern. | திருத்தம், நீக்கம் அல்லது புகார் — இவற்றில் ஒன்றைத் தேர்ந்தெடுங்கள். | |
| 87 | `message.privacy.for_whom` | Say whether the request is for you or for a family member. | இந்தக் கோரிக்கை உங்களுக்கா அல்லது குடும்ப உறுப்பினருக்கா என்பதைக் குறிப்பிடுங்கள். | |
| 88 | `message.privacy.name` | Enter your name. | உங்கள் பெயரை உள்ளிடுங்கள். | |
| 89 | `message.privacy.details` | Tell us what you would like us to do. | நாங்கள் என்ன செய்ய வேண்டும் என்பதைத் தெரிவியுங்கள். | |
| 90 | `message.privacy.changes_only_correction` | Only a correction lists changes. | திருத்தக் கோரிக்கையில் மட்டுமே மாற்றங்களைக் குறிப்பிட முடியும். | |
| 91 | `message.field.mobile` | Enter a valid 10-digit mobile number. | சரியான 10 இலக்க மொபைல் எண்ணை உள்ளிடுங்கள். | |
| 92 | `message.field.phone` | Enter a valid phone number. | சரியான தொலைபேசி எண்ணை உள்ளிடுங்கள். | |
| 93 | `message.field.pin_code` | PIN code must be 6 digits. | PIN குறியீடு 6 இலக்கங்களாக இருக்க வேண்டும். | |
| 94 | `message.field.characters` | Contains characters that are not allowed. | அனுமதிக்கப்படாத எழுத்துகள் உள்ளன. | |
| 95 | `message.field.patient` | Choose a registered patient. | பதிவு செய்யப்பட்ட ஒரு நோயாளியைத் தேர்ந்தெடுங்கள். | |
| 96 | `message.field.patient_merged` | This record was merged into another one. Choose the patient's current record. | இந்தப் பதிவு வேறொரு பதிவுடன் இணைக்கப்பட்டுவிட்டது. நோயாளியின் தற்போதைய பதிவைத் தேர்ந்தெடுங்கள். | |
| 97 | `message.field.practitioner` | Choose an active doctor. | செயலில் உள்ள ஒரு மருத்துவரைத் தேர்ந்தெடுங்கள். | |
