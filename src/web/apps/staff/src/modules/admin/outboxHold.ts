import { api } from '@/lib/api';

/**
 * Outgoing messages held after a restore (the hospital-wide setting `messaging.outbox.hold`), as System health reports
 * it. The messages queued before the hold began do not go to the internet (SMS, WhatsApp, e-mail, ABDM, e-invoicing)
 * until they are released; those queued since go as usual, and so do alerts in the app, the analysers' orders and Tally.
 */
export interface OutboxHold {
  /** Why: `restore` when the restore set it, else words for the administrator. */
  reason: string | null;
  /** When the hold began: messages queued before it are held. The year 1 when the server could not read the hold. */
  since: string;
  /** When the restored backup was made, when known: what was waiting then may have gone out since. */
  backupTakenAt: string | null;
}

/** The reason the restore gives the hold. */
export const RESTORE_REASON = 'restore';

/** What releasing the hold did. */
export interface OutboxReleased {
  releasedAt: string;
  /** The messages it held when it was released, which go now. */
  held: number;
}

/**
 * Releases the held messages, needing `admin.system.manage`; the release is audited, and ends the restore's warning on
 * System health too. 409 `outbox.not_held` when they are not held (someone released them already).
 */
export function releaseOutbox() {
  return api.post<OutboxReleased>('/api/v1/admin/system/outbox/release');
}

/** When the hold began, or null when the server could not tell (a hold it could not read, which holds every message). */
export function heldSince(hold: Pick<OutboxHold, 'since'>): string | null {
  const at = Date.parse(hold.since);
  return Number.isNaN(at) || new Date(at).getUTCFullYear() < 2000 ? null : hold.since;
}

/** Whether the restore set the hold (its reason is then not one to show). */
export function setByRestore(hold: Pick<OutboxHold, 'reason'>): boolean {
  return hold.reason?.trim().toLowerCase() === RESTORE_REASON;
}

/** How many messages are held, in words: "12 messages", "1 message", "No message". */
export function heldCount(held: number): string {
  if (held === 0) {
    return 'No message';
  }
  return held === 1 ? '1 message' : `${held} messages`;
}

/** What the release toast says: how many messages go now. */
export function releasedText({ held }: Pick<OutboxReleased, 'held'>): string {
  if (held === 0) {
    return 'No message was held.';
  }
  return `${heldCount(held)} ${held === 1 ? 'goes' : 'go'} now, as soon as each provider answers.`;
}
