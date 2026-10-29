# Hindi text for review (SRS PP-06)

**Status: needs review by a native Hindi speaker before go-live.** The Hindi below was written by the development team.
It has not yet been checked by a native speaker or by the hospital's clinical staff. Please check each line for meaning,
tone and the words your patients use, mark the last column, and send corrections to the HC360 team.

This list covers the text the **server** writes for patients:

* the labels of the documents patients receive: bills, lab, radiology and heart test reports, discharge summaries, and
  the prescription (printed in English and Hindi for every patient whose record does not say Tamil, ERX-04);
* the messages the patient portal shows when the server refuses something (a slot that was just taken, a wrong code,
  a payment that did not go through).

The portal's own screens are translated separately, in `src/web/apps/portal/src/i18n/messages`.

What is never translated: medicine names, doses, test names and values, units, codes (UHID, SAC, ICD-10, GSTIN),
numbers, dates and amounts, and anything a doctor or staff member typed. Words in `{braces}` are filled in by HC360
(a doctor's name, a date, a number) and must stay as they are. A few legal titles keep their English in brackets
("कर बीजक (Tax invoice)") so the document can still be recognised for GST.

Where the text lives: `src/server/HyFib.HealthCare360.Platform/Localization/Resources/patient-texts-en.json` (English)
and `patient-texts-hi.json` (Hindi). A server message is matched on its English text, so a correction to the Hindi is
made in the Hindi file only. The tests check that every English text has a Hindi one with the same `{placeholders}`
(a server message may leave one out, as `message.problem.not_found_detail` does with an English record name).

Not in this list: the validators' built-in messages for an empty or too-long field ("'Reason' must not be empty"),
which come in Hindi from the FluentValidation library's own translation, with the field's English name in them.

Points we would most like checked:

* "रोगी" for *patient* throughout (some hospitals prefer "मरीज़").
* Bill terms: "कर बीजक", "आपूर्ति बिल", "बट्टे खाते में", "पूर्णांकन", "अग्रिम राशि से समायोजित".
* Report terms: "संदर्भ सीमा" (reference interval), "राय (इम्प्रेशन)", "प्रतिरोधी / मध्यम / संवेदनशील".
* "पृथक किया गया सूक्ष्मजीव" for *organism isolated* on culture reports: a neutral word (microorganism) chosen on
  purpose, because a culture can grow a fungus or a yeast as well as a bacterium; "जीवाणु" (bacterium) would be wrong
  for those. Please confirm the word your microbiologists and patients use ("सूक्ष्मजीव", "जीव" or the English
  "ऑर्गेनिज़्म").
* "दोबारा दिखाना (फ़ॉलो-अप)" and "छुट्टी" for discharge.

## Document labels (bills, prescriptions, reports, discharge summaries)

| # | Key | English | Hindi | OK? |
|---|---|---|---|---|
| 1 | `doc.patient` | Patient | रोगी | |
| 2 | `doc.age_sex` | Age / sex | आयु / लिंग | |
| 3 | `doc.date` | Date | दिनांक | |
| 4 | `doc.page_of` | Page {page} of {pages} | पृष्ठ {page} / {pages} | |
| 5 | `doc.phone` | Phone {phone} | फ़ोन {phone} | |
| 6 | `doc.nabh_accreditation` | NABH accreditation {number} | NABH प्रत्यायन {number} | |
| 7 | `doc.registration_number` | Reg. no. {number} | पंजीकरण सं. {number} | |
| 8 | `doc.self` | Self | स्वयं | |
| 9 | `doc.referred_by` | Referred by | रेफ़र करने वाले | |
| 10 | `doc.ordered` | Ordered | जाँच लिखी गई | |
| 11 | `doc.reported` | Reported | रिपोर्ट की गई | |
| 12 | `doc.not_signed` | Not signed | हस्ताक्षर नहीं हुए | |
| 13 | `doc.report_number` | Report no. | रिपोर्ट सं. | |
| 14 | `doc.report_revision` | {number} (revision {revision}) | {number} (संशोधन {revision}) | |
| 15 | `doc.accession_number` | Accession no. | एक्सेशन सं. | |
| 16 | `doc.verify.report` | Scan to verify this report | इस रिपोर्ट की जाँच के लिए स्कैन करें | |
| 17 | `doc.verify.code` | Verification code {code} | सत्यापन कोड {code} | |
| 18 | `doc.verify.code_label` | Verification code | सत्यापन कोड | |
| 19 | `doc.verify.qr_alt` | QR code to verify this document | इस दस्तावेज़ की जाँच के लिए QR कोड | |
| 20 | `doc.report.end` | — End of report — | — रिपोर्ट समाप्त — | |
| 21 | `doc.report.signed_not_released` | Signed, not yet released | हस्ताक्षरित, अभी जारी नहीं | |
| 22 | `doc.report.critical_note` | Critical finding: communicated to the treating doctor. | गंभीर निष्कर्ष: इलाज करने वाले डॉक्टर को सूचित किया गया। | |
| 23 | `doc.bill.tax_invoice` | Tax invoice | कर बीजक (Tax invoice) | |
| 24 | `doc.bill.bill_of_supply` | Bill of supply | आपूर्ति बिल (Bill of supply) | |
| 25 | `doc.bill.estimate` | Estimate (draft bill) | अनुमान (मसौदा बिल) | |
| 26 | `doc.bill.ip_bill` | IP bill | आईपी बिल | |
| 27 | `doc.bill.ip_final_bill` | IP final bill | आईपी अंतिम बिल | |
| 28 | `doc.bill.stamp_cancelled` | CANCELLED | रद्द | |
| 29 | `doc.bill.stamp_draft` | DRAFT | मसौदा | |
| 30 | `doc.bill.computer_generated` | This is a computer-generated {title}. | यह कंप्यूटर द्वारा बनाया गया {title} है। | |
| 31 | `doc.bill.footer_number` | Bill {number} | बिल {number} | |
| 32 | `doc.bill.number` | Bill no. | बिल सं. | |
| 33 | `doc.bill.draft` | Draft | मसौदा | |
| 34 | `doc.bill.registration` | Registration {number} | पंजीकरण {number} | |
| 35 | `doc.bill.status` | Status | स्थिति | |
| 36 | `doc.bill.status.draft` | Draft | मसौदा | |
| 37 | `doc.bill.status.unpaid` | Unpaid | भुगतान बाकी | |
| 38 | `doc.bill.status.partly_paid` | Partly paid | आंशिक भुगतान | |
| 39 | `doc.bill.status.paid` | Paid | भुगतान हो गया | |
| 40 | `doc.bill.status.cancelled` | Cancelled | रद्द | |
| 41 | `doc.bill.status.refunded` | Cancelled, refunded | रद्द, राशि वापस | |
| 42 | `doc.bill.payer` | Payer | भुगतानकर्ता | |
| 43 | `doc.bill.payer.self` | Self pay | स्वयं भुगतान | |
| 44 | `doc.bill.payer.insurance` | Insurance | बीमा | |
| 45 | `doc.bill.payer.corporate` | Corporate | कॉरपोरेट | |
| 46 | `doc.bill.payer.government_scheme` | Government scheme | सरकारी योजना | |
| 47 | `doc.bill.payer.staff` | Staff | स्टाफ़ | |
| 48 | `doc.bill.payer.charity` | Charity | धर्मार्थ | |
| 49 | `doc.bill.tariff` | Tariff | टैरिफ़ | |
| 50 | `doc.bill.package` | Package | पैकेज | |
| 51 | `doc.bill.co_pay` | Co-pay | सह-भुगतान | |
| 52 | `doc.bill.cancelled` | Cancelled | रद्द | |
| 53 | `doc.bill.cancelled_on` | on {at} | {at} को | |
| 54 | `doc.bill.service` | Service | सेवा | |
| 55 | `doc.bill.qty` | Qty | मात्रा | |
| 56 | `doc.bill.rate` | Rate | दर | |
| 57 | `doc.bill.amount` | Amount | राशि | |
| 58 | `doc.bill.discount` | Discount | छूट | |
| 59 | `doc.bill.taxable` | Taxable | कर योग्य | |
| 60 | `doc.bill.net` | Net | शुद्ध | |
| 61 | `doc.bill.return` | Return | वापसी | |
| 62 | `doc.bill.exempt` | Exempt | छूट प्राप्त | |
| 63 | `doc.bill.gross_amount` | Gross amount | कुल राशि | |
| 64 | `doc.bill.less_package` | Less: covered by package | घटाएँ: पैकेज में शामिल | |
| 65 | `doc.bill.less_tariff` | Less: tariff concession | घटाएँ: टैरिफ़ रियायत | |
| 66 | `doc.bill.less_discount` | Less: discount | घटाएँ: छूट | |
| 67 | `doc.bill.less_discount_reason` | Less: discount ({reason}) | घटाएँ: छूट ({reason}) | |
| 68 | `doc.bill.taxable_value` | Taxable value | कर योग्य मूल्य | |
| 69 | `doc.bill.round_off` | Round off | पूर्णांकन | |
| 70 | `doc.bill.net_amount` | Net amount | शुद्ध राशि | |
| 71 | `doc.bill.payable_by` | Payable by {payer} | {payer} द्वारा देय | |
| 72 | `doc.bill.insurer` | insurer | बीमा कंपनी | |
| 73 | `doc.bill.payable_by_patient` | Payable by patient | रोगी द्वारा देय | |
| 74 | `doc.bill.payable_by_patient_words` | Payable by patient: {words} | रोगी द्वारा देय (शब्दों में): {words} | |
| 75 | `doc.bill.credit_notes` | Credit notes / write-off | क्रेडिट नोट / बट्टे खाते में | |
| 76 | `doc.bill.paid` | Paid | भुगतान किया गया | |
| 77 | `doc.bill.refunded` | Refunded | वापस किया गया | |
| 78 | `doc.bill.refundable` | Refundable to patient | रोगी को वापस देय | |
| 79 | `doc.bill.balance_due` | Balance due | बकाया राशि | |
| 80 | `doc.bill.gst_summary` | GST summary | GST सारांश | |
| 81 | `doc.bill.cgst_rate` | CGST rate | CGST दर | |
| 82 | `doc.bill.sgst_rate` | SGST rate | SGST दर | |
| 83 | `doc.bill.payments_received` | Payments received | प्राप्त भुगतान | |
| 84 | `doc.bill.receipt` | Receipt | रसीद | |
| 85 | `doc.bill.mode` | Mode | भुगतान का तरीका | |
| 86 | `doc.bill.mode.cash` | Cash | नकद | |
| 87 | `doc.bill.mode.card` | Card | कार्ड | |
| 88 | `doc.bill.mode.upi` | UPI | UPI | |
| 89 | `doc.bill.mode.bank_transfer` | Bank transfer | बैंक ट्रांसफ़र | |
| 90 | `doc.bill.mode.cheque` | Cheque | चेक | |
| 91 | `doc.bill.mode.online` | Online payment | ऑनलाइन भुगतान | |
| 92 | `doc.bill.mode.insurance` | Insurance settlement | बीमा निपटान | |
| 93 | `doc.bill.mode.wallet` | Wallet | वॉलेट | |
| 94 | `doc.bill.mode.advance` | Advance adjusted | अग्रिम राशि से समायोजित | |
| 95 | `doc.bill.sign_patient` | Patient / attendant | रोगी / परिचारक | |
| 96 | `doc.bill.sign_authorised` | Authorised signatory | अधिकृत हस्ताक्षरकर्ता | |
| 97 | `doc.clinical.facility_registration` | Reg. {number} | पंजीकरण {number} | |
| 98 | `doc.clinical.doctor_registration` | Reg. No. {number} | पंजीकरण सं. {number} | |
| 99 | `doc.clinical.doctor_registration_council` | Reg. No. {number} ({council}) | पंजीकरण सं. {number} ({council}) | |
| 100 | `doc.clinical.known_allergies` | Known allergies | ज्ञात एलर्जी | |
| 101 | `doc.clinical.none_recorded` | none recorded | कोई दर्ज नहीं | |
| 102 | `doc.clinical.diagnosis` | Diagnosis | निदान | |
| 103 | `doc.clinical.advice` | Advice | सलाह | |
| 104 | `doc.clinical.follow_up` | Follow-up | दोबारा दिखाना (फ़ॉलो-अप) | |
| 105 | `doc.clinical.review_on` | Review on {date}. | {date} को दोबारा दिखाएँ। | |
| 106 | `doc.clinical.signature_of` | Signature of {name} | {name} के हस्ताक्षर | |
| 107 | `doc.rx.medicine` | Medicine | दवा | |
| 108 | `doc.rx.dose_directions` | Dose and directions | खुराक और निर्देश | |
| 109 | `doc.rx.duration` | Duration | अवधि | |
| 110 | `doc.rx.quantity` | Qty | मात्रा | |
| 111 | `doc.rx.once` | Once | एक बार | |
| 112 | `doc.rx.one_day` | 1 day | 1 दिन | |
| 113 | `doc.rx.days` | {count} days | {count} दिन | |
| 114 | `doc.rx.weeks` | {count} weeks | {count} सप्ताह | |
| 115 | `doc.rx.refills_allowed` | Refills allowed: {count} | दवा दोबारा लेने की अनुमति: {count} बार | |
| 116 | `doc.rx.signed_on` | Signed electronically on {at} | {at} को इलेक्ट्रॉनिक रूप से हस्ताक्षरित | A prescription signed in HC360 without the doctor's DSC is not "digitally signed" under the IT Act, so the line says "electronically". |
| 117 | `doc.rx.dsc_signed_by` | Digitally signed by {signer} — certificate {serial}, {at} | {signer} द्वारा डिजिटल हस्ताक्षर — प्रमाणपत्र {serial}, {at} | Printed only when the doctor's DSC signature checks out (ERX-04). |
| 117 | `doc.rx.scan_to_verify` | Scan to verify this prescription | इस पर्चे की जाँच के लिए स्कैन करें | |
| 118 | `doc.rx.qr_alt` | QR code to verify this prescription | इस पर्चे की जाँच के लिए QR कोड | |
| 119 | `doc.rx.cancelled` | CANCELLED — do not dispense | रद्द — दवा न दें | |
| 120 | `doc.rx.draft` | DRAFT — not valid for dispensing | मसौदा — दवा देने के लिए मान्य नहीं | |
| 121 | `doc.discharge.title` | Discharge summary | डिस्चार्ज सारांश | |
| 122 | `doc.discharge.document_title` | Discharge summary {uhid} | डिस्चार्ज सारांश {uhid} | |
| 123 | `doc.discharge.discharged_on` | Discharged {date} | छुट्टी {date} | |
| 124 | `doc.discharge.draft` | DRAFT — not signed | मसौदा — हस्ताक्षर नहीं हुए | |
| 125 | `doc.discharge.signed` | Signed {at} | हस्ताक्षरित {at} | |
| 126 | `doc.discharge.amended` |  · amended {at} ({reason}) |  · संशोधित {at} ({reason}) | |
| 127 | `doc.discharge.stay` | Admitted / discharged | भर्ती / छुट्टी | |
| 128 | `doc.discharge.presenting_complaints` | Presenting complaints | भर्ती के समय की तकलीफ़ें | |
| 129 | `doc.discharge.course` | Course in hospital | अस्पताल में इलाज का विवरण | |
| 130 | `doc.discharge.procedures` | Procedures | प्रक्रियाएँ | |
| 131 | `doc.discharge.investigations` | Investigations | जाँचें | |
| 132 | `doc.discharge.condition` | Condition at discharge | छुट्टी के समय स्थिति | |
| 133 | `doc.discharge.medicines` | Medicines on discharge | छुट्टी पर दी गई दवाएँ | |
| 134 | `doc.discharge.emergency` | In an emergency, come to the hospital casualty or call the hospital. | आपात स्थिति में अस्पताल के आपातकालीन विभाग (कैज़ुअल्टी) में आएँ या अस्पताल को फ़ोन करें। | |
| 135 | `doc.discharge.consultant` | Consultant | कंसल्टेंट | |
| 136 | `doc.lab.title` | Laboratory report | प्रयोगशाला रिपोर्ट | |
| 137 | `doc.lab.document_title` | Lab report {number} | लैब रिपोर्ट {number} | |
| 138 | `doc.lab.preliminary` | Preliminary copy for the laboratory: not released | प्रयोगशाला के लिए प्रारंभिक प्रति: जारी नहीं की गई | |
| 139 | `doc.lab.collected` | Collected | नमूना लिया गया | |
| 140 | `doc.lab.received` | Received | नमूना प्राप्त | |
| 141 | `doc.lab.released` | Released | जारी | |
| 142 | `doc.lab.not_released` | Not released | जारी नहीं | |
| 143 | `doc.lab.amendments` | Amendments | संशोधन | |
| 144 | `doc.lab.revision` | Revision | संशोधन सं. | |
| 145 | `doc.lab.when` | When | कब | |
| 146 | `doc.lab.by` | By | किसने | |
| 147 | `doc.lab.reason_changes` | Reason and changes | कारण और बदलाव | |
| 148 | `doc.lab.note.partial` | Partial report: some tests are still in progress and will follow in an updated report. | आंशिक रिपोर्ट: कुछ जाँचें अभी चल रही हैं; उनके परिणाम अद्यतन रिपोर्ट में आएँगे। | |
| 149 | `doc.lab.note.auto_validated` | * Verified automatically under the laboratory auto-validation rules (in range, QC accepted, delta check passed). | * प्रयोगशाला के स्वचालित सत्यापन नियमों के अनुसार अपने-आप सत्यापित (सीमा के भीतर, QC स्वीकृत, डेल्टा जाँच सफल)। | |
| 150 | `doc.lab.note.amended` | This report was amended after release. The amendments and their reasons are listed above. | जारी होने के बाद इस रिपोर्ट में संशोधन किया गया। संशोधन और उनके कारण ऊपर दिए गए हैं। | |
| 151 | `doc.lab.method` | Method: {method} | विधि: {method} | |
| 152 | `doc.lab.tested_at` | Tested at {lab} | जाँच {lab} में की गई | |
| 153 | `doc.lab.verified_not_released` | (verified, not yet released) | (सत्यापित, अभी जारी नहीं) | |
| 154 | `doc.lab.in_progress` | (in progress) | (जाँच जारी है) | |
| 155 | `doc.lab.investigation` | Investigation | जाँच | |
| 156 | `doc.lab.result` | Result | परिणाम | |
| 157 | `doc.lab.flag` | Flag | संकेत | |
| 158 | `doc.lab.unit` | Unit | इकाई | |
| 159 | `doc.lab.reference_interval` | Reference interval | संदर्भ सीमा | |
| 160 | `doc.lab.interpretation` | Interpretation | व्याख्या | |
| 161 | `doc.lab.awaited` | Awaited | प्रतीक्षित | |
| 162 | `doc.lab.not_verified` | (not verified) | (सत्यापित नहीं) | |
| 163 | `doc.lab.organism` | Organism isolated | पृथक किया गया सूक्ष्मजीव | |
| 164 | `doc.lab.colony_count` | Colony count {count} | कॉलोनी गणना {count} | |
| 165 | `doc.lab.antibiotic` | Antibiotic | एंटीबायोटिक | |
| 166 | `doc.lab.sensitive` | Sensitive (S) | संवेदनशील (S) | |
| 167 | `doc.lab.intermediate` | Intermediate (I) | मध्यम (I) | |
| 168 | `doc.lab.resistant` | Resistant (R) | प्रतिरोधी (R) | |
| 169 | `doc.lab.flag.low` | Low | कम | |
| 170 | `doc.lab.flag.high` | High | अधिक | |
| 171 | `doc.lab.flag.critically_low` | Critically low | गंभीर रूप से कम | |
| 172 | `doc.lab.flag.critically_high` | Critically high | गंभीर रूप से अधिक | |
| 173 | `doc.lab.flag.abnormal` | Abnormal | असामान्य | |
| 174 | `doc.lab.flag.critical` | Critical | गंभीर | |
| 175 | `doc.radiology.title` | Radiology report | रेडियोलॉजी रिपोर्ट | |
| 176 | `doc.radiology.document_title` | Radiology report {number} | रेडियोलॉजी रिपोर्ट {number} | |
| 177 | `doc.radiology.note` | This report is a professional opinion and should be correlated clinically. | यह रिपोर्ट विशेषज्ञ की राय है; इसे रोगी की नैदानिक स्थिति के साथ मिलाकर देखा जाना चाहिए। | |
| 178 | `doc.radiology.draft` | Draft: not signed by the radiologist | मसौदा: रेडियोलॉजिस्ट के हस्ताक्षर नहीं हुए हैं | |
| 179 | `doc.radiology.scanned` | Scanned | स्कैन किया गया | |
| 180 | `doc.radiology.clinical_history` | Clinical history | नैदानिक इतिहास | |
| 181 | `doc.radiology.technique` | Technique | तकनीक | |
| 182 | `doc.radiology.comparison` | Comparison | तुलना | |
| 183 | `doc.radiology.findings` | Findings | जाँच के निष्कर्ष | |
| 184 | `doc.radiology.impression` | Impression | राय (इम्प्रेशन) | |
| 185 | `doc.radiology.addenda` | Addenda | परिशिष्ट | |
| 186 | `doc.radiology.critical_finding` | critical finding | गंभीर निष्कर्ष | |
| 187 | `doc.cardiology.document_title` | Cardiology report {number} | हृदय जाँच रिपोर्ट {number} | |
| 188 | `doc.cardiology.title.ecg` | ECG report | ECG रिपोर्ट | |
| 189 | `doc.cardiology.title.echo` | Echo report | इको रिपोर्ट | |
| 190 | `doc.cardiology.title.tmt` | TMT report | TMT रिपोर्ट | |
| 191 | `doc.cardiology.title.holter` | Holter report | होल्टर रिपोर्ट | |
| 192 | `doc.cardiology.title.abpm` | ABPM report | ABPM रिपोर्ट | |
| 193 | `doc.cardiology.title.other` | Cardiology report | हृदय जाँच रिपोर्ट | |
| 194 | `doc.cardiology.recorded` | Recorded | रिकॉर्ड किया गया | |
| 195 | `doc.cardiology.draft` | Draft: not signed by the cardiologist | मसौदा: कार्डियोलॉजिस्ट के हस्ताक्षर नहीं हुए हैं | |
| 196 | `doc.cardiology.conclusion` | Conclusion | निष्कर्ष | |
| 197 | `doc.cardiology.recommendations` | Recommendations | सुझाव | |
| 198 | `doc.cardiology.one_recording` | 1 device recording is stored with this report. | इस रिपोर्ट के साथ उपकरण की 1 रिकॉर्डिंग सुरक्षित है। | |
| 199 | `doc.cardiology.recordings` | {count} device recordings are stored with this report. | इस रिपोर्ट के साथ उपकरण की {count} रिकॉर्डिंग सुरक्षित हैं। | |

## Server messages shown on the patient portal

| # | Key | English | Hindi | OK? |
|---|---|---|---|---|
| 1 | `message.problem.validation` | Please correct the highlighted fields. | कृपया चिह्नित जानकारी ठीक करें। | |
| 2 | `message.problem.not_found` | Not found | नहीं मिला | |
| 3 | `message.problem.conflict` | Conflict | यह अनुरोध पूरा नहीं हो सका | |
| 4 | `message.problem.not_allowed` | Not allowed | अनुमति नहीं है | |
| 5 | `message.problem.sign_in_required` | Sign-in required | साइन इन करना ज़रूरी है | |
| 6 | `message.problem.cannot_complete` | Cannot complete this action | यह काम पूरा नहीं किया जा सकता | |
| 7 | `message.problem.unexpected` | Unexpected error | अनपेक्षित त्रुटि | |
| 8 | `message.problem.something_went_wrong` | Something went wrong | कुछ गड़बड़ हो गई | |
| 9 | `message.problem.server_error` | The server could not complete the request. Quote the reference ID if you contact support. | सर्वर आपका अनुरोध पूरा नहीं कर सका। सहायता के लिए संपर्क करें तो संदर्भ आईडी बताएँ। | |
| 10 | `message.problem.bad_request` | Bad request | अनुरोध सही नहीं है | |
| 11 | `message.problem.body_required` | A request body is required. | अनुरोध के साथ ज़रूरी जानकारी नहीं भेजी गई। | |
| 12 | `message.problem.concurrency` | This record was changed by someone else. Reload it and try again. | यह रिकॉर्ड किसी और ने बदल दिया है। पेज दोबारा खोलें और फिर से कोशिश करें। | |
| 13 | `message.problem.duplicate` | A record with the same unique value already exists. | यही जानकारी वाला रिकॉर्ड पहले से मौजूद है। | |
| 14 | `message.problem.reference` | This record is linked to other records and cannot be changed this way. | यह रिकॉर्ड दूसरे रिकॉर्ड से जुड़ा है, इसलिए इसे इस तरह नहीं बदला जा सकता। | |
| 15 | `message.problem.not_found_detail` | {entity} was not found. | माँगी गई जानकारी नहीं मिली। | |
| 16 | `message.pdf.title` | The PDF could not be made | PDF नहीं बन सका | |
| 17 | `message.pdf.failed` | The PDF could not be made. | PDF नहीं बन सका। | |
| 18 | `message.pdf.not_set_up` | PDF generation is not set up on this server. | इस सर्वर पर PDF बनाने की सुविधा चालू नहीं है। | |
| 19 | `message.pdf.too_long` | Making the PDF took too long. | PDF बनाने में बहुत समय लग गया। | |
| 20 | `message.auth.otp_throttled` | Too many codes requested for this number. Please wait a few minutes. | इस नंबर के लिए बहुत अधिक कोड माँगे गए हैं। कृपया कुछ मिनट रुकें। | |
| 21 | `message.auth.inactive` | This portal account is disabled. Please contact the hospital. | यह पोर्टल खाता बंद कर दिया गया है। कृपया अस्पताल से संपर्क करें। | |
| 22 | `message.auth.code_incorrect_or_expired` | The code is incorrect or has expired. | कोड गलत है या उसकी समय-सीमा खत्म हो गई है। | |
| 23 | `message.auth.code_incorrect` | The code is incorrect. | कोड गलत है। | |
| 24 | `message.auth.code_incorrect_check_time` | The code is incorrect. Check the time on your phone and try again. | कोड गलत है। अपने फ़ोन का समय जाँचें और फिर कोशिश करें। | |
| 25 | `message.appointment.already_paid` | This appointment is already paid. | इस अपॉइंटमेंट का भुगतान पहले ही हो चुका है। | |
| 26 | `message.appointment.duplicate_day` | {patient} already has an appointment with {doctor} on this day. | {patient} का इस दिन {doctor} के साथ पहले से अपॉइंटमेंट है। | |
| 27 | `message.appointment.not_open_payment` | Only upcoming appointments can be paid online. | केवल आने वाले अपॉइंटमेंट का ही ऑनलाइन भुगतान हो सकता है। | |
| 28 | `message.appointment.not_open_reschedule` | Only booked or confirmed appointments can be rescheduled. | केवल बुक या पक्के किए गए अपॉइंटमेंट का ही समय बदला जा सकता है। | |
| 29 | `message.appointment.online_unavailable` | {doctor} is not available for online booking. Please call the hospital. | {doctor} के साथ ऑनलाइन बुकिंग उपलब्ध नहीं है। कृपया अस्पताल को फ़ोन करें। | |
| 30 | `message.appointment.past` | This time has already passed. Choose a later slot. | यह समय निकल चुका है। कृपया बाद का कोई समय चुनें। | |
| 31 | `message.appointment.same_slot` | Choose a different time to reschedule. | समय बदलने के लिए कोई दूसरा समय चुनें। | |
| 32 | `message.appointment.slot_full` | This slot has just been booked. Choose another slot. | यह समय अभी-अभी बुक हो गया है। कृपया कोई दूसरा समय चुनें। | |
| 33 | `message.appointment.slot_unavailable` | This time is not available for booking. | यह समय बुकिंग के लिए उपलब्ध नहीं है। | |
| 34 | `message.appointment.telehealth_unavailable` | This doctor does not offer video consultations. | ये डॉक्टर वीडियो परामर्श नहीं देते। | |
| 35 | `message.appointment.telehealth_unavailable_named` | {doctor} does not offer video consultations. | {doctor} वीडियो परामर्श नहीं देते। | |
| 36 | `message.appointment.choose_patient` | Choose who the appointment is for. | चुनें कि अपॉइंटमेंट किसके लिए है। | |
| 37 | `message.appointment.choose_doctor` | Choose a doctor. | डॉक्टर चुनें। | |
| 38 | `message.appointment.choose_slot` | Choose a time slot. | समय चुनें। | |
| 39 | `message.appointment.choose_new_slot` | Choose the new time slot. | नया समय चुनें। | |
| 40 | `message.patient.inactive_booking` | Appointments cannot be booked for inactive or deceased patients. | निष्क्रिय या दिवंगत रोगियों के लिए अपॉइंटमेंट बुक नहीं किया जा सकता। | |
| 41 | `message.patient.inactive` | This patient record is not active. | यह रोगी रिकॉर्ड सक्रिय नहीं है। | |
| 42 | `message.patient.merged_booking` | This record was merged into another UHID. Book with the surviving record. | यह रिकॉर्ड किसी दूसरे UHID में मिला दिया गया है। कृपया उस मौजूदा रिकॉर्ड से बुक करें। | |
| 43 | `message.waitlist.duplicate` | This patient is already on the waitlist for this doctor. | यह रोगी पहले से इस डॉक्टर की प्रतीक्षा सूची में है। | |
| 44 | `message.waitlist.no_schedule` | {doctor} has no OPD sessions to wait for. | {doctor} का कोई OPD सत्र नहीं है जिसकी प्रतीक्षा की जा सके। | |
| 45 | `message.waitlist.dates` | The last date must be on or after the first date. | अंतिम तारीख पहली तारीख के दिन या उसके बाद की होनी चाहिए। | |
| 46 | `message.payment.gateway_not_configured` | No payment gateway is configured. | ऑनलाइन भुगतान की व्यवस्था अभी उपलब्ध नहीं है। | |
| 47 | `message.payment.gateway_unavailable` | The payment gateway could not be reached. Try again shortly. | भुगतान गेटवे से संपर्क नहीं हो सका। थोड़ी देर बाद फिर कोशिश करें। | |
| 48 | `message.payment.in_progress` | A payment for this appointment is still being processed. Check its status again in a few minutes. | इस अपॉइंटमेंट का एक भुगतान अभी प्रक्रिया में है। कुछ मिनट बाद उसकी स्थिति फिर से देखें। | |
| 49 | `message.payment.invalid_signature` | The payment could not be verified. If money was deducted, it will be refunded automatically. | भुगतान की पुष्टि नहीं हो सकी। अगर पैसे कट गए हैं, तो वे अपने-आप वापस आ जाएँगे। | |
| 50 | `message.payment.not_captured` | The payment has not been completed. Please try again or pay at the hospital. | भुगतान पूरा नहीं हुआ है। कृपया फिर कोशिश करें या अस्पताल में भुगतान करें। | |
| 51 | `message.payment.not_started` | No online payment was started for this appointment. | इस अपॉइंटमेंट के लिए कोई ऑनलाइन भुगतान शुरू नहीं किया गया था। | |
| 52 | `message.payment.order_mismatch` | This payment does not belong to this appointment. | यह भुगतान इस अपॉइंटमेंट का नहीं है। | |
| 53 | `message.payment.unavailable` | Online payment is not available right now. Please pay at the hospital. | ऑनलाइन भुगतान अभी उपलब्ध नहीं है। कृपया अस्पताल में भुगतान करें। | |
| 54 | `message.payment.amount` | Enter an amount greater than zero. | शून्य से अधिक राशि दर्ज करें। | |
| 55 | `message.portal.account_required` | Sign in to the patient portal first. | पहले रोगी पोर्टल में साइन इन करें। | |
| 56 | `message.portal.patient_denied` | This record is not linked to your mobile number. | यह रिकॉर्ड आपके मोबाइल नंबर से जुड़ा नहीं है। | |
| 57 | `message.portal.whatsapp_not_holder` | WhatsApp messages can be switched on or off only by signing in with the patient's own mobile number. | WhatsApp संदेश केवल रोगी के अपने मोबाइल नंबर से साइन इन करके ही चालू या बंद किए जा सकते हैं। | |
| 58 | `message.portal.family_already_linked` | This family member is already in your family. | यह सदस्य पहले से आपके परिवार में जुड़ा है। | |
| 59 | `message.portal.family_already_sees` | This mobile number can already see the record. | यह मोबाइल नंबर पहले से ही यह रिकॉर्ड देख सकता है। | |
| 60 | `message.portal.family_code_expired` | This code can no longer be used. Ask for a new one. | यह कोड अब इस्तेमाल नहीं किया जा सकता। नया कोड माँगें। | |
| 61 | `message.portal.family_code_throttled_account` | You have asked for several codes in the last hour. Please try again later. | आपने पिछले एक घंटे में कई कोड माँगे हैं। कृपया बाद में फिर कोशिश करें। | |
| 62 | `message.portal.family_code_throttled_number` | Several codes have just been sent to this number. Please wait a few minutes. | इस नंबर पर अभी-अभी कई कोड भेजे गए हैं। कृपया कुछ मिनट रुकें। | |
| 63 | `message.portal.family_own_number` | Everyone registered with your own number is already in your family. | आपके अपने नंबर से पंजीकृत सभी लोग पहले से आपके परिवार में हैं। | |
| 64 | `message.portal.family_patient_inactive` | This record is no longer in use, so it cannot be shared. | यह रिकॉर्ड अब उपयोग में नहीं है, इसलिए इसे साझा नहीं किया जा सकता। | |
| 65 | `message.portal.family_same_number` | The patient is registered with this mobile number, so its portal account sees the record already. | यह रोगी इसी मोबाइल नंबर से पंजीकृत है, इसलिए इस नंबर का पोर्टल खाता यह रिकॉर्ड पहले से देख सकता है। | |
| 66 | `message.portal.family_uhid` | Enter the UHID printed on the hospital card or a bill. | अस्पताल कार्ड या बिल पर छपा UHID दर्ज करें। | |
| 67 | `message.prescription.not_printable` | Only signed prescriptions can be printed or shared. | केवल हस्ताक्षरित पर्चे ही प्रिंट या साझा किए जा सकते हैं। | |
| 68 | `message.refill.already_requested` | A refill for this prescription is already waiting for the pharmacy. | इस पर्चे की दवा दोबारा लेने का अनुरोध पहले से फ़ार्मेसी के पास है। | |
| 69 | `message.refill.pickup_date` | Choose a pickup date within the next 30 days. | अगले 30 दिनों में से दवा लेने की कोई तारीख चुनें। | |
| 70 | `message.feedback.link_expired` | This feedback link has expired. | इस फ़ीडबैक लिंक की समय-सीमा खत्म हो गई है। | |
| 71 | `message.feedback.link_used` | Thank you — feedback has already been received for this link. | धन्यवाद — इस लिंक से फ़ीडबैक पहले ही मिल चुका है। | |
| 72 | `message.feedback.form` | Choose an available feedback form. | कोई उपलब्ध फ़ीडबैक फ़ॉर्म चुनें। | |
| 73 | `message.feedback.no_comments` | This form does not take comments. | इस फ़ॉर्म में टिप्पणी नहीं दी जा सकती। | |
| 74 | `message.feedback.contact_phone` | Leave a phone number so that we can call you back. | फ़ोन नंबर दें, ताकि हम आपको वापस कॉल कर सकें। | |
| 75 | `message.feedback.nps_range` | The recommendation score is from 0 to 10. | सिफ़ारिश का अंक 0 से 10 के बीच होना चाहिए। | |
| 76 | `message.feedback.no_nps` | This form does not ask for a recommendation score. | इस फ़ॉर्म में सिफ़ारिश का अंक नहीं पूछा जाता। | |
| 77 | `message.feedback.ratings_on_form` | Rate only the aspects on this form. | केवल इस फ़ॉर्म में दिए गए पहलुओं को रेटिंग दें। | |
| 78 | `message.feedback.ratings_range` | Ratings are from 1 to 5. | रेटिंग 1 से 5 के बीच होती है। | |
| 79 | `message.feedback.ratings_required` | Answer at least one question. | कम से कम एक प्रश्न का उत्तर दें। | |
| 80 | `message.feedback.visit` | Choose one of your visits at this hospital. | इस अस्पताल में अपनी किसी विज़िट को चुनें। | |
| 81 | `message.privacy.too_many_open` | There are already {count} open requests for this patient. Please wait for our replies. | इस रोगी के {count} अनुरोध पहले से खुले हैं। कृपया हमारे जवाब की प्रतीक्षा करें। | |
| 82 | `message.privacy.request_closed` | This request is already closed. | यह अनुरोध पहले ही बंद हो चुका है। | |
| 83 | `message.privacy.request_type` | Choose a correction, an erasure or a concern. | सुधार, डेटा मिटाने या शिकायत में से एक चुनें। | |
| 84 | `message.privacy.for_whom` | Say whether the request is for you or for a family member. | बताएँ कि अनुरोध आपके लिए है या परिवार के किसी सदस्य के लिए। | |
| 85 | `message.privacy.name` | Enter your name. | अपना नाम दर्ज करें। | |
| 86 | `message.privacy.details` | Tell us what you would like us to do. | बताएँ कि आप हमसे क्या करवाना चाहते हैं। | |
| 87 | `message.privacy.changes_only_correction` | Only a correction lists changes. | बदलावों की सूची केवल सुधार के अनुरोध में दी जाती है। | |
| 88 | `message.field.mobile` | Enter a valid 10-digit mobile number. | सही 10 अंकों का मोबाइल नंबर दर्ज करें। | |
| 89 | `message.field.phone` | Enter a valid phone number. | सही फ़ोन नंबर दर्ज करें। | |
| 90 | `message.field.pin_code` | PIN code must be 6 digits. | पिन कोड 6 अंकों का होना चाहिए। | |
| 91 | `message.field.characters` | Contains characters that are not allowed. | इसमें ऐसे अक्षर हैं जिनकी अनुमति नहीं है। | |
| 92 | `message.field.patient` | Choose a registered patient. | कोई पंजीकृत रोगी चुनें। | |
| 93 | `message.field.patient_merged` | This record was merged into another one. Choose the patient's current record. | यह रिकॉर्ड किसी दूसरे रिकॉर्ड में मिला दिया गया है। रोगी का मौजूदा रिकॉर्ड चुनें। | |
| 94 | `message.field.practitioner` | Choose an active doctor. | कोई सक्रिय डॉक्टर चुनें। | |
| 95 | `message.request.check_failed` | We could not complete this request. Please try again. | हम आपका अनुरोध पूरा नहीं कर सके। कृपया फिर से कोशिश करें। | |
| 96 | `message.prescription.dsc_not_signed` | Your doctor has not digitally signed this prescription yet. | आपके डॉक्टर ने अभी इस पर्चे पर डिजिटल हस्ताक्षर नहीं किए हैं। | |
| 97 | `message.prescription.cannot_issue` | This prescription cannot be issued right now. Please contact the hospital. | यह पर्चा अभी जारी नहीं किया जा सकता। कृपया अस्पताल से संपर्क करें। | |
| 98 | `doc.bill.place_of_supply` | Place of supply: {state} | आपूर्ति का स्थान: {state} | |
| 99 | `doc.bill.irn` | IRN | बीजक संदर्भ संख्या (IRN) | |
| 100 | `doc.bill.ack_no` | Ack no. | पावती सं. | |
| 101 | `doc.bill.ack_date` | Ack date | पावती दिनांक | |
| 102 | `doc.bill.einvoice_qr` | Signed QR code of the e-invoice | ई-इनवॉइस का हस्ताक्षरित QR कोड | |

## The rate list (displayed at the counter and in the wards)

The hospital displays its rates in English and the local language (Clinical Establishments Rules 2012, r.9(i)). HC360
prints the list's own words in English with Hindi after each, for a hospital in the states whose official language is Hindi. The services' names in
Hindi are typed by the hospital in its service master and are not in this list. Please check especially "आप देंगे"
(*you pay*), "उपयोग के अनुसार" (*by what is used*, for medicines charged at their MRP) and the notes at the end.

| # | Key | English | Hindi | OK? |
|---|---|---|---|---|
| 1 | `doc.rates.title` | Rate list | सेवा दर सूची | |
| 2 | `doc.rates.as_on` | Rates as on {date} | {date} को लागू दरें | |
| 3 | `doc.rates.registration` | Registration of a new patient | नए रोगी का पंजीकरण | |
| 4 | `doc.rates.consultations` | Consultation fees | परामर्श शुल्क | |
| 5 | `doc.rates.doctor` | Doctor | डॉक्टर | |
| 6 | `doc.rates.department` | Department | विभाग | |
| 7 | `doc.rates.in_person` | In person | अस्पताल में | |
| 8 | `doc.rates.by_video` | By video | वीडियो पर | |
| 9 | `doc.rates.follow_up` | Follow-up | दोबारा दिखाना (फ़ॉलो-अप) | |
| 10 | `doc.rates.follow_up_free` | Free within {days} days | {days} दिन के भीतर निःशुल्क | |
| 11 | `doc.rates.follow_up_charged` | Charged as a consultation | परामर्श शुल्क लगता है | |
| 12 | `doc.rates.rooms` | Rooms (per day) | कमरे (प्रति दिन) | |
| 13 | `doc.rates.room` | Room | कमरा | |
| 14 | `doc.rates.wards` | Wards | वार्ड | |
| 15 | `doc.rates.bed` | Bed | बिस्तर | |
| 16 | `doc.rates.nursing` | Nursing | नर्सिंग | |
| 17 | `doc.rates.visit` | Doctor's visit | डॉक्टर की विज़िट | |
| 18 | `doc.rates.multiplier` | Multiplier | गुणक | |
| 19 | `doc.rates.intensive_care` | Intensive care | गहन चिकित्सा | |
| 20 | `doc.rates.with_gst` | with {percent} GST | {percent} GST सहित | |
| 21 | `doc.rates.code` | Code | कोड | |
| 22 | `doc.rates.service` | Service | सेवा | |
| 23 | `doc.rates.rate` | Rate | दर | |
| 24 | `doc.rates.exempt` | Exempt | छूट प्राप्त | |
| 25 | `doc.rates.payable` | You pay | आप देंगे | |
| 26 | `doc.rates.as_used` | By what is used | उपयोग के अनुसार | |
| 27 | `doc.rates.packages` | Packages | पैकेज | |
| 28 | `doc.rates.package` | Package | पैकेज | |
| 29 | `doc.rates.stay` | Stay | भर्ती अवधि | |
| 30 | `doc.rates.package_stay` | {days} days | {days} दिन | |
| 31 | `doc.rates.package_for` | For | किसके लिए | |
| 32 | `doc.rates.package_use.in_patient` | In-patients | भर्ती रोगी | |
| 33 | `doc.rates.package_use.out_patient` | Out-patients | बाह्य रोगी (ओपीडी) | |
| 34 | `doc.rates.package_use.both` | In-patients and out-patients | भर्ती और बाह्य रोगी | |
| 35 | `doc.rates.category.registration` | Registration | पंजीकरण | |
| 36 | `doc.rates.category.consultation` | Other consultations | अन्य परामर्श | |
| 37 | `doc.rates.category.laboratory` | Laboratory tests | प्रयोगशाला जाँचें | |
| 38 | `doc.rates.category.radiology` | Radiology and imaging | रेडियोलॉजी और इमेजिंग | |
| 39 | `doc.rates.category.cardiology` | Heart tests | हृदय जाँचें | |
| 40 | `doc.rates.category.procedure` | Procedures | प्रक्रियाएँ | |
| 41 | `doc.rates.category.surgery` | Surgery | सर्जरी | |
| 42 | `doc.rates.category.nursing` | Nursing | नर्सिंग | |
| 43 | `doc.rates.category.doctor_visit` | Doctors' visits | डॉक्टर की विज़िट | |
| 44 | `doc.rates.category.health_checkup` | Health check-ups | स्वास्थ्य जाँच | |
| 45 | `doc.rates.category.physiotherapy` | Physiotherapy | फ़िज़ियोथेरेपी | |
| 46 | `doc.rates.category.dialysis` | Dialysis | डायलिसिस | |
| 47 | `doc.rates.category.dietary` | Diet and nutrition | आहार और पोषण | |
| 48 | `doc.rates.category.blood_bank` | Blood bank | ब्लड बैंक | |
| 49 | `doc.rates.category.ambulance` | Ambulance | एम्बुलेंस | |
| 50 | `doc.rates.category.consumable` | Medicines and consumables | दवाएँ और उपभोग्य सामग्री | |
| 51 | `doc.rates.category.miscellaneous` | Other services | अन्य सेवाएँ | |
| 52 | `doc.rates.note.self_pay` | These are the rates for a patient who pays the hospital. A patient covered by insurance, an employer or a government scheme is charged the rates agreed with them. | ये दरें स्वयं भुगतान करने वाले रोगी के लिए हैं। बीमा, नियोक्ता या सरकारी योजना से कवर रोगी से उनके साथ तय दरें ली जाती हैं। | |
| 53 | `doc.rates.note.concession` | The rates include a concession of {percent}. | इन दरों में {percent} की रियायत शामिल है। | |
| 54 | `doc.rates.note.gst` | GST is charged only where it is shown; other services are exempt. | GST केवल वहीं लगता है जहाँ वह दिखाया गया है; बाकी सेवाएँ छूट प्राप्त हैं। | |
| 55 | `doc.rates.note.may_vary` | * The billing counter may set a different price by what is used, such as an implant or a consumable. | * उपयोग की गई चीज़ों (जैसे इम्प्लांट या उपभोग्य सामग्री) के अनुसार बिलिंग काउंटर अलग कीमत तय कर सकता है। | |
| 56 | `doc.rates.note.medicines` | Medicines, consumables and implants are charged at no more than their MRP. | दवाएँ, उपभोग्य सामग्री और इम्प्लांट उनके MRP से अधिक पर नहीं लिए जाते। | |
| 57 | `doc.rates.note.multiplier` | For a patient in a room, the procedures, surgery, nursing and doctors' visits listed below cost their rate times the room's multiplier. The nursing and visit charges in this table already include it. | किसी कमरे में भर्ती रोगी के लिए नीचे दी गई प्रक्रियाएँ, सर्जरी, नर्सिंग और डॉक्टर की विज़िट उनकी दर को उस कमरे के गुणक से गुणा करके लगती हैं। इस तालिका के नर्सिंग और विज़िट शुल्क में यह पहले से शामिल है। | |
| 58 | `doc.rates.note.emergency_fee` | A doctor's own consultation fee, where they have one, is charged in place of the emergency consultation. | डॉक्टर का अपना परामर्श शुल्क हो तो आपातकालीन परामर्श की जगह वही लिया जाता है। | |
| 59 | `doc.rates.note.packages` | A package covers the services its terms include, up to their limits; other services are charged at the rates in this list. | पैकेज में उसकी शर्तों में शामिल सेवाएँ उनकी सीमा तक आती हैं; बाकी सेवाओं पर इस सूची की दरें लगती हैं। | |
