import { create } from 'zustand';

/** Why the session ended, so the sign-in page can say so ("You have signed out", "signed out after 20 minutes"). */
export type SessionEndReason = 'signed-out' | 'idle';

let pendingReason: SessionEndReason | null = null;

export function markSessionEnd(reason: SessionEndReason): void {
  pendingReason = reason;
}

/** Returns and forgets the reason (null when the session simply expired or was revoked). */
export function takeSessionEndReason(): SessionEndReason | null {
  const reason = pendingReason;
  pendingReason = null;
  return reason;
}

export function isSessionEndReason(value: unknown): value is SessionEndReason {
  return value === 'signed-out' || value === 'idle';
}

interface CallActivityState {
  /** True while a video consultation is open: idle sign-out must never end a call. */
  inCall: boolean;
  setInCall(inCall: boolean): void;
}

export const useCallActivity = create<CallActivityState>((set) => ({
  inCall: false,
  setInCall: (inCall) => set({ inCall }),
}));

/** Minutes without any touch, click or key press before the portal signs out (shared phones, family devices). */
export const IDLE_SIGN_OUT_MINUTES = 20;
