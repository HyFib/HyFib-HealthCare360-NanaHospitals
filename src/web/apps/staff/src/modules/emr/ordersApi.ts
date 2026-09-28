import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { TemplateServiceDto } from './api';
import type { ServiceCategory } from './orderCart';

export type OrderPriority = 'Routine' | 'Urgent' | 'Stat';
export type ClinicalOrderStatus = 'Active' | 'Completed' | 'Cancelled';
export type OrderItemStatus = 'Ordered' | 'InProgress' | 'Completed' | 'Cancelled';
export type OrderSource = 'Encounter' | 'HealthCheckup' | 'Direct';

export const ORDER_PRIORITIES: readonly OrderPriority[] = ['Routine', 'Urgent', 'Stat'];
export const orderPriorityLabel: Record<OrderPriority, string> = { Routine: 'Routine', Urgent: 'Urgent', Stat: 'STAT (immediately)' };

/** Service catalogue row (masters look-up). */
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

export interface OrderItemDto {
  id: string;
  serviceItemId: string;
  serviceCode: string;
  serviceName: string;
  category: ServiceCategory;
  quantity: number;
  status: OrderItemStatus;
  notes: string | null;
  statusChangedAt: string | null;
  completedAt: string | null;
  resultReference: string | null;
  cancelReason: string | null;
}

export interface OrderDto {
  id: string;
  orderNumber: string;
  patientId: string;
  encounterId: string | null;
  visitId: string | null;
  admissionId: string | null;
  orderingPractitionerId: string | null;
  orderingPractitionerName: string | null;
  priority: OrderPriority;
  status: ClinicalOrderStatus;
  source: OrderSource;
  orderedAt: string;
  clinicalNotes: string | null;
  items: OrderItemDto[];
}

export interface DuplicateOrderLine {
  serviceItemId: string;
  serviceName: string;
  orderNumber: string;
  orderedAt: string;
}

export interface PlaceOrderRequest {
  patientId: string;
  encounterId: string | null;
  visitId: string | null;
  admissionId: string | null;
  priority: OrderPriority;
  clinicalNotes: string | null;
  items: { serviceItemId: string; notes: string | null; quantity: number | null }[];
  orderSetId: string | null;
  confirmDuplicates: boolean;
}

export interface OrderSetDto {
  id: string;
  code: string;
  name: string;
  specialtyCode: string | null;
  description: string | null;
  services: TemplateServiceDto[];
  isActive: boolean;
  version: number;
}

export interface OrderListParams {
  encounterId?: string | null;
  status?: ClinicalOrderStatus;
  page?: number;
}

export const orderKeys = {
  list: (patientId: string, params: OrderListParams) => ['emr', 'patient', patientId, 'orders', params] as const,
  orderSets: (specialtyCode: string | null) => ['emr', 'order-sets', specialtyCode ?? 'all'] as const,
  services: (category: ServiceCategory | null, search: string) => ['lookups', 'services', category ?? 'all', search] as const,
};

export const orderQueries = {
  list: (patientId: string, params: OrderListParams = {}) =>
    queryOptions({
      queryKey: orderKeys.list(patientId, params),
      queryFn: ({ signal }) =>
        api.get<PagedResult<OrderDto>>('/api/v1/clinical/orders', {
          query: { patientId, encounterId: params.encounterId, status: params.status, page: params.page, pageSize: 20 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  orderSets: (specialtyCode: string | null = null) =>
    queryOptions({
      queryKey: orderKeys.orderSets(specialtyCode),
      queryFn: ({ signal }) => api.get<OrderSetDto[]>('/api/v1/clinical/order-sets', { query: { specialtyCode }, signal }),
      staleTime: 10 * 60_000,
    }),
  service: (id: string) =>
    queryOptions({
      queryKey: ['masters', 'service', id] as const,
      queryFn: ({ signal }) => api.get<ServiceItemDto>(`/api/v1/masters/services/${id}`, { signal }),
      staleTime: 10 * 60_000,
    }),
  services: (category: ServiceCategory | null, search: string) =>
    queryOptions({
      queryKey: orderKeys.services(category, search),
      queryFn: ({ signal }) =>
        api.get<ServiceItemDto[]>('/api/v1/masters/lookups/services', { query: { category: category ?? undefined, search: search || undefined }, signal }),
      staleTime: 10 * 60_000,
    }),
};

export const ordersApi = {
  place: (request: PlaceOrderRequest) => api.post<OrderDto>('/api/v1/clinical/orders', request),
  cancel: (id: string, reason: string) => api.post<OrderDto>(`/api/v1/clinical/orders/${id}/cancel`, { reason }),
  cancelItem: (itemId: string, reason: string) => api.post<OrderDto>(`/api/v1/clinical/orders/items/${itemId}/cancel`, { reason }),
  updateItemStatus: (itemId: string, status: 'InProgress' | 'Completed', resultReference: string | null) =>
    api.post<OrderDto>(`/api/v1/clinical/orders/items/${itemId}/status`, { status, resultReference }),
};
