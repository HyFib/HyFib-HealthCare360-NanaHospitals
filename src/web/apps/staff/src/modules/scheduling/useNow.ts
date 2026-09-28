import { useSyncExternalStore } from 'react';
import { istDate, type IsoDate } from './dates';

/**
 * One shared ticking clock for "waiting 12 min", "no-show after 10:30", today's date and similar. Reading the time
 * during render would make components impure; this store ticks every 15 seconds while anything is subscribed and
 * refreshes on subscribe, so a screen opened after a long idle period never shows a stale time.
 */
const TICK_MS = 15_000;
let current = Date.now();
let timer: ReturnType<typeof setInterval> | undefined;
const listeners = new Set<() => void>();

function tick() {
  current = Date.now();
  listeners.forEach((listener) => listener());
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  if (!timer) {
    current = Date.now();
    timer = setInterval(tick, TICK_MS);
  }
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0 && timer) {
      clearInterval(timer);
      timer = undefined;
    }
  };
}

function getSnapshot(): number {
  return current;
}

/** Current time in epoch milliseconds, updated every 15 seconds. */
export function useNow(): number {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}

/** Today on the hospital (IST) calendar, rolling over at midnight. */
export function useToday(): IsoDate {
  return istDate(new Date(useNow()).toISOString());
}
