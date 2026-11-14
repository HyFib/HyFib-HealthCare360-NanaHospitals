import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ConfirmRecordsRequest, RecordsConfirmed, RecordsToConfirm } from '@/lib/types';

const base = '/api/v1/portal/records-to-confirm';

/**
 * Records on this mobile number that wait to be confirmed before the portal shows them (ASVS V6.6.1): the desk may have
 * typed a digit wrong, or the number may have belonged to someone else before. The account's, not one person's, so not
 * keyed by the person being viewed. Confirming one changes whose records the account sees: refresh the profiles too.
 */
export const confirmQueries = {
  all: ['portal', 'records-to-confirm'] as const,
  status: () =>
    queryOptions({
      queryKey: ['portal', 'records-to-confirm'],
      queryFn: ({ signal }) => api.get<RecordsToConfirm>(base, { signal }),
      staleTime: 60_000,
    }),
};

export const confirmApi = {
  /**
   * Confirms the waiting records the details match. 422 `portal.confirm_no_match` with `data.attemptsLeft` for details
   * that match none, 422 `portal.confirm_throttled` with `data.retryAfterSeconds` once the number has used its five tries
   * of the day, 422 `portal.confirm_nothing` when nothing waits.
   */
  confirm: (request: ConfirmRecordsRequest) => api.post<RecordsConfirmed>(base, request),
};
