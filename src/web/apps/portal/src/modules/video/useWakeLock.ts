import { useEffect } from 'react';

/** Keeps the phone's screen on while `active` (a consultation must not go dark mid-sentence). Best effort. */
export function useWakeLock(active: boolean): void {
  useEffect(() => {
    if (!active || typeof navigator === 'undefined' || !('wakeLock' in navigator)) {
      return;
    }
    let sentinel: WakeLockSentinel | null = null;
    let cancelled = false;
    const request = async () => {
      try {
        const lock = await navigator.wakeLock.request('screen');
        if (cancelled) {
          void lock.release().catch(() => undefined);
        } else {
          sentinel = lock;
        }
      } catch {
        // Battery saver or an unsupported browser: the call still works.
      }
    };
    // The lock is dropped whenever the page is hidden; take it again when the patient comes back.
    const onVisible = () => {
      if (document.visibilityState === 'visible' && !cancelled) {
        void request();
      }
    };
    void request();
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      cancelled = true;
      document.removeEventListener('visibilitychange', onVisible);
      void sentinel?.release().catch(() => undefined);
    };
  }, [active]);
}
