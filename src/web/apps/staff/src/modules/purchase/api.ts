import type { Address, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { DocumentCreated } from '@/modules/inventory/api';

export const REQUISITION_STATUSES = ['Submitted', 'Approved', 'Rejected', 'Ordered', 'Cancelled'] as const;
export type RequisitionStatus = (typeof REQUISITION_STATUSES)[number];

export const ORDER_STATUSES = [
  'Draft',
  'Submitted',
  'Approved',
  'Sent',
  'PartiallyReceived',
  'Closed',
  'Cancelled',
] as const;
export type PurchaseOrderStatus = (typeof ORDER_STATUSES)[number];
export const orderStatusLabel: Record<PurchaseOrderStatus, string> = {
  Draft: 'Draft',
  Submitted: 'Awaiting approval',
  Approved: 'Approved',
  Sent: 'Sent to vendor',
  PartiallyReceived: 'Partly received',
  Closed: 'Closed',
  Cancelled: 'Cancelled',
};

export const GRN_STATUSES = ['Draft', 'Posted', 'Cancelled'] as const;
export type GoodsReceiptStatus = (typeof GRN_STATUSES)[number];

// ---------------------------------------------------------------------------------------------- vendors

export interface VendorListItem {
  id: string;
  code: string;
  name: string;
  gstin: string | null;
  drugLicenseNumber: string | null;
  drugLicenseValidUntil: string | null;
  drugLicenseExpired: boolean;
  contactPerson: string | null;
  phone: string | null;
  city: string | null;
  paymentTermsDays: number;
  rating: number;
  isActive: boolean;
}

export interface VendorDetail {
  id: string;
  code: string;
  name: string;
  gstin: string | null;
  pan: string | null;
  drugLicenseNumber: string | null;
  drugLicenseValidUntil: string | null;
  contactPerson: string | null;
  phone: string | null;
  email: string | null;
  address: Address;
  paymentTermsDays: number;
  rating: number;
  notes: string | null;
  isActive: boolean;
  version: number;
}

export type SaveVendorRequest = Omit<VendorDetail, 'id' | 'version' | 'address'> & {
  address: Address | null;
  version: number | null;
};

// ---------------------------------------------------------------------------------------------- requisitions

export interface RequisitionListItem {
  id: string;
  number: string;
  storeId: string;
  storeName: string;
  status: RequisitionStatus;
  requiredBy: string | null;
  lineCount: number;
  createdAt: string;
  requestedBy: string | null;
}

export interface RequisitionDetail {
  id: string;
  number: string;
  storeId: string;
  storeName: string;
  status: RequisitionStatus;
  requiredBy: string | null;
  remarks: string | null;
  rejectReason: string | null;
  purchaseOrderId: string | null;
  createdAt: string;
  decidedAt: string | null;
  lines: { id: string; itemId: string; itemName: string; quantity: number; remarks: string | null }[];
  version: number;
}

export interface CreateRequisitionRequest {
  storeId: string;
  requiredBy: string | null;
  remarks: string | null;
  lines: { itemId: string; quantity: number; remarks: string | null }[];
}

// ---------------------------------------------------------------------------------------------- purchase orders

export interface PurchaseOrderListItem {
  id: string;
  number: string;
  vendorId: string;
  vendorName: string;
  storeId: string;
  storeName: string;
  orderDate: string;
  expectedDeliveryDate: string | null;
  status: PurchaseOrderStatus;
  netAmount: number;
  lineCount: number;
}

export interface PurchaseOrderLineDto {
  id: string;
  itemId: string;
  itemName: string;
  purchaseUnit: string;
  quantity: number;
  freeQuantity: number;
  rate: number;
  discountPercent: number;
  gstRate: number;
  taxableAmount: number;
  cgstAmount: number;
  sgstAmount: number;
  igstAmount: number;
  lineTotal: number;
  receivedQuantity: number;
  pendingQuantity: number;
}

export interface PurchaseOrderDetail {
  id: string;
  number: string;
  vendorId: string;
  vendorName: string;
  vendorGstin: string | null;
  storeId: string;
  storeName: string;
  requisitionId: string | null;
  orderDate: string;
  expectedDeliveryDate: string | null;
  isInterState: boolean;
  paymentTermsDays: number;
  remarks: string | null;
  status: PurchaseOrderStatus;
  reviewNote: string | null;
  approvedAt: string | null;
  sentAt: string | null;
  closedAt: string | null;
  closeReason: string | null;
  grossAmount: number;
  discountAmount: number;
  taxableAmount: number;
  cgstAmount: number;
  sgstAmount: number;
  igstAmount: number;
  roundOff: number;
  netAmount: number;
  lines: PurchaseOrderLineDto[];
  version: number;
}

export interface PurchaseOrderLineInput {
  itemId: string;
  quantity: number;
  freeQuantity: number;
  rate: number;
  discountPercent: number;
  gstRate: number | null;
}

export interface SavePurchaseOrderRequest {
  vendorId: string;
  storeId: string;
  requisitionId: string | null;
  expectedDeliveryDate: string | null;
  paymentTermsDays: number | null;
  remarks: string | null;
  lines: PurchaseOrderLineInput[];
  version: number | null;
}

// ---------------------------------------------------------------------------------------------- goods receipts

export interface GoodsReceiptListItem {
  id: string;
  number: string;
  vendorId: string;
  vendorName: string;
  storeId: string;
  storeName: string;
  vendorInvoiceNumber: string;
  vendorInvoiceDate: string | null;
  receivedOn: string;
  status: GoodsReceiptStatus;
  netAmount: number;
  purchaseOrderId: string | null;
}

export interface GoodsReceiptLineDto {
  id: string;
  itemId: string;
  itemName: string;
  purchaseOrderLineId: string | null;
  batchNumber: string;
  manufactureDate: string | null;
  expiryDate: string | null;
  quantity: number;
  freeQuantity: number;
  rate: number;
  mrp: number;
  discountPercent: number;
  gstRate: number;
  taxableAmount: number;
  cgstAmount: number;
  sgstAmount: number;
  igstAmount: number;
  lineTotal: number;
  baseQuantity: number;
  unitCost: number;
  unitMrp: number;
  stockBatchId: string | null;
}

export interface GoodsReceiptDetail {
  id: string;
  number: string;
  vendorId: string;
  vendorName: string;
  vendorGstin: string | null;
  storeId: string;
  storeName: string;
  purchaseOrderId: string | null;
  vendorInvoiceNumber: string;
  vendorInvoiceDate: string | null;
  receivedOn: string;
  isInterState: boolean;
  status: GoodsReceiptStatus;
  remarks: string | null;
  postedAt: string | null;
  grossAmount: number;
  discountAmount: number;
  taxableAmount: number;
  cgstAmount: number;
  sgstAmount: number;
  igstAmount: number;
  roundOff: number;
  netAmount: number;
  lines: GoodsReceiptLineDto[];
  version: number;
}

export interface GoodsReceiptLineInput {
  itemId: string;
  purchaseOrderLineId: string | null;
  batchNumber: string | null;
  manufactureDate: string | null;
  expiryDate: string | null;
  quantity: number;
  freeQuantity: number;
  rate: number;
  mrp: number;
  discountPercent: number;
  gstRate: number | null;
  acceptShortExpiry: boolean;
}

export interface CreateGoodsReceiptRequest {
  vendorId: string;
  storeId: string;
  purchaseOrderId: string | null;
  vendorInvoiceNumber: string;
  vendorInvoiceDate: string | null;
  receivedOn: string | null;
  remarks: string | null;
  lines: GoodsReceiptLineInput[];
  post: boolean;
}

export interface GoodsReceiptCreated {
  id: string;
  number: string;
  status: GoodsReceiptStatus;
  netAmount: number;
}

// ---------------------------------------------------------------------------------------------- queries

const BASE = '/api/v1/purchase';
export const PAGE_SIZE = 25;

export interface OrderFilters {
  status?: PurchaseOrderStatus;
  vendorId?: string;
  storeId?: string;
  from?: string;
  to?: string;
  search?: string;
  page?: number;
}

export const purchaseKeys = { all: ['purchase'] as const };

export const purchaseQueries = {
  vendors: (filters: { search?: string; includeInactive?: boolean; page?: number; pageSize?: number }) =>
    queryOptions({
      queryKey: ['purchase', 'vendors', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<VendorListItem>>(`${BASE}/vendors`, { query: { pageSize: PAGE_SIZE, ...filters }, signal }),
      placeholderData: keepPreviousData,
    }),
  vendor: (id: string) =>
    queryOptions({
      queryKey: ['purchase', 'vendor', id],
      queryFn: ({ signal }) => api.get<VendorDetail>(`${BASE}/vendors/${id}`, { signal }),
    }),
  requisitions: (filters: { status?: RequisitionStatus; storeId?: string; search?: string; page?: number }) =>
    queryOptions({
      queryKey: ['purchase', 'requisitions', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<RequisitionListItem>>(`${BASE}/requisitions`, {
          query: { ...filters, pageSize: PAGE_SIZE },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  requisition: (id: string) =>
    queryOptions({
      queryKey: ['purchase', 'requisition', id],
      queryFn: ({ signal }) => api.get<RequisitionDetail>(`${BASE}/requisitions/${id}`, { signal }),
    }),
  orders: (filters: OrderFilters) =>
    queryOptions({
      queryKey: ['purchase', 'orders', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<PurchaseOrderListItem>>(`${BASE}/orders`, {
          query: { ...filters, pageSize: PAGE_SIZE },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  order: (id: string) =>
    queryOptions({
      queryKey: ['purchase', 'order', id],
      queryFn: ({ signal }) => api.get<PurchaseOrderDetail>(`${BASE}/orders/${id}`, { signal }),
    }),
  grns: (filters: {
    status?: GoodsReceiptStatus;
    vendorId?: string;
    storeId?: string;
    from?: string;
    to?: string;
    search?: string;
    page?: number;
  }) =>
    queryOptions({
      queryKey: ['purchase', 'grns', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<GoodsReceiptListItem>>(`${BASE}/grns`, {
          query: { ...filters, pageSize: PAGE_SIZE },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  grn: (id: string) =>
    queryOptions({
      queryKey: ['purchase', 'grn', id],
      queryFn: ({ signal }) => api.get<GoodsReceiptDetail>(`${BASE}/grns/${id}`, { signal }),
    }),
  /** Count only (page size 1) — for overview tiles. */
  count: (kind: 'requisitions' | 'orders' | 'grns', status: string) =>
    queryOptions({
      queryKey: ['purchase', 'count', kind, status],
      queryFn: ({ signal }) =>
        api.get<PagedResult<unknown>>(`${BASE}/${kind}`, { query: { status, pageSize: 1 }, signal }),
      select: (result) => result.totalCount,
      refetchInterval: 120_000,
    }),
};

export const purchaseApi = {
  saveVendor: async (id: string | null, body: SaveVendorRequest): Promise<string> =>
    id
      ? (await api.put<void>(`${BASE}/vendors/${id}`, body), id)
      : (await api.post<{ id: string }>(`${BASE}/vendors`, body)).id,
  createRequisition: (body: CreateRequisitionRequest) => api.post<DocumentCreated>(`${BASE}/requisitions`, body),
  requisitionFromReorder: (storeId: string) =>
    api.post<DocumentCreated>(`${BASE}/requisitions/from-reorder`, undefined, { query: { storeId } }),
  approveRequisition: (id: string) => api.post<void>(`${BASE}/requisitions/${id}/approve`),
  rejectRequisition: (id: string, reason: string) => api.post<void>(`${BASE}/requisitions/${id}/reject`, { reason }),
  cancelRequisition: (id: string, reason: string) => api.post<void>(`${BASE}/requisitions/${id}/cancel`, { reason }),
  createOrder: (body: SavePurchaseOrderRequest) => api.post<DocumentCreated>(`${BASE}/orders`, body),
  updateOrder: (id: string, body: SavePurchaseOrderRequest) => api.put<void>(`${BASE}/orders/${id}`, body),
  submitOrder: (id: string) => api.post<void>(`${BASE}/orders/${id}/submit`),
  approveOrder: (id: string) => api.post<void>(`${BASE}/orders/${id}/approve`),
  returnOrder: (id: string, reason: string) => api.post<void>(`${BASE}/orders/${id}/return`, { reason }),
  sendOrder: (id: string) => api.post<void>(`${BASE}/orders/${id}/send`),
  closeOrder: (id: string, reason: string) => api.post<void>(`${BASE}/orders/${id}/close`, { reason }),
  cancelOrder: (id: string, reason: string) => api.post<void>(`${BASE}/orders/${id}/cancel`, { reason }),
  createGrn: (body: CreateGoodsReceiptRequest) => api.post<GoodsReceiptCreated>(`${BASE}/grns`, body),
  postGrn: (id: string) => api.post<void>(`${BASE}/grns/${id}/post`),
  cancelGrn: (id: string, reason: string) => api.post<void>(`${BASE}/grns/${id}/cancel`, { reason }),
};
