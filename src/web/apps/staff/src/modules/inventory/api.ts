import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type {
  AbcClass,
  AbcVedCategory,
  AdjustmentReason,
  AdjustmentStatus,
  DrugSchedule,
  FsnClass,
  IndentPriority,
  IndentStatus,
  ItemType,
  StockCountStatus,
  StockMovementType,
  StoreType,
  TransferStatus,
  VedClass,
} from './labels';

// ---------------------------------------------------------------------------------------------- shared

/** `{ id, number }` returned when a stock document is created. */
export interface DocumentCreated {
  id: string;
  number: string;
}

export interface ReasonRequest {
  reason: string;
}

// ---------------------------------------------------------------------------------------------- catalogue

export interface ItemCategoryDto {
  id: string;
  code: string;
  name: string;
  defaultType: ItemType;
  description: string | null;
  isActive: boolean;
  version: number;
}

export interface SaveItemCategoryRequest {
  code: string;
  name: string;
  defaultType: ItemType;
  description: string | null;
  isActive: boolean;
  version: number | null;
}

export interface ItemListItem {
  id: string;
  code: string;
  name: string;
  genericName: string | null;
  strength: string | null;
  dosageForm: string | null;
  type: ItemType;
  categoryName: string | null;
  schedule: DrugSchedule;
  isControlled: boolean;
  isColdChain: boolean;
  baseUnit: string;
  purchaseUnit: string;
  gstRate: number;
  reorderLevel: number;
  isActive: boolean;
}

export interface ItemDetail {
  id: string;
  code: string;
  name: string;
  genericName: string | null;
  brandName: string | null;
  type: ItemType;
  categoryId: string | null;
  strength: string | null;
  dosageForm: string | null;
  packSize: string | null;
  baseUnit: string;
  purchaseUnit: string;
  purchaseUnitFactor: number;
  hsnCode: string | null;
  gstRate: number;
  schedule: DrugSchedule;
  isControlled: boolean;
  isColdChain: boolean;
  vedClass: VedClass;
  reorderLevel: number;
  reorderQuantity: number;
  minimumStock: number;
  maximumStock: number;
  manufacturer: string | null;
  barcode: string | null;
  isActive: boolean;
  requiresPrescription: boolean;
  requiresRegister: boolean;
  version: number;
}

export type SaveItemRequest = Omit<ItemDetail, 'id' | 'requiresPrescription' | 'requiresRegister' | 'version'> & { version: number | null };

/** Picker row: the item plus its sellable stock in the chosen store. */
export interface ItemLookup {
  id: string;
  code: string;
  name: string;
  genericName: string | null;
  strength: string | null;
  type: ItemType;
  schedule: DrugSchedule;
  isControlled: boolean;
  isColdChain: boolean;
  baseUnit: string;
  purchaseUnit: string;
  purchaseUnitFactor: number;
  gstRate: number;
  available: number | null;
  mrp: number | null;
  nearestExpiry: string | null;
}

export interface StoreDto {
  id: string;
  code: string;
  name: string;
  type: StoreType;
  departmentId: string | null;
  departmentName: string | null;
  inChargeUserId: string | null;
  inChargeName: string | null;
  location: string | null;
  isActive: boolean;
  version: number;
}

export interface SaveStoreRequest {
  code: string;
  name: string;
  type: StoreType;
  departmentId: string | null;
  inChargeUserId: string | null;
  location: string | null;
  isActive: boolean;
  version: number | null;
}

// ---------------------------------------------------------------------------------------------- stock

export interface StockSummaryRow {
  itemId: string;
  itemCode: string;
  itemName: string;
  type: ItemType;
  baseUnit: string;
  onHand: number;
  reserved: number;
  available: number;
  expiredQuantity: number;
  nearestExpiry: string | null;
  valueAtCost: number;
  reorderLevel: number;
  belowReorder: boolean;
}

/** One batch of an item in a store (FEFO order from the server). */
export interface BatchStock {
  batchId: string;
  storeId: string;
  itemId: string;
  batchNumber: string;
  expiryDate: string | null;
  mrp: number;
  unitCost: number;
  gstRate: number;
  onHand: number;
  reserved: number;
  available: number;
  isExpired: boolean;
  isBlocked: boolean;
  blockReason: string | null;
  daysToExpiry: number | null;
}

export interface LedgerRow {
  id: string;
  occurredAt: string;
  movementType: StockMovementType;
  itemId: string;
  itemName: string;
  batchId: string;
  batchNumber: string;
  expiryDate: string | null;
  quantityIn: number;
  quantityOut: number;
  balanceAfter: number;
  batchBalanceAfter: number;
  unitCost: number;
  referenceModule: string;
  referenceId: string | null;
  referenceNumber: string | null;
  remarks: string | null;
  userName: string | null;
  counterpartStoreId: string | null;
}

export interface ReservationRow {
  id: string;
  itemId: string;
  itemName: string;
  batchId: string;
  batchNumber: string;
  quantity: number;
  outstanding: number;
  referenceModule: string;
  referenceId: string;
  referenceNumber: string | null;
  createdAt: string;
}

// ---------------------------------------------------------------------------------------------- reports

export interface ExpiringBatch {
  batchId: string;
  storeId: string;
  storeName: string;
  itemId: string;
  itemCode: string;
  itemName: string;
  batchNumber: string;
  expiryDate: string;
  daysToExpiry: number;
  quantity: number;
  valueAtCost: number;
  valueAtMrp: number;
}

export interface ExpiryReport {
  asOf: string;
  withinDays: number;
  batchCount: number;
  valueAtCost: number;
  valueAtMrp: number;
  batches: ExpiringBatch[];
}

export interface ReorderRow {
  itemId: string;
  itemCode: string;
  itemName: string;
  type: ItemType;
  vedClass: VedClass;
  baseUnit: string;
  purchaseUnit: string;
  purchaseUnitFactor: number;
  available: number;
  onOrder: number;
  reorderLevel: number;
  reorderQuantity: number;
  maximumStock: number;
  suggestedQuantity: number;
  suggestedPurchaseUnits: number;
  lastUnitCost: number | null;
}

export interface ValuationRow {
  storeId: string;
  storeName: string;
  itemId: string;
  itemCode: string;
  itemName: string;
  type: ItemType;
  quantity: number;
  valueAtCost: number;
  valueAtMrp: number;
}

export interface ValuationReport {
  asOf: string;
  totalAtCost: number;
  totalAtMrp: number;
  byType: { type: ItemType; valueAtCost: number; valueAtMrp: number }[];
  rows: ValuationRow[];
}

export interface AnalysisRow {
  itemId: string;
  itemCode: string;
  itemName: string;
  type: ItemType;
  consumedQuantity: number;
  consumedValue: number;
  issueCount: number;
  sharePercent: number;
  cumulativePercent: number;
  abc: AbcClass;
  ved: VedClass;
  fsn: FsnClass;
  abcVedCategory: AbcVedCategory;
}

export interface ClassSummary {
  class: string;
  items: number;
  value: number;
  valuePercent: number;
}

export interface InventoryAnalysis {
  from: string;
  to: string;
  storeId: string | null;
  totalValue: number;
  abc: ClassSummary[];
  fsn: ClassSummary[];
  abcVed: ClassSummary[];
  items: AnalysisRow[];
}

export interface InventorySummary {
  stockValueAtCost: number;
  stockValueAtMrp: number;
  nearExpiryBatches: number;
  nearExpiryValue: number;
  expiredBatches: number;
  expiredValue: number;
  itemsBelowReorder: number;
  pendingIndents: number;
  pendingAdjustments: number;
  openPurchaseOrders: number;
  transfersInTransit: number;
}

// ---------------------------------------------------------------------------------------------- indents

export interface IndentListItem {
  id: string;
  number: string;
  fromStoreId: string;
  fromStoreName: string;
  toStoreId: string | null;
  toStoreName: string | null;
  departmentId: string | null;
  departmentName: string | null;
  priority: IndentPriority;
  status: IndentStatus;
  requestedAt: string;
  requestedBy: string | null;
  lineCount: number;
}

export interface IndentLineDto {
  id: string;
  itemId: string;
  itemName: string;
  requestedQuantity: number;
  approvedQuantity: number | null;
  issuedQuantity: number;
  pendingQuantity: number;
  reserved: number;
  availableInStore: number;
  remarks: string | null;
}

export interface IndentDetail {
  id: string;
  number: string;
  fromStoreId: string;
  fromStoreName: string;
  toStoreId: string | null;
  toStoreName: string | null;
  departmentId: string | null;
  departmentName: string | null;
  priority: IndentPriority;
  status: IndentStatus;
  requestedAt: string;
  requestedBy: string | null;
  remarks: string | null;
  decidedAt: string | null;
  decisionNote: string | null;
  lines: IndentLineDto[];
  version: number;
}

export interface IndentIssueDto {
  id: string;
  number: string;
  indentId: string;
  issuedAt: string;
  issuedBy: string | null;
  receivedByName: string | null;
  remarks: string | null;
  totalValue: number;
  lines: { itemId: string; itemName: string; batchNumber: string; expiryDate: string | null; quantity: number; unitCost: number; value: number }[];
}

export interface CreateIndentRequest {
  fromStoreId: string;
  toStoreId: string | null;
  departmentId: string | null;
  priority: IndentPriority;
  remarks: string | null;
  lines: { itemId: string; quantity: number; remarks: string | null }[];
}

export interface IssueIndentRequest {
  lines: { lineId: string; quantity: number; batchId: string | null }[];
  receivedByName: string | null;
  remarks: string | null;
}

// ---------------------------------------------------------------------------------------------- transfers

export interface TransferListItem {
  id: string;
  number: string;
  fromStoreId: string;
  fromStoreName: string;
  toStoreId: string;
  toStoreName: string;
  status: TransferStatus;
  dispatchedAt: string;
  receivedAt: string | null;
  totalValue: number;
  lineCount: number;
}

export interface TransferDetail {
  id: string;
  number: string;
  fromStoreId: string;
  fromStoreName: string;
  toStoreId: string;
  toStoreName: string;
  status: TransferStatus;
  dispatchedAt: string;
  receivedAt: string | null;
  cancelledAt: string | null;
  cancelReason: string | null;
  remarks: string | null;
  totalValue: number;
  lines: {
    id: string;
    itemId: string;
    itemName: string;
    batchNumber: string;
    expiryDate: string | null;
    quantity: number;
    mrp: number;
    purchaseRate: number;
    sourceBatchId: string;
    destinationBatchId: string | null;
  }[];
  version: number;
}

export interface DispatchTransferRequest {
  fromStoreId: string;
  toStoreId: string;
  remarks: string | null;
  lines: { itemId: string; quantity: number; batchId: string | null }[];
}

// ---------------------------------------------------------------------------------------------- adjustments and counts

export interface AdjustmentListItem {
  id: string;
  number: string;
  storeId: string;
  storeName: string;
  reason: AdjustmentReason;
  status: AdjustmentStatus;
  netValue: number;
  requestedAt: string;
  requestedBy: string | null;
  lineCount: number;
}

export interface AdjustmentLineDto {
  id: string;
  itemId: string;
  itemName: string;
  batchId: string;
  batchNumber: string;
  expiryDate: string | null;
  systemQuantity: number;
  quantityChange: number;
  unitCost: number;
  value: number;
}

export interface AdjustmentDetail {
  id: string;
  number: string;
  storeId: string;
  storeName: string;
  reason: AdjustmentReason;
  status: AdjustmentStatus;
  remarks: string | null;
  netValue: number;
  requestedAt: string;
  requestedBy: string | null;
  decidedAt: string | null;
  decidedBy: string | null;
  rejectReason: string | null;
  physicalCountId: string | null;
  lines: AdjustmentLineDto[];
  version: number;
}

export interface CreateAdjustmentRequest {
  storeId: string;
  reason: AdjustmentReason;
  remarks: string | null;
  lines: { batchId: string; quantityChange: number }[];
}

export interface CountListItem {
  id: string;
  number: string;
  storeId: string;
  storeName: string;
  status: StockCountStatus;
  startedAt: string;
  submittedAt: string | null;
  lineCount: number;
  countedLines: number;
  adjustmentId: string | null;
}

export interface CountLineDto {
  id: string;
  itemId: string;
  itemName: string;
  batchId: string;
  batchNumber: string;
  expiryDate: string | null;
  systemQuantity: number;
  countedQuantity: number | null;
  varianceQuantity: number | null;
  unitCost: number;
  remarks: string | null;
}

export interface CountDetail {
  id: string;
  number: string;
  storeId: string;
  storeName: string;
  status: StockCountStatus;
  startedAt: string;
  submittedAt: string | null;
  itemTypeFilter: ItemType | null;
  categoryFilterId: string | null;
  remarks: string | null;
  adjustmentId: string | null;
  lines: CountLineDto[];
  version: number;
}

export interface StartCountRequest {
  storeId: string;
  itemType: ItemType | null;
  categoryId: string | null;
  remarks: string | null;
}

// ---------------------------------------------------------------------------------------------- queries

const BASE = '/api/v1/inventory';
export const PAGE_SIZE = 25;

export interface ItemFilters {
  search?: string;
  type?: ItemType;
  categoryId?: string;
  schedule?: DrugSchedule;
  controlledOnly?: boolean;
  includeInactive?: boolean;
  page?: number;
}

export interface LedgerFilters {
  storeId?: string;
  itemId?: string;
  from?: string;
  to?: string;
  movement?: StockMovementType;
  search?: string;
  page?: number;
  desc?: boolean;
}

export const inventoryKeys = {
  all: ['inventory'] as const,
  stores: ['inventory', 'stores'] as const,
  items: ['inventory', 'items'] as const,
  stock: ['inventory', 'stock'] as const,
  batches: ['inventory', 'batches'] as const,
  reports: ['inventory', 'reports'] as const,
  indents: ['inventory', 'indents'] as const,
  transfers: ['inventory', 'transfers'] as const,
  adjustments: ['inventory', 'adjustments'] as const,
  counts: ['inventory', 'counts'] as const,
};

export const inventoryQueries = {
  categories: (activeOnly = false) =>
    queryOptions({
      queryKey: ['inventory', 'items', 'categories', activeOnly],
      queryFn: ({ signal }) => api.get<ItemCategoryDto[]>(`${BASE}/categories`, { query: { activeOnly }, signal }),
      staleTime: 5 * 60_000,
    }),
  items: (filters: ItemFilters) =>
    queryOptions({
      queryKey: ['inventory', 'items', 'list', filters],
      queryFn: ({ signal }) => api.get<PagedResult<ItemListItem>>(`${BASE}/items`, { query: { ...filters, pageSize: PAGE_SIZE }, signal }),
      placeholderData: keepPreviousData,
    }),
  item: (id: string) =>
    queryOptions({
      queryKey: ['inventory', 'items', 'detail', id],
      queryFn: ({ signal }) => api.get<ItemDetail>(`${BASE}/items/${id}`, { signal }),
      staleTime: 10 * 60_000,
    }),
  lookup: (search: string, storeId?: string | null, type?: ItemType, take = 20) =>
    queryOptions({
      queryKey: ['inventory', 'items', 'lookup', search, storeId ?? null, type ?? null, take],
      queryFn: ({ signal }) => api.get<ItemLookup[]>(`${BASE}/items/lookup`, { query: { search, storeId, type, take }, signal }),
      staleTime: 15_000,
    }),
  stores: (type?: StoreType, includeInactive = false) =>
    queryOptions({
      queryKey: ['inventory', 'stores', type ?? 'all', includeInactive],
      queryFn: ({ signal }) => api.get<StoreDto[]>(`${BASE}/stores`, { query: { type, includeInactive }, signal }),
      staleTime: 5 * 60_000,
    }),
  stock: (storeId: string, filters: { search?: string; type?: ItemType; belowReorder?: boolean; page?: number }) =>
    queryOptions({
      queryKey: ['inventory', 'stock', storeId, filters],
      queryFn: ({ signal }) => api.get<PagedResult<StockSummaryRow>>(`${BASE}/stock`, { query: { storeId, ...filters, pageSize: PAGE_SIZE }, signal }),
      placeholderData: keepPreviousData,
    }),
  batches: (storeId: string, itemId: string, includeEmpty = false) =>
    queryOptions({
      queryKey: ['inventory', 'batches', storeId, itemId, includeEmpty],
      queryFn: ({ signal }) => api.get<BatchStock[]>(`${BASE}/stock/batches`, { query: { storeId, itemId, includeEmpty }, signal }),
      staleTime: 10_000,
    }),
  reservations: (storeId: string, itemId?: string) =>
    queryOptions({
      queryKey: ['inventory', 'batches', 'reservations', storeId, itemId ?? null],
      queryFn: ({ signal }) => api.get<ReservationRow[]>(`${BASE}/stock/reservations`, { query: { storeId, itemId }, signal }),
    }),
  ledger: (filters: LedgerFilters) =>
    queryOptions({
      queryKey: ['inventory', 'stock', 'ledger', filters],
      queryFn: ({ signal }) => api.get<PagedResult<LedgerRow>>(`${BASE}/stock/ledger`, { query: { ...filters, pageSize: 50 }, signal }),
      placeholderData: keepPreviousData,
    }),
  summary: (storeId?: string | null) =>
    queryOptions({
      queryKey: ['inventory', 'reports', 'summary', storeId ?? null],
      queryFn: ({ signal }) => api.get<InventorySummary>(`${BASE}/reports/summary`, { query: { storeId }, signal }),
      refetchInterval: 120_000,
    }),
  nearExpiry: (storeId?: string | null, days = 90) =>
    queryOptions({
      queryKey: ['inventory', 'reports', 'near-expiry', storeId ?? null, days],
      queryFn: ({ signal }) => api.get<ExpiryReport>(`${BASE}/reports/near-expiry`, { query: { storeId, days }, signal }),
    }),
  expired: (storeId?: string | null) =>
    queryOptions({
      queryKey: ['inventory', 'reports', 'expired', storeId ?? null],
      queryFn: ({ signal }) => api.get<ExpiryReport>(`${BASE}/reports/expired`, { query: { storeId }, signal }),
    }),
  reorder: (storeId?: string | null) =>
    queryOptions({
      queryKey: ['inventory', 'reports', 'reorder', storeId ?? null],
      queryFn: ({ signal }) => api.get<ReorderRow[]>(`${BASE}/reports/reorder`, { query: { storeId }, signal }),
    }),
  valuation: (storeId?: string | null) =>
    queryOptions({
      queryKey: ['inventory', 'reports', 'valuation', storeId ?? null],
      queryFn: ({ signal }) => api.get<ValuationReport>(`${BASE}/reports/valuation`, { query: { storeId }, signal }),
    }),
  analysis: (from?: string, to?: string, storeId?: string | null) =>
    queryOptions({
      queryKey: ['inventory', 'reports', 'analysis', from ?? null, to ?? null, storeId ?? null],
      queryFn: ({ signal }) => api.get<InventoryAnalysis>(`${BASE}/reports/analysis`, { query: { from, to, storeId }, signal }),
      staleTime: 5 * 60_000,
    }),
  indents: (filters: { status?: IndentStatus; fromStoreId?: string; toStoreId?: string; openOnly?: boolean; search?: string; page?: number }) =>
    queryOptions({
      queryKey: ['inventory', 'indents', 'list', filters],
      queryFn: ({ signal }) => api.get<PagedResult<IndentListItem>>(`${BASE}/indents`, { query: { ...filters, pageSize: PAGE_SIZE }, signal }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  indent: (id: string) =>
    queryOptions({
      queryKey: ['inventory', 'indents', 'detail', id],
      queryFn: ({ signal }) => api.get<IndentDetail>(`${BASE}/indents/${id}`, { signal }),
    }),
  indentIssues: (id: string) =>
    queryOptions({
      queryKey: ['inventory', 'indents', 'issues', id],
      queryFn: ({ signal }) => api.get<IndentIssueDto[]>(`${BASE}/indents/${id}/issues`, { signal }),
    }),
  transfers: (filters: { status?: TransferStatus; storeId?: string; search?: string; page?: number }) =>
    queryOptions({
      queryKey: ['inventory', 'transfers', 'list', filters],
      queryFn: ({ signal }) => api.get<PagedResult<TransferListItem>>(`${BASE}/transfers`, { query: { ...filters, pageSize: PAGE_SIZE }, signal }),
      placeholderData: keepPreviousData,
    }),
  transfer: (id: string) =>
    queryOptions({
      queryKey: ['inventory', 'transfers', 'detail', id],
      queryFn: ({ signal }) => api.get<TransferDetail>(`${BASE}/transfers/${id}`, { signal }),
    }),
  adjustments: (filters: { status?: AdjustmentStatus; storeId?: string; reason?: AdjustmentReason; search?: string; page?: number }) =>
    queryOptions({
      queryKey: ['inventory', 'adjustments', 'list', filters],
      queryFn: ({ signal }) => api.get<PagedResult<AdjustmentListItem>>(`${BASE}/adjustments`, { query: { ...filters, pageSize: PAGE_SIZE }, signal }),
      placeholderData: keepPreviousData,
    }),
  adjustment: (id: string) =>
    queryOptions({
      queryKey: ['inventory', 'adjustments', 'detail', id],
      queryFn: ({ signal }) => api.get<AdjustmentDetail>(`${BASE}/adjustments/${id}`, { signal }),
    }),
  counts: (filters: { storeId?: string; status?: StockCountStatus; page?: number }) =>
    queryOptions({
      queryKey: ['inventory', 'counts', 'list', filters],
      queryFn: ({ signal }) => api.get<PagedResult<CountListItem>>(`${BASE}/counts`, { query: { ...filters, pageSize: PAGE_SIZE }, signal }),
      placeholderData: keepPreviousData,
    }),
  count: (id: string) =>
    queryOptions({
      queryKey: ['inventory', 'counts', 'detail', id],
      queryFn: ({ signal }) => api.get<CountDetail>(`${BASE}/counts/${id}`, { signal }),
    }),
};

/** The signed-in facility (letterhead and GSTIN for invoices, and the GST state for purchase previews). */
export interface FacilityInfo {
  id: string;
  code: string;
  name: string;
  legalName: string | null;
  address: { line1?: string | null; line2?: string | null; city?: string | null; district?: string | null; state?: string | null; pinCode?: string | null };
  phone: string | null;
  email: string | null;
  gstin: string | null;
  registrationNumber: string | null;
}

export function facilityQuery(facilityId: string | null | undefined) {
  return queryOptions({
    queryKey: ['masters', 'facility', facilityId ?? null],
    queryFn: ({ signal }) => api.get<FacilityInfo>(`/api/v1/masters/facilities/${facilityId}`, { signal }),
    enabled: Boolean(facilityId),
    staleTime: 30 * 60_000,
    retry: false,
  });
}

// ---------------------------------------------------------------------------------------------- commands

export const inventoryApi = {
  /** Creates (id null) or updates a category; returns the id. */
  saveCategory: async (id: string | null, body: SaveItemCategoryRequest): Promise<string> =>
    id ? (await api.put<void>(`${BASE}/categories/${id}`, body), id) : (await api.post<{ id: string }>(`${BASE}/categories`, body)).id,
  saveItem: async (id: string | null, body: SaveItemRequest): Promise<string> =>
    id ? (await api.put<void>(`${BASE}/items/${id}`, body), id) : (await api.post<{ id: string }>(`${BASE}/items`, body)).id,
  itemByCode: (code: string) => api.get<ItemDetail>(`${BASE}/items/by-code/${encodeURIComponent(code)}`),
  saveStore: async (id: string | null, body: SaveStoreRequest): Promise<string> =>
    id ? (await api.put<void>(`${BASE}/stores/${id}`, body), id) : (await api.post<{ id: string }>(`${BASE}/stores`, body)).id,
  blockBatch: (batchId: string, reason: string) => api.post<void>(`${BASE}/stock/batches/${batchId}/block`, { reason }),
  unblockBatch: (batchId: string) => api.post<void>(`${BASE}/stock/batches/${batchId}/unblock`),

  createIndent: (body: CreateIndentRequest) => api.post<DocumentCreated>(`${BASE}/indents`, body),
  approveIndent: (id: string, lines: { lineId: string; approvedQuantity: number }[] | null) => api.post<void>(`${BASE}/indents/${id}/approve`, { lines }),
  issueIndent: (id: string, body: IssueIndentRequest) => api.post<DocumentCreated>(`${BASE}/indents/${id}/issue`, body),
  rejectIndent: (id: string, reason: string) => api.post<void>(`${BASE}/indents/${id}/reject`, { reason }),
  closeIndent: (id: string, reason: string) => api.post<void>(`${BASE}/indents/${id}/close`, { reason }),
  cancelIndent: (id: string, reason: string) => api.post<void>(`${BASE}/indents/${id}/cancel`, { reason }),

  dispatchTransfer: (body: DispatchTransferRequest) => api.post<DocumentCreated>(`${BASE}/transfers`, body),
  receiveTransfer: (id: string) => api.post<void>(`${BASE}/transfers/${id}/receive`),
  cancelTransfer: (id: string, reason: string) => api.post<void>(`${BASE}/transfers/${id}/cancel`, { reason }),

  createAdjustment: (body: CreateAdjustmentRequest) => api.post<DocumentCreated>(`${BASE}/adjustments`, body),
  expiredWriteOff: (storeId: string) => api.post<DocumentCreated>(`${BASE}/adjustments/expired-write-off`, undefined, { query: { storeId } }),
  approveAdjustment: (id: string) => api.post<void>(`${BASE}/adjustments/${id}/approve`),
  rejectAdjustment: (id: string, reason: string) => api.post<void>(`${BASE}/adjustments/${id}/reject`, { reason }),

  startCount: (body: StartCountRequest) => api.post<DocumentCreated>(`${BASE}/counts`, body),
  recordCounts: (id: string, lines: { lineId: string; countedQuantity: number; remarks: string | null }[]) => api.put<void>(`${BASE}/counts/${id}/lines`, { lines }),
  submitCount: (id: string) => api.post<{ adjustmentId: string | null }>(`${BASE}/counts/${id}/submit`),
  cancelCount: (id: string) => api.post<void>(`${BASE}/counts/${id}/cancel`),
};
