import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { PROFILES_QUERY_KEY, SYSTEM_INFO_QUERY_KEY } from '@/lib/session';
import type { PortalProfile, SystemInfo } from '@/lib/types';

export const profileQueries = {
  /** Everyone registered with the verified mobile number (family members share numbers in India). */
  list: () =>
    queryOptions({
      queryKey: PROFILES_QUERY_KEY,
      queryFn: ({ signal }) => api.get<PortalProfile[]>('/api/v1/portal/profiles', { signal }),
      staleTime: 5 * 60_000,
    }),
};

export const systemQueries = {
  /** Hospital name for the sign-in page and header (anonymous endpoint, no patient data). */
  info: () =>
    queryOptions({
      queryKey: SYSTEM_INFO_QUERY_KEY,
      queryFn: ({ signal }) => api.get<SystemInfo>('/api/v1/system/info', { signal }),
      staleTime: 60 * 60_000,
      gcTime: 24 * 60 * 60_000,
      retry: 1,
    }),
};
