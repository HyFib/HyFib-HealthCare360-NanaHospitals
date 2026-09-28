import type { PagedResult } from '@hc360/core';
import { infiniteQueryOptions, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type {
  CreateRefillRequest,
  PortalPrescriptionDetail,
  PortalPrescriptionListItem,
  PortalRefillView,
  RefillRequestCreated,
} from '@/lib/types';

const PAGE_SIZE = 20;
const id = (value: string) => encodeURIComponent(value);

export const prescriptionQueries = {
  list: (patientId: string) =>
    infiniteQueryOptions({
      queryKey: ['portal', patientId, 'prescriptions', 'list'],
      queryFn: ({ pageParam, signal }) =>
        api.get<PagedResult<PortalPrescriptionListItem>>('/api/v1/portal/prescriptions', {
          query: { patientId, page: pageParam, pageSize: PAGE_SIZE },
          signal,
        }),
      initialPageParam: 1,
      getNextPageParam: (last) => (last.page * last.pageSize < last.totalCount ? last.page + 1 : undefined),
    }),

  detail: (patientId: string, prescriptionId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'prescriptions', 'detail', prescriptionId],
      queryFn: ({ signal }) => api.get<PortalPrescriptionDetail>(`/api/v1/portal/prescriptions/${id(prescriptionId)}`, { signal }),
      staleTime: 5 * 60_000,
    }),

  /** Chronic prescriptions that can still be refilled, and refill requests already made (this person only). */
  refills: (patientId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'prescriptions', 'refills'],
      queryFn: ({ signal }) => api.get<PortalRefillView>('/api/v1/portal/refills', { signal }),
      select: (view): PortalRefillView => ({
        prescriptions: view.prescriptions.filter((row) => row.patientId === patientId),
        requests: view.requests.filter((row) => row.patientId === patientId),
      }),
    }),
};

export const prescriptionApi = {
  requestRefill: (request: CreateRefillRequest) => api.post<RefillRequestCreated>('/api/v1/portal/refills', request),
};
