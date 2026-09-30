import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { PortalTelehealthSession, TelehealthJoinInfo } from '@/lib/types';

const id = (value: string) => encodeURIComponent(value);

export const videoQueries = {
  /** Upcoming consultations and those of the last 30 days, for the person being viewed. */
  sessions: (patientId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'video', 'sessions'],
      queryFn: ({ signal }) => api.get<PortalTelehealthSession[]>('/api/v1/portal/telehealth/sessions', { signal }),
      select: (rows) => rows.filter((row) => row.patientId === patientId),
      // "Join" opens at a set time: keep the list current while it is on screen.
      refetchInterval: 30_000,
    }),

  joinInfo: (patientId: string, sessionId: string) =>
    queryOptions({
      queryKey: ['portal', patientId, 'video', 'join', sessionId],
      queryFn: ({ signal }) =>
        api.get<TelehealthJoinInfo>(`/api/v1/portal/telehealth/sessions/${id(sessionId)}/join`, { signal }),
      refetchInterval: 30_000,
    }),
};
