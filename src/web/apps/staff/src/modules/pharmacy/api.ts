import type { Gender, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { BatchStock, ExpiringBatch } from '@/modules/inventory/api';
import type { DrugSchedule, StockMovementType } from '@/modules/inventory/labels';
import type { PatientSummary } from '@/modules/patients/api';

// ---------------------------------------------------------------------------------------------- enums

export const QUEUE_STATUSES = ['Pending', 'PartiallyDispensed', 'Dispensed', 'Cancelled'] as const;
export type QueueEntryStatus = (typeof QUEUE_STATUSES)[number];
export type QueueLineStatus = QueueEntryStatus;

export const PAYMENT_MODES = ['Cash', 'Card', 'Upi', 'Credit'] as const;
export type PaymentMode = (typeof PAYMENT_MODES)[number];
export const paymentModeLabel: Record<PaymentMode, string> = {
  Cash: 'Cash',
  Card: 'Card',
  Upi: 'UPI',
  Credit: 'Credit (pay later)',
};

export const SALE_STATUSES = ['Paid', 'PartiallyPaid', 'Credit', 'Cancelled'] as const;
export type SaleStatus = (typeof SALE_STATUSES)[number];
export type SaleType = 'Prescription' | 'Counter';

export const WARD_INDENT_STATUSES = ['Pending', 'PartiallyIssued', 'Issued', 'Cancelled'] as const;
export type WardIndentStatus = (typeof WARD_INDENT_STATUSES)[number];
export type IndentUrgency = 'Routine' | 'Stat';
export type ChargeReversal = 'Cancelled' | 'Credited';

export const REFILL_STATUSES = ['Requested', 'Approved', 'Rejected', 'Cancelled'] as const;
export type RefillStatus = (typeof REFILL_STATUSES)[number];
export type RefillChannel = 'Portal' | 'Staff';

// ---------------------------------------------------------------------------------------------- queue

export interface QueueListItem {
  id: string;
  prescriptionNumber: string;
  patientId: string;
  patientName: string;
  uhid: string;
  gender: Gender;
  age: string | null;
  prescriberName: string | null;
  isInpatient: boolean;
  status: QueueEntryStatus;
  lineCount: number;
  openLines: number;
  hasControlledDrugs: boolean;
  isRefill: boolean;
  signedAt: string;
  receivedAt: string;
  waitingMinutes: number;
}

export interface QueueLineDto {
  id: string;
  prescriptionLineId: string;
  drugItemId: string | null;
  drugName: string;
  genericName: string | null;
  strength: string | null;
  dosageForm: string | null;
  route: string | null;
  dose: string;
  frequency: string;
  durationDays: number;
  instructions: string | null;
  isControlledDrug: boolean;
  requestedQuantity: number;
  dispensedQuantity: number;
  remainingQuantity: number;
  status: QueueLineStatus;
  /** Sellable quantity of the prescribed item in the chosen store. */
  available: number | null;
  mrp: number | null;
  nearestExpiry: string | null;
}

export interface QueueEntryDetail {
  id: string;
  prescriptionId: string;
  prescriptionNumber: string;
  patient: PatientSummary;
  visitId: string | null;
  admissionId: string | null;
  practitionerId: string;
  prescriberName: string | null;
  prescriberRegistrationNumber: string | null;
  isInpatient: boolean;
  status: QueueEntryStatus;
  signedAt: string;
  receivedAt: string;
  closeReason: string | null;
  refillsAuthorized: number;
  refillsUsed: number;
  refillValidUntil: string | null;
  refillOfEntryId: string | null;
  lines: QueueLineDto[];
  version: number;
}

export interface SalePayment {
  mode: PaymentMode;
  amount: number;
  reference: string | null;
}

export interface DispenseLineRequest {
  queueLineId: string;
  itemId: string;
  quantity: number;
  batchId: string | null;
  substitutionReason: string | null;
  prescriberConsent: boolean;
}

export interface DispenseRequest {
  storeId: string;
  lines: DispenseLineRequest[];
  counsellingNotes: string | null;
  discountPercent: number;
  payments: SalePayment[];
  confirmControlledDrugs: boolean;
}

export interface PharmacySaleCreated {
  id: string;
  invoiceNumber: string;
  netAmount: number;
  paidAmount: number;
  dueAmount: number;
  status: SaleStatus;
}

// ---------------------------------------------------------------------------------------------- counter and invoices

/** Counter picker row: what the store can sell now, at which MRP, and whether a prescription is needed. */
export interface PharmacyStockRow {
  itemId: string;
  code: string;
  name: string;
  genericName: string | null;
  strength: string | null;
  schedule: DrugSchedule;
  isControlled: boolean;
  requiresPrescription: boolean;
  isColdChain: boolean;
  baseUnit: string;
  available: number;
  mrp: number | null;
  nearestExpiry: string | null;
  /** When the search was a scanned pharmacy tag: the batch it names, in this store. */
  scanned?: ScannedBatch | null;
}

/**
 * The batch a scanned pharmacy tag names (INT-BAR): `batchId` is the store's batch to hand out, or null with `problem`
 * saying why it cannot be (not in this store, none left, expired or blocked).
 */
export interface ScannedBatch {
  batchNumber: string;
  batchId: string | null;
  expiryDate: string | null;
  mrp: number | null;
  available: number;
  problem: string | null;
}

export interface ExternalPrescription {
  prescriberName: string;
  prescriberRegistrationNumber: string | null;
  prescriptionDate: string | null;
  reference: string | null;
}

export interface CounterSaleRequest {
  storeId: string;
  patientId: string | null;
  customerName: string | null;
  customerPhone: string | null;
  prescription: ExternalPrescription | null;
  lines: { itemId: string; quantity: number; batchId: string | null }[];
  discountPercent: number;
  payments: SalePayment[];
  confirmControlledDrugs: boolean;
}

export interface PharmacySaleListItem {
  id: string;
  invoiceNumber: string;
  type: SaleType;
  soldAt: string;
  storeId: string;
  patientId: string | null;
  customerName: string | null;
  prescriptionNumber: string | null;
  netAmount: number;
  paidAmount: number;
  dueAmount: number;
  returnedAmount: number;
  status: SaleStatus;
}

export interface PharmacySaleItemDto {
  id: string;
  itemId: string;
  itemName: string;
  genericName: string | null;
  hsnCode: string | null;
  schedule: DrugSchedule;
  batchNumber: string;
  expiryDate: string | null;
  quantity: number;
  unitMrp: number;
  gstRate: number;
  grossAmount: number;
  discountAmount: number;
  taxableAmount: number;
  cgstAmount: number;
  sgstAmount: number;
  netAmount: number;
  returnedQuantity: number;
  isSubstitution: boolean;
  prescribedDrugName: string | null;
  substitutionReason: string | null;
}

export interface PharmacyPaymentDto {
  id: string;
  mode: PaymentMode;
  amount: number;
  reference: string | null;
  receivedAt: string;
  isRefund: boolean;
}

export interface PharmacySaleDetail {
  id: string;
  invoiceNumber: string;
  type: SaleType;
  storeId: string;
  storeName: string;
  patientId: string | null;
  patientUhid: string | null;
  customerName: string | null;
  customerPhone: string | null;
  prescriptionNumber: string | null;
  prescriberName: string | null;
  prescriberRegistrationNumber: string | null;
  soldAt: string;
  soldBy: string | null;
  counsellingNotes: string | null;
  discountPercent: number;
  grossAmount: number;
  discountAmount: number;
  taxableAmount: number;
  cgstAmount: number;
  sgstAmount: number;
  roundOff: number;
  netAmount: number;
  paidAmount: number;
  dueAmount: number;
  returnedAmount: number;
  refundedAmount: number;
  status: SaleStatus;
  cancelReason: string | null;
  items: PharmacySaleItemDto[];
  payments: PharmacyPaymentDto[];
  version: number;
}

export interface SaleReturnRequest {
  lines: { saleItemId: string; quantity: number; restock: boolean }[];
  reason: string;
  refundMode: PaymentMode;
  refundReference: string | null;
}

export interface SaleReturnCreated {
  id: string;
  returnNumber: string;
  refundAmount: number;
  adjustedAgainstDue: number;
  cashRefund: number;
}

export interface SaleReturnDto {
  id: string;
  returnNumber: string;
  returnedAt: string;
  reason: string;
  refundAmount: number;
  adjustedAgainstDue: number;
  cashRefund: number;
  refundMode: PaymentMode;
  items: {
    saleItemId: string;
    itemName: string;
    batchNumber: string;
    quantity: number;
    refundAmount: number;
    restocked: boolean;
  }[];
}

export interface SaleFilters {
  from?: string;
  to?: string;
  status?: SaleStatus;
  type?: SaleType;
  storeId?: string;
  withDues?: boolean;
  search?: string;
  page?: number;
}

// ---------------------------------------------------------------------------------------------- in-patient pharmacy

export interface WardIndentListItem {
  id: string;
  number: string;
  patientId: string;
  patientName: string;
  uhid: string;
  admissionId: string;
  wardName: string | null;
  bedLabel: string | null;
  urgency: IndentUrgency;
  status: WardIndentStatus;
  raisedAt: string;
  raisedBy: string | null;
  lineCount: number;
}

export interface WardIndentLineDto {
  id: string;
  itemId: string;
  itemName: string;
  requestedQuantity: number;
  issuedQuantity: number;
  remainingQuantity: number;
  remarks: string | null;
  available: number | null;
}

export interface WardIndentDetail {
  id: string;
  number: string;
  patient: PatientSummary;
  admissionId: string;
  visitId: string | null;
  wardName: string | null;
  bedLabel: string | null;
  urgency: IndentUrgency;
  status: WardIndentStatus;
  raisedAt: string;
  raisedBy: string | null;
  prescriptionId: string | null;
  remarks: string | null;
  cancelReason: string | null;
  lines: WardIndentLineDto[];
  version: number;
}

export interface RaiseWardIndentRequest {
  patientId: string;
  admissionId: string;
  visitId: string | null;
  wardName: string | null;
  bedLabel: string | null;
  urgency: IndentUrgency;
  prescriptionId: string | null;
  remarks: string | null;
  lines: { itemId: string; quantity: number; remarks: string | null }[];
}

export interface IpIssueLineInput {
  itemId: string;
  quantity: number;
  batchId: string | null;
  wardIndentLineId: string | null;
  queueLineId: string | null;
  substitutionReason: string | null;
  prescriberConsent: boolean;
}

export interface IpIssueRequest {
  storeId: string;
  wardIndentId: string | null;
  queueEntryId: string | null;
  patientId: string | null;
  admissionId: string | null;
  visitId: string | null;
  lines: IpIssueLineInput[];
  confirmControlledDrugs: boolean;
}

export interface IpIssueCreated {
  id: string;
  number: string;
  totalAmount: number;
}

export interface IpIssueListItem {
  id: string;
  number: string;
  patientId: string;
  patientName: string;
  uhid: string;
  admissionId: string;
  wardIndentId: string | null;
  issuedAt: string;
  totalAmount: number;
  lineCount: number;
  issuedBy: string | null;
}

export interface IpIssueLineDto {
  id: string;
  itemId: string;
  itemName: string;
  batchNumber: string;
  expiryDate: string | null;
  quantity: number;
  unitMrp: number;
  amount: number;
  returnedQuantity: number;
  chargeId: string | null;
}

export interface WardReturnDto {
  id: string;
  number: string;
  returnedAt: string;
  reason: string;
  creditAmount: number;
  lines: {
    ipIssueLineId: string;
    itemName: string;
    batchNumber: string;
    quantity: number;
    creditAmount: number;
    restocked: boolean;
    chargeReversal: ChargeReversal;
  }[];
}

export interface IpIssueDetail {
  id: string;
  number: string;
  storeId: string;
  patient: PatientSummary;
  admissionId: string;
  wardIndentId: string | null;
  queueEntryId: string | null;
  issuedAt: string;
  issuedBy: string | null;
  totalAmount: number;
  lines: IpIssueLineDto[];
  returns: WardReturnDto[];
}

export interface WardReturnRequest {
  lines: { ipIssueLineId: string; quantity: number; restock: boolean }[];
  reason: string;
}

export interface WardReturnCreated {
  id: string;
  number: string;
  creditAmount: number;
}

/** Active admissions (Inpatient module) used to raise a ward indent. */
export interface AdmissionOption {
  id: string;
  admissionNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  status: string;
  wardName: string | null;
  bedLabel: string | null;
  consultantName: string | null;
}

// ---------------------------------------------------------------------------------------------- registers

export interface ControlledRegisterRow {
  id: string;
  occurredAt: string;
  storeId: string;
  storeName: string;
  itemId: string;
  itemName: string;
  schedule: DrugSchedule;
  isControlled: boolean;
  batchNumber: string;
  expiryDate: string | null;
  movementType: StockMovementType;
  quantityIn: number;
  quantityOut: number;
  balanceAfter: number;
  patientName: string | null;
  patientUhid: string | null;
  patientAddress: string | null;
  prescriberName: string | null;
  prescriberRegistrationNumber: string | null;
  prescriptionNumber: string | null;
  referenceNumber: string | null;
  recordedBy: string | null;
}

export interface ScheduleHRegisterRow {
  issuedAt: string;
  documentNumber: string;
  source: string;
  patientName: string | null;
  prescriberName: string | null;
  prescriptionNumber: string | null;
  itemName: string;
  batchNumber: string;
  quantity: number;
}

export interface RegisterFilters {
  storeId?: string;
  itemId?: string;
  schedule?: DrugSchedule;
  from?: string;
  to?: string;
  page?: number;
}

// ---------------------------------------------------------------------------------------------- refills and dashboard

export interface RefillRequestDto {
  id: string;
  number: string;
  queueEntryId: string;
  prescriptionNumber: string;
  patientId: string;
  patientName: string;
  uhid: string;
  channel: RefillChannel;
  status: RefillStatus;
  requestedAt: string;
  preferredPickupDate: string | null;
  notes: string | null;
  rejectReason: string | null;
  refillEntryId: string | null;
}

export interface PharmacyDashboard {
  pendingOutpatient: number;
  pendingInpatient: number;
  partiallyDispensed: number;
  pendingWardIndents: number;
  pendingRefills: number;
  salesToday: number;
  salesAmountToday: number;
  refundsToday: number;
  byMode: { mode: PaymentMode; amount: number; count: number }[];
  nearExpiryBatches: number;
  nearExpiryValueAtMrp: number;
  nearExpiry: ExpiringBatch[];
  averageWaitMinutes: number | null;
}

// ---------------------------------------------------------------------------------------------- queries

const BASE = '/api/v1/pharmacy';
export const PAGE_SIZE = 25;

export interface QueueFilters {
  status?: QueueEntryStatus;
  inpatient?: boolean;
  includeClosed?: boolean;
  search?: string;
  page?: number;
}

export const pharmacyKeys = {
  all: ['pharmacy'] as const,
  queue: ['pharmacy', 'queue'] as const,
  sales: ['pharmacy', 'sales'] as const,
  stock: ['pharmacy', 'stock'] as const,
  ward: ['pharmacy', 'ward'] as const,
  refills: ['pharmacy', 'refills'] as const,
  dashboard: ['pharmacy', 'dashboard'] as const,
};

export const pharmacyQueries = {
  dashboard: (storeId?: string | null) =>
    queryOptions({
      queryKey: ['pharmacy', 'dashboard', storeId ?? null],
      queryFn: ({ signal }) => api.get<PharmacyDashboard>(`${BASE}/dashboard`, { query: { storeId }, signal }),
      refetchInterval: 60_000,
    }),
  queue: (filters: QueueFilters) =>
    queryOptions({
      queryKey: ['pharmacy', 'queue', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<QueueListItem>>(`${BASE}/queue`, { query: { ...filters, pageSize: 50 }, signal }),
      placeholderData: keepPreviousData,
      refetchInterval: 30_000,
    }),
  entry: (id: string, storeId?: string | null) =>
    queryOptions({
      queryKey: ['pharmacy', 'queue', 'detail', id, storeId ?? null],
      queryFn: ({ signal }) => api.get<QueueEntryDetail>(`${BASE}/queue/${id}`, { query: { storeId }, signal }),
    }),
  stock: (storeId: string, search: string, inStockOnly = false, take = 30) =>
    queryOptions({
      queryKey: ['pharmacy', 'stock', storeId, search, inStockOnly, take],
      queryFn: ({ signal }) =>
        api.get<PharmacyStockRow[]>(`${BASE}/stock`, { query: { storeId, search, inStockOnly, take }, signal }),
      staleTime: 10_000,
    }),
  batches: (storeId: string, itemId: string) =>
    queryOptions({
      queryKey: ['pharmacy', 'stock', 'batches', storeId, itemId],
      queryFn: ({ signal }) => api.get<BatchStock[]>(`${BASE}/stock/batches`, { query: { storeId, itemId }, signal }),
      staleTime: 10_000,
    }),
  sales: (filters: SaleFilters) =>
    queryOptions({
      queryKey: ['pharmacy', 'sales', 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<PharmacySaleListItem>>(`${BASE}/sales`, {
          query: { ...filters, pageSize: PAGE_SIZE },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  sale: (id: string) =>
    queryOptions({
      queryKey: ['pharmacy', 'sales', 'detail', id],
      queryFn: ({ signal }) => api.get<PharmacySaleDetail>(`${BASE}/sales/${id}`, { signal }),
    }),
  saleReturns: (id: string) =>
    queryOptions({
      queryKey: ['pharmacy', 'sales', 'returns', id],
      queryFn: ({ signal }) => api.get<SaleReturnDto[]>(`${BASE}/sales/${id}/returns`, { signal }),
    }),
  wardIndents: (filters: {
    status?: WardIndentStatus;
    admissionId?: string;
    includeClosed?: boolean;
    search?: string;
    page?: number;
  }) =>
    queryOptions({
      queryKey: ['pharmacy', 'ward', 'indents', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<WardIndentListItem>>(`${BASE}/ward-indents`, {
          query: { ...filters, pageSize: PAGE_SIZE },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 30_000,
    }),
  wardIndent: (id: string, storeId?: string | null) =>
    queryOptions({
      queryKey: ['pharmacy', 'ward', 'indent', id, storeId ?? null],
      queryFn: ({ signal }) => api.get<WardIndentDetail>(`${BASE}/ward-indents/${id}`, { query: { storeId }, signal }),
    }),
  ipIssues: (filters: { admissionId?: string; patientId?: string; search?: string; page?: number }) =>
    queryOptions({
      queryKey: ['pharmacy', 'ward', 'issues', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<IpIssueListItem>>(`${BASE}/ip-issues`, {
          query: { ...filters, pageSize: PAGE_SIZE },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  ipIssue: (id: string) =>
    queryOptions({
      queryKey: ['pharmacy', 'ward', 'issue', id],
      queryFn: ({ signal }) => api.get<IpIssueDetail>(`${BASE}/ip-issues/${id}`, { signal }),
    }),
  controlledRegister: (filters: RegisterFilters) =>
    queryOptions({
      queryKey: ['pharmacy', 'registers', 'controlled', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<ControlledRegisterRow>>(`${BASE}/registers/controlled`, {
          query: { ...filters, pageSize: 50 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  scheduleHRegister: (filters: RegisterFilters) =>
    queryOptions({
      queryKey: ['pharmacy', 'registers', 'schedule-h', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<ScheduleHRegisterRow>>(`${BASE}/registers/schedule-h`, {
          query: { storeId: filters.storeId, from: filters.from, to: filters.to, page: filters.page, pageSize: 50 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  refills: (status: RefillStatus | undefined, page: number) =>
    queryOptions({
      queryKey: ['pharmacy', 'refills', status ?? 'all', page],
      queryFn: ({ signal }) =>
        api.get<PagedResult<RefillRequestDto>>(`${BASE}/refills`, {
          query: { status, page, pageSize: PAGE_SIZE },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  admissions: (search: string) =>
    queryOptions({
      queryKey: ['pharmacy', 'admissions', search],
      queryFn: ({ signal }) =>
        api.get<PagedResult<AdmissionOption>>('/api/v1/inpatient/admissions', {
          query: { search, activeOnly: true, pageSize: 10 },
          signal,
        }),
      staleTime: 30_000,
    }),
};

// ---------------------------------------------------------------------------------------------- commands

export const pharmacyApi = {
  dispense: (entryId: string, body: DispenseRequest) =>
    api.post<PharmacySaleCreated>(`${BASE}/queue/${entryId}/dispense`, body),
  closeEntry: (entryId: string, reason: string) => api.post<void>(`${BASE}/queue/${entryId}/close`, { reason }),
  authorizeRefills: (entryId: string, body: { refills: number; validUntil: string; note: string }) =>
    api.post<void>(`${BASE}/queue/${entryId}/refills`, body),
  counterSale: (body: CounterSaleRequest) => api.post<PharmacySaleCreated>(`${BASE}/sales/counter`, body),
  collectPayment: (saleId: string, body: SalePayment) => api.post<void>(`${BASE}/sales/${saleId}/payments`, body),
  returnSale: (saleId: string, body: SaleReturnRequest) =>
    api.post<SaleReturnCreated>(`${BASE}/sales/${saleId}/returns`, body),
  cancelSale: (saleId: string, reason: string) => api.post<void>(`${BASE}/sales/${saleId}/cancel`, { reason }),
  raiseWardIndent: (body: RaiseWardIndentRequest) =>
    api.post<{ id: string; number: string }>(`${BASE}/ward-indents`, body),
  cancelWardIndent: (id: string, reason: string) => api.post<void>(`${BASE}/ward-indents/${id}/cancel`, { reason }),
  issueToWard: (body: IpIssueRequest) => api.post<IpIssueCreated>(`${BASE}/ip-issues`, body),
  wardReturn: (issueId: string, body: WardReturnRequest) =>
    api.post<WardReturnCreated>(`${BASE}/ip-issues/${issueId}/returns`, body),
  requestRefill: (body: { queueEntryId: string; preferredPickupDate: string | null; notes: string | null }) =>
    api.post<{ id: string; number: string }>(`${BASE}/refills`, body),
  approveRefill: (id: string) => api.post<{ queueEntryId: string }>(`${BASE}/refills/${id}/approve`),
  rejectRefill: (id: string, reason: string) => api.post<void>(`${BASE}/refills/${id}/reject`, { reason }),
  exportRegister: (kind: 'controlled' | 'schedule-h', filters: RegisterFilters) =>
    api.blob(`${BASE}/registers/${kind}/export`, {
      query:
        kind === 'controlled'
          ? {
              storeId: filters.storeId,
              itemId: filters.itemId,
              schedule: filters.schedule,
              from: filters.from,
              to: filters.to,
            }
          : { storeId: filters.storeId, from: filters.from, to: filters.to },
      timeoutMs: 120_000,
    }),
};
