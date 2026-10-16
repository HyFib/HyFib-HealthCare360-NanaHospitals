import { auth, useSession } from '@hc360/core';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect } from 'react';

/** Minutes without use before the screen locks when the hospital has not set `ui.idleLockMinutes`. */
export const DEFAULT_IDLE_LOCK_MINUTES = 10;

/**
 * The facility's `ui.idleLockMinutes` as the server reads it (AuthService): a whole number, written as a number or as
 * text; 0 turns the lock off; anything else is the default. The screen and the server then lock at the same time.
 */
export function idleLockMinutes(setting: unknown): number {
  const value = typeof setting === 'string' && /^\s*-?\d+\s*$/.test(setting) ? Number(setting) : setting;
  return typeof value === 'number' && Number.isInteger(value) ? Math.max(0, value) : DEFAULT_IDLE_LOCK_MINUTES;
}

interface LockMessage {
  userId?: string;
  locked?: boolean;
}

/**
 * The app's windows on this computer (browser tabs, HC360 Desktop's second windows) share one session, whose lock the
 * server keeps (SRS UX-11). Locking one window locks the others at once, so none keeps showing patient data, and
 * unlocking one unlocks the others as soon as the server, asked by each, confirms it. Every window keeps its screens as
 * they were.
 */
export function useLockSync(): void {
  const queryClient = useQueryClient();

  useEffect(() => {
    if (typeof BroadcastChannel === 'undefined') {
      return;
    }
    const channel = new BroadcastChannel('hc360:lock');
    let applying = false;
    const apply = (locked: boolean) => {
      applying = true;
      try {
        useSession.getState().setLocked(locked);
      } finally {
        applying = false;
      }
    };

    const unsubscribe = useSession.subscribe((state, previous) => {
      if (!applying && state.locked !== previous.locked && state.status === 'authenticated' && state.user) {
        channel.postMessage({ userId: state.user.id, locked: state.locked } satisfies LockMessage);
      }
    });

    channel.onmessage = (event: MessageEvent<LockMessage | null>) => {
      const { userId, locked } = event.data ?? {};
      const state = useSession.getState();
      if (state.status !== 'authenticated' || !state.user || state.user.id !== userId || state.locked === locked) {
        return;
      }
      if (locked) {
        apply(true);
        return;
      }
      // Unlocked in another window: the server unlocked the session, and gives this window a token without the lock.
      void auth.refresh().then(() => {
        const now = useSession.getState();
        if (now.status === 'authenticated' && now.locked && now.user?.restriction !== 'locked') {
          apply(false);
          void queryClient.invalidateQueries();
        }
      });
    };

    return () => {
      unsubscribe();
      channel.close();
    };
  }, [queryClient]);
}
