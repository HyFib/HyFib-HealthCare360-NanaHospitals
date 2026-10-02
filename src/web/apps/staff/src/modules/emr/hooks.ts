import { isApiError, useRealtimeEvent, useSession } from '@hc360/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { usePermission } from '@/lib/permissions';
import { emrKeys, emrQueries } from './api';
import { FLAG_READERS, NO_RECORD, type FlagsState } from './flags';

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

/**
 * A patient's banner flags (allergies and alerts, EHR-03) for the signed-in user, kept live: an allergy or alert
 * recorded at any desk refreshes every open banner at once. `hidden` for staff who may not see clinical flags; a screen
 * without a patient record (an unregistered emergency patient) has nothing recorded.
 */
export function usePatientFlags(patientId: string | null | undefined): FlagsState {
  const queryClient = useQueryClient();
  const canSee = usePermission(FLAG_READERS);
  const live = canSee && Boolean(patientId);
  useRealtimeEvent(live ? `patient:${patientId}` : null, 'clinical.flags.changed', () => {
    if (patientId) {
      void queryClient.invalidateQueries({ queryKey: emrKeys.flags(patientId) });
    }
  });
  const flags = useQuery({ ...emrQueries.flags(patientId ?? ''), enabled: live });

  if (!canSee) {
    return { kind: 'hidden' };
  }
  if (!patientId) {
    return { kind: 'ready', flags: NO_RECORD };
  }
  // Flags already shown stay up while a refresh fails (a WiFi blip); only a first load can fail visibly.
  if (flags.data) {
    return { kind: 'ready', flags: flags.data };
  }
  return flags.error ? { kind: 'failed', error: flags.error } : { kind: 'loading' };
}

/** Refreshes order lists when orders change anywhere in the facility (diagnostics completing lines). */
export function useLiveOrders(patientId: string) {
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  useRealtimeEvent<{ patientId?: string }>(facilityId ? `facility:${facilityId}` : null, null, (payload, envelope) => {
    if (
      (envelope.name === 'clinical.order.placed' || envelope.name === 'clinical.order.changed') &&
      (!payload?.patientId || payload.patientId === patientId)
    ) {
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
