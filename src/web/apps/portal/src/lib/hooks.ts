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

/**
 * The ref of a bar stuck to the bottom of the page (above the phone's bottom navigation): while it is shown, the page
 * scrolls a focused element clear of the bar as well as of the navigation (WCAG 2.4.11, focus not obscured). It sets
 * --hc360-sticky-bottom, which the page's scroll-padding-bottom adds (styles.css), to the bar's height and a margin.
 */
export function keepFocusClearOf(bar: HTMLElement | null): (() => void) | undefined {
  if (!bar) {
    return undefined;
  }
  const root = document.documentElement;
  const update = () => root.style.setProperty('--hc360-sticky-bottom', `${Math.ceil(bar.offsetHeight) + 16}px`);
  update();
  const resized = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(update);
  resized?.observe(bar);
  return () => {
    resized?.disconnect();
    root.style.removeProperty('--hc360-sticky-bottom');
  };
}

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
