import type { PagedResult } from '@hc360/core';
import { infiniteQueryOptions, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { PortalBillDetail, PortalBillListItem, PortalPaymentOrder, PortalPaymentStatus } from '@/lib/types';

const PAGE_SIZE = 50;
const id = (value: string) => encodeURIComponent(value);

export const billQueries = {
  all: (patientId: string) => ['portal', patientId, 'bills'] as const,

  list: (patientId: string) =>
    infiniteQueryOptions({
      queryKey: ['portal', patientId, 'bills', 'list'],
      queryFn: ({ pageParam, signal }) =>
        api.get<PagedResult<PortalBillListItem>>('/api/v1/portal/bills', {
          query: { patientId, page: pageParam, pageSize: PAGE_SIZE },
          signal,
        }),
      initialPageParam: 1,
      getNextPageParam: (last) => (last.page * last.pageSize < last.totalCount ? last.page + 1 : undefined),
    }),

  detail: (patientId: string, billId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'bills', 'detail', billId],
      queryFn: ({ signal }) => api.get<PortalBillDetail>(`/api/v1/portal/bills/${id(billId)}`, { signal }),
    }),
};

export const billApi = {
  /** Creates a gateway checkout order for the bill's balance. */
  pay: (billId: string) => api.post<PortalPaymentOrder>(`/api/v1/portal/bills/${id(billId)}/pay`),
  /** Asks the server to confirm the payment with the gateway (server to server) and record it. */
  verify: (billId: string, orderRowId: string) =>
    api.post<PortalPaymentStatus>(`/api/v1/portal/bills/${id(billId)}/payments/${id(orderRowId)}/verify`),
};
