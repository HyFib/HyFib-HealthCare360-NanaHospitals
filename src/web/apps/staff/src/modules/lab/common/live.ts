import { useRealtimeEvent, useSession } from '@hc360/core';
import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { invalidationTargets, mergePrefixes, type KeyPrefix } from './events';

export type LiveScope = 'lab' | 'radiology' | 'cardiology';

/**
 * Collects invalidations for a short moment and refetches once per burst: an analyser or a busy receiving bench can
 * push dozens of events a minute, and every screen should refetch each list once, not once per event.
 */
class InvalidationBatcher {
  private pending: KeyPrefix[] = [];
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly queryClient: QueryClient,
    private readonly delayMs: number,
  ) {}

  add(prefixes: KeyPrefix[]): void {
    if (prefixes.length === 0) {
      return;
    }
    this.pending.push(...prefixes);
    this.timer ??= setTimeout(() => this.flush(), this.delayMs);
  }

  flush(): void {
    const keys = mergePrefixes(this.pending);
    this.pending = [];
    this.timer = null;
    for (const queryKey of keys) {
      void this.queryClient.invalidateQueries({ queryKey: [...queryKey] });
    }
  }

  dispose(): void {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    this.pending = [];
  }
}

/**
 * Keeps a diagnostics screen live (PERF-04): subscribes to `lab:{facilityId}` (lab, radiology and cardiology changes
 * are all pushed there) and refetches the affected lists. `onEvent` lets a screen react to a specific event, for
 * example a new critical value.
 */
export function useDiagnosticsLive(
  scopes: readonly LiveScope[],
  onEvent?: (name: string, payload: unknown) => void,
): void {
  const facilityId = useSession((s) => s.user?.facilityId);
  const queryClient = useQueryClient();
  const [batcher] = useState(() => new InvalidationBatcher(queryClient, 600));

  useEffect(() => () => batcher.dispose(), [batcher]);

  useRealtimeEvent<unknown>(facilityId ? `lab:${facilityId}` : null, null, (payload, envelope) => {
    const targets = invalidationTargets(envelope.name).filter((prefix) => scopes.includes(prefix[0] as LiveScope));
    batcher.add(targets);
    onEvent?.(envelope.name, payload);
  });
}
