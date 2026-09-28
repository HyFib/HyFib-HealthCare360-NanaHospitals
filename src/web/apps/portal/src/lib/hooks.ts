import { useCallback, useEffect, useState, useSyncExternalStore } from 'react';

/** True while the CSS media query matches (e.g. `(min-width: 900px)`); updates on rotation and resize. */
export function useMediaQuery(query: string): boolean {
  const subscribe = useCallback(
    (notify: () => void) => {
      const media = window.matchMedia(query);
      media.addEventListener('change', notify);
      return () => media.removeEventListener('change', notify);
    },
    [query],
  );
  return useSyncExternalStore(
    subscribe,
    () => window.matchMedia(query).matches,
    () => false,
  );
}

export const WIDE_LAYOUT_QUERY = '(min-width: 900px)';

/** The current time, refreshed every `intervalMs` while enabled (countdowns, "join opens in 4 min"). */
export function useNow(intervalMs = 1000, enabled = true): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!enabled) {
      return;
    }
    const timer = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(timer);
  }, [intervalMs, enabled]);
  return now;
}
