import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { FamilyCodeSent, PortalFamily, PortalLinkedRelative, RequestFamilyCode } from '@/lib/types';

const base = '/api/v1/portal/family';

/**
 * Family members on other mobile numbers (PP-01). The list is the account's, not one person's, so it is not keyed by
 * the person being viewed. Adding or removing someone changes whose records the account sees: refresh the profiles too.
 */
export const familyQueries = {
  all: ['portal', 'family'] as const,
  list: () =>
    queryOptions({
      queryKey: ['portal', 'family'],
      queryFn: ({ signal }) => api.get<PortalFamily>(`${base}/`, { signal }),
    }),
};

export const familyApi = {
  /** Sends a code to the relative's own number when the UHID and the number belong together; the answer never says. */
  requestCode: (request: RequestFamilyCode) => api.post<FamilyCodeSent>(`${base}/codes`, request),
  verifyCode: (codeId: string, code: string) =>
    api.post<PortalLinkedRelative>(`${base}/codes/${encodeURIComponent(codeId)}/verify`, { code }),
  /** Removes a relative this account sees, or stops another account seeing someone on this number. */
  end: (linkId: string) => api.post<void>(`${base}/links/${encodeURIComponent(linkId)}/end`),
};
