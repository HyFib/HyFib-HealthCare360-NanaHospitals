import type { PagedResult } from '@hc360/core';
import { infiniteQueryOptions, keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { DirectoryFilters, PublicDoctor, PublicDoctorProfile, PublicSlotDay } from '@/lib/types';

export interface DoctorSearch {
  q?: string;
  specialtyId?: string;
  facilityId?: string;
  language?: string;
  video?: boolean;
}

const PAGE_SIZE = 12;

/** The hospital's public doctor directory (no patient data; the same for everyone). */
export const doctorQueries = {
  filters: (facilityId?: string) =>
    queryOptions({
      queryKey: ['public', 'doctors', 'filters', facilityId ?? 'all'],
      queryFn: ({ signal }) => api.get<DirectoryFilters>('/api/v1/public/doctors/filters', { query: { facilityId }, signal }),
      staleTime: 10 * 60_000,
    }),

  search: (search: DoctorSearch) =>
    infiniteQueryOptions({
      queryKey: ['public', 'doctors', 'search', search],
      queryFn: ({ pageParam, signal }) =>
        api.get<PagedResult<PublicDoctor>>('/api/v1/public/doctors', {
          query: {
            search: search.q,
            specialtyId: search.specialtyId,
            facilityId: search.facilityId,
            language: search.language,
            telehealth: search.video ? true : undefined,
            page: pageParam,
            pageSize: PAGE_SIZE,
          },
          signal,
        }),
      initialPageParam: 1,
      getNextPageParam: (last) => (last.page * last.pageSize < last.totalCount ? last.page + 1 : undefined),
      placeholderData: keepPreviousData,
      staleTime: 60_000,
    }),

  profile: (doctorId: string) =>
    queryOptions({
      queryKey: ['public', 'doctors', 'profile', doctorId],
      queryFn: ({ signal }) => api.get<PublicDoctorProfile>(`/api/v1/public/doctors/${encodeURIComponent(doctorId)}`, { signal }),
      staleTime: 5 * 60_000,
    }),

  /** Bookable slots (≤ 14 days per call; never inside the online cut-off or beyond the booking window). */
  slots: (doctorId: string, from: string, to: string, video: boolean, facilityId?: string) =>
    queryOptions({
      queryKey: ['public', 'doctors', 'slots', doctorId, from, to, video, facilityId ?? 'all'],
      queryFn: ({ signal }) =>
        api.get<PublicSlotDay[]>(`/api/v1/public/doctors/${encodeURIComponent(doctorId)}/slots`, {
          query: { from, to, telehealth: video, facilityId },
          signal,
        }),
      // Seats go quickly: keep slots fresh and refresh when the patient comes back to the tab.
      staleTime: 20_000,
      refetchInterval: 60_000,
      placeholderData: keepPreviousData,
    }),
};
