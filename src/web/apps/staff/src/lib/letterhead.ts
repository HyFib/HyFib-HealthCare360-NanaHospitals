import { useSession, type Letterhead } from '@hc360/core';
import { queryOptions, useQuery } from '@tanstack/react-query';
import { useMemo } from 'react';
import { api } from './api';

/** Query key of the letterhead: invalidate it after changing a print template or the facility's letterhead. */
export const LETTERHEAD_KEY = ['print', 'letterhead'] as const;

/** The signed-in user's facility's letterhead, logo and print templates; kept for 10 minutes, as other look-ups are. */
export function letterheadQuery(facilityId: string | null | undefined, enabled = true) {
  return queryOptions({
    queryKey: [...LETTERHEAD_KEY, facilityId ?? null],
    queryFn: ({ signal }) => api.get<Letterhead>('/api/v1/print/letterhead', { signal }),
    staleTime: 10 * 60_000,
    enabled,
  });
}

/**
 * The hospital as the staff app's own prints show it (UX-09): the facility's letterhead with its logo, and how an
 * administrator set each kind of document to print (Hospital setup › Print templates). Every member of staff may read
 * it. Until it arrives, or when it cannot be read, prints show the facility's name with the default templates.
 */
export function useLetterhead(): Letterhead {
  const signedIn = useSession((s) => Boolean(s.user));
  const facilityId = useSession((s) => s.user?.facilityId);
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const { data } = useQuery(letterheadQuery(facilityId, signedIn));
  return useMemo(() => data ?? { name: facilityName }, [data, facilityName]);
}
