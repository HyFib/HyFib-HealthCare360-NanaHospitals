# Billing module (`billing`)

Tariffs and pricing, charge capture from every clinical/support module, OP and IP billing, payments (counter, POS
terminal, payment gateway), cashier shifts and day-end, patient advances (online appointment prepayments), refunds and
credit notes, insurance/TPA (payers, policies, pre-authorisation, claims, receivable ageing), doctor fee accounting,
the Tally day book, and the reconciliation of the payment gateway and of card and UPI terminals. SRS **MB-01..06, OPB-01..07, IPB-01..08,
INS-01..07, DFA-01..05**, business side of **INT-PG / INT-POS / INT-TALLY**, and OA-03 (appointment prepayments).

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Billing` (layer 7; references Patients and Scheduling).
Schema `billing`. Tests: `tests/Modules/HyFib.HealthCare360.Modules.Billing.Tests` (372 unit tests: pure domain, the settlement reports and the
printed bill's HTML, plus change-tracking checks on the real EF model without a database), and the end-to-end
`GatewayReconciliationTests` in `tests/HyFib.HealthCare360.IntegrationTests` (payment link to settlement journal, a
webhook delivered twice, who may reconcile) and `PosSettlementTests` (a terminal sale and a typed card payment matched
from the acquirer's report, the review, the settlement journal, a card receipt never settled).

The module implements the Platform contracts `IChargePoster` and `IBillingStatusProvider` (replacing the Null
fallbacks) — see [Contracts for other modules](#contracts-for-other-modules). For insurance claims it reads the coding
medical records verified for a stay or visit through `IVerifiedCodingSource`, which the Clinical module implements.

## Money rules

* `decimal` only; every amount is rounded with `Money.Round` (half away from zero) per line.
* GST is charged on the discounted value of each line and split into CGST/SGST (`Money.SplitGst`, SGST takes the paisa).
* Only the **patient share** is rounded to the rupee (it is collected in cash); the payer share stays at paise.
* Every document number is allocated inside the transaction that saves the document, so series are gap-free.
* Charge posting (per source line), gateway payments, POS transactions and patient advances are idempotent — a WiFi
  retry or a replayed webhook can never charge or receipt twice (backed by unique indexes).
* Days are Indian Standard Time days (`BillingTime`: IST calendar day, half-open UTC ranges).
* A **return** is a negative-quantity charge (credit line). It mirrors its sale — same tariff concession, same GST, amounts
  negated — takes a proportional (negative) share of any bill discount, never receives a staff line discount, gives a
  package back what the package absorbed (after first refunding what the patient paid beyond the package), and can
  make a bill's patient share negative, in which case the difference is refundable.

## Permissions

| Permission | Purpose | Default roles |
|---|---|---|
| `billing.charges.read` | View charges, bills, receipts, IP accounts, reports | Billing, Cashier, InsuranceDesk, Accountant, HospitalAdmin |
| `billing.charges.manage` | Manual charges, cancel unbilled charges | Billing, HospitalAdmin |
| `billing.op.create` | Create/edit draft OP bills (discounts within own limit) | Billing, Cashier, FrontOffice, HospitalAdmin |
| `billing.ip.manage` | IP accounts: packages, interim/final bills, settlement | Billing, InsuranceDesk, HospitalAdmin |
| `billing.bills.finalize` | Finalize bills (allocates the number) | Billing, Cashier, HospitalAdmin |
| `billing.bills.cancel` | Cancel finalized bills | HospitalAdmin, Accountant |
| `billing.discounts.approve` | Approve discounts above a user's limit, discharge with dues, set limits | HospitalAdmin, Accountant |
| `billing.payments.collect` | Collect payments, POS, payment links, advance adjustment, pay out approved refunds | Cashier, Billing, FrontOffice, HospitalAdmin |
| `billing.refunds.request` | Request refunds | Cashier, Billing, HospitalAdmin |
| `billing.refunds.approve` | Approve refunds, issue credit notes/write-offs | HospitalAdmin, Accountant |
| `billing.deposits` | Collect IP advances | Cashier, Billing, HospitalAdmin |
| `billing.shifts` | Cashier shifts, day-end report | Cashier, Billing, FrontOffice, Accountant, HospitalAdmin |
| `billing.tariffs.manage` | Tariff plans, prices, packages | Billing, HospitalAdmin |
| `billing.tally.export` | Post and export the Tally day book, mark it imported and reconcile it with Tally | HospitalAdmin, Accountant |
| `billing.tally.configure` | Set the Tally ledger names the day book posts to | HospitalAdmin, Accountant |
| `billing.gateway.reconcile` | Gateway reconciliation: settlements, exceptions, record a missed online receipt, send a failed gateway refund again. Card and UPI reconciliation: import the acquirer's settlement reports, review what does not match | HospitalAdmin, Accountant |
| `insurance.payers.manage` | Insurers, TPAs, corporates, schemes, non-payable items | InsuranceDesk, HospitalAdmin |
| `insurance.policies.manage` | Patient policies | InsuranceDesk, Billing, HospitalAdmin |
| `insurance.preauth.manage` | Cashless pre-authorisation, enhancements | InsuranceDesk, HospitalAdmin |
| `insurance.claims.manage` | Claims, settlements, ageing | InsuranceDesk, Accountant, HospitalAdmin |
| `doctorfees.rules.manage` | Doctor revenue-share rules | HospitalAdmin, Accountant |
| `doctorfees.payouts.manage` | Accruals, statements, payouts | HospitalAdmin, Accountant |

Segregation of duties is enforced in the domain, not only by roles: the requester of a refund or a discount cannot
approve it, and the preparer of a doctor payout cannot approve it. A refund requested by the system (no user, for
example a cancelled prepaid appointment) can be approved by any holder of `billing.refunds.approve`.

## Endpoints

All routes return RFC 7807 problems on failure (`422` business rule, `404`, `409` concurrency/duplicate). Updates that
carry `version` use optimistic concurrency (row `xmin`).

### Tariffs — `/api/v1/billing/tariffs`
Readers = any of `billing.tariffs.manage`, `billing.charges.read`, `billing.op.create`, `billing.ip.manage`.

| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/` | readers | → `TariffPlanListItem[]` |
| GET | `/{id}` | readers | → `TariffPlanDetail` (bed-class multipliers) |
| POST | `/` | `billing.tariffs.manage` | `SaveTariffPlanRequest` → 201 |
| PUT | `/{id}` | `billing.tariffs.manage` | `SaveTariffPlanRequest` (+`version`) → 204 |
| GET | `/{id}/prices` (paged, `sort=effectiveFrom\|price`) | readers | → `PagedResult<TariffPriceDto>` |
| PUT | `/{id}/prices` | `billing.tariffs.manage` | `SaveTariffPriceRequest` (effective from/to) → 204 |
| POST | `/{id}/prices/bulk` | `billing.tariffs.manage` | `BulkTariffPricesRequest` → `BulkTariffPricesResult` |
| DELETE | `/{id}/prices/{priceId}` | `billing.tariffs.manage` | → 204 |
| GET | `/quote?serviceItemId&planId&date&bedServiceItemId` | readers | → `PriceQuoteDto` (price source: base, plan, override, free follow-up) |

### Packages — `/api/v1/billing/packages`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/` · `/{id}` | `billing.tariffs.manage` / `billing.ip.manage` / `billing.charges.read` | → `PackageListItem[]` / `PackageDetail` |
| POST | `/` | `billing.tariffs.manage` | `SavePackageRequest` (price, LOS, inclusions: item or category, quantity/amount caps) → 201 |
| PUT | `/{id}` | `billing.tariffs.manage` | `SavePackageRequest` (+`version`) → 204 |

### Charges — `/api/v1/billing/charges`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?patientId&visitId&admissionId&status&unbilledOnly&…` (paged) | `charges.read` / `op.create` / `ip.manage` | → `PagedResult<ChargeDto>` |
| GET | `/{id}` | same | → `ChargeDto` |
| POST | `/` | `billing.charges.manage` | `PostChargeRequest` → 201 `ChargeDto` (source `billing.manual`; price editable only if the service allows) |
| POST | `/{id}/cancel` | `billing.charges.manage` | `CancelChargeRequest` → 204 (refused once billed) |

### Bills — `/api/v1/billing/bills`
Readers = any of `billing.charges.read`, `billing.op.create`, `billing.ip.manage`, `billing.payments.collect`.

| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?from&to&type&status&patientId&visitId&admissionId&payerId&withDueOnly&search` (paged, `sort=createdAt\|finalizedAt\|billNumber\|netAmount\|dueAmount`) | readers | → `PagedResult<BillListItem>` |
| GET | `/{id}` | readers | → `BillDetail` (lines as `BillLineDto` with `isReturn`, receipts, refunds, credit notes, pending discount); PHI access guarded and audited |
| GET | `/{id}/document` | readers | → `PrintableDocumentDto` (title, file name, page size `A4`, HTML): the [printed bill](#the-printed-bill) for the staff app to print, drafts included; guarded and audited ("Printed bill …") |
| GET | `/{id}/pdf` | readers | → `application/pdf` attachment `bill-<number>.pdf` (a draft: `bill-draft-<UHID>-<yyyy-MM-dd>.pdf`), the same document; 503 `pdf.unavailable` when the server cannot make PDFs; guarded and audited ("Downloaded bill … as a PDF") |
| POST | `/` | `billing.op.create` | `CreateBillRequest(visitId?, patientId?, chargeIds?)` → 201 `BillDetail`: OP draft from the visit's unbilled charges, or a patient-level draft from charges with no visit/admission |
| POST | `/{id}/lines` | `billing.op.create` | `AddBillLineRequest` → `BillDetail` (ad-hoc service, posted as `billing.adhoc`) |
| DELETE | `/{id}/lines/{lineId}?version` | `op.create` / `ip.manage` | → `BillDetail` |
| POST | `/{id}/refresh` | `billing.op.create` | `BillVersionRequest` → `BillDetail` (pulls charges posted since the draft was made) |
| POST | `/{id}/discount` | `op.create` / `ip.manage` | `ApplyDiscountRequest` (bill % or ₹, line ₹, reason) → `DiscountOutcome` (applied, or an approval request above the user's limit) |
| POST | `/{id}/finalize` | `billing.bills.finalize` | `BillVersionRequest` → `BillDetail`; allocates `{FAC}OB{YY}{SEQ:7}` / `{FAC}IB{YY}{SEQ:7}`; adjusts the visit's online prepayment |
| POST | `/{id}/cancel` | `billing.bills.cancel` | `CancelBillRequest(reason, version)` → `BillDetail`; charges released, advances restored, cash refunds requested |
| DELETE | `/{id}?version` | `op.create` / `ip.manage` | → 204 (discard a draft) |
| POST | `/{id}/credit-notes` | `billing.refunds.approve` | `IssueCreditNoteRequest(kind, amount, reason)` → 201 (`{FAC}CN{YY}{SEQ:6}`) |
| POST | `/{id}/adjust-advance` | `billing.payments.collect` | `AdjustAdvanceRequest(advanceId, amount?, version)` → 201 `ReceiptDto` (patient advance used on an OP bill) |

### Discounts — `/api/v1/billing/discount-approvals`, `/api/v1/billing/discount-limits`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/discount-approvals?status` (paged, pending by default) | `billing.discounts.approve` | → `PagedResult<DiscountApprovalDto>` |
| POST | `/discount-approvals/{id}/approve` · `/reject` | `billing.discounts.approve` | `DecideDiscountRequest(remarks)` → 204 (requester may not decide) |
| GET | `/discount-limits` | `billing.discounts.approve` | → `DiscountLimitDto[]` |
| PUT | `/discount-limits` | `billing.discounts.approve` | `SaveDiscountLimitRequest(userId, maxPercent, maxAmount?)` → 204 |
| DELETE | `/discount-limits/{id}` | `billing.discounts.approve` | → 204 |
| GET | `/discount-limits/me` | `op.create` / `ip.manage` | → the caller's authority (falls back to `billing.discount.defaultMaxPercent`) |

### Payments, advances and refunds
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/api/v1/billing/receipts?from&to&patientId&billId&ipAccountId&purpose&counter` (paged) | `charges.read` / `payments.collect` | → `PagedResult<ReceiptListItem>` |
| GET | `/api/v1/billing/receipts/{id}` | same | → `ReceiptDto` |
| POST | `/api/v1/billing/receipts` | `billing.payments.collect` | `CollectPaymentRequest(billId, payments[], notes)` → 201 `ReceiptDto` (`{FAC}RC{YY}{SEQ:7}`; split modes; cash needs an open shift when `billing.shift.required`) |
| POST | `/api/v1/billing/ip-accounts/{id}/deposits` | `billing.deposits` | `CollectDepositRequest` → 201 `ReceiptDto` |
| GET | `/api/v1/billing/patients/{patientId}/dues` | `payments.collect` / `charges.read` | → `PatientDues` (open bills, IP deposits + open patient advances, `advances[]`) |
| GET | `/api/v1/billing/patients/{patientId}/advances?openOnly` | `payments.collect` / `charges.read` | → `PatientAdvanceDto[]` (amount, adjusted, refunded, balance, refund pending) |
| GET | `/api/v1/billing/refunds?status&patientId&billId` (paged) | `refunds.request` / `refunds.approve` / `charges.read` | → `PagedResult<RefundDto>` |
| GET | `/api/v1/billing/refunds/{id}` | same | → `RefundDto` (incl. `advanceId`) |
| POST | `/api/v1/billing/refunds` | `billing.refunds.request` | `RequestRefundRequest(billId? \| ipAccountId? \| advanceId?, amount, reason)` → 201 (`{FAC}RF{YY}{SEQ:6}`) |
| POST | `/api/v1/billing/refunds/{id}/approve` · `/reject` | `billing.refunds.approve` | `ApproveRefundRequest` / `RejectRefundRequest` → 204 |
| POST | `/api/v1/billing/refunds/{id}/pay` | `billing.payments.collect` | `PayRefundRequest(mode, reference)` → 204 (`PaymentGateway` reverses the original online payment; the refund is paid in HC360 when the gateway accepts it, and `RefundDto.gatewayStatus` then follows it: `Pending` until the bank confirms, `Processed` with the bank's reference in `gatewayReference`, or `Failed` with `gatewayError`) |

Counter payment evidence (`PaymentLineRules`): card last 4 digits, UPI/net-banking/wallet reference, 6-digit cheque
number + bank + date (no post-dated cheques, valid 3 months). Advance, insurance and gateway modes are never keyed at
the counter.

### POS terminals and payment links — `/api/v1/billing/pos/sales`, `/api/v1/billing/payment-links`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| POST | `/pos/sales` | `payments.collect` / `deposits` | `StartPosSaleRequest(billId? \| ipAccountId?, amount?, terminalId, channel)` → `PosSaleDto` |
| GET | `/pos/sales/{id}` | same | → `PosSaleDto` (polls the terminal; an approved sale records the receipt once, keyed on the terminal transaction id) |
| POST | `/pos/sales/{id}/cancel` | same | → `PosSaleDto` |
| POST | `/payment-links` | `payments.collect` / `deposits` | `CreatePaymentLinkRequest` → 201 `GatewayOrderDto` (SMS/WhatsApp template `payment.link`) |
| GET | `/payment-links?billId&ipAccountId&status` (paged) | `payments.collect` / `charges.read` | → `PagedResult<GatewayOrderDto>` (`paidAt` = when the gateway took the payment; `lastError` = why it failed, expired or was cancelled, or why its paid receipt could not be recorded yet; `paymentId`) |
| POST | `/payment-links/{id}/refresh` | `payments.collect` / `deposits` | → `GatewayOrderDto` (asks the gateway for the real status: records a captured payment, or marks the order `Failed`, `Expired` or `Cancelled`; for a paid order whose receipt could not be recorded, it tries again) |
| POST | `/api/v1/billing/webhooks/payment-gateway` (optional `?facility=<id>`) | anonymous, rate limit `public`; the signature is checked with the merchant account of the facility that owns the order, prepaid booking or refund named in the payload (`?facility=` only when the payload names none of them) | raw provider JSON (≤ 64 KB) → 200 `WebhookAck`; 401 bad signature (`X-Razorpay-Signature` / `X-PayU-Signature` / `X-Webhook-Signature` / `X-Signature`), 503 no gateway, 413 too large. The status is always re-fetched from the gateway; receipts are idempotent on the gateway payment id. Each verified event is kept by its id (`X-Razorpay-Event-Id`, else a SHA-256 of the body) in `gateway_webhook_events`: a redelivery of an event already handled gets `processed: false` and changes nothing. A webhook for a portal booking's prepayment confirms the booking and receipts its advance; one for a gateway refund updates the refund's status. |

### Cash counter — `/api/v1/billing/shifts`, day-end
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/shifts/current` | `billing.shifts` | → `ShiftDto` or 204 |
| GET | `/shifts` (paged) · `/shifts/{id}` | `billing.shifts` | → `ShiftDto` |
| POST | `/shifts/open` | `billing.shifts` | `OpenShiftRequest(counter, openingFloat)` → `ShiftDto` (one open shift per cashier) |
| POST | `/shifts/{id}/close` | `billing.shifts` | `CloseShiftRequest(declaredCash, notes, version)` → `ShiftDto` (expected per mode, variance) |
| GET | `/api/v1/billing/reports/day-end?date&counter` | `billing.shifts` | → `DayEndReport` (per cashier, per mode, advances collected, bills, discounts, cancellations) |

### In-patient accounts — `/api/v1/billing/ip-accounts`
Readers = `billing.ip.manage` or `billing.charges.read`.

| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/?status` (paged, `search`) · `/{id}` · `/by-admission/{admissionId}` | readers | → `IpAccountListItem` / `IpAccountDetail` |
| PUT | `/{id}` | `billing.ip.manage` | `UpdateIpAccountRequest` (payer, policy, pre-auth, estimate; unbilled charges re-priced) → 204 |
| GET | `/{id}/running-bill` | readers | → `RunningBill` (by category, by day, lines with `isReturn`, `returnsNet`, package vs actuals, insurance estimate, deposit, estimated balance); guarded + audited |
| GET | `/{id}/package-status` | readers | → `PackageStatus` |
| PUT | `/{id}/package` | `billing.ip.manage` | `AssignPackageRequest(packageId?)` → 204 |
| POST | `/{id}/interim-bills` | `billing.ip.manage` | `CreateInterimBillRequest(upTo?)` → 201 `BillDetail` (self-pay, non-package stays only) |
| POST | `/{id}/final-bill` | `billing.ip.manage` | → 201 `BillDetail`: draft final bill (or a supplementary bill for late services) |
| POST | `/{id}/settle` | `billing.ip.manage` | → `SettlementResult` (advance adjusted oldest bill first; unused advance becomes a refund request) |
| GET | `/{id}/clearance` | readers | → `ClearanceDto` |
| POST | `/{id}/discharge-approval` | `billing.discounts.approve` | `ApproveDischargeWithDuesRequest(reason)` → 204 |

IP account statuses: `Open`, `DischargeInitiated`, `Discharged`, `Closed`, `Cancelled` (admission cancelled; no new
advances or packages; leftover services can still be billed; any advance is refunded through the refund flow).

### Insurance — `/api/v1/insurance`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/payers` · `/payers/{id}` | payers/policies/pre-auth/claims manage, `op.create`, `ip.manage`, `charges.read` | → `PayerListItem[]` / `PayerDetail` |
| POST · PUT | `/payers` · `/payers/{id}` | `insurance.payers.manage` | `SavePayerRequest` → 201 / 204 |
| GET | `/non-payable-items?payerId` | payers/claims manage, `ip.manage` | → `NonPayableItemDto[]` |
| POST · DELETE | `/non-payable-items` · `/{id}` | `insurance.payers.manage` | `SaveNonPayableItemRequest` → 201 / 204 |
| GET | `/policies?patientId&includeInactive` · `/policies/{id}` | policies/pre-auth/claims manage, `ip.manage`, `op.create` | → `PolicyDto[]` (number masked) |
| GET | `/policies/search?policyNumber` | policies/claims manage | → `PolicyDto[]` (blind-index lookup) |
| GET | `/policies/{id}/policy-number` | policies/pre-auth/claims manage | → `PolicyNumberDto` (decrypted; audited) |
| POST · PUT | `/policies` · `/policies/{id}` | `insurance.policies.manage` | `SavePolicyRequest` → 201 / 204 |
| POST | `/policies/{id}/verify` | `insurance.policies.manage` | `VerifyPolicyRequest` → 204 |
| GET | `/preauths?status&patientId&admissionId` (paged) · `/preauths/{id}` | pre-auth/claims manage, `ip.manage` | → `PreAuthListItem` / `PreAuthDetail` |
| POST · PUT | `/preauths` · `/preauths/{id}` | `insurance.preauth.manage` | `SavePreAuthRequest` → 201 / 204 (`{FAC}PA{YY}{SEQ:6}` on submit) |
| POST | `/preauths/{id}/submit` · `/query` · `/answer` · `/approve` · `/reject` · `/enhancements` · `/enhancements/{enhancementId}/respond` · `/final-approval` | `insurance.preauth.manage` | `PreAuthTextRequest`, `ApprovePreAuthRequest`, `RejectPreAuthRequest`, `RequestEnhancementRequest`, `RespondEnhancementRequest`, `FinalApprovalRequest` → 204 |
| GET | `/claims?status&payerId&patientId` (paged) · `/claims/{id}` | `insurance.claims.manage` | → `ClaimListItem` / `ClaimDetail` (`verifiedCoding`: the coding medical records verified for the bill's admission, or for its visit on any other bill, as claim text; null until verified) |
| POST | `/claims` | `insurance.claims.manage` | `CreateClaimRequest(billId, …)` → 201 (`{FAC}CL{YY}{SEQ:6}`); codes left empty are the verified coding, procedures included, or else the pre-authorisation's |
| PUT | `/claims/{id}/coding` | `insurance.claims.manage` | `UpdateClaimCodingRequest` (ICD / procedure codes as text) → 204 |
| POST | `/claims/{id}/query` · `/answer` · `/approve` · `/settle` · `/reject` | `insurance.claims.manage` | `ClaimTextRequest`, `ApproveClaimRequest`, `SettleClaimRequest` (tranche, deductions with reasons, UTR), `RejectClaimRequest` → 204; a rejection is a full disallowance |
| GET | `/receivables/ageing?asOf` | `insurance.claims.manage` | → `ReceivablesAgeingReport` (0-30 / 31-60 / 61-90 / 90+ by payer) |

### Doctor fees — `/api/v1/doctor-fees`
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/rules` | rules or payouts manage | → `ShareRuleDto[]` |
| POST · PUT · DELETE | `/rules` · `/rules/{id}` | `doctorfees.rules.manage` | `SaveShareRuleRequest` (practitioner/item/category/payer type, % or fixed, effective dates) |
| GET | `/accruals?practitionerId&from&to&status` (paged) | `doctorfees.payouts.manage` | → `PagedResult<AccrualDto>` |
| GET | `/payouts?practitionerId&status` (paged) · `/payouts/{id}` | `doctorfees.payouts.manage` | → `PayoutDto` |
| POST | `/payouts` | `doctorfees.payouts.manage` | `CreatePayoutRequest(practitionerId, period, deductions)` → 201 (`{FAC}DP{YY}{SEQ:5}`; TDS `doctorfees.tds.percent` on gross) |
| POST | `/payouts/{id}/approve` | `doctorfees.payouts.manage` | `PayoutVersionRequest` → 204 (not by its preparer) |
| POST | `/payouts/{id}/pay` | `doctorfees.payouts.manage` | `PayPayoutRequest(reference)` → 204 |
| DELETE | `/payouts/{id}?version` | `doctorfees.payouts.manage` | → 204 (draft only) |
| GET | `/statements?practitionerId&from&to` | `doctorfees.payouts.manage` | → `DoctorStatement` |

### Tally — `/api/v1/billing/tally`
Everything works on the signed-in user's facility. See [The Tally day book](#the-tally-day-book) for what the day book
holds and how posting, the checks and the transition for older days work.

| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/exports?from&to` | `billing.tally.export` | → `TallyExportDto[]`, the posting log (at most a year; the last 30 days by default) |
| POST | `/exports` | `billing.tally.export` | `PostDayBookRequest(date, to?)` → 204; queues `billing.tally.post` for each day (at most 31, none in the future). 409 `billing.tally_busy` when someone queued one of the days at the same moment |
| GET | `/day-book?date` | `billing.tally.export` | → the day's `AccountingVoucher[]` |
| GET | `/export-file?date` | `billing.tally.export` | → Tally import file (XML through `IAccountingExporter`, else JSON); records the file's fingerprint on the day. Audited as `billing.tally.exported` |
| GET | `/days/{date}/vouchers` | `billing.tally.export` | → `TallyDayVouchersDto`: every voucher of the day book with Tally's answer, then vouchers that left it and vouchers found only in Tally |
| GET | `/days/{date}/check` | `billing.tally.export` | → `TallyDayCheckDto`, the records check as it stands now (nothing kept) |
| POST | `/days/{date}/check?againstTally` | `billing.tally.export` | → `TallyDayCheckDto`; runs the records check, and with `againstTally=true` reads the day back from Tally; keeps the results on the day. Audited as `billing.tally.checked` when Tally is read |
| POST | `/days/{date}/imported` | `billing.tally.export` | → `TallyExportDto`; marks the downloaded file imported (see below). Audited as `billing.tally.imported` |
| GET | `/ledgers` | `billing.tally.export` or `billing.tally.configure` | → `TallyLedgerMap` in effect, built-in names filled in |
| PUT | `/ledgers` | `billing.tally.configure` | `TallyLedgerMap` → 204; validated (every name present, one line, at most 100 characters, known payment modes). Audited as `billing.tally.ledgers` |

### Gateway reconciliation — `/api/v1/billing/gateway`
Every route needs `billing.gateway.reconcile` and works on the signed-in facility; a user without one gets 422
`facility.required` on every route. See
[Gateway reconciliation](#gateway-reconciliation-int-pg) for what happens behind them.

| Method | Path | Request → response |
|---|---|---|
| GET | `/summary` | → `ReconciliationSummaryDto` (reconciliation on or off, expected settlement days, open exceptions by kind with count and amount, last settlement day and when it was read) |
| GET | `/settlements?from&to` (IST days, the last week by default, at most 92 days) | → `SettlementDayDto[]`: per day the count and totals (payments, refunds, adjustments, charges without GST, GST, paid to the bank, transactions not matched) with that day's `GatewaySettlementDto` rows (id, UTR, status, `difference` between the payout and its transactions). A settlement another facility holds on a shared merchant account counts with this facility's part only (`isShare`: its own transactions' amounts, charges and payout, no difference) |
| GET | `/settlements/{id}` | → `GatewaySettlementDetail`: the settlement and its `SettlementLineDto` transactions (type, gateway id, method, time, amount, fee with GST, net, match, the receipt or refund number). A facility that shares the merchant account sees only its own transactions, and the settlement's totals as its part (`isShare`) |
| POST | `/settlements/fetch` | `FetchSettlementsRequest(date)` → `SettlementFetchResult(date, settlements, added, updated, message)`; reads that day from the gateway now (today or up to a year back). 422 `billing.gateway_unavailable` with the gateway's reason |
| GET | `/exceptions?status&kind` (paged) | → `PagedResult<GatewayDiscrepancyDto>`: open ones worst first (kind, then amount, then age); `status=Resolved` newest first with who resolved it and the note |
| GET | `/exceptions/{id}` | → `GatewayDiscrepancyDto` (summary in words, amounts in HC360 and at the gateway, bill, receipt, refund and appointment by number, `canRecordReceipt`, `version`) |
| POST | `/exceptions/{id}/record-receipt` | → `DiscrepancyActionResult(message, discrepancy)`; only for money taken without a receipt. Records the receipt against its bill, admission or booking after asking the gateway; if that can no longer take it (a closed admission, an older order of a booking), keeps it as a patient advance. 422 `billing.payment_unlinked` when HC360 does not know what the payment was for. Audited `billing.gateway.receipt_recorded` |
| POST | `/exceptions/{id}/retry` | → `DiscrepancyActionResult`; by kind: asks the gateway again, reads the settlements of the next five days again, sends a failed gateway refund again (audited `billing.refund.gateway_retried`; 422 `billing.refund_retrying` while another request is sending it), or matches the settled transaction again. 422 `billing.nothing_to_retry` for a settlement-level difference |
| POST | `/exceptions/{id}/resolve` | `ResolveDiscrepancyRequest(note, version)` (5–500 characters) → `GatewayDiscrepancyDto`; 409 when someone changed it meanwhile. Audited `billing.gateway.exception_resolved` |

### Card and UPI reconciliation — `/api/v1/billing/pos/settlements`
Every route needs `billing.gateway.reconcile` and works on the signed-in facility (422 `facility.required` without
one). See [Card and UPI reconciliation](#card-and-upi-reconciliation-int-pos) for what happens behind them.

| Method | Path | Request → response |
|---|---|---|
| GET | `/summary` | → `PosSettlementSummaryDto` (expected settlement days, transactions to look at by reason with count and amount, receipts not settled with their total, the last payout day, the last report imported, the other counters' terminals) |
| POST | `/imports` | multipart `file` (CSV or .xlsx, at most 10 MB), `acquirer` (at most 64 characters), `settledOn` (only for a report without a settlement date column) → 201 `PosSettlementImportResult(import, columns, problems, matched, toLookAt, message)`. 400 `file` when the report cannot be read (no headings found, an .xls file, too many rows; the message says why), 400 `settledOn` when it has no settlement dates and none was given, 409 `billing.pos_settlement_imported` for a report imported before (same content), 409 `billing.pos_settlement_busy` when another import or match ran at the same time. Audited `billing.pos.settlement_imported` |
| GET | `/imports` (paged) | → `PagedResult<PosSettlementImportDto>` newest first (acquirer, file, rows, new, already imported, left out, the days paid, who imported it) |
| GET | `/days?from&to` (IST days, the last month by default, at most 92 days) | → `PosSettlementDayDto[]`: per day the payouts (`PosPayoutDto`: UTR, acquirer, transactions, payments, refunds and chargebacks, charges without GST, GST, paid to the bank, to look at, and the settlement journal's number) |
| GET | `/lines?attention&settledOn&utr&noUtr` (paged) | → `PagedResult<PosSettlementLineDto>`: `attention=true` the ones to look at, worst first (no receipt, amount differs, no refund, chargeback); `settledOn` with `utr` (or `noUtr=true`) one payout's transactions. Each with its references (RRN, approval code, card's last four digits, TID), amounts, what it matched (receipt or refund number, bill, how it was found) and any review |
| POST | `/lines/{id}/review` | `ReviewPosSettlementLineRequest(note, version)` (5–500 characters) → `PosSettlementLineDto`; 422 `billing.pos_settlement_matched` for a line that matches, `billing.pos_settlement_reviewed` for one already reviewed; 409 when someone changed it meanwhile. Audited `billing.pos.settlement_reviewed` |
| POST | `/match` | → `PosMatchResult(checked, matched, toLookAt, message)`: matches again the transactions without a receipt or refund settled in the last 90 days, for example after a missing receipt was recorded |
| GET | `/unsettled` | → `UnsettledPosReceiptDto[]` oldest first: card and UPI receipts no report carries although the reports already go `settlementDays` past them (receipt and bill, mode, amount, reference, card's last four digits, HC360's terminal, days waiting) |
| GET | `/training-file?date` | → `text/csv` `training-card-settlement-<date>.csv`: the training terminal's approved sales of the day as an acquirer would report them, to practise the import |

### Patient portal — `/api/v1/portal/bills` (`patients.portal`, own/linked patients only)
| Method | Path | Request → response |
|---|---|---|
| GET | `/` · `/{id}` | → `PortalBillListItem[]` / `PortalBillDetail` (lines with `isReturn`, no staff names or internal codes) |
| GET | `/{id}/pdf` | → `application/pdf` `bill-<number>.pdf`: the same [printed bill](#the-printed-bill) the counter prints, for the bills listed above only (finalized, part-paid, paid, refunded); audited ("Downloaded bill … as a PDF (patient portal)"); 503 `pdf.unavailable` when the server cannot make PDFs |
| POST | `/{id}/pay` | → `PortalPaymentOrder` (gateway checkout / link) |
| POST | `/{id}/payments/{orderId}/verify` | → `PortalPaymentStatus` |

## The printed bill
The A4 bill is made on the server (`Documents/BillDocument.cs`) from `BillDetail` and the facility letterhead, so the
copy printed at the counter, the staff PDF and the patient's PDF are the same document. The 80 mm counter copy is still
printed by the staff app.

* Title: "Tax invoice" when any line carries GST, otherwise "Bill of supply"; IP bills add "IP bill" or "IP final bill".
  A draft prints as "Estimate (draft bill)" with DRAFT across every page; cancelled and refunded bills print CANCELLED
  and the cancellation date and reason.
* Header: logo, legal name, address, phone and e-mail, GSTIN and PAN, registration number; the title, bill number and
  date on the right.
* Patient, UHID, status, payer, tariff and co-pay; every line with service code, doctor, date, SAC code, quantity, rate,
  package cover (when a package covers anything), discount and, on a tax invoice, taxable value, GST rate, CGST and SGST.
* Totals from the gross amount to the net amount, the payer's and the patient's share, credit notes, paid, refunded,
  and the balance due or the refund due; the patient's share in words (Indian system, "Rupees … Only").
* On a tax invoice, the GST summary by SAC code and rate. Then the payments received (advances adjusted included), the
  bill's notes, signature lines for the patient and the authorised signatory, and the facility's letterhead note.
* Every page's foot names the patient, UHID and bill number, with "Page X of Y".
* The **Bills** print template (Hospital setup › Print templates) decides the rest: on pre-printed stationery the
  header keeps only the GSTIN and PAN line with the title, number and date, and the page leaves the blank space set for
  the stationery; the logo can be left off; a closing note ends the bill. The 80 mm counter bill and the IP running bill
  follow the same template (an 80 mm roll always prints the letterhead); receipts and refund vouchers follow
  **Receipts and refund vouchers**. See `docs/modules/print-templates.md`.

## Contracts for other modules

### `IChargePoster` (implemented by `ChargeService`)
* `PostAsync` **stages** the charge in the caller's scoped `HospitalDbContext` and returns its id; it never saves and never
  opens a transaction, so it is safe inside a domain-event handler (`ServiceOrderPlaced`, …) and inside the caller's own
  transaction — the caller's `SaveChangesAsync` commits the order line and its charge together.
* Idempotent on (`SourceModule`, `SourceReference`) in every facility, including charges staged earlier in the same unit
  of work; re-posting returns the existing charge id. A unique index settles concurrent retries.
* Works without a signed-in user or facility (Inpatient bed-day job, SupportServices meal-plan job): facility, tariff and
  bed class come from the admission's IP account, else the visit (both looked up across facilities); a charge with
  neither falls back to the signed-in user's facility, then the patient's registration facility, at the self-pay tariff,
  and is billed on a patient-level OP bill (`POST /bills` with `patientId`).
* Price: the payer's tariff plan price (bed-class multiplier for procedures, surgery, nursing and doctor visits),
  else the service's base price; `UnitPriceOverride` wins (MRP, implant invoice price). GST comes from the service item.
* Negative quantity = a **return**: it must name its admission (or visit) and may not credit more than was charged for
  the same service item there, net of earlier returns (`billing.credit_exceeds_charge`). Pharmacy posts these as
  `pharmacy.ip.return`; bill, running-bill and portal lines flag them with `isReturn`.
* `CancelAsync(sourceModule, sourceReference, reason)` also only stages: it joins the caller's transaction (Inpatient
  reverses all of a cancelled admission's charges atomically), takes an unbilled charge off any draft bill, succeeds for
  unknown or already-cancelled sources, and fails with **`billing.charge_billed`** (422) when the charge is already on a
  finalized bill — billing then cancels the bill or issues a credit note.

Source keys in use: `inpatient.bed` / `inpatient.nursing` (`{admissionId}:{yyyy-MM-dd}`), `inpatient.visit`
(consultant-visit id), `supportservices.diet` (`{dietOrderId}:{yyyy-MM-dd}`), `pharmacy.ip` (issue line id),
`pharmacy.ip.return` (return line id), `diagnostics.*`, SpecialtyCare sources, and Billing's own: `billing.consultation`
(visit id), `billing.registration` (patient id), `billing.order` (clinical order item id), `billing.package`,
`billing.manual`, `billing.adhoc`.

### `IBillingStatusProvider` (implemented by `BillingStatusProvider`)
* `GetStatesAsync(sourceModule, references)` → `NotFound` / `Unbilled` (also while on a draft) / `Billed` / `Paid` (its
  bill is paid) / `Cancelled`, for any source key, across facilities, keyed by the caller's strings. Charges posted or
  cancelled earlier in the caller's unit of work count, so a job that checks and posts in one pass (a mid-day diet change)
  never charges the same day twice.
* Health-checkup packages: Clinical posts the package's HC-* service item when the patient checks in (`PackageBilling`,
  see `clinical.md`), so the state of a package is available as `GetStatesAsync("healthcheck.package",
  [packageBookingId.ToString()])`. Component tests (`ServiceOrderPlaced.IsPackageComponent`) are never charged
  individually by Billing.
* `GetOutstandingAsync(patientId)` → the patient's unpaid share of finalized bills (all facilities).
* `GetDischargeClearanceAsync(admissionId)` → cleared when every IP charge is on a finalized bill, the final bill exists
  and the patient's share is paid, or management approved discharge with dues.

## Events

Raised:
* `BillFinalized` (Platform) — on finalization, in the same transaction, with the facility.
* `BillCancelled` (Platform) — a finalized bill was cancelled (bill number, net amount, reason); doctor-fee reversal
  inside Billing, and Engagement drops the bill from the patient's spend.
* `PaymentReceived` (Platform) — for real patient money: counter, POS, gateway, IP deposits and patient advances; never
  for advance adjustments or payer settlements. Carries the facility.
* `RefundPaid` (Platform) — a refund was paid out (cash, card reversal, UPI, bank transfer, cheque or gateway); not
  for refunds kept as a patient advance.
* `BillPaid` (Billing-internal) — accrual under the "paid" trigger whichever
  payment settled the bill (counter, POS, gateway or an advance adjustment) or when the payer covers it all; a credit
  note or write-off clearing the due is not a payment.

Handled (all inside the raising transaction; handlers stage changes and never save):

| Event | Effect |
|---|---|
| `VisitCreated` | Consultation charge for OP/tele/ER visits with a doctor (`CONS-NEW/FU/TELE/ER`; doctor's fee unless the payer tariff prices it; free follow-up at 0) and `REG-NEW` on the first visit (`billing.registrationFee.enabled`); an unbilled registration fee left by a cancelled first visit moves to the new visit |
| `VisitCancelled` | OP visit: Billing's own unbilled charges of the visit cancelled, registration fee kept for the next visit, empty draft discarded. In-patient visit (= admission cancelled): drafts discarded, Billing's own charges cancelled, account → `Cancelled`, IP desk alerted if services, finalized bills or an advance remain |
| `ServiceOrderPlaced` | Prices **procedures** only (`billing.order`, quantity from the order); never package components; labs/imaging/cardiology (Diagnostics), surgery/physio/dialysis/blood (SpecialtyCare), diet (SupportServices) and consultations are charged by their own modules |
| `ServiceOrderCancelled` | Cancels the `billing.order` charge; if already billed, refund approvers are asked to issue a credit note |
| `PatientAdmitted` | Opens the IP account (payer, tariff, policy, bed class); re-prices charges staged earlier in the same transaction |
| `PatientTransferred` | Follows the bed class for later pricing |
| `DischargeInitiated` | Drafts the final bill (package vs actuals, payer split) and alerts the IP desk |
| `PatientDischarged` | Closes a settled account or alerts billing about dues |
| `PatientsMerged` | Re-points every billing row (incl. advances, policies, claims) to the surviving UHID |
| `AppointmentPaymentCaptured` (Scheduling) | Queues `billing.appointment.prepayment` (outbox) |
| `AppointmentCancelled` | For a cancelled (not rescheduled) prepaid booking, queues the same message to refund it |
| `BillFinalized`, `BillPaid`, `BillCancelled` | Doctor-fee accrual (idempotent per bill line) and reversal |

### Appointment prepayments (OA-03)
Scheduling posts nothing to billing. The outbox handler `billing.appointment.prepayment` (retried with back-off)
receipts the verified online payment as a **patient advance** (`ReceiptPurpose.Advance`, mode `PaymentGateway`,
idempotent on the gateway payment id, receipt number allocated in the dispatcher's transaction), following merges to
the surviving UHID. The advance follows a rescheduled booking (keyed by the gateway payment). When the visit booked
from the appointment is billed, finalizing the OP bill adjusts the advance against it (a journal, mode `Advance`,
nothing collected twice); staff can also adjust any open advance with `POST /bills/{id}/adjust-advance`. If the
booking ends up cancelled, a refund request (purpose `Advance`) is raised for approval and, when paid by
`PaymentGateway`, reversed on the original payment. Cancelling a bill gives its advance back.

A portal patient who pays and then closes the page is still credited: the gateway's webhook for the booking's order,
or the reconciliation job, confirms the booking through Scheduling's `AppointmentPaymentService` and receipts the
advance at once (`PrepaymentChecker`). The advance is dated when the gateway took the money.

## Gateway reconciliation (INT-PG)

What the payment gateway paid to the bank is checked against HC360's receipts and refunds, and what does not match
is listed for the accountants, worst first.

**Settlements.** `IPaymentGateway.GetSettlementsAsync(day, facility)` returns a day's settlements with their
transactions (Razorpay's settlement reconciliation API; the training gateway settles too, see
[integrations.md](integrations.md#payment-gateway-int-pg-and-the-signature-convention)). `GatewaySettlementService`
stores each settlement once (unique on provider and settlement id) with its lines: payment, refund or adjustment id,
the payment a refund came from, the order, amount, fee with GST, GST, credit, debit, method, and the times. Only ids,
amounts, the UTR and the method are kept, never card or bank details. A settlement already stored only has its status
updated (created → processed or failed). The reconciliation job reads today and yesterday every hour for every active
facility, in facility-code order; accountants can read any day of the last year from the screen.

**Shared merchant accounts.** Facilities that use the same gateway account (the all-facility settings) share its
settlements. The settlement is kept under the first facility that read it, and each line takes the facility of the
receipt or refund it matched, else of its payment link, checkout or prepaid booking. Each facility's day book clears
its own receipts; the facility holding the settlement also takes any settlement-level charge and any unexplained
difference. A facility that does not hold a settlement sees only its own transactions and their totals in it, and
each facility is alerted about the exceptions that are its own.

**Matching** (by gateway id, prepaid bookings included): a payment line matches the receipt payment whose
`ExternalId` is that payment id (mode `PaymentGateway`); a refund line matches the refund whose `GatewayRefundId` is
that refund id. A line is `Matched`, `AmountDiffers`, `NoReceipt`, `NoRefund`, or `NotApplicable` (an adjustment or
transfer by the gateway). Recording a receipt later matches the line again and raises or clears its exceptions (an
amount difference when the receipt records another amount); when that moves the line to the receipt's facility, the
settlement day's day books of both facilities are queued for Tally again.

Exceptions are raised once a settlement is stored and saved on their own. A settlement is marked
(`exceptions_raised`) only after its exceptions are saved, and every fetch of a day matches again and raises again
the settlements not marked or with lines that still do not match, so a run that stopped or clashed with a webhook
raising the same exception is made up by the next fetch; after a clash the exceptions are read again and saved, so
only the one that clashed is merely refreshed.

**Exceptions** (`GatewayDiscrepancy`, one per kind and gateway id, in `gateway_discrepancies`), worst first:

| Rank | Kind | Raised when | Clears itself when |
|---|---|---|---|
| 1 | `UnrecordedPayment` | a settled payment has no receipt; or the gateway reports a payment captured but its receipt could not be recorded (the reason is kept on the order and accountants get an alert) | the receipt is recorded |
| 2 | `UnsettledReceipt` | an online receipt is not in any settlement `settlementDays` after the payment, while the gateway has settled something newer (receipts of the last month are looked at) | a settlement carries the payment |
| 3 | `AmountMismatch` | a settled payment's amount differs from its receipt; the gateway adjusted a settlement; a settlement's payout differs from its transactions | the amounts agree on a later match |
| 4 | `RefundMismatch` | a settled refund is unknown to HC360 or has a different amount; a gateway refund failed (accountants get an alert) | the refund matches, or the bank confirms it; a failed refund only when it is sent again or the bank confirms it, never by a settlement line |

An exception a person resolved stays resolved; one HC360 cleared opens again if the cause comes back. Accountants
can record the receipt, try again or resolve it with a note (see the endpoints above); every action that records
money or closes an exception is audited. The list never shows patients' names, only document numbers and gateway ids.

**Closing the gaps.** `GatewayReconciliationJob` runs where `PlatformOptions.RunsJobs` is set, every 5 minutes:
- payment links and portal checkouts still waiting (or paid but not recorded) are asked about again: every run for
  two hours, then about hourly for two days, then about every six hours for 30 days. Each of these windows is asked
  for its newest 200 per run, so fresh links are never crowded out by many older ones. A paid one is recorded. A link
  the gateway reports expired or cancelled is marked `Expired` or `Cancelled`; a portal checkout nobody paid within
  `checkoutExpiryHours` is marked `Expired`. Money taken after that is still recorded;
- portal bookings whose online payment was started but not confirmed are asked about every run for two hours, then
  hourly while the booking is recent or still ahead;
- hourly: gateway refunds are followed until processed or failed, by when the latest attempt was sent (a refund sent
  again weeks after it was first paid out is followed like a new one): the newest 200 of the last two days every hour,
  the newest 200 of the rest of the month every six hours; the settlements of today and yesterday are read;
  unsettled receipts are listed.

**Sending a refund again.** A failed gateway refund is sent again by one request only: the retry is first claimed
(`refunds.gateway_retry_started_at`) in a save of its own under the refund's concurrency token, so a second click is
refused, and only then is the gateway called. Each attempt carries an idempotency key (the refund's id for the first
payout, the refund with the gateway refund that failed for a retry; Razorpay's `X-Refund-Idempotency` header and the
refund's `receipt`), so a request the gateway already acted on gets the same refund back rather than a second one. A
gateway that refuses the retry returns the refund to failed; one that does not answer leaves the claim, which another
request may take over after five minutes with the same key.

**Dates.** A gateway receipt is dated when the gateway took the money (`PaymentStatusResult.PaidAt`, never later than
now), so a payment recorded late belongs to the day the patient paid. When that day's day book was already posted to
Tally, it is queued again (`TallyCorrections`); vouchers keep their numbers, so Tally alters rather than duplicates.

**Settlement journal.** Each processed settlement is a Journal in the day book of its IST settlement day, numbered
with the settlement id (a shared account's other facilities add a suffix): the bank (`gatewaySettlementBank`) is
debited with the payout, `gatewayCharges` with the fees without GST, `gatewayInputGst` with the GST on them, and the
payment-gateway clearing ledger (the `PaymentGateway` mode ledger, "Payment Gateway Clearing" by default) is credited
with the gross (payments less refunds). The clearing ledger, debited by the day's online receipts and credited by
gateway refunds, is cleared this way; whatever stays in it is unreconciled. A settlement read after its day was posted
queues that day again.

**Webhook events** (`gateway_webhook_events`): each verified delivery is kept with the provider, event id, event type,
the gateway's order, payment, refund and settlement ids, the facility, when it arrived and was handled, the outcome
and how many times it was delivered. A delivery of an event still being handled (for less than two minutes) is
acknowledged and not acted on; after that, or after a failure, one delivery takes it over (a conditional update). An
event recorded as processed stays processed. Never the payload, which carries the patient's contact and card or bank details.
They are deleted with the integration log after its retention period (Privacy › Retention).

## Card and UPI reconciliation (INT-POS)

Card and UPI money reaches the bank through the acquirer (the bank or POS company behind the terminals), less its
charges (MDR) and the GST on them. The acquirer reports what it paid in a settlement report (its MIS), which an
accountant imports on **Billing › Card and UPI settlements**; HC360 checks it against the card and UPI receipts.

**Reading the report** (`PosSettlementFile`). CSV (comma, semicolon, tab or pipe separated, quoted fields allowed) or
an Excel workbook (.xlsx, read by HC360's own reader, `XlsxSheets`: cell values only, no DTDs, at most 150 MB unpacked);
an older .xls is refused with what to do. Every sheet with the headings is read (some acquirers put cards and UPI on
sheets of their own), a sheet without them (a summary) is passed over, and the problems then name their sheet. Banks lay their reports out differently, so the columns are found by their
headings, in the first 30 rows (title rows before them are skipped): RRN (UPI: the UPI reference), approval code,
transaction amount, MDR or commission, GST (or IGST, CGST and SGST added up), net amount, TID, transaction date and
time, card number, transaction type, payment mode, settlement date and UTR, each under the names banks use ("Txn
Amount", "Auth Code", "Cust Ref No", "MDR Amount", "Net Amount", "Settlement Date", "UTR No"…). A column whose heading is a
rate ("MDR %") is never taken for an amount. The hospital can add its acquirer's own headings in
`billing.pos.settlements` (`columns`). Dates are read day first (07/10/2026, 07-Oct-2026, 2026-10-07, Excel serial
numbers) in Indian time; amounts with commas, ₹, INR, brackets or Dr. Only the card's last four digits are kept, never
the card number or anything about the customer.

* A payment's net is what reached the bank; when the report gives it, the charges are the amount less GST less the net,
  so `amount = net + charges + GST` always holds. Without a net column, the net is the amount less MDR and GST.
* A refund, void or reversal (or a negative amount) takes money back; a chargeback, dispute or adjustment is kept for
  review. On these, any charge returned with the money counts as charges. Chargebacks without an RRN or approval code
  that look the same (terminal, amount, days, UTR) are all kept, numbered in the report's order, so a later report
  listing them again adds none of them.
* Rows left out, each with its reason (at most 20 listed): rows without an amount, rows with neither an RRN nor an
  approval code (except chargebacks), rows whose net, charges and GST do not add up, and repeated rows. The report's
  total rows are skipped and not counted. The import says which heading it read as what (`Amount from "Txn Amount"`).
* At most 20,000 transactions and 10 MB per file. The same file (by its SHA-256) is refused a second time. Reports
  overlap: each transaction is stored once (`line_key`: its type and RRN, or without one its TID, approval code, amount
  and day), so a later report adds only what is new.

**Matching** (each receipt payment and refund once; a partial unique index on the matched payment and on the refund
stops two imports taking the same one). References are compared by their letters and digits, whatever the case, so an
RRN typed as "6279 1012-3456" matches 627910123456: the receipts and refunds of the report's days (from two days before
its first transaction) are read and compared that way, and older ones by the reference as typed.

| Settled | Matches | Shown as |
|---|---|---|
| Payment | the card or UPI receipt payment whose reference is the RRN: the terminal's (a connected terminal's sale records it) or the one the cashier typed | `Matched` by the RRN, or `AmountDiffers` when HC360 recorded another amount |
| Payment | else a connected terminal's approved sale with the approval code and amount, within two days, or a payment whose typed reference is the approval code | `Matched` by the approval code |
| Payment by card | else a card payment typed with the same last four digits and amount, within a day | `Matched` by the card and amount |
| Payment | nothing | `NoReceipt` |
| Refund | the refund paid by card or UPI whose reference is the RRN or (same amount) the approval code | `Matched`, else `NoRefund` |
| Chargeback | nothing to match | `Review` |
| Any, at a TID in `otherTerminals` | the pharmacy's or canteen's terminal, whose sales Billing does not record | `OtherCounter` |

`NoReceipt`, `AmountDiffers`, `NoRefund` and `Review` are listed to look at until an accountant reviews them with a
note. Transactions without a receipt or refund settled in the last 90 days are matched again with each import and by
**Match again** (after the missing receipt is recorded); one that matched stays matched.

**Not settled.** A card or UPI receipt is listed when no report carries it although the reports of its mode already go
`settlementDays` past it (from the first day a report covers, for the last 60 days, at most 500).

**Settlement journal.** Each payout (a day's transactions under one UTR, or the day's without a UTR) is a Journal in
the day book of its settlement day, numbered `POS-<facility>-<UTR>` (`POS-<facility>-<yyyyMMdd>` without a UTR):
`posSettlementBank` is debited with what reached the bank, `posCharges` with the charges and `gatewayInputGst` with the
GST on them, and the Card and UPI mode ledgers ("Card Settlement Account", "UPI Settlement Account" by default) are
credited with the gross (payments less refunds and chargebacks). The receipts debited those ledgers, so what stays in
them is not settled. Importing a report queues the posted days it touches again (`TallyCorrections`); the journal
keeps its number, so Tally alters it.

**Training.** `/training-file` turns the training terminal's approved sales of a day into a report, paid the next day
with a 1.5 % charge on cards plus GST, under the UTR `SIMUTR<yyyyMMdd>`, so the whole flow can be tried without an
acquirer.

## The Tally day book

`TallyService` builds one facility's day as balanced vouchers (`TallyVoucherBuilder`) with the ledger names of
`billing.tally.ledgers`:

* a Sales voucher per bill finalised (income by department, discount, CGST/SGST, round-off, patient and payer
  receivables), and a Credit Note `<bill>/C` for a bill cancelled on the day;
* a Receipt per receipt (a Journal for an advance adjustment), a Payment per refund paid, a Credit Note per credit note
  or write-off, a Journal per claim deduction (`<claim>/<six hex digits of the settlement>`);
* a Journal per doctor for the day's fee accruals, numbered `DF-<facility>-<yyyyMMdd>-<doctor code>`
  (`DF-NH-20260925-DR005`). A doctor without a code is numbered by an eight-digit hash of the name. Two doctors whose
  numbers would be the same (a shared code) both add the last eight hex digits of their practitioner id, so neither
  number depends on the order the doctors come in. Payouts are Payments with TDS and recoveries;
* the pharmacy's and the canteen's day, read through the Platform contract `ICounterSalesSource` (implemented by
  Pharmacy and SupportServices): one summary Sales voucher (`PH-<facility>-<yyyyMMdd>`, `CT-…`) with income by GST
  rate, a Credit Note for returns (`…-R`) and a Receipt for balances paid later (`…-C`). A counter can be left out with
  `pharmacy.include` / `canteen.include` while its sales are still entered in Tally by hand;
* a Journal per payment gateway settlement processed on the day (see [Settlement journal](#gateway-reconciliation-int-pg));
* a Journal per card and UPI payout of the day, from the acquirer's reports (see
  [Card and UPI reconciliation](#card-and-upi-reconciliation-int-pos)).

Every voucher carries a reference; Tally keeps it as `REMOTEID="HC360-<reference>"`, so posting a day again alters its
vouchers instead of adding new ones. Narrations carry document numbers, never patient names.

**Per-voucher tracking.** Every voucher sent is a `TallyVoucherRecord` (`billing.tally_vouchers`): type, number,
remote id, amount, a SHA-256 of its content, what Tally answered (created, altered, accepted, refused, not confirmed,
not sent, or imported from the file) and the result of the last check against Tally. A voucher that leaves the day
book after Tally confirmed it stays listed as "No longer in HC360" until it is deleted in Tally. The staff screen shows
each voucher's status and what to do about it. A reference longer than 200 characters, or two vouchers with the same
reference, fails the day with that reason before anything is sent; a retry cannot fix either.

**Posting.** `POST /exports` and the nightly job start a post run on the day (`TallyExport`, one per facility and
date) and queue `billing.tally.post`. Each attempt sends what Tally has not confirmed in the run, 50 vouchers at a
time, and records Tally's answer for each voucher as it goes. The day is Posted when Tally confirmed every voucher;
otherwise it is Failed with the reason, and the outbox tries again with back-off (vouchers Tally confirmed are not sent
again in the same run). Ten seconds after a post, `billing.tally.check` reads the day back. Without a Tally connection
the day is Not configured (not a failure): the accountant downloads the file, imports it into Tally and marks the day
imported.

**Time limits and retries.** The outbox gives a handler 30 seconds and has no per-handler setting, so the post plans
inside it: an attempt starts no new chunk after 15 seconds (the rest follows in a new message of the same run), and
stops calling Tally after 24 seconds, when the exporter sends nothing new and cuts a call in progress short. Each call
to Tally has 20 seconds. A post that runs out of time anyway is recorded as Failed ("The post did not finish within
the 30 seconds …") and retried by the outbox; a check that runs out of time records its check against Tally as
Failed and is not retried. The post and the check run in a scope of their own and save as they go, so the outbox's
batch transaction holds only its message rows while Tally is called, and a conflict on a day fails that message alone.
`TallyDayBookScheduler` also queues again, every hour, any day still Queued two hours after it was queued or last
tried (its message was lost or gave up), so no day waits for ever.

**Concurrency.** `TallyExport` and `TallyVoucherRecord` carry an `xmin` concurrency token. A check that loses to a
post (or another check) starts again from what was saved, up to three times; a post that loses fails its message and
the outbox tries again; "mark imported" that loses answers 409 `billing.tally_busy`. Downloading the file at the same
moment as a post records the file on the row the post created.

**Mark imported.** Allowed once the file was downloaded and only while the day book still has the fingerprint of that
file; refused while the day is Queued (409 `billing.tally_posting`) and once Tally confirmed the day itself
(`billing.tally_posted`), as the screen hides it then. It marks every voucher imported and drops the records of
vouchers that left the day book (the screen lists them for deleting in Tally first).

**Records check** (`TallyRecordsCheck`, inside HC360). The day book's figures against the records they come from:
bills finalised and cancelled (count and amount), money received and paid back per payment mode against the cashier
day-end report, deposits and advances, advance adjustments, insurer settlements, claim deductions, credit notes,
write-offs, doctor-fee accruals and payouts. Open shifts and cash shortages or excesses are listed as notes (the day
book has no voucher for them). The pharmacy and canteen lines are totals only: the day book takes them from the
counter's own day, which HC360 has no second record of, so they are shown but never counted as matched or different.
Vouchers not in Tally (not sent, refused, changed since sent, no longer in HC360) count as differences too.

**Check against Tally** (read-back). `IAccountingExporter.ReadBackAsync` reads the day's vouchers from Tally (22
seconds at most) and matches them by remote id: present, amount, type and ledgers. HC360 vouchers Tally holds on the
day that no facility's day book has are listed as only in Tally, to delete there; a bill of this facility among them
is listed as "Bill of another day" with the day it belongs to, to post that day again instead (see below).

**Days posted before per-voucher tracking.** Before tracking, a doctor-fee journal was numbered by its place in the
day (`DF-20260925-001`, `REMOTEID="HC360-DF-20260925-001"`), and the credit note for a cancelled bill reused the
bill's own reference, so Tally turned the bill's sale into that credit note on the cancellation day. The
`TallyReconciliation` migration sets `legacy_references` on every day that existed then. Such a day:

* keeps the old doctor-fee numbers (`DF-yyyyMMdd-001`, doctors in order of code and name), so posting it again alters
  the journals Tally already holds instead of adding a second set. This is safer than asking the accountant to delete
  the old journals first: nothing is doubled at any moment, and it works the same for days imported from a file. The
  sequence may now give a doctor's journal another doctor's number; the post alters every journal of the day, so the
  totals stay right;
* when posted again, also queues the day each bill cancelled on it was finalised (unless that day is queued already,
  or has sent the bill since tracking began): the cancellation now goes as `<bill>/C`, and posting the bill's day
  turns `HC360-<bill>` back into the sale, so Tally does not reverse the bill twice. The day's message names the days
  queued. Where Tally is not set up, those days show Not configured and the accountant imports their files too;
* after the migration shows its vouchers as confirmed ("N of N in Tally") when it was Posted.

`HC360-<bill>` found only in Tally on a cancellation day is that pre-tracking credit note: the read-back says to post
the bill's day again, never to delete it.

## Outbox, jobs and notifications
* `billing.tally.post` — day book per facility and date (see [The Tally day book](#the-tally-day-book));
  `TallyDayBookScheduler` queues yesterday after 00:30 IST (`billing.tally.dailyPost.enabled`, only where
  `PlatformOptions.RunsJobs`), posts again a day someone posted before it ended, and queues again days stuck in Queued.
* `billing.tally.check` — reconciles a posted day with its records and with Tally; not retried.
* `billing.appointment.prepayment` — see above.
* `GatewayReconciliationJob` — re-checks open online payments and prepaid bookings, follows gateway refunds, reads
  settlements; see [Gateway reconciliation](#gateway-reconciliation-int-pg).
* SMS/WhatsApp templates used: `bill.receipt` (every counter/POS/gateway receipt), `payment.link`.
* In-app alerts: discount approval needed, refund awaiting approval, discharge initiated (final bill drafted), patient
  discharged with dues, admission cancelled (billing follow-up), cancelled order already billed; to holders of
  `billing.gateway.reconcile`: an online payment not recorded, an online refund failed, settled transactions that do
  not match.
* Realtime: `billing.bill.changed`, `billing.pos.changed` (facility group).

## Number series
| Key | Format | Reset |
|---|---|---|
| `billing.bill.op` | `{FAC}OB{YY}{SEQ:7}` | yearly |
| `billing.bill.ip` | `{FAC}IB{YY}{SEQ:7}` | yearly |
| `billing.receipt` | `{FAC}RC{YY}{SEQ:7}` | yearly |
| `billing.refund` | `{FAC}RF{YY}{SEQ:6}` | yearly |
| `billing.creditnote` | `{FAC}CN{YY}{SEQ:6}` | yearly |
| `insurance.preauth` | `{FAC}PA{YY}{SEQ:6}` | yearly |
| `insurance.claim` | `{FAC}CL{YY}{SEQ:6}` | yearly |
| `doctorfees.payout` | `{FAC}DP{YY}{SEQ:5}` | yearly |

## Settings (per facility, `ISettingsService`)
| Key | Default | Meaning |
|---|---|---|
| `billing.registrationFee.enabled` | `true` | Charge `REG-NEW` on a patient's first visit |
| `billing.discount.defaultMaxPercent` | `0` | Staff discount allowed without approval when the user has no limit row |
| `billing.shift.required` | `true` | Counter collections need an open cashier shift |
| `billing.tally.ledgers` | built-in names | `TallyLedgerMap` JSON (income by department, mode ledgers, payer and doctor ledgers, `pharmacy` and `canteen` ledgers, `salesLedgerPerGstRate`; for the settlement journal `gatewaySettlementBank` "Bank", `gatewayCharges` "Payment Gateway Charges", `gatewayInputGst` "Input GST", and the clearing ledger is `modeLedgers.PaymentGateway`, "Payment Gateway Clearing" by default; for the card and UPI settlement journal `posSettlementBank` "Bank" and `posCharges` "Card & UPI Charges (MDR)", GST on `gatewayInputGst`); set on the Tally screen |
| `billing.tally.dailyPost.enabled` | `true` | Queue the daily Tally post |
| `billing.gateway.reconciliation` | `{ "enabled": true, "settlementDays": 3, "checkoutExpiryHours": 48 }` | Gateway reconciliation: read settlements and list exceptions (`enabled`); an online receipt not settled after `settlementDays` (1–30) is an exception; a portal checkout nobody paid is marked expired after `checkoutExpiryHours` (1–720) |
| `billing.pos.settlements` | `{ "settlementDays": 3, "otherTerminals": [], "columns": {} }` | Card and UPI reconciliation: a card or UPI receipt not settled after `settlementDays` (1–30) is listed; `otherTerminals` are the acquirer's TIDs of counters Billing does not record (pharmacy, canteen); `columns` adds the acquirer's headings by field (`rrn`, `approvalCode`, `amount`, `charges`, `tax`, `net`, `terminalId`, `transactionDate`, `transactionTime`, `card`, `type`, `mode`, `settlementDate`, `utr`), tried before the built-in ones |
| `doctorfees.accrual.trigger` | `finalized` | `finalized` or `paid` |
| `doctorfees.tds.percent` | `10` | TDS (194J) on doctor payouts |

## Seed data (order 400)
Always: tariff plan `GEN` ("General", default). Demo data only: payers `STAR`, `HDFCERGO`, `ICICILOM`, `NIA`, TPAs
`MEDIASSIST`, `MDINDIA`, `PARAMOUNT`, schemes `PMJAY`, `CGHS`, `ECHS` (with CGHS/ECHS rate plans), the `PKG-NVD`
normal-delivery package, and doctor share rules (consultation 80 %, IP visit 80 %, procedure 30 %, surgery 40 %, plus a
fixed ₹250 government-scheme consultation rule for `DR005`).

## Persistence notes
Schema `billing`; enums stored as text (no ordering on enum columns in SQL; filtered indexes use the text values).
Key unique indexes: charge (`source_module`, `source_reference`); bill number (non-null); one OP draft per visit; one
draft final bill per admission; receipt payment (`mode`, `external_id`); gateway order (provider, order id), merchant
reference, gateway payment id; POS (provider, transaction id); patient advance gateway payment id and receipt;
one open shift per cashier; one doctor-fee accrual per bill line (and one reversal per accrual); one Tally export per
facility/day (`tally_exports`, with `xmin` as its concurrency token); one Tally voucher record per export and reference
(`tally_vouchers`, also `xmin`); refund `gateway_refund_id` (non-null); gateway settlement (provider, settlement id);
gateway exception (kind, key); webhook event (provider, event id). Gateway settlements, their lines and webhook events
are append-mostly and not data-audited; exceptions are. The migrations live in
`HyFib.HealthCare360.Server/Persistence/Migrations`: `TallyReconciliation` adds the Tally voucher records and the
export's tracking columns, and `GatewayReconciliation` adds `gateway_settlements`, `gateway_settlement_lines`,
`gateway_discrepancies`, `gateway_webhook_events` and the gateway status, attempt and retry columns of `refunds`.
`PosSettlements` adds `pos_settlement_imports` (unique facility and file SHA-256) and `pos_settlement_lines` (unique
facility and `line_key`; unique matched `receipt_payment_id` and `refund_id` where set; `xmin` as the concurrency
token), and indexes on `receipt_payments.reference` and `pos_transactions.approval_code` for the matching. Both tables
are append-mostly and not data-audited; imports and reviews are audited.

## Not covered yet
* **A failed gateway refund paid another way.** The refund stays paid through the gateway in HC360; the accountant
  pays it by bank transfer outside HC360, records that in Tally, and resolves the exception with the reference.
* **Card and UPI settlement reports are imported by hand.** HC360 does not fetch them from the acquirer (Pine Labs and
  the banks offer them as downloads or e-mails, not through the terminal API).
* **The pharmacy's and the canteen's card sales** are not recorded with references, so they are not matched: their
  terminals are set as `otherTerminals`, and their money still clears through the settlement journal.
* **Reversing a settlement journal.** A settlement the gateway reports as failed after it was journalled is not
  reversed in Tally; the accountant corrects it there.
* **Chargebacks and disputes** arrive as gateway adjustments and are listed as amount differences; there is no dispute
  workflow.
* **A payment on an older order of a prepaid booking** (the patient restarted the checkout and paid both) is found by
  the settlement match, and "Record the receipt" keeps it as a patient advance for staff to refund.
