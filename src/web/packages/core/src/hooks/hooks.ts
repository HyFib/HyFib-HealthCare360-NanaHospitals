import { useCallback, useEffect, useEffectEvent, useState, type RefObject } from 'react';
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
 *
 * A shortcut belongs to the page, so it does nothing while a modal dialog or drawer is open over the page: F9 pressed
 * in a dialog must not dispense the prescription behind it. The key is still kept from the browser (Alt+Left would
 * otherwise navigate away from the page, dialog and all). A shortcut that belongs to a dialog passes the dialog's
 * surface as `within` and then works only for keys pressed inside it, so not in another dialog opened over it. One
 * that must work whatever is open, such as locking the screen, sets `overDialogs`.
 */
export function useHotkey(
  combo: string,
  handler: (event: KeyboardEvent) => void,
  options: {
    allowInInputs?: boolean;
    enabled?: boolean;
    /** The surface of the dialog or drawer the shortcut belongs to. */
    within?: RefObject<Element | null>;
    /** Works even while a dialog is open over the page. */
    overDialogs?: boolean;
  } = {},
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
        const applies = options.within
          ? target instanceof Node && options.within.current?.contains(target) === true
          : options.overDialogs || !modalOpen();
        if (applies) {
          onHotkey(event);
        }
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [combo, options.allowInInputs, options.enabled, options.within, options.overDialogs]);
}

/**
 * Whether a modal dialog or drawer is open over the page. Fluent marks their surfaces `aria-modal="true"` and unmounts
 * them when they close; the lock screen is marked too. A surface's `aria-hidden` does not mean it is closed: Tabster
 * sets it on an open dialog while another one is active over it, or when focus has left it.
 */
function modalOpen(): boolean {
  return document.querySelector('[aria-modal="true"]') !== null;
}
