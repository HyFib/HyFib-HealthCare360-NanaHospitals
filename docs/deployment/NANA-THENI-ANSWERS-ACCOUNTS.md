# Accounts and Tally: the hospital's answers of 17 November 2026

**Status: answers recorded, HC360 changed to follow them.** Nana Hospitals, Theni, answered the questions about how
HC360's vouchers should look in its Tally Prime books (N11 to N13, T1 to T21) on 17 November 2026. HyFib confirmed the
decisions taken from them. This record lists every question with its answer, what HC360 now does, and what is still
open, and gives the accountant's steps in Tally before go-live.

The questions are listed by number and subject; their full wording is in the questionnaire sent to the hospital. The
hospital's answers are quoted with their spelling tidied. How the day book is built is in [billing.md, The Tally day book](../modules/billing.md#the-tally-day-book),
and what goes over the wire in [integrations.md, Tally Prime](../modules/integrations.md#tally-prime-int-tally).

## 1. What changes in HC360

| Answer | What HC360 now does | Setting (`billing.tally.ledgers`, per facility) |
|---|---|---|
| Separate voucher types for IP, OP, Pharmacy and Lab, as sales vouchers (N11, N12) | An in-patient bill posts under "IP Sales"; an out-patient bill whose every service is a laboratory test under "Lab Sales"; any other out-patient bill under "OP Sales"; each pharmacy invoice (or the pharmacy's day, where it is kept summarised) under "Pharmacy Sales". The bill number stays the voucher number. Cancellations stay Credit Notes; the canteen stays Sales | `voucherTypes` |
| Patient details before the narration (N12) | A bill, its cancellation, the patient's receipts, refunds, credit notes and write-offs, and pharmacy invoices, returns and balances paid start with the patient: "Ravi Kumar (UHID NHU26000123): Bill NHOB260000001". A VIP, or a patient in a protected category (HIV, mental health, MTP, sexual violence), is named by UHID only; a walk-in pharmacy customer by the name given at the counter | `patientInNarration`: name and UHID (default), UHID only, or none |
| Bill references on supplier and payer ledgers (T14: yes) | The payer's share of a bill is a New Ref of the bill; the bill's cancellation, the insurer's settlement and the claim deduction are Agst Ref the bill. A purchase is a New Ref of the supplier's invoice; a purchase return is Agst Ref that invoice (On Account when the return names no goods receipt); a payable adjustment is On Account | none: always on |
| Cheque or UTR on bank lines (T15: yes) | A line on a bank ledger carries the cheque's number, date and bank, or the transfer's UTR, and its date | `bankLedgers`: by default the ledgers HC360 posts bank money to |
| One voucher per pharmacy invoice (T17) | Each invoice a voucher; each return a Credit Note; each balance paid later a Receipt | `pharmacyPerInvoice` (on); off keeps the daily summary |
| CGST, SGST, IGST, rate of tax and party GST details matching GSTR-2B (T13) | GST goes to a ledger per tax and rate ("Output CGST @ 6%", "Input IGST @ 12%"); a purchase and a B2B pharmacy invoice carry the party's GSTIN, state and place of supply, and a purchase the supplier's invoice number and date | `gstLedgerPerRate` (on) |
| Pharmacy purchases party ledger wise (N11) | Each goods receipt posted into stock is a Purchase voucher from the supplier's ledger, with purchases and input GST by rate | `purchasesFrom` (pharmacy stores), `purchases`, `voucherTypes.purchase` |

All of it is set on the Tally screen's **Ledgers and voucher types** tab (Billing › Tally export) by someone who holds
`billing.tally.configure`. Posting a day again sends its vouchers as they are now.

## 2. Every question and its answer

| # | Subject | The hospital's answer | What HC360 does, or why it is open |
|---|---|---|---|
| N11 | The Tally company's ledger list (List of Accounts export) for each facility, to map HC360's ledgers to Tally's | Instead of the list, what the books need: "IP, OP, Pharmacy & labs as sales voucher"; "Mode of payment like cash or bank ledger needs to be fetched"; "Purchase entry for pharmacy needs to be fetched party ledger wise"; and (N11.4) "Expenses booked in software needs to be fetched in software on ledger wise" | Sales vouchers under the four voucher types (section 1). Money already posts to the cash or bank ledger of its payment mode, and bank lines now carry the cheque or UTR (T15). Goods receipts are Purchase vouchers from each supplier's ledger. **Expenses (N11.4): a gap.** HC360 has no expense booking; by HyFib's decision this is recorded as a gap, and expenses stay entered in Tally. **The ledger list itself was not sent** (section 5) |
| N12 | The accountant's decision on T12: patient bills on a receivables ledger for each payer type, with the bill number and no patient names in Tally | "Separate voucher type needs to be made for IP, OP, Pharmacy and Lab"; "Bill no needs to be mentioned in voucher no."; "patient details can be fetched before narration" | Done (section 1). A bill's voucher number was already its bill number. The patient's name and UHID come first, by the hospital's decision of 17 November 2026; protected and VIP patients by UHID only. Patients' bills stay on the patient debtors and payer ledgers |
| N13 | The switch-over date in T21, and who in accounts compares the first month's day book and GST ledgers in Tally with HC360's | Not answered | Open: ask again |
| T1 | The connector: one voucher or a list per response, and how it pages | "List of vouchers, by count" | A requirement for the Tally partner's connector (section 4). HC360 today pushes vouchers to Tally's XML port in batches of `batchSize` |
| T2 | The connector's request: a POST with a JSON body or a GET with the dates in the URL; whether fromDate and toDate are inclusive and in Indian time | Not answered | Open: ask again |
| T3 | The connector: "College" as the facility's code, one Tally company per facility | "Yes" | A requirement for the connector. HC360's Tally settings (`integrations.tally`, with the company's name) are per facility, and its own voucher numbers carry the facility's code, so facilities can share a company or have one each |
| T4 | The connector: voucher types per call, and whether "Collection" means receipts | "Multiple voucher type, yes": several voucher types per call, and "Collection" means receipts | A requirement for the connector. HC360's Receipts are what "Collection" asks for |
| T5 | The connector: decimals in amounts, and whether a rounding difference is allowed | "2 decimals, yes rounding allowed" | Already so: every amount is sent with two decimals, every voucher balances to the paisa, and a document's rounding goes to the round-off ledger |
| T6 | The connector: fetching the same dates twice, and altering a voucher it holds instead of importing it again | "In case of changes, confirm before the alteration" | A requirement for the connector. Today a day posted again alters the vouchers Tally holds (same REMOTEID) without asking: the post is started by staff, by the nightly job, or by HC360 itself when an online payment or a settlement is recorded for a day already sent (`TallyCorrections`). Whether HC360's own post should ask first too is open (section 5) |
| T7 | The connector: a voucher changed in HC360 after it was posted: served again with the same id, or a "changed since" query | "Yes" | Read as the first: serve it again with the same id (to confirm, as the question offered two ways). Already so for the XML post: a voucher keeps its REMOTEID (`HC360-<reference>`), and the day's records show it "changed since sent" until it is sent again. A requirement for the connector too |
| T8 | The connector: deleting or cancelling a voucher it imported, or a new voucher for every reversal | "Should not delete or cancel without approval" | Already so: HC360 never deletes or cancels in Tally. A voucher that leaves the day book is listed "No longer in HC360" for the accountant to delete. A requirement for the connector |
| T9 | Whether the connector tells HC360 which vouchers it imported and which it rejected, and why, and how | Not answered | Open: ask again |
| T10 | The formats of Credit Note, Debit Note and Journal vouchers | "Format enclosed" | Open: the enclosed formats were not received. They are needed for per-line HSN details (T13) and to match the hospital's layouts |
| T11 | The connector: ledgers missing in Tally; and the company's ledger list (List of Accounts export) | "Connector create the missing ledger after approval" | A requirement for the connector. Today HC360 creates no ledgers: Tally refuses a voucher whose ledger is missing, and the day's message names the voucher. The ledger list was not sent (section 5) |
| T12 | Whether Party Name must be a ledger in Tally; and whether patient bills on a receivables ledger per payer type, with the bill number and no patient names, are acceptable | No answer under T12; the accountant answered it through N12 | Answered by N12: the bill number as the voucher number, and the patient's details before the narration. Whether the partner's JSON needs Party Name to be a ledger: not answered, a question for the connector |
| T13 | GST: one GST line or CGST, SGST and IGST ledgers per rate; HSN, taxable value and rate per line for purchases (GSTR-2B); Party State as a name or a code | "Details like CGST, SGST, and IGST, HSN details, rate of tax and Party GST details which should match its GSTR2B" | GST by tax and rate, party GSTIN, state and place of supply, the supplier's invoice number and date: done (section 1). The XML names the state, as Tally needs; how the partner's JSON wants it was not answered. **HSN per line: open.** HC360 sends no per-line HSN details until the formats enclosed with T10 show where the hospital wants them |
| T14 | Bill references on supplier and payer ledgers (New Ref / Agst Ref), so outstandings show bill by bill | "Yes" | Done (section 1). Bill-by-bill must be on for those ledgers in Tally (section 3) |
| T15 | The cheque or UTR number and its date on a bank ledger's receipt or payment line | "Yes" | Done (section 1). Only ledgers listed as bank ledgers carry them; the cheque ledger ("Cheques in Hand") only when it is listed |
| T16 | Whether the connector's JSON can carry cost centres (HC360 posts each bill's department as one), and in which field | Not answered | Open: ask again. The XML post keeps sending them |
| T17 | The pharmacy's sales: one voucher per invoice, or the daily summary per GST rate | "One voucher per invoice" | Done (section 1). The daily summary per GST rate stays an option |
| T18 | An API key in a header over HTTPS, tied to one facility, instead of a username and password in the URL | Not answered | Open: ask again |
| T19 | Whether the PC that runs the connector can trust the hospital's own certificate authority | Not answered | Open: ask again |
| T20 | A test Tally company or a test run of the connector, to put a full month of vouchers through before go-live | Not answered | Open: ask again |
| T21 | The switch-over date, when HC360 stops posting XML to Tally so no voucher is posted twice | Not answered | Open: ask again |

## 3. The accountant's steps in Tally before go-live

Tally refuses a voucher whose voucher type or ledger it does not have, and the day's message names it. Before the first
post (or the first file import), in each Tally company HC360 posts to:

1. **Create the voucher types.** "IP Sales", "OP Sales", "Lab Sales" and "Pharmacy Sales", each with *Type of voucher*
   Sales. Purchases post under "Purchase", Tally's own Purchase voucher type; a facility that names another in HC360
   creates it with *Type of voucher* Purchase. Set *Method of voucher numbering* to Manual, so Tally keeps HC360's numbers
   (the bill, invoice and goods receipt numbers). Use the names exactly as on the Ledgers and voucher types tab.
2. **Create the GST ledgers per rate** under Duties & Taxes, with their GST type and rate: "Output CGST @ 2.5%", "@ 6%",
   "@ 9%" and "@ 14%" and the same for Output SGST; "Output IGST @ 5%", "@ 12%", "@ 18%" and "@ 28%"; and the Input
   CGST, SGST and IGST ledgers at the same rates. Only the rates the hospital's services and medicines carry are needed;
   the names follow the tab's CGST, SGST and IGST names. Keep the single ledgers ("Output CGST" and the rest) for
   documents without a rate.
3. **Create the purchase ledgers** under Purchase Accounts: "Purchase Accounts @ 5%", "@ 12%" and "@ 18%" (or the
   one "Purchase Accounts" with per-rate ledgers off), and "Purchase Returns".
4. **Create the suppliers' ledgers** under Sundry Creditors, named as in HC360's vendor master, with their GSTIN and
   state; and the insurers' and corporates' ledgers under Sundry Debtors, named as in HC360's payer master.
5. **Turn on bill-by-bill** (*Maintain balances bill-by-bill*: Yes) on every payer and supplier ledger, and on
   "Insurance & Corporate Receivables" and "Sundry Creditors" if the hospital posts to those instead.
6. **Check the bank-ledger list** on the Ledgers and voucher types tab: it must name exactly the ledgers that are under
   Bank Accounts in Tally. By default it is the ledgers HC360 posts bank money to (the net banking and insurers' ledgers,
   the doctors' payout bank, and the banks the gateway and the card acquirer pay into). Add "Cheques in Hand" only if it
   is a bank account in Tally.
7. **Post one day and check it** (Billing › Tally export: post, then *Check against Tally*), and look at a bill, a
   pharmacy invoice and a goods receipt in Tally: voucher type, narration, bill-wise details, GST ledgers and party
   details.

## 4. Requirements for the Tally partner's connector

The hospital's Tally partner would pull HC360's vouchers as JSON through a connector of its own. Its format and a
sample JSON have not arrived, so the connector is not built. The answers recorded for it:

| # | Requirement |
|---|---|
| T1 | List the vouchers a page at a time, by count |
| T3 | One Tally company per facility code |
| T4 | Ask for several voucher types in one call; "Collection" means receipts |
| T5 | Amounts with two decimals; rounding is allowed |
| T6 | Confirm before altering a voucher Tally holds |
| T7 | Serve a voucher changed in HC360 again, with the same id |
| T8 | Never delete or cancel a voucher without approval |
| T11 | Create a ledger missing in Tally, after approval |

## 5. Still open

1. **Unanswered:** T2, T9, T16, T18, T19, T20, T21 and N13; T12's question whether Party Name must be a ledger; and
   T13's whether the JSON names Party State or gives its code.
2. **Not received:** the formats enclosed with T10, and the Tally partner's sample JSON. They are needed for HSN details
   per line (T13) and for the partner's connector (section 4). Nor the Tally company's ledger list (List of Accounts
   export) asked for in N11 and T11: without it, the names on the Ledgers and voucher types tab are HC360's defaults, and
   the accountant checks each against Tally before the first post (section 3).
3. **Confirm before altering (T6) for HC360's own post.** The hospital asked it of the connector. HC360's XML post alters
   the vouchers of a day sent again without asking, including when HC360 sends a day again itself for a late online
   payment or settlement. To ask the accountant whether that is acceptable until the connector takes over.
4. **T7's "Yes"** answered a question that offered two ways; it is read as "serve a changed voucher again with the same
   id". To confirm with the Tally partner.
5. **Expenses booked in HC360 (N11.4):** HC360 has no expense booking. Recorded as a gap by HyFib's decision; expenses
   stay entered in Tally.
6. **Bills to registered insurers and corporates** carry no party GST details in Tally yet (only B2B pharmacy sales and
   purchases do, as asked); their e-invoices carry them. To confirm whether the hospital's GSTR-1 work needs them on the
   sales vouchers too.

## 6. Things to know

- **Goods receipts are dated by the day the goods were received**, as GSTR-3B takes their input credit. A receipt posted
  into stock after that day joins that day's day book: post that day again.
- **Pharmacy per invoice or summarised.** A facility that switches after days were posted sees the other way's vouchers
  on those days as "No longer in HC360", to delete in Tally, and posts the days again. HC360 was not yet live when the
  per-invoice way came, so no day had been posted as a summary.
- **Patients' names in Tally.** The hospital's accounts now hold patients' names and UHIDs (never a protected or VIP
  patient's name). The patient's data summary says so, under bills and payments.
- **Changing a setting** (voucher types, narrations, bank ledgers, GST per rate) changes the vouchers HC360 sends: days
  posted before show them as changed since sent until they are posted again.
