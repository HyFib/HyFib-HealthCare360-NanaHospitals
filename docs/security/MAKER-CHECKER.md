# Maker-checker: the risk inventory

Which writes can harm a patient, lose money, or hide either, and whether a second person stands between one user and that
harm. SECURITY.md (Maker-checker) describes each control that is built; this page is the list of what is still done on
one person's word, in the order it is being closed. A slice's status says when its control is in force.

**How the list was made (7 November 2026).** Every write endpoint (`MapPost`, `MapPut`, `MapPatch`, `MapDelete`) in the
server, 840 in 19 modules and the Platform, and every background job that moves money, stock or records, was opened and
read for a second-person check in the code: an approval request (`platform.approval_requests`), a refusal of the maker
(`approval.self` or a module's own check), a co-signature or witness (ADR-014), or a verification by another user.
Routes and names were not taken on trust. A write is listed when it is high-risk (money leaving or not arriving, stock
lost, pay redirected, a patient's identity or safety record changed, a statutory register altered, a control switched
off) and goes through on one person's word. Writes that are routine, reversible, audited, or a professional's own
signed act (a doctor signing their own note) are not listed.

**What is already covered** (SECURITY.md): credit notes and write-offs, receipt and refund reversals, card and UPI
import withdrawal, bill cancellation, pharmacy invoice cancellations, returns paid out or written off and ward returns
not restocked, a discount above anyone's own limit (an approver's included) and an own-authority limit raised, a charge
cancelled at the billing desk above the hospital's amount (and every OT, procedure, implant or bed-day charge), a cash
shortage at a shift's close, every price master (services, a doctor's consultation fee, tariff plans, payer terms,
packages, check-ups, the PM-JAY HBP master, doctor-fee rules, the non-payable list), the plan a stay is billed on, a
third party paying on cover verified by a second person, goods receipts, purchase requisitions and orders, stock
adjustments and expiry write-offs, asset condemnation, drug class changes, an item's NPPA ceiling price raised or removed and its GST rate
changed (a scheduled medicine not received or sold above its ceiling), user and role grants, patient merge, lab
corrections where a separate verifier is required, death certificates, high-alert drug and transfusion bedside
co-signatures, blood released from quarantine, issued, and released uncrossmatched, NDPS witnesses, payroll runs, leave
approval, audit-trail retention and purge, a patient's mobile number changed only with a code sent to the new number, a
staff password reset sent to the account's own mobile number and e-mail address, two-step verification resets, another
user's contact details, an account linked to a practitioner, a linked doctor's registration and signature, another
administration desktop enrolled, a ward medication order written in a doctor's name by anyone else (countersigned by
that doctor) and a nurse's stop of one, a dose with no order, a refill's safety override and a controlled drug's
refill, a prescriber's safety override acknowledged by a pharmacist before the line is dispensed, the
prescribing-safety tables loosened, and the patient's record: an existing patient's identity, a VIP
restriction lifted, the deceased status set or taken back at the desk, a sensitive category removed, DNR taken off (and
put on only by a doctor), an allergy taken out of the checks or lowered, and a stay's medico-legal flag cleared; and a
stay closed as expired (only with a confirmed death certificate, or a second doctor's approval pending post-mortem) or
absconded (the nurse in charge approves), LAMA and DAMA witnessed (on the ward and from the emergency department), and a
confirmed death certificate voided; a visit cancelled after the doctor started it; and in the theatre a side or
procedure changed after the WHO Sign-in (the checklist redone), site marking with a side booked, the swab and
instrument counts witnessed with their discrepancies settled, and implants corrected, not deleted, after the operation
starts; and a sentinel or never event closed (on its completed analysis) or re-graded out of sentinel, a data breach
closed (on its Board report) or patients taken off its notification list, a bulk campaign scheduled, a family link
confirmed by a code to the patient's own number, and a whole-record sharing consent with no signed form witnessed.

## Still on one person's word, by slice

| Slice | What one person can do today | Where | The control | Status |
|---|---|---|---|---|
| 1 | Switch off a control (second pathologist, second doctor on death certificates, desktop-only administration, payment before sample collection, cashier shifts), raise a discount or credit limit, lengthen a grace or lock time, or redirect payment keys, SMS/WhatsApp/ABDM/AI providers, the portal address or alert contacts | `PUT /api/v1/admin/settings/{key}` | The looser direction waits for a second administrator; the stricter applies at once; secrets stay encrypted while they wait | In force (`settings.change`) |
| 2 | Cancel a same-day pharmacy invoice or take a return and pay the refund out (a card or UPI sale refunded in cash), with no limit and no rule against cancelling one's own sale; credit a stay for ward returns without restocking (a controlled drug then leaves no register entry) | Pharmacy `SaleAdjustmentService`, `PharmacySale.Cancel`, `InpatientPharmacyService.ReturnAsync` | A refund in the mode it was paid; whoever sold the invoice or took its payment neither asks nor approves; a second person who approves refunds approves money paid out and stock written off; a ward return not restocked approved by a second pharmacist, a controlled drug entered in the register as returned and written off | In force (`pharmacy.sale_cancel`, `pharmacy.sale_return`, `pharmacy.ward_return`) |
| 3 | Release donated blood from quarantine on one officer's screening results and grouping; group, crossmatch and issue the same unit alone (grouping and crossmatch record a typed name, not a user); release uncrossmatched blood on a doctor's name typed by the issuer | SpecialtyCare `DonorService`, `BloodComponent`, `TransfusionService`, `BloodRequest` | Results that clear a donation leave its units in quarantine until another blood bank officer (`bloodbank.inventory.manage`) verifies them, and only the one who entered them corrects them before that; a reactive result discards at once. Grouping and crossmatch record the officer's account, and a unit is not issued by the officer who both grouped the patient and cross-matched it (plasma: who grouped). A grouping that disagrees with the patient's earlier requests holds the issue until another officer groups a new sample. Uncrossmatched release is authorised by a doctor from their own sign-in. A facility with one officer on duty sets `bloodbank.separateVerifier` off (a second administrator approves): the officer then verifies and issues alone, audited at Warning and listed for the medical officer's review | In force (`bloodbank.verify_own_results`, `bloodbank.issue_own_checks`, `bloodbank.grouping_history_discrepancy`, `bloodbank.doctor_required`) |
| 4 | Reset another user's password (and be shown it), reset their MFA, change their e-mail, then sign in as them; link an account to another doctor's practitioner record or signature; enrol any PC as an administration desktop | Platform `AccessAdminEndpoints`, `OrganizationEndpoints`, `DeviceEndpoints` | A temporary password goes to the account's own mobile number and e-mail address, and the administrator is told only where; an account with neither waits for a second administrator, who is shown it once to hand over. A two-step verification reset, another user's e-mail address or mobile number (the old ones told once it changes), an account linked or re-linked to a practitioner (a new account's included), a linked practitioner's registration number, HPR ID or signature, and a workstation or tablet code once one is enrolled (shown once, after approval, to the one who asked) wait for a second administrator who holds `admin.grants.approve`. Decided as access given is (a hospital setting up with one approver makes them at once, audited); made only over the account, practitioner or request as asked (`approval.stale`); nobody approves a change to their own account | In force (`identity.password_reset`, `identity.mfa_reset`, `identity.contact_change`, `identity.practitioner_link`, `masters.practitioner_identity`, `devices.enrollment`) |
| 5 | Write a ward medication order in any doctor's name, chemotherapy and high-alert drugs included, with no countersignature; stop a doctor's order; clear the controlled and high-alert flags by typing a drug name; give a dose with no order and no witness; refill a controlled-drug prescription; override a blocking alert in the doctor's name; switch off drug interactions or raise maximum doses for the whole hospital | Inpatient `MarService`; Clinical `PrescriptionService`, `DrugSafetyEndpoints` | Only a prescriber writing in their own name signs a ward order at once; anyone else writes the doctor's verbal or telephone order, read back, and only that doctor countersigns it from their own account within the hospital's time (`inpatient.verbalOrderCountersignHours`, 24 hours; lengthening it waits for a second administrator). A high-alert, controlled or chemotherapy verbal order gives no dose until then, unless taken as an emergency order (each dose witnessed, flagged until countersigned). Past its time an order is flagged, listed for the ward and the doctor, and the doctor and the nurses in charge (`nursing.in_charge`) are told. A medicine typed in is high-alert; a dose with no order is always witnessed. A nurse's stop holds the doses until the doctor confirms or declines it. A refill names who refilled it; a controlled-drug prescription is not refilled; a blocking alert at refill is overridden only by the doctor who wrote it, and goes back to them. Loosening the drug-safety tables waits for the pharmacy and therapeutics committee (`clinical.drug_safety.approve`), never the one who asked, over the row as asked; tightening applies at once | In force (`medication.verbal_order_required`, `medication.countersign_not_prescriber`, `medication.awaiting_countersignature`, `medication.stop_pending`, `prescription.refill_controlled`, `prescription.refill_needs_prescriber`, `clinical.drug_interaction`, `clinical.dose_limit`) |
| 6 | Discount any bill up to 100% while holding the approval permission (recorded as one's own approver); raise a cashier's own-authority limit to 100%; cancel any unbilled charge (OT, lab, bed) with only a reason; settle a cash shortage on the cashier's own word; set a doctor's consultation fee that overrides the approved service price | Billing `DiscountService`, `ChargeService`, `ShiftService`; Platform practitioner fee and `PriceResolver` | Everyone, an approver included, discounts on their own only up to their own limit; above it another approver decides and is recorded as the approver. Raising anyone's own-authority limit (a higher percentage, a cap raised or taken away, a limit removed so a higher default applies) waits for another holder of `billing.discounts.approve`, never the one who asked nor the user whose limit it is; lowering is at once. An OT, procedure, implant or bed-day charge cancelled at the desk, and any other above `billing.chargeCancel.approvalAbove` (₹1,000; raising it waits for a second administrator), waits for someone else who approves discounts; charges a module cancels itself (an order withdrawn, a visit or stay cancelled) are cancelled at once. A shift closed short beyond `billing.shift.shortageTolerance` (₹0) is "closed — shortage awaiting supervisor", in the shift list, the day-end report and the Tally records check, until a holder of `billing.refunds.approve` who is not its cashier accepts it with the cashier's reason; an excess is recorded and flagged and needs no one. A doctor's consultation fee waits for someone who approves prices (never the doctor), the fee in force charged until then (a new doctor's: none, so the service price) | In force (`billing.discount_self_approval`, `billing.discount_limit`, `billing.discount_limit_own`, `billing.charge_cancel`, `billing.shift_shortage`, `billing.shift_shortage_own`, `masters.practitioner_fee`, `masters.practitioner_fee_own`) |
| 7 | Rewrite a patient's name, date of birth, sex, ABHA or Aadhaar; lift a VIP restriction; mark a patient deceased or undo it; remove an HIV, mental-health, MTP or sexual-violence category; put on or take off Do Not Resuscitate; deactivate an allergy or lower its severity; clear the medico-legal flag (which also ends free treatment for sexual-assault and acid-attack victims) | Patients `PatientRegistrationService`, `PatientEndpoints`; Clinical `FlagEndpoints`; Inpatient `AdmissionService` | An existing patient's identity (name, title, sex, date of birth or estimated age, ABHA number or address, Aadhaar) waits for someone else who holds `patients.identity.approve` (medical records officer, hospital administrator), the rest of the edit saved at once; only the desk that registered the patient corrects it at once, within 24 hours and before any visit has begun. A VIP restriction is added at once and lifted only by someone else who opens restricted records. The deceased status set or taken back from the registration screen waits (`patients.identity.approve`); a certified death is taken back only by voiding its certificate. A sensitive category is added at once and removed only by another doctor who manages them. DNR is put on at once by a doctor only (never a nurse, never with an end date) and taken off only when a second doctor agrees. An active allergy taken out of the checks or lowered in severity waits for a prescriber who did not ask; recorded or made stricter at once. A stay's MLC flag is set at once; cleared or unlinked only by someone who keeps the MLC register, free treatment kept until then. The old value stays in force everywhere while a change waits, and the record says so | In force (`patients.identity`, `patients.restriction_lift`, `patients.deceased`, `patient.death_certified`, `patients.sensitive_remove`, `alert.dnr_doctor_only`, `clinical.dnr_lift`, `clinical.allergy_change`, `inpatient.mlc_clear`) |
| 8 | Close a stay as Absconded or Expired, skipping billing clearance and with no death certificate; discharge DAMA or LAMA on free text; void a death certificate a second doctor confirmed (which clears the deceased status); LAMA from the emergency department without a witness | Inpatient `DischargeService`; Clinical `VitalRegistrationService`, `EmergencyService` | A stay closes as expired only with the patient's death certificate a second doctor confirmed (the ward is told why it waits); a medico-legal death whose certificate waits for the post-mortem only when a second doctor who confirms certificates approves. An absconded stay waits for the nurse in charge (`nursing.in_charge`), with when the patient was last seen, who searched and who was told (the police, for a medico-legal stay). Both still skip billing clearance, and the dues are recorded on the stay. LAMA and DAMA on the ward, and LAMA from the emergency department, record who signed (or "refused to sign"), the doctor who explained the risks from their own sign-in or confirmation, and a witness, another member of staff confirming with their own credentials (ADR-014), never the doctor; a scanned form can go with it. A confirmed death certificate is voided only when another doctor who confirms certificates agrees, the certificate and the deceased status standing until then; one reported to the Registrar is not voided here | In force (`discharge.death_certificate_required`, `discharge.death_certificate_unconfirmed`, `inpatient.expired_pending_postmortem`, `inpatient.absconded`, `discharge.police_intimation_required`, `against_advice.doctor_required`, `against_advice.witness_is_doctor`, `er.lama_witness_required`, `mrd.death_certificate_void`, `death.void_waiting`) |
| 9 | Cancel a pregnancy ultrasound after the scan, closing its Form F as cancelled (PCPNDT); record the second doctor's MTP opinion by typing it; release a body from the mortuary typing the police clearance | Diagnostics `ImagingService`, `FormF`; SpecialtyCare `MtpService`; SupportServices `Mortuary` | Cancellation only before the scan; the second registered medical practitioner confirms in the system (MTP Act 2021: 20–24 weeks); body release witnessed | Open |
| 10 | Change the operation side after Sign-in and Time-out without redoing the checklist; mark "site marked" not applicable when a side is booked; tick the swab and instrument count with no record of a discrepancy; delete an implant record after the operation | SpecialtyCare `SurgeryService`, `SurgeryBooking`, `SurgicalSafetyChecklist`, `OtClinicalService` | After the WHO Sign-in only the operating surgeon or the anaesthetist changes the side, level or procedure, from their own sign-in, with a reason: the Sign-in and Time-out done are voided and redone, the site marked again, the change audited at Warning and the team told; after the incision it is refused. With a side or level booked, site marking is never not applicable, and the pre-op mark is recorded by a surgeon from their own sign-in. Swab, needle and instrument counts (initial, added, final) are each made by one member of the team and witnessed by a second at the same screen; a final count that does not tally holds the Sign-out until it is settled (found, an X-ray with its result, or a retained item raised in the Quality register as a never event). Once the operation has started an implant or consumable line is not removed: a correction keeps the original, waits for the OT in-charge (never the one who asked), and its charge and stock follow | In force (`surgery.site_change_team`, `surgery.site_after_incision`, `checklist.site_marking_required`, `checklist.site_marked_by_surgeon`, `surgery.count_discrepancy_open`, `surgery.counts_missing`, `surgery.material_locked`, `ot.material_correction`) |
| 11 | Point an employee's salary at another bank account or switch them to cash (the bank file reads accounts at export, after the run was approved); revise one's own salary structure, add earnings, override loss of pay or TDS, delete another module's deductions; credit one's own leave balance | HR `EmployeeService`, `PayrollInputService`, `LeaveService`, `PayrollService` | Bank details and pay inputs approved by someone other than who made them, never one's own; the bank file uses the accounts in force at approval | Open |
| 12 | Settle an insurance claim with deductions, or reject it, writing off what the insurer owes; record the pre-authorised amount that moves the bill from patient to insurer | Billing `ClaimService`, `PayerCoverageService` | Write-offs above a threshold approved by a second person; pre-authorised amounts checked by a second insurance desk user | Open |
| 13 | Raise, approve and issue one's own indent (stock leaves as consumption); receive a transfer one dispatched; release a recalled batch. (Set an implant's or a medicine's ceiling price or an item's GST rate: in force) | SupplyChain `Indents`, `ItemEndpoints`, `Transfers`, `StockEndpoints` | Never approve or receive one's own; unblocking a recalled batch approved by a pharmacist. **In force:** an item's NPPA ceiling price raised or removed, or its GST rate changed, waits for someone else who approves prices (`masters.prices.approve`), never the one who asked, over the item's pricing as asked (`approval.stale`); a ceiling set or lowered applies at once. A ceiling also applies to scheduled medicines (DPCO 2013): a pack with an MRP above it is not received (checked again when posted), and a batch above it is not dispensed, sold or issued | In part (`inventory.item_price`, `grn.mrp_above_ceiling`, `pharmacy.price_above_ceiling`); indents, transfers and recalled batches open |
| 14 | Close a sentinel event or never event straight from Reported, or downgrade it so no root-cause analysis is due; close a data breach without the Board report, or take patients off its notification list unaudited; schedule one's own bulk campaign; share a patient's reports and bills with any phone number; record an unscoped record-sharing consent with no signed form | Quality `IncidentService`; Privacy `BreachService`; Engagement `CampaignService`; Patients `PortalFamilyService`, front office consents | A sentinel or never event closes only on its completed root-cause analysis (from Reported too); closing it, and re-grading it out of sentinel with a reason, wait for the quality manager (`quality.incidents.approve`), never the one who asked nor its reporter. A breach closes only on its detailed report to the Board recorded with the reference and day; closing it, and taking patients off its notification list with why, wait for the Data Protection Officer (`privacy.breaches.approve`), audited with each patient taken off. A campaign is scheduled only when someone other than its author approves (`crm.campaigns.approve`): not the asker, nor who wrote or last edited it. A family link at the desk waits for a code sent to the patient's own registered number (the phone-change mechanism), entered as the patient reads it out; no link until then. A consent sharing the whole record with no signed form is witnessed at the screen (ADR-014, `consent.record_sharing`) | In force (`incident.sentinel_needs_rca`, `incident.reporter_approves`, `quality.sentinel_close`, `quality.sentinel_downgrade`, `privacy.breach_board_report_required`, `privacy.breach_close`, `privacy.breach_affected_remove`, `privacy.breach_already_notified`, `engagement.campaign_schedule`, `campaign.author_approves`, `portal.family_no_patient_phone`, `portal.family_number_changed`, `consent.witness_required`) |
| 15 | Override a lab quality-control failure for the day; change critical limits or switch on automatic verification; downgrade a transfusion reaction (skipping haemovigilance reporting); change a dialysis patient's infection status; cancel an admission after days of care; acknowledge another doctor's critical result; sign a discharge summary as any doctor; amend another doctor's signed encounter; waive an ambulance trip charge | Diagnostics, SpecialtyCare, Inpatient, Clinical, SupportServices | Each a threshold or a second person as the hospital's policy decides; listed for the decision | Open |
| 15 | Cancel a visit after the doctor started it (the consultation under way, or a note, order or prescription on it) | Patients `VisitService` (`POST /api/v1/visits/{id}/cancel`) | The desk asks with a reason; a front-office supervisor or the treating doctor approves, never the asker; the token, charges and orders follow only once approved | In force (`patients.visit_cancel`) |

## Not covered (decided, not built)

- **Two-step verification for doctors and nurses.** Only the SuperAdmin, HospitalAdmin, Accountant and HrManager roles,
  and anyone holding an administrative permission, must use it; a doctor's or nurse's account signs in with the password
  alone, which is why a reset's temporary password goes to the account's own phone (slice 4). Requiring it of them is a
  policy setting for the hospital (`HealthCare360:Security:MfaRequiredRoles`), not a control to build.
- **One's own contact details** change at once from Administration › Users, with the old ones told: there is no
  self-service screen for them, and none was added.
- **Where an approval is decided.** The approvals inbox answers any signed-in session, a browser included, for every
  kind; an administrator who approves a temporary password hand-over may be shown it away from an enrolled desktop.
- **Slice 5 leaves these as they are.** An e-prescription line typed in by the doctor keeps the controlled flag the
  doctor confirmed when signing (only ward orders typed in are made high-alert). The ward's nurses in charge are told
  facility-wide: HC360 does not know which nurse is in charge of which ward, so the notice goes to everyone holding
  `nursing.in_charge` in the facility (the hospital administrator by default), and the ward's own list filters by ward.
  The countersign time is fixed on each order when it is written: changing the setting changes it for orders written
  after. A refill's printed copy still shows the doctor and their registration only; who refilled it is on the record,
  the screen and the audit trail. Any doctor still keeps the drug-safety tables (`clinical.templates.manage`) and so
  tightens them alone. The pharmacy's dispensing queue now shows a prescriber's safety override (the clinical report's
  item 25): the line waits until a pharmacist acknowledges it from their own sign-in (`pharmacy.dispense`), never the
  account that signed the prescription, audited with the alert and the reason (`pharmacy.override_unacknowledged`,
  `pharmacy.override_own_prescription`). An issue against a ward indent is not tied to a prescription line and carries
  no override; the ward's order, and its witnessed doses, do.

- **Slice 13 (in part) leaves these as they are.** A GST rate changed waits whichever way it goes: a lower rate under-
  declares tax and a higher one overstates the GST share of a fixed MRP, so neither is the stricter direction. A save
  that loosens both an item's class and its pricing asks for both; the answer names the classification's request. A
  cold-chain unit's allowed range is changed at once by whoever manages cold chain, audited at Warning with the range it
  replaced, not approved (it follows the manufacturer's label and rarely changes).

- **Slice 6 leaves these as they are.** A bill is still finalised on the finaliser's own limit: a discount given within
  a cashier's limit, and not approved by anyone, is refused at finalisation by a colleague whose own limit is lower (an
  approver's included, now that approvers have limits), who asks for approval instead; a discount approved by a second
  person finalises for anyone. A charge whose cancellation waits can still be billed meanwhile: the approval then fails
  (`approval.stale`) and a credit note is asked for instead; a final bill is not held while a cancellation waits. A charge
  posted by the desk itself (`billing.manual`) waits like any other of its kind and amount. Removing a line from a draft
  bill or discarding a draft still releases a clinical charge to the unbilled pool, and cancels at once a line that exists
  on the draft alone (an ad hoc line typed on it, an OP bill's package line), as before. A supervisor may still
  close another cashier's shift and declare the cash for them (the count is not witnessed by the cashier); its shortage
  then waits for a third person. A shortage accepted is not written off or posted: the day book still has no voucher for
  it, as before. The practitioner's fee for a follow-up and for a new visit wait together as one request; a doctor who
  approves prices is refused only for their own fee, by the account linked to their practitioner record. Insurance claim
  deductions and pre-authorised amounts stay open (slice 12).

- **Slice 7 leaves these as they are.** The identity correction window belongs to the user who entered the record, not
  to a desk or counter (HC360 does not know which desk a workstation is); "seen" means a visit begun, completed or an
  admission, so an order placed on a visit still open does not close the window. Linking an ABHA number to an existing
  patient past the window from a verified ABDM profile share waits like any other identity change; a number linked by
  ABDM's own verification (Aadhaar fingerprint or ABHA code, `patients.identity.verify`) is unchanged. The ABHA number
  waiting in a request keeps the encryption key it was asked under; a key rotated meanwhile still reads it. Deactivating
  a patient's record (`isActive`) and the rest of the registration form are made at once, as before. Voiding a death
  certificate a second doctor confirmed, Absconded and Expired discharges, and DAMA/LAMA were left to slice 8 (now in
  force). An allergy
  entered in error by the clinician who recorded it a moment ago still waits for a prescriber; DNR's own detail (who
  decided, the consent form) is free text, not a signed consent. A sensitive category removed waits for any other doctor
  who manages them, not a named second opinion. Changing the MLC number of a stay from none to one, and marking an
  out-patient visit medico-legal, are made at once; the ER MLC register itself is unchanged (its facts lock once the
  police are told).

- **Slice 8 leaves these as they are.** Expired is still closed by anyone who completes discharges (a nurse included)
  once the certificate is confirmed: the two doctors are on the certificate, not on the discharge. The certificate a
  stay is closed with is the patient's one standing certificate (a certificate naming no stay counts, and is not
  linked back to it). A death certificate voided after the stay was closed with it leaves the stay closed as expired
  (the certificate and the stay are corrected separately). Pending post-mortem, the patient is recorded deceased only
  when the second doctor approves, so the mortuary receives the body after that, not before. Absconded and the
  post-mortem path bill the remaining bed days with the approval, and the bed board learns of the closing when it next
  refreshes (no live update from an approval). An ER patient who absconds, or dies, is recorded by the doctor alone, as
  before; only ER LAMA is witnessed. The doctor who explained the risks is confirmed as a user who signs encounters, not
  matched to the stay's consultant. Normal and Referred discharges completed by a non-doctor still need no signed
  discharge summary (the clinical report's item 17, slice 15). The scanned LAMA/DAMA form is optional, and the paper
  form stays the signed original.
- **Slice 10 leaves these as they are.** The second look at a side changed after the Sign-in is the team's, through the
  checklist done again, not an approval inbox: the operating surgeon or the anaesthetist changes it alone, and the team
  is told. A change of the surgical team, the anaesthesia or the diagnosis after the Sign-in voids nothing, as before.
  "A level" is a free-text field on the booking (`siteLevel`: a spinal level, a digit, a tooth); HC360 does not work out a
  level from the procedure's name. The surgeon who marks the site is anyone who holds `ot.surgery.perform` (a doctor), not
  only the operating surgeon. The count's two staff are any two who hold `ot.checklists.record`, the signed-in one and
  the witness; HC360 does not know which is the scrub and which the circulating nurse. A count's number, once recorded,
  is not corrected: a wrong final count is settled as a discrepancy. The implant recall search
  (`GET /api/v1/ot/implants/trace`) still lists a line superseded by a correction, so a recall by the lot first written
  still finds the patient. A correction approved after the line's charge was put on a final bill fails
  (`surgery.charge_cancel_failed`: the charge cannot be withdrawn) and a credit note is asked for at the billing desk, as
  slice 6 has it. The OT in-charge
  is a permission, `ot.materials.approve`, not the nurse named on the booking.

- **Slice 14 leaves these as they are.** The quality manager is a permission (`quality.incidents.approve`, the
  QualityManager role and the hospital administrator), not a named person; an anonymously reported incident has no
  reporter to refuse, and the theatre's never event is reported by whoever settled the count. A sentinel event re-graded
  out of sentinel waits as one request with all its other corrections (place, patient, staff): none is saved until it is
  approved. Reopening a closed sentinel event, and the analysis and corrective actions themselves, are one person's as
  before. A breach found not to be a personal data breach after all is still reported to the Board (saying so) before it
  closes; the Board's acknowledgement is not recorded, only the reference and day of the report sent. The breach register
  is group-wide, but a request is decided in the facility it was asked in (the first facility when none is chosen). A
  breach's notification list takes one removal request at a time; adding patients, and sending the notices, are one
  person's as before. Holding, unscheduling and cancelling a campaign, which only stop sending, are one person's; a
  campaign taken back to draft is approved again before it is scheduled again. The desk's family-link code is the
  portal's (`PortalFamilyCode`) and counts against the same limits per number; the dialog keeps it, and a desk that
  closes the dialog sends a new one. The portal's own family code is unchanged. A record-sharing consent recorded with a
  signed form trusts that the file stored for the patient is that form (it is not read); consents of other kinds keep a
  typed witness name.

## How each control is built

Every slice uses the one mechanism the others use (ADR-010's audit trail, `platform.approval_requests`):
* the maker asks, with a reason; the change waits, and the record shows it waiting;
* the checker holds the kind's approval permission, is never the maker (checked by user id), and sees what changes in
  words, with what it reaches (stays on a package, money on a bill);
* the change is applied only over the record as it was asked (`approval.stale`), in one transaction, audited with both
  names;
* the stricter direction of a control applies at once; only the looser one waits.
