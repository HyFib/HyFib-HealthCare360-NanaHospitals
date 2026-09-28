import { isApiError, useRealtimeEvent, useSession } from '@hc360/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { emrKeys, emrQueries } from './api';

/**
 * The patient's clinical summary (banner flags, problems, medicines, last vitals), kept live: allergy/alert changes
 * and newly recorded vitals on the patient's real-time topic refresh every open chart immediately.
 */
export function useClinicalSummary(patientId: string) {
  const queryClient = useQueryClient();
  useRealtimeEvent(`patient:${patientId}`, null, (_payload, envelope) => {
    if (envelope.name === 'clinical.flags.changed' || envelope.name === 'clinical.vitals.recorded') {
      void queryClient.invalidateQueries({ queryKey: emrKeys.patient(patientId) });
    }
  });
  return useQuery(emrQueries.summary(patientId));
}

/** Refreshes order lists when orders change anywhere in the facility (diagnostics completing lines). */
export function useLiveOrders(patientId: string) {
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  useRealtimeEvent<{ patientId?: string }>(facilityId ? `facility:${facilityId}` : null, null, (payload, envelope) => {
    if ((envelope.name === 'clinical.order.placed' || envelope.name === 'clinical.order.changed') && (!payload?.patientId || payload.patientId === patientId)) {
      void queryClient.invalidateQueries({ queryKey: [...emrKeys.patient(patientId), 'orders'] });
    }
  });
}

/** True for the error the server returns when a restricted (VIP) record needs break-the-glass access. */
export function isRestricted(error: unknown): boolean {
  return isApiError(error) && error.code === 'patient.restricted';
}

/** True when someone else saved the record first (optimistic concurrency). */
export function isConflict(error: unknown): boolean {
  return isApiError(error) && error.status === 409;
}
