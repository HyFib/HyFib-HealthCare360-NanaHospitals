import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { PublicRateList, PublicRateListFacility } from '@/lib/types';

/** The rate list each facility's counter prints and displays, for anyone to read (no patient data). */
export const RATE_LIST_PATH = '/api/v1/public/rate-list';

/** The hospital's own PDF of a facility's list: the one its counter prints. */
export function rateListPdfPath(facilityId: string): string {
  return `${RATE_LIST_PATH}/${encodeURIComponent(facilityId)}/pdf`;
}

export const rateListQueries = {
  /** The facilities that show their list online, by name. */
  facilities: () =>
    queryOptions({
      queryKey: ['public', 'rate-list', 'facilities'],
      queryFn: ({ signal }) => api.get<PublicRateListFacility[]>(RATE_LIST_PATH, { signal }),
      staleTime: 10 * 60_000,
    }),

  list: (facilityId: string) =>
    queryOptions({
      queryKey: ['public', 'rate-list', facilityId],
      queryFn: ({ signal }) =>
        api.get<PublicRateList>(`${RATE_LIST_PATH}/${encodeURIComponent(facilityId)}`, { signal }),
      staleTime: 5 * 60_000,
    }),
};
