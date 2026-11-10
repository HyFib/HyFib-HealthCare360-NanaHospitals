import type { PayerType } from '@hc360/core';
import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ApprovalRequestDto } from '@/modules/approvals/api';

/** Who pays for an out-patient visit, changed after registration: /api/v1/billing/visits/{visitId}/payer. */

export interface VisitPayerTerms {
  payerType: PayerType;
  payerId: string | null;
}

/** A change of the visit's payer waiting for a second person. */
export interface PendingVisitPayer {
  approvalId: string;
  asked: VisitPayerTerms;
  payerName: string | null;
  requestedBy: string;
  requestedAt: string;
}

export interface VisitPayerView {
  visitId: string;
  visitNumber: string;
  terms: VisitPayerTerms;
  payerName: string | null;
  pending: PendingVisitPayer | null;
  version: number;
}

export interface ChangeVisitPayerRequest {
  payerType: PayerType;
  payerId: string | null;
  reason: string | null;
  version: number;
}

/** Changed at once (what changed), or the approval the change waits for. */
export type VisitPayerOutcome = { kind: 'changed'; changed: string } | { kind: 'asked'; approval: ApprovalRequestDto };

const path = (visitId: string) => `/api/v1/billing/visits/${visitId}/payer`;

export const visitPayerKeys = {
  all: ['billing', 'visit-payer'] as const,
};

export const visitPayerQueries = {
  view: (visitId: string) =>
    queryOptions({
      queryKey: [...visitPayerKeys.all, visitId],
      queryFn: ({ signal }) => api.get<VisitPayerView>(path(visitId), { signal }),
    }),
};

export const visitPayerApi = {
  /**
   * Changes the visit's payer: at once when the payer stays of the same kind and no unbilled service gets cheaper (200),
   * otherwise asked of someone who approves discounts (202).
   */
  change: async (visitId: string, request: ChangeVisitPayerRequest): Promise<VisitPayerOutcome> => {
    const response = await api.post<{ changed?: string } & Partial<ApprovalRequestDto>>(path(visitId), request);
    return response.kind !== undefined && response.id !== undefined
      ? { kind: 'asked', approval: response as ApprovalRequestDto }
      : { kind: 'changed', changed: response.changed ?? '' };
  },
};
