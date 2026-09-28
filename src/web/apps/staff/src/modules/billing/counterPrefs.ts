import { useSyncExternalStore } from 'react';

/**
 * Settings of this billing counter (the workstation, not the user): its name for cashier shifts, the card/UPI
 * terminal wired to it and how receipts print. Kept in local storage, like the theme, so every cashier who signs in
 * at the counter gets the same printer and terminal.
 */
export interface CounterPrefs {
  /** Counter name used when opening a shift ("OPD Counter 1"). */
  counter: string;
  /** Card/UPI terminal id for POS sales; empty when no terminal is attached. */
  terminalId: string;
  /** Receipt printer: 80 mm thermal printed silently, or A4 through the print dialog. */
  receiptFormat: 'Receipt80' | 'A4';
  /** Print the receipt as soon as a payment is recorded. */
  autoPrint: boolean;
}

const STORAGE_KEY = 'hc360:billing:counter';
const DEFAULTS: CounterPrefs = { counter: '', terminalId: '', receiptFormat: 'Receipt80', autoPrint: true };
const listeners = new Set<() => void>();
let cachedRaw: string | null | undefined;
let cachedValue: CounterPrefs = DEFAULTS;
/** Used only when local storage refuses writes (private mode, full disk). */
let memoryRaw: string | null = null;

function readRaw(): string | null {
  if (memoryRaw !== null) {
    return memoryRaw;
  }
  try {
    return localStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

function snapshot(): CounterPrefs {
  const raw = readRaw();
  if (raw !== cachedRaw) {
    cachedRaw = raw;
    try {
      const parsed = raw ? (JSON.parse(raw) as Partial<CounterPrefs>) : {};
      cachedValue = {
        counter: typeof parsed.counter === 'string' ? parsed.counter : DEFAULTS.counter,
        terminalId: typeof parsed.terminalId === 'string' ? parsed.terminalId : DEFAULTS.terminalId,
        receiptFormat: parsed.receiptFormat === 'A4' ? 'A4' : 'Receipt80',
        autoPrint: typeof parsed.autoPrint === 'boolean' ? parsed.autoPrint : DEFAULTS.autoPrint,
      };
    } catch {
      cachedValue = DEFAULTS;
    }
  }
  return cachedValue;
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  const onStorage = (event: StorageEvent) => {
    if (event.key === STORAGE_KEY) {
      listener();
    }
  };
  window.addEventListener('storage', onStorage);
  return () => {
    listeners.delete(listener);
    window.removeEventListener('storage', onStorage);
  };
}

export function saveCounterPrefs(patch: Partial<CounterPrefs>): void {
  const next = JSON.stringify({ ...snapshot(), ...patch });
  try {
    localStorage.setItem(STORAGE_KEY, next);
    memoryRaw = null;
  } catch {
    // Storage unavailable: the setting lasts until the page reloads.
    memoryRaw = next;
  }
  listeners.forEach((listener) => listener());
}

/** Current counter settings; components re-render when they change (also from another tab). */
export function useCounterPrefs(): CounterPrefs {
  return useSyncExternalStore(subscribe, snapshot, () => DEFAULTS);
}
