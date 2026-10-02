import type { Address, PagedResult, PayerType } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { DiscountAuthority } from './billTotals';
import type {
  BillStatus,
  BillType,
  ChargeStatus,
  CreditNoteKind,
  DiscountApprovalStatus,
  GatewayOrderKind,
  GatewayOrderStatus,
  PaymentChannel,
  PaymentMode,
  PosTransactionStatus,
  PriceSource,
  ReceiptPurpose,
  ReceiptSource,
  RefundPurpose,
  RefundStatus,
  ServiceCategory,
  ShiftStatus,
} from './labels';
import type { PaymentLineInput } from './paymentRules';

/*
 * Billing API (src/server/Modules/HyFib.HealthCare360.Modules.Billing/Features). Types mirror the C# records in
 * camelCase; enums are strings, money is a number in rupees, DateOnly is "yyyy-MM-dd".
 */

// ------------------------------------------------------------------ bills

export interface BillListItem {
  id: string;
  billNumber: string | null;
  type: BillType;
  isFinalBill: boolean;
  status: BillStatus;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  visitId: string | null;
  admissionId: string | null;
  payerType: PayerType;
  payerId: string | null;
  payerName: string | null;
  netAmount: number;
  patientAmount: number;
  payerAmount: number;
  paidAmount: number;
  dueAmount: number;
  createdAt: string;
  finalizedAt: string | null;
}

export interface BillLineDto {
  id: string;
  chargeId: string;
  serviceItemId: string;
  serviceCode: string;
  description: string;
  category: ServiceCategory;
  sacCode: string | null;
  quantity: number;
  unitPrice: number;
  grossAmount: number;
  packageCoveredAmount: number;
  tariffDiscountAmount: number;
  lineDiscountAmount: number;
  billDiscountShare: number;
  taxRate: number;
  taxableAmount: number;
  cgstAmount: number;
  sgstAmount: number;
  netAmount: number;
  isDiscountAllowed: boolean;
  isPayerPayable: boolean;
  practitionerId: string | null;
  practitionerName: string | null;
  departmentId: string | null;
  serviceDate: string;
  isReturn: boolean;
}

export interface ReceiptSummary {
  id: string;
  receiptNumber: string;
  purpose: ReceiptPurpose;
  amount: number;
  modes: string;
  receivedAt: string;
}

export interface RefundSummary {
  id: string;
  refundNumber: string;
  amount: number;
  status: RefundStatus;
  mode: PaymentMode | null;
  requestedAt: string;
  paidAt: string | null;
}

export interface CreditNoteDto {
  id: string;
  number: string;
  kind: CreditNoteKind;
  amount: number;
  reason: string;
  issuedAt: string;
  issuedByName: string | null;
}

export interface DiscountApprovalDto {
  id: string;
  billId: string;
  billNumber: string | null;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  requestedAmount: number;
  discountBase: number;
  billDiscountPercent: number | null;
  billDiscountAmount: number | null;
  reason: string;
  status: DiscountApprovalStatus;
  requestedByName: string | null;
  requestedAt: string;
  decidedBy: string | null;
  decidedAt: string | null;
  remarks: string | null;
}

export interface BillDetail {
  id: string;
  billNumber: string | null;
  type: BillType;
  isFinalBill: boolean;
  status: BillStatus;
  facilityId: string;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  visitId: string | null;
  admissionId: string | null;
  ipAccountId: string | null;
  payerType: PayerType;
  payerId: string | null;
  payerName: string | null;
  tariffPlanId: string | null;
  tariffPlanName: string | null;
  policyId: string | null;
  preAuthorizationId: string | null;
  coPayPercent: number;
  payerCoverageLimit: number | null;
  billDiscountPercent: number | null;
  billDiscountValue: number | null;
  discountReason: string | null;
  approvedDiscountAmount: number;
  grossAmount: number;
  packageCoveredAmount: number;
  tariffDiscountAmount: number;
  lineDiscountAmount: number;
  billDiscountAmount: number;
  discountAmount: number;
  taxableAmount: number;
  cgstAmount: number;
  sgstAmount: number;
  taxAmount: number;
  roundOffAmount: number;
  netAmount: number;
  payerAmount: number;
  patientAmount: number;
  coPayAmount: number;
  nonPayableAmount: number;
  paidAmount: number;
  refundedAmount: number;
  creditAmount: number;
  dueAmount: number;
  refundableAmount: number;
  payerSettledAmount: number;
  payerDisallowedAmount: number;
  isTaxInvoice: boolean;
  createdAt: string;
  finalizedAt: string | null;
  cancelledAt: string | null;
  cancelReason: string | null;
  notes: string | null;
  lines: BillLineDto[];
  receipts: ReceiptSummary[];
  refunds: RefundSummary[];
  creditNotes: CreditNoteDto[];
  pendingDiscount: DiscountApprovalDto | null;
  version: number;
}

export interface CreateBillRequest {
  visitId: string | null;
  patientId: string | null;
  chargeIds: string[] | null;
}

export interface AddBillLineRequest {
  serviceItemId: string;
  quantity: number;
  unitPrice: number | null;
  description: string | null;
  practitionerId: string | null;
  departmentId: string | null;
  version: number;
}

export interface LineDiscountInput {
  lineId: string;
  percent: number | null;
  amount: number | null;
}

export interface ApplyDiscountRequest {
  billDiscountPercent: number | null;
  billDiscountAmount: number | null;
  lineDiscounts: LineDiscountInput[] | null;
  reason: string;
  version: number;
}

export interface DiscountOutcome {
  applied: boolean;
  approvalId: string | null;
  requestedDiscount: number;
  allowedWithoutApproval: number;
}

export interface DiscountLimitDto {
  id: string;
  userId: string;
  userName: string | null;
  maxPercent: number;
  maxAmount: number | null;
  notes: string | null;
  version: number;
}

export interface SaveDiscountLimitRequest {
  userId: string;
  maxPercent: number;
  maxAmount: number | null;
  notes: string | null;
}

export interface BillFilters {
  from?: string;
  to?: string;
  type?: BillType;
  status?: BillStatus;
  patientId?: string;
  visitId?: string;
  admissionId?: string;
  payerId?: string;
  withDueOnly?: boolean;
  search?: string;
  sort?: 'createdAt' | 'finalizedAt' | 'billNumber' | 'netAmount' | 'dueAmount';
  desc?: boolean;
  page?: number;
  pageSize?: number;
}

// ------------------------------------------------------------------ charges

export interface ChargeDto {
  id: string;
  patientId: string;
  visitId: string | null;
  admissionId: string | null;
  serviceItemId: string;
  serviceCode: string;
  description: string;
  category: ServiceCategory;
  quantity: number;
  unitPrice: number;
  grossAmount: number;
  discountAmount: number;
  taxAmount: number;
  netAmount: number;
  priceSource: PriceSource;
  sourceModule: string;
  sourceReference: string;
  practitionerId: string | null;
  practitionerName: string | null;
  departmentId: string | null;
  serviceDate: string;
  status: ChargeStatus;
  billId: string | null;
  cancelReason: string | null;
}

// ------------------------------------------------------------------ receipts, dues, advances, refunds

export interface ReceiptPaymentDto {
  mode: PaymentMode;
  amount: number;
  reference: string | null;
  cardLast4: string | null;
  cardNetwork: string | null;
  chequeNumber: string | null;
  chequeBank: string | null;
  chequeDate: string | null;
}

export interface ReceiptDto {
  id: string;
  receiptNumber: string;
  purpose: ReceiptPurpose;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  billId: string | null;
  billNumber: string | null;
  ipAccountId: string | null;
  amount: number;
  source: ReceiptSource;
  counter: string | null;
  receivedByName: string | null;
  receivedAt: string;
  notes: string | null;
  payments: ReceiptPaymentDto[];
}

export interface ReceiptListItem {
  id: string;
  receiptNumber: string;
  purpose: ReceiptPurpose;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  billId: string | null;
  amount: number;
  modes: string;
  counter: string | null;
  receivedByName: string | null;
  receivedAt: string;
}

export interface ReceiptFilters {
  from?: string;
  to?: string;
  patientId?: string;
  billId?: string;
  ipAccountId?: string;
  purpose?: ReceiptPurpose;
  counter?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface CollectPaymentRequest {
  billId: string;
  payments: PaymentLineInput[];
  notes: string | null;
}

export interface CollectDepositRequest {
  payments: PaymentLineInput[];
  notes: string | null;
}

export interface PatientAdvanceDto {
  id: string;
  patientId: string;
  receiptNumber: string;
  appointmentId: string | null;
  appointmentNumber: string | null;
  provider: string | null;
  amount: number;
  adjustedAmount: number;
  refundedAmount: number;
  balance: number;
  refundPending: number;
  receivedAt: string;
}

export interface PatientDues {
  patientId: string;
  totalDue: number;
  advanceBalance: number;
  bills: BillListItem[];
  advances: PatientAdvanceDto[];
}

export interface RefundDto {
  id: string;
  refundNumber: string;
  purpose: RefundPurpose;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  billId: string | null;
  ipAccountId: string | null;
  amount: number;
  reason: string;
  status: RefundStatus;
  requestedByName: string | null;
  requestedAt: string;
  decidedBy: string | null;
  decidedAt: string | null;
  decisionRemarks: string | null;
  mode: PaymentMode | null;
  reference: string | null;
  paidAt: string | null;
  advanceId: string | null;
}

export interface RequestRefundRequest {
  billId: string | null;
  ipAccountId: string | null;
  advanceId: string | null;
  amount: number;
  reason: string;
}

export interface RefundFilters {
  status?: RefundStatus;
  patientId?: string;
  billId?: string;
  page?: number;
  pageSize?: number;
}

// ------------------------------------------------------------------ cash counter

export interface ShiftModeTotalDto {
  mode: PaymentMode;
  collected: number;
  refunded: number;
  net: number;
}

export interface ShiftDto {
  id: string;
  userId: string;
  userName: string;
  counter: string;
  status: ShiftStatus;
  openedAt: string;
  closedAt: string | null;
  openingFloat: number;
  expectedCash: number;
  declaredCash: number | null;
  variance: number | null;
  totals: ShiftModeTotalDto[];
  receiptCount: number;
  notes: string | null;
  version: number;
}

export interface DayEndCashierRow {
  shiftId: string;
  userId: string;
  userName: string;
  counter: string;
  status: ShiftStatus;
  openedAt: string;
  closedAt: string | null;
  collected: number;
  refunded: number;
  expectedCash: number;
  declaredCash: number | null;
  variance: number | null;
}

export interface DayEndReport {
  date: string;
  counter: string | null;
  shifts: DayEndCashierRow[];
  byMode: ShiftModeTotalDto[];
  totalCollected: number;
  totalRefunded: number;
  netCollection: number;
  depositsCollected: number;
  billsFinalized: number;
  billedAmount: number;
  discountGiven: number;
  billsCancelled: number;
  cancelledAmount: number;
  openShifts: number;
}

export interface ShiftFilters {
  date?: string;
  status?: ShiftStatus;
  counter?: string;
  page?: number;
}

// ------------------------------------------------------------------ POS terminals and payment links

export interface PosSaleDto {
  id: string;
  provider: string;
  terminalId: string;
  transactionId: string;
  amount: number;
  mode: PaymentMode;
  status: PosTransactionStatus;
  approvalCode: string | null;
  cardLast4: string | null;
  rrn: string | null;
  message: string | null;
  receiptId: string | null;
  createdAt: string;
  completedAt: string | null;
}

export interface StartPosSaleRequest {
  billId: string | null;
  ipAccountId: string | null;
  amount: number;
  terminalId: string;
  mode: 'Card' | 'Upi';
}

export interface CreatePaymentLinkRequest {
  billId: string | null;
  ipAccountId: string | null;
  amount: number | null;
  sendSms: boolean;
  sendWhatsApp: boolean;
}

export interface GatewayOrderDto {
  id: string;
  provider: string;
  kind: GatewayOrderKind;
  orderId: string;
  amount: number;
  currency: string;
  status: GatewayOrderStatus;
  linkUrl: string | null;
  checkoutKey: string | null;
  billId: string | null;
  ipAccountId: string | null;
  receiptId: string | null;
  channel: PaymentChannel;
  createdAt: string;
  paidAt: string | null;
}

export interface PaymentLinkFilters {
  billId?: string;
  ipAccountId?: string;
  status?: GatewayOrderStatus;
  page?: number;
}

// ------------------------------------------------------------------ masters used by billing screens

/** ServiceItemDto from /api/v1/masters/lookups/services (catalogue look-up for all staff). */
export interface ServiceItemDto {
  id: string;
  code: string;
  name: string;
  shortName: string | null;
  category: ServiceCategory;
  departmentId: string | null;
  basePrice: number;
  taxRate: number;
  sacCode: string | null;
  isDiscountAllowed: boolean;
  isPriceEditable: boolean;
  description: string | null;
  isActive: boolean;
  version: number;
}

export interface PriceQuoteDto {
  serviceItemId: string;
  serviceCode: string;
  serviceName: string;
  category: ServiceCategory;
  unitPrice: number;
  source: PriceSource;
  tariffPlanId: string | null;
  tariffPlanName: string | null;
  multiplier: number;
  taxRate: number;
  isPriceEditable: boolean;
  isDiscountAllowed: boolean;
}

/** FacilityDto from /api/v1/masters/facilities/{id}: the hospital header on bills and receipts. */
export interface FacilityDto {
  id: string;
  code: string;
  name: string;
  legalName: string | null;
  address: Address;
  phone: string | null;
  email: string | null;
  website: string | null;
  gstin: string | null;
  pan: string | null;
  registrationNumber: string | null;
  nabhNumber: string | null;
  letterheadNote: string | null;
}

// ------------------------------------------------------------------ query keys and queries

export const billingKeys = {
  all: ['billing'] as const,
  bills: () => ['billing', 'bills'] as const,
  bill: (id: string) => ['billing', 'bills', 'detail', id] as const,
  charges: () => ['billing', 'charges'] as const,
  dues: (patientId: string) => ['billing', 'dues', patientId] as const,
  receipts: () => ['billing', 'receipts'] as const,
  refunds: () => ['billing', 'refunds'] as const,
  discounts: () => ['billing', 'discounts'] as const,
  shifts: () => ['billing', 'shifts'] as const,
  links: () => ['billing', 'payment-links'] as const,
};

export const billingQueries = {
  bills: (filters: BillFilters) =>
    queryOptions({
      queryKey: [...billingKeys.bills(), 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<BillListItem>>('/api/v1/billing/bills', { query: { pageSize: 25, ...filters }, signal }),
      placeholderData: keepPreviousData,
    }),
  bill: (id: string) =>
    queryOptions({
      queryKey: billingKeys.bill(id),
      queryFn: ({ signal }) => api.get<BillDetail>(`/api/v1/billing/bills/${id}`, { signal }),
    }),
  unbilledCharges: (patientId: string) =>
    queryOptions({
      queryKey: [...billingKeys.charges(), 'unbilled', patientId],
      queryFn: ({ signal }) =>
        api.get<PagedResult<ChargeDto>>('/api/v1/billing/charges', {
          query: { patientId, unbilledOnly: true, pageSize: 200 },
          signal,
        }),
    }),
  dues: (patientId: string) =>
    queryOptions({
      queryKey: billingKeys.dues(patientId),
      queryFn: ({ signal }) => api.get<PatientDues>(`/api/v1/billing/patients/${patientId}/dues`, { signal }),
    }),
  advances: (patientId: string, openOnly: boolean) =>
    queryOptions({
      queryKey: [...billingKeys.dues(patientId), 'advances', openOnly],
      queryFn: ({ signal }) =>
        api.get<PatientAdvanceDto[]>(`/api/v1/billing/patients/${patientId}/advances`, { query: { openOnly }, signal }),
    }),
  receipts: (filters: ReceiptFilters) =>
    queryOptions({
      queryKey: [...billingKeys.receipts(), 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<ReceiptListItem>>('/api/v1/billing/receipts', {
          query: { pageSize: 25, ...filters },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  receipt: (id: string) =>
    queryOptions({
      queryKey: [...billingKeys.receipts(), 'detail', id],
      queryFn: ({ signal }) => api.get<ReceiptDto>(`/api/v1/billing/receipts/${id}`, { signal }),
      staleTime: 5 * 60_000,
    }),
  refunds: (filters: RefundFilters) =>
    queryOptions({
      queryKey: [...billingKeys.refunds(), 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<RefundDto>>('/api/v1/billing/refunds', { query: { pageSize: 25, ...filters }, signal }),
      placeholderData: keepPreviousData,
    }),
  refund: (id: string) =>
    queryOptions({
      queryKey: [...billingKeys.refunds(), 'detail', id],
      queryFn: ({ signal }) => api.get<RefundDto>(`/api/v1/billing/refunds/${id}`, { signal }),
    }),
  discountApprovals: (status: DiscountApprovalStatus, page: number, pageSize = 25) =>
    queryOptions({
      queryKey: [...billingKeys.discounts(), 'approvals', status, page, pageSize],
      queryFn: ({ signal }) =>
        api.get<PagedResult<DiscountApprovalDto>>('/api/v1/billing/discount-approvals', {
          query: { status, page, pageSize },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  discountLimits: () =>
    queryOptions({
      queryKey: [...billingKeys.discounts(), 'limits'],
      queryFn: ({ signal }) => api.get<DiscountLimitDto[]>('/api/v1/billing/discount-limits', { signal }),
    }),
  myDiscountAuthority: () =>
    queryOptions({
      queryKey: [...billingKeys.discounts(), 'me'],
      queryFn: ({ signal }) => api.get<DiscountAuthority>('/api/v1/billing/discount-limits/me', { signal }),
      staleTime: 5 * 60_000,
    }),
  currentShift: () =>
    queryOptions({
      queryKey: [...billingKeys.shifts(), 'current'],
      // 204 (no open shift) arrives as undefined; React Query needs null.
      queryFn: async ({ signal }) =>
        (await api.get<ShiftDto | undefined>('/api/v1/billing/shifts/current', { signal })) ?? null,
      refetchInterval: 60_000,
    }),
  shifts: (filters: ShiftFilters) =>
    queryOptions({
      queryKey: [...billingKeys.shifts(), 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<ShiftDto>>('/api/v1/billing/shifts', { query: { pageSize: 25, ...filters }, signal }),
      placeholderData: keepPreviousData,
    }),
  shift: (id: string) =>
    queryOptions({
      queryKey: [...billingKeys.shifts(), 'detail', id],
      queryFn: ({ signal }) => api.get<ShiftDto>(`/api/v1/billing/shifts/${id}`, { signal }),
    }),
  dayEnd: (date: string, counter?: string) =>
    queryOptions({
      queryKey: [...billingKeys.shifts(), 'day-end', date, counter ?? ''],
      queryFn: ({ signal }) =>
        api.get<DayEndReport>('/api/v1/billing/reports/day-end', { query: { date, counter }, signal }),
    }),
  paymentLinks: (filters: PaymentLinkFilters) =>
    queryOptions({
      queryKey: [...billingKeys.links(), 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<GatewayOrderDto>>('/api/v1/billing/payment-links', {
          query: { pageSize: 25, ...filters },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  posSale: (id: string) =>
    queryOptions({
      queryKey: ['billing', 'pos', id],
      queryFn: ({ signal }) => api.get<PosSaleDto>(`/api/v1/billing/pos/sales/${id}`, { signal }),
    }),
  facility: (id: string) =>
    queryOptions({
      queryKey: ['billing', 'facility', id],
      queryFn: ({ signal }) => api.get<FacilityDto>(`/api/v1/masters/facilities/${id}`, { signal }),
      staleTime: 30 * 60_000,
    }),
};

// ------------------------------------------------------------------ writes

/** Reuse `idempotencyKey` when the cashier retries the very same money operation (e.g. after a WiFi timeout). */
export interface WriteOptions {
  idempotencyKey?: string;
}

export const billingApi = {
  createBill: (request: CreateBillRequest) => api.post<BillDetail>('/api/v1/billing/bills', request),
  addLine: (billId: string, request: AddBillLineRequest) =>
    api.post<BillDetail>(`/api/v1/billing/bills/${billId}/lines`, request),
  removeLine: (billId: string, lineId: string, version: number) =>
    api.delete<BillDetail>(`/api/v1/billing/bills/${billId}/lines/${lineId}`, { query: { version } }),
  refresh: (billId: string, version: number) =>
    api.post<BillDetail>(`/api/v1/billing/bills/${billId}/refresh`, { version }),
  applyDiscount: (billId: string, request: ApplyDiscountRequest) =>
    api.post<DiscountOutcome>(`/api/v1/billing/bills/${billId}/discount`, request),
  finalize: (billId: string, version: number) =>
    api.post<BillDetail>(`/api/v1/billing/bills/${billId}/finalize`, { version }),
  cancel: (billId: string, reason: string, version: number) =>
    api.post<BillDetail>(`/api/v1/billing/bills/${billId}/cancel`, { reason, version }),
  discard: (billId: string, version: number) =>
    api.delete<void>(`/api/v1/billing/bills/${billId}`, { query: { version } }),
  issueCreditNote: (billId: string, kind: CreditNoteKind, amount: number, reason: string) =>
    api.post<BillDetail>(`/api/v1/billing/bills/${billId}/credit-notes`, { kind, amount, reason }),
  adjustAdvance: (
    billId: string,
    advanceId: string,
    amount: number | null,
    version: number,
    options: WriteOptions = {},
  ) => api.post<ReceiptDto>(`/api/v1/billing/bills/${billId}/adjust-advance`, { advanceId, amount, version }, options),
  collectPayment: (request: CollectPaymentRequest, options: WriteOptions = {}) =>
    api.post<ReceiptDto>('/api/v1/billing/receipts', request, options),
  collectDeposit: (ipAccountId: string, request: CollectDepositRequest, options: WriteOptions = {}) =>
    api.post<ReceiptDto>(`/api/v1/billing/ip-accounts/${ipAccountId}/deposits`, request, options),
  requestRefund: (request: RequestRefundRequest) => api.post<{ id: string }>('/api/v1/billing/refunds', request),
  approveRefund: (id: string, remarks: string | null) =>
    api.post<void>(`/api/v1/billing/refunds/${id}/approve`, { remarks }),
  rejectRefund: (id: string, reason: string) => api.post<void>(`/api/v1/billing/refunds/${id}/reject`, { reason }),
  payRefund: (id: string, mode: PaymentMode, reference: string | null, options: WriteOptions = {}) =>
    api.post<void>(`/api/v1/billing/refunds/${id}/pay`, { mode, reference }, options),
  approveDiscount: (id: string, remarks: string | null) =>
    api.post<void>(`/api/v1/billing/discount-approvals/${id}/approve`, { remarks }),
  rejectDiscount: (id: string, remarks: string | null) =>
    api.post<void>(`/api/v1/billing/discount-approvals/${id}/reject`, { remarks }),
  saveDiscountLimit: (request: SaveDiscountLimitRequest) => api.put<void>('/api/v1/billing/discount-limits', request),
  deleteDiscountLimit: (id: string) => api.delete<void>(`/api/v1/billing/discount-limits/${id}`),
  openShift: (counter: string, openingFloat: number) =>
    api.post<ShiftDto>('/api/v1/billing/shifts/open', { counter, openingFloat }),
  closeShift: (id: string, declaredCash: number, notes: string | null, version: number) =>
    api.post<ShiftDto>(`/api/v1/billing/shifts/${id}/close`, { declaredCash, notes, version }),
  startPosSale: (request: StartPosSaleRequest, options: WriteOptions = {}) =>
    api.post<PosSaleDto>('/api/v1/billing/pos/sales', request, options),
  cancelPosSale: (id: string) => api.post<PosSaleDto>(`/api/v1/billing/pos/sales/${id}/cancel`),
  createPaymentLink: (request: CreatePaymentLinkRequest, options: WriteOptions = {}) =>
    api.post<GatewayOrderDto>('/api/v1/billing/payment-links', request, options),
  refreshPaymentLink: (id: string) => api.post<GatewayOrderDto>(`/api/v1/billing/payment-links/${id}/refresh`),
};

/**
 * The A4 bill (tax invoice or bill of supply with GST details; an estimate while it is a draft) as the server prints it
 * and makes its PDF (see `@/lib/documents`).
 */
export const billDocument = (billId: string) => `/api/v1/billing/bills/${billId}`;

/** Catalogue search for the "add service" box (active services, at most 50). */
export function searchServices(search: string, category?: ServiceCategory): Promise<ServiceItemDto[]> {
  return api.get<ServiceItemDto[]>('/api/v1/masters/lookups/services', { query: { search, category } });
}

/** The price a service would be charged at for a visit, an admission or a tariff plan. */
export function quotePrice(params: {
  serviceItemId: string;
  visitId?: string | null;
  admissionId?: string | null;
  tariffPlanId?: string | null;
  date?: string;
}) {
  return api.get<PriceQuoteDto>('/api/v1/billing/tariffs/quote', { query: { ...params } });
}

/** The draft-exists conflict carries the existing draft's id (`data.billId`). */
export function existingDraftId(error: unknown): string | null {
  if (
    typeof error === 'object' &&
    error !== null &&
    'code' in error &&
    (error as { code?: string }).code === 'billing.draft_exists'
  ) {
    const data = (error as { data?: unknown }).data;
    if (
      typeof data === 'object' &&
      data !== null &&
      'billId' in data &&
      typeof (data as { billId: unknown }).billId === 'string'
    ) {
      return (data as { billId: string }).billId;
    }
  }
  return null;
}
