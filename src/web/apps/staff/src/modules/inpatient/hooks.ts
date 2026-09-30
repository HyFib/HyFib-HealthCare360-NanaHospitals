import { useRealtimeEvent, useSession } from '@hc360/core';
import { useQueryClient, type QueryKey } from '@tanstack/react-query';
import { useCallback, useEffect, useRef, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { inpatientKeys } from './api';
import { nursingKeys } from './nursingApi';

/** A clock that ticks every `intervalMs` so waiting times and "due" labels stay current without refetching. */
export function useNow(intervalMs = 60_000): number {
  const [now, setNow] = useState(() => new Date().getTime());
  useEffect(() => {
    const timer = setInterval(() => setNow(new Date().getTime()), intervalMs);
    return () => clearInterval(timer);
  }, [intervalMs]);
  return now;
}

interface ThrottleState {
  timer: ReturnType<typeof setTimeout> | null;
  pending: Map<string, QueryKey>;
}

/**
 * Collapses a burst of realtime events (a bulk bed change, a ward round) into one refetch per query key, so a busy
 * hospital never floods the WiFi with duplicate requests.
 */
export function useThrottledInvalidate(delayMs = 400): (...keys: QueryKey[]) => void {
  const queryClient = useQueryClient();
  const state = useRef<ThrottleState>({ timer: null, pending: new Map() });

  useEffect(() => {
    const current = state.current;
    return () => {
      if (current.timer) {
        clearTimeout(current.timer);
      }
    };
  }, []);

  return useCallback(
    (...keys: QueryKey[]) => {
      const current = state.current;
      keys.forEach((key) => current.pending.set(JSON.stringify(key), key));
      if (current.timer) {
        return;
      }
      current.timer = setTimeout(() => {
        current.timer = null;
        const batch = [...current.pending.values()];
        current.pending.clear();
        batch.forEach((queryKey) => void queryClient.invalidateQueries({ queryKey }));
      }, delayMs);
    },
    [queryClient, delayMs],
  );
}

/**
 * Live in-patient data: `beds.changed` on `beds:{facilityId}` refreshes the bed board, occupancy, available beds and
 * housekeeping; `admissions.changed` and `inpatient.discharge_initiated` on the facility topic refresh admissions and
 * the pending-discharge worklist. Payloads carry ids only; data is refetched with the user's own permissions.
 */
export function useInpatientRealtime(): void {
  const facilityId = useSession((s) => s.user?.facilityId);
  const invalidate = useThrottledInvalidate();

  useRealtimeEvent(facilityId ? `beds:${facilityId}` : null, 'beds.changed', () => {
    invalidate(
      inpatientKeys.boardAll,
      inpatientKeys.occupancy,
      ['inpatient', 'available'],
      ['inpatient', 'housekeeping'],
      ['inpatient', 'beds'],
      ['inpatient', 'wards'],
    );
  });

  useRealtimeEvent<{ admissionId?: string }>(
    facilityId ? `facility:${facilityId}` : null,
    'admissions.changed',
    (payload) => {
      invalidate(
        inpatientKeys.admissionsAll,
        ['inpatient', 'pending-discharges'],
        ['inpatient', 'patient-admissions'],
        ...(payload?.admissionId ? [inpatientKeys.admission(payload.admissionId)] : []),
      );
    },
  );

  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'inpatient.discharge_initiated', () => {
    invalidate(['inpatient', 'pending-discharges']);
  });
}

/** Ward-level updates for the nursing station (`beds.changed` on `ward:{wardId}`). */
export function useWardRealtime(wardId: string | null | undefined): void {
  const invalidate = useThrottledInvalidate();
  useRealtimeEvent(wardId ? `ward:${wardId}` : null, 'beds.changed', () => {
    if (wardId) {
      invalidate(inpatientKeys.board(wardId), nursingKeys.ward(wardId));
    }
  });
}

/** A string preference kept on this workstation (chosen ward, last sterilizer…). */
export function useStoredState(key: string, initial = ''): [string, (value: string) => void] {
  const [value, setValue] = useState<string>(() => {
    try {
      return localStorage.getItem(key) ?? initial;
    } catch {
      return initial;
    }
  });
  const update = useCallback(
    (next: string) => {
      setValue(next);
      try {
        if (next) {
          localStorage.setItem(key, next);
        } else {
          localStorage.removeItem(key);
        }
      } catch {
        // Preferences are a convenience only.
      }
    },
    [key],
  );
  return [value, update];
}

export interface InpatientPermissions {
  read: boolean;
  admit: boolean;
  transfer: boolean;
  discharge: boolean;
  masters: boolean;
  manageBeds: boolean;
  housekeeping: boolean;
  chart: boolean;
  mar: boolean;
}

export function useInpatientPermissions(): InpatientPermissions {
  return {
    read: usePermission('inpatient.admissions.read'),
    admit: usePermission('inpatient.admissions.admit'),
    transfer: usePermission('inpatient.admissions.transfer'),
    discharge: usePermission('inpatient.admissions.discharge'),
    masters: usePermission('inpatient.masters.manage'),
    manageBeds: usePermission('beds.manage'),
    housekeeping: usePermission('beds.housekeeping'),
    chart: usePermission('nursing.chart'),
    mar: usePermission('nursing.mar'),
  };
}
