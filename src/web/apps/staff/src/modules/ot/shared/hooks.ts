import { useRealtimeEvent, useSession } from '@hc360/core';
import { useQueryClient, type QueryKey } from '@tanstack/react-query';
import { useEffect, useState } from 'react';

/**
 * The current time, refreshed every `intervalMs`. Components read the clock through this hook instead of calling
 * `Date.now()` while rendering, so renders stay pure and countdowns (cross-match validity, cold chain, separation
 * window, the "now" line on the OT board) still move.
 */
export function useNow(intervalMs = 30_000): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(timer);
  }, [intervalMs]);
  return now;
}

export function useFacilityId(): string | null {
  return useSession((s) => s.user?.facilityId ?? null);
}

/**
 * Refetches the given queries when the server signals a change. Payloads carry ids only; screens refetch with their
 * own authorisation. `facility:{id}` is joined automatically; other topics (for example `ot:{facilityId}`) are
 * subscribed while the component is mounted.
 */
export function useLiveInvalidation(topic: string | null, eventName: string, queryKeys: readonly QueryKey[]): void {
  const queryClient = useQueryClient();
  useRealtimeEvent(topic, eventName, () => {
    for (const queryKey of queryKeys) {
      void queryClient.invalidateQueries({ queryKey });
    }
  });
}
