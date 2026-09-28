import type { LookupItem } from '@hc360/core';
import { queryOptions, useQuery } from '@tanstack/react-query';
import { api } from './api';

export type DepartmentKind = 'Clinical' | 'Diagnostic' | 'Support' | 'Administrative';

/** Shared master-data look-ups (cached for 10 minutes — masters rarely change during a shift). */
export const lookupQueries = {
  departments: (kind?: DepartmentKind) =>
    queryOptions({
      queryKey: ['lookups', 'departments', kind ?? 'all'],
      queryFn: () => api.get<LookupItem[]>('/api/v1/masters/lookups/departments', { query: { kind } }),
      staleTime: 10 * 60_000,
    }),
  practitioners: (departmentId?: string | null) =>
    queryOptions({
      queryKey: ['lookups', 'practitioners', departmentId ?? 'all'],
      queryFn: () => api.get<LookupItem[]>('/api/v1/masters/lookups/practitioners', { query: { departmentId } }),
      staleTime: 10 * 60_000,
    }),
  specialties: () =>
    queryOptions({
      queryKey: ['lookups', 'specialties'],
      queryFn: () => api.get<LookupItem[]>('/api/v1/masters/lookups/specialties'),
      staleTime: 10 * 60_000,
    }),
};

export function useDepartments(kind?: DepartmentKind) {
  return useQuery(lookupQueries.departments(kind));
}

export function usePractitioners(departmentId?: string | null) {
  return useQuery(lookupQueries.practitioners(departmentId));
}

/** Server-side practitioner search for large hospitals (LookupField `load`). */
export function searchPractitioners(search: string, departmentId?: string | null): Promise<LookupItem[]> {
  return api.get<LookupItem[]>('/api/v1/masters/lookups/practitioners', { query: { search, departmentId } });
}

/** Non-secret `ui.*` and `features.*` settings for the current facility. */
export function usePublicSettings() {
  return useQuery({
    queryKey: ['settings', 'public'],
    queryFn: () => api.get<Record<string, unknown>>('/api/v1/settings/public'),
    staleTime: 5 * 60_000,
  });
}

export function toOptions(items: LookupItem[] | undefined): { value: string; label: string }[] {
  return (items ?? []).map((item) => ({ value: item.id, label: item.name }));
}

/** Options for a string enum, labelled in plain words ("PartiallyPaid" → "Partially paid"). */
export function enumOptions<T extends string>(values: readonly T[], labels: Partial<Record<T, string>> = {}): { value: T; label: string }[] {
  return values.map((value) => ({ value, label: labels[value] ?? value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, (c) => c.toUpperCase()) }));
}
