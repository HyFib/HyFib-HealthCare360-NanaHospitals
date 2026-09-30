import { useCallback, useEffect, useEffectEvent, useState } from 'react';
import { draftStorageKey, useSession } from '../auth/session';

/** Debounces a fast-changing value (search boxes). */
export function useDebouncedValue<T>(value: T, delayMs = 300): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(timer);
  }, [value, delayMs]);
  return debounced;
}

/**
 * Auto-saves free text locally every few seconds so a WiFi drop or crash never loses typing (SRS DED-10).
 * Drafts are per user and cleared at sign-out.
 */
export function useDraft<T>(key: string | null, value: T, options: { intervalMs?: number; enabled?: boolean } = {}) {
  const userId = useSession((s) => s.user?.id);
  const storageKey = key && userId ? draftStorageKey(userId, key) : null;
  // Reads the latest value from inside the timer without restarting it on every keystroke.
  const currentValue = useEffectEvent(() => value);
  const [savedAt, setSavedAt] = useState<number | null>(null);
  const enabled = options.enabled ?? true;

  useEffect(() => {
    if (!storageKey || !enabled) {
      return;
    }
    const timer = setInterval(() => {
      try {
        localStorage.setItem(storageKey, JSON.stringify({ value: currentValue(), savedAt: Date.now() }));
        setSavedAt(Date.now());
      } catch {
        // Quota or private mode: drafts are best-effort.
      }
    }, options.intervalMs ?? 5000);
    return () => clearInterval(timer);
  }, [storageKey, enabled, options.intervalMs]);

  const restore = useCallback((): { value: T; savedAt: number } | null => {
    if (!storageKey) {
      return null;
    }
    try {
      const raw = localStorage.getItem(storageKey);
      return raw ? (JSON.parse(raw) as { value: T; savedAt: number }) : null;
    } catch {
      return null;
    }
  }, [storageKey]);

  const clear = useCallback(() => {
    if (storageKey) {
      try {
        localStorage.removeItem(storageKey);
      } catch {
        // ignore
      }
    }
    setSavedAt(null);
  }, [storageKey]);

  return { restore, clear, savedAt };
}

/** Calls `onIdle` after `timeoutMs` without keyboard/mouse/touch activity (screen lock, SRS UX-11). */
export function useIdle(timeoutMs: number, onIdle: () => void, enabled = true): void {
  const fireIdle = useEffectEvent(onIdle);

  useEffect(() => {
    if (!enabled || timeoutMs <= 0) {
      return;
    }
    let timer = setTimeout(() => fireIdle(), timeoutMs);
    const reset = () => {
      clearTimeout(timer);
      timer = setTimeout(() => fireIdle(), timeoutMs);
    };
    const events = ['mousemove', 'mousedown', 'keydown', 'touchstart', 'wheel'] as const;
    events.forEach((name) => window.addEventListener(name, reset, { passive: true }));
    return () => {
      clearTimeout(timer);
      events.forEach((name) => window.removeEventListener(name, reset));
    };
  }, [timeoutMs, enabled]);
}

/**
 * Keyboard shortcut, e.g. `useHotkey('mod+k', open)`. "mod" is Ctrl on Windows. Ignored while typing in inputs
 * unless `allowInInputs` is set.
 */
export function useHotkey(
  combo: string,
  handler: (event: KeyboardEvent) => void,
  options: { allowInInputs?: boolean; enabled?: boolean } = {},
): void {
  const onHotkey = useEffectEvent(handler);

  useEffect(() => {
    if (options.enabled === false) {
      return;
    }
    const parts = combo.toLowerCase().split('+');
    const key = parts.at(-1) ?? '';
    const wantCtrl = parts.includes('ctrl') || parts.includes('mod');
    const wantAlt = parts.includes('alt');
    const wantShift = parts.includes('shift');

    const onKeyDown = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement | null;
      const typing = target && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName));
      if (typing && !options.allowInInputs && !wantCtrl && !wantAlt) {
        return;
      }
      if (
        event.key.toLowerCase() === key &&
        (event.ctrlKey || event.metaKey) === wantCtrl &&
        event.altKey === wantAlt &&
        event.shiftKey === wantShift
      ) {
        event.preventDefault();
        onHotkey(event);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [combo, options.allowInInputs, options.enabled]);
}
