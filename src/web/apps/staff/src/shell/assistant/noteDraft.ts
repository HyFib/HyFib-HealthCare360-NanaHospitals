import type { ClinicalNoteType } from '@/modules/emr/api';
import type { NoteDraft } from './intents';

/**
 * Hands a dictated note to the EMR without putting clinical text in the address bar.
 *
 * The EMR note composer (modules/emr/NotesPanel.tsx, via useFormDraft) keeps each patient's unsaved note on this
 * computer under draftStorageKey(userId, `emr-note:${patientId}`) as `{ value: { type, title, body }, savedAt }` and
 * offers "Restore it" when the clinician opens Write note. Writing the dictated text there means it is reviewed and
 * saved in the normal screen. An unsaved note that is already there is never overwritten: the new text is appended.
 * Drafts are per user and wiped at sign-out like every other draft.
 */

const NOTE_TYPES: readonly ClinicalNoteType[] = ['ProgressNote', 'Procedure', 'Referral', 'Counselling', 'Other'];

/** The EMR composer's draft name for a patient-level note (no encounter). */
export function emrNoteDraftName(patientId: string): string {
  return `emr-note:${patientId}`;
}

function isNoteType(value: unknown): value is ClinicalNoteType {
  return typeof value === 'string' && (NOTE_TYPES as readonly string[]).includes(value);
}

/** The `value` of a stored draft, or null when there is none or it cannot be read. */
function storedDraftValue(json: string | null): Partial<Record<keyof NoteDraft, unknown>> | null {
  if (!json) {
    return null;
  }
  try {
    const parsed = JSON.parse(json) as { value?: unknown } | null;
    return parsed && typeof parsed.value === 'object' && parsed.value !== null ? (parsed.value as Partial<Record<keyof NoteDraft, unknown>>) : null;
  } catch {
    return null;
  }
}

/** The draft to store: the new note, or the existing unsaved note with the new text added at the end. */
export function mergeNoteDraft(existingJson: string | null, draft: NoteDraft, savedAt: number): string {
  const existing = storedDraftValue(existingJson);
  const previousBody = typeof existing?.body === 'string' ? existing.body.trim() : '';
  if (!existing || !previousBody) {
    return JSON.stringify({ value: draft, savedAt });
  }
  const value: NoteDraft = {
    type: isNoteType(existing.type) ? existing.type : draft.type,
    title: typeof existing.title === 'string' && existing.title.trim() ? existing.title : draft.title,
    body: draft.body ? `${previousBody}\n\n${draft.body}` : previousBody,
  };
  return JSON.stringify({ value, savedAt });
}

/** Stores the note for the EMR composer. Returns false when this computer's storage is unavailable. */
export function keepNoteDraft(storage: Pick<Storage, 'getItem' | 'setItem'> | null, storageKey: string, draft: NoteDraft, now = Date.now()): boolean {
  if (!storage) {
    return false;
  }
  try {
    storage.setItem(storageKey, mergeNoteDraft(storage.getItem(storageKey), draft, now));
    return true;
  } catch {
    return false;
  }
}
