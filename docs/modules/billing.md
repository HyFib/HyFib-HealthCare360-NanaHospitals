# Billing module (`billing`)

Tariffs and pricing, charge capture from every clinical/support module, OP and IP billing, payments (counter, POS
terminal, payment gateway), cashier shifts and day-end, patient advances (online appointment prepayments), refunds and
credit notes, insurance/TPA (payers, policies, pre-authorisation, claims, receivable ageing), doctor fee accounting,
the Tally day book and the reconciliation of the payment gateway. SRS **MB-01..06, OPB-01..07, IPB-01..08,
INS-01..07, DFA-01..05**, business side of **INT-PG / INT-POS / INT-TALLY**, and OA-03 (appointment prepayments).

Project: `src/server/Modules/HyFib.HealthCare360.Modules.Billing` (layer 7; references Patients and Scheduling).
Schema `billing`. Tests: `tests/Modules/HyFib.HealthCare360.Modules.Billing.Tests` (277 unit tests: pure domain and the
printed bill's HTML, plus change-tracking checks on the real EF model without a database), and the end-to-end
`GatewayReconciliationTests` in `tests/HyFib.HealthCare360.IntegrationTests` (payment link to settlement journal, a
webhook delivered twice, who may reconcile).

The module implements the Platform contracts `IChargePoster` and `IBillingStatusProvider` (replacing the Null
fallbacks) — see [Contracts for other modules](#contracts-for-other-modules).

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
| `billing.tally.export` | Post/export the Tally day book | HospitalAdmin, Accountant |
| `billing.gateway.reconcile` | Gateway reconciliation: settlements, exceptions, record a missed online receipt, send a failed gateway refund again | HospitalAdmin, Accountant |
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
| GET | `/claims?status&payerId&patientId` (paged) · `/claims/{id}` | `insurance.claims.manage` | → `ClaimListItem` / `ClaimDetail` |
| POST | `/claims` | `insurance.claims.manage` | `CreateClaimRequest(billId, …)` → 201 (`{FAC}CL{YY}{SEQ:6}`) |
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
| Method | Path | Permission | Request → response |
|---|---|---|---|
| GET | `/exports` | `billing.tally.export` | → `TallyExportDto[]` |
| POST | `/exports` | `billing.tally.export` | `PostDayBookRequest(date)` → queues `billing.tally.post` |
| GET | `/day-book?date` | `billing.tally.export` | → the day's `AccountingVoucher[]` |
| GET | `/export-file?date` | `billing.tally.export` | → Tally import file (via `IAccountingExporter`) |

### Gateway reconciliation — `/api/v1/billing/gateway`
Every route needs `billing.gateway.reconcile` and works on the signed-in facility. See
[Gateway reconciliation](#gateway-reconciliation-int-pg) for what happens behind them.

| Method | Path | Request → response |
|---|---|---|
| GET | `/summary` | → `ReconciliationSummaryDto` (reconciliation on or off, expected settlement days, open exceptions by kind with count and amount, last settlement day and when it was read) |
| GET | `/settlements?from&to` (IST days, the last week by default, at most 92 days) | → `SettlementDayDto[]`: per day the count and totals (payments, refunds, adjustments, charges without GST, GST, paid to the bank, transactions not matched) with that day's `GatewaySettlementDto` rows (id, UTR, status, `difference` between the payout and its transactions) |
| GET | `/settlements/{id}` | → `GatewaySettlementDetail`: the settlement and its `SettlementLineDto` transactions (type, gateway id, method, time, amount, fee with GST, net, match, the receipt or refund number). A facility that shares the merchant account sees only its own transactions |
| POST | `/settlements/fetch` | `FetchSettlementsRequest(date)` → `SettlementFetchResult(date, settlements, added, updated, message)`; reads that day from the gateway now (today or up to a year back). 422 `billing.gateway_unavailable` with the gateway's reason |
| GET | `/exceptions?status&kind` (paged) | → `PagedResult<GatewayDiscrepancyDto>`: open ones worst first (kind, then amount, then age); `status=Resolved` newest first with who resolved it and the note |
| GET | `/exceptions/{id}` | → `GatewayDiscrepancyDto` (summary in words, amounts in HC360 and at the gateway, bill, receipt, refund and appointment by number, `canRecordReceipt`, `version`) |
| POST | `/exceptions/{id}/record-receipt` | → `DiscrepancyActionResult(message, discrepancy)`; only for money taken without a receipt. Records the receipt against its bill, admission or booking after asking the gateway; if that can no longer take it (a closed admission, an older order of a booking), keeps it as a patient advance. 422 `billing.payment_unlinked` when HC360 does not know what the payment was for. Audited `billing.gateway.receipt_recorded` |
| POST | `/exceptions/{id}/retry` | → `DiscrepancyActionResult`; by kind: asks the gateway again, reads the settlements of the next five days again, sends a failed gateway refund again (audited `billing.refund.gateway_retried`), or matches the settled transaction again. 422 `billing.nothing_to_retry` for a settlement-level difference |
| POST | `/exceptions/{id}/resolve` | `ResolveDiscrepancyRequest(note, version)` (5–500 characters) → `GatewayDiscrepancyDto`; 409 when someone changed it meanwhile. Audited `billing.gateway.exception_resolved` |

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
* Health-checkup packages: the state of a package is available as `GetStatesAsync("healthcheck.package",
  [packageBookingId.ToString()])` **once the package's HC-* service item is posted under that key** (see open items).
  Component tests (`ServiceOrderPlaced.IsPackageComponent`) are never charged individually by Billing.
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
difference.

**Matching** (by gateway id, prepaid bookings included): a payment line matches the receipt payment whose
`ExternalId` is that payment id (mode `PaymentGateway`); a refund line matches the refund whose `GatewayRefundId` is
that refund id. A line is `Matched`, `AmountDiffers`, `NoReceipt`, `NoRefund`, or `NotApplicable` (an adjustment or
transfer by the gateway). Recording a receipt later matches the line again.

**Exceptions** (`GatewayDiscrepancy`, one per kind and gateway id, in `gateway_discrepancies`), worst first:

| Rank | Kind | Raised when | Clears itself when |
|---|---|---|---|
| 1 | `UnrecordedPayment` | a settled payment has no receipt; or the gateway reports a payment captured but its receipt could not be recorded (the reason is kept on the order and accountants get an alert) | the receipt is recorded |
| 2 | `UnsettledReceipt` | an online receipt is not in any settlement `settlementDays` after the payment, while the gateway has settled something newer (receipts of the last month are looked at) | a settlement carries the payment |
| 3 | `AmountMismatch` | a settled payment's amount differs from its receipt; the gateway adjusted a settlement; a settlement's payout differs from its transactions | the amounts agree on a later match |
| 4 | `RefundMismatch` | a settled refund is unknown to HC360 or has a different amount; a gateway refund failed (accountants get an alert) | the refund matches, or the bank confirms it |

An exception a person resolved stays resolved; one HC360 cleared opens again if the cause comes back. Accountants
can record the receipt, try again or resolve it with a note (see the endpoints above); every action that records
money or closes an exception is audited. The list never shows patients' names, only document numbers and gateway ids.

**Closing the gaps.** `GatewayReconciliationJob` runs where `PlatformOptions.RunsJobs` is set, every 5 minutes:
- payment links and portal checkouts still waiting (or paid but not recorded) are asked about again: every run for
  two hours, then about hourly for two days, then about every six hours for 30 days. A paid one is recorded. A link
  the gateway reports expired or cancelled is marked `Expired` or `Cancelled`; a portal checkout nobody paid within
  `checkoutExpiryHours` is marked `Expired`. Money taken after that is still recorded;
- portal bookings whose online payment was started but not confirmed are asked about every run for two hours, then
  hourly while the booking is recent or still ahead;
- hourly: gateway refunds of the last month are followed until processed or failed; the settlements of today and
  yesterday are read; unsettled receipts are listed.

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
and how many times it was delivered. Never the payload, which carries the patient's contact and card or bank details.
They are deleted with the integration log after its retention period (Privacy › Retention).

## Outbox, jobs and notifications
* `billing.tally.post` — day book per facility and date; `TallyDayBookScheduler` queues yesterday after 00:30 IST
  (`billing.tally.dailyPost.enabled`, only where `PlatformOptions.RunsJobs`); pushes through `IAccountingExporter` when
  registered; one `TallyExport` row per facility/day.
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
| `billing.tally.ledgers` | built-in names | `TallyLedgerMap` JSON (income by department, mode ledgers, payer/doctor ledgers; for the settlement journal `gatewaySettlementBank` "Bank", `gatewayCharges` "Payment Gateway Charges", `gatewayInputGst` "Input GST", and the clearing ledger is `modeLedgers.PaymentGateway`, "Payment Gateway Clearing" by default) |
| `billing.tally.dailyPost.enabled` | `true` | Queue the daily Tally post |
| `billing.gateway.reconciliation` | `{ "enabled": true, "settlementDays": 3, "checkoutExpiryHours": 48 }` | Gateway reconciliation: read settlements and list exceptions (`enabled`); an online receipt not settled after `settlementDays` (1–30) is an exception; a portal checkout nobody paid is marked expired after `checkoutExpiryHours` (1–720) |
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
facility/day; refund `gateway_refund_id` (non-null); gateway settlement (provider, settlement id); gateway exception
(kind, key); webhook event (provider, event id). Gateway settlements, their lines and webhook events are append-mostly
and not data-audited; exceptions are. The migrations live in `HyFib.HealthCare360.Server/Persistence/Migrations`;
`GatewayReconciliation` adds `gateway_settlements`, `gateway_settlement_lines`, `gateway_discrepancies`,
`gateway_webhook_events` and the gateway status columns of `refunds`.

## Open items for maintainers
* Health-checkup packages: no module posts the HC-* package charge yet. Clinical (booking owner) should post it
  through `IChargePoster` with `SourceModule = "healthcheck.package"`, `SourceReference = booking id` when the booking is
  made or checked in; Billing then bills it like any OP charge and Diagnostics can gate on its state.

## Not covered yet
* **A failed gateway refund paid another way.** The refund stays paid through the gateway in HC360; the accountant
  pays it by bank transfer outside HC360, records that in Tally, and resolves the exception with the reference.
* **Card/UPI POS settlements.** Terminal sales are recorded when approved, but the acquirer's settlement file is not
  read yet, so POS money is not matched to the bank.
* **Reversing a settlement journal.** A settlement the gateway reports as failed after it was journalled is not
  reversed in Tally; the accountant corrects it there.
* **Chargebacks and disputes** arrive as gateway adjustments and are listed as amount differences; there is no dispute
  workflow.
* **A payment on an older order of a prepaid booking** (the patient restarted the checkout and paid both) is found by
  the settlement match, and "Record the receipt" keeps it as a patient advance for staff to refund.
