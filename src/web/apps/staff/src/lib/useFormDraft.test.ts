import {
  auth,
  clearDrafts,
  closeDrafts,
  draftStorage,
  draftStorageKey,
  openDrafts,
  useDrafts,
  useSession,
  type AuthUser,
} from '@hc360/core';
import { act, renderHook } from '@testing-library/react';
import { useForm } from 'react-hook-form';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useFormDraft } from './useFormDraft';

interface Note {
  category: string;
  note: string;
}

const blank: Note = { category: 'General', note: '' };
const ADMISSION_NOTE = 'nursing-note:adm-1';

const staff = (id: string, userName: string): AuthUser => ({
  id,
  userName,
  displayName: userName,
  type: 'staff',
  facilityId: null,
  facilityName: null,
  practitionerId: null,
  patientId: null,
  roles: ['Nurse'],
  permissions: [],
  facilities: [],
  mfaEnabled: false,
  hasPin: true,
  mustChangePassword: false,
  restriction: null,
});

/** Each user's draft key, as the server gives it at sign-in. */
const draftKeys = { asha: btoa('a'.repeat(32)), ravi: btoa('r'.repeat(32)) };

async function signIn(id: keyof typeof draftKeys): Promise<void> {
  useSession.setState({ status: 'authenticated', accessToken: 't1', user: staff(id, id) });
  await openDrafts(id, draftKeys[id]);
}

/** A nursing note form and its draft, as NursingNotes has them. */
function openNote(key = ADMISSION_NOTE) {
  return renderHook(() => {
    const form = useForm<Note>({ defaultValues: blank });
    return { form, draft: useFormDraft(form, key) };
  });
}

function stored(userId: string, key = ADMISSION_NOTE): { value: Note; savedAt: number } | null {
  const raw = draftStorage.getItem(draftStorageKey(userId, key));
  return raw ? (JSON.parse(raw) as { value: Note; savedAt: number }) : null;
}

/** Waits until the computer holds the user's draft of the form (encrypted, a moment after it is saved). */
async function keptOnComputer(userId: string, key = ADMISSION_NOTE): Promise<void> {
  await vi.waitFor(() => expect(localStorage.getItem(draftStorageKey(userId, key))).not.toBeNull());
}

/** Everything the computer holds, as text. */
function onComputer(): string {
  return Array.from({ length: localStorage.length }, (_, i) => localStorage.getItem(localStorage.key(i)!)).join('\n');
}

function type(note: ReturnType<typeof openNote>, text: string): void {
  act(() => note.result.current.form.setValue('note', text, { shouldDirty: true }));
}

function wait(ms: number): void {
  act(() => vi.advanceTimersByTime(ms));
}

/**
 * SRS DED-10: a form with free text keeps a draft on this computer while it is typed, at most 4 seconds behind (the SRS
 * asks for 5), offers it when the form is opened again after a WiFi drop or a crash, and lets it go once the form is
 * saved or its user signs out. Each draft belongs to one user and one form.
 */
describe('useFormDraft', () => {
  beforeEach(async () => {
    vi.useFakeTimers();
    await signIn('asha');
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    useSession.setState({ status: 'unknown', accessToken: null, expiresAt: null, user: null, locked: false });
    clearDrafts();
    localStorage.clear();
  });

  it('saves what is typed within 4 seconds, and every 4 seconds while the typing goes on', () => {
    const note = openNote();

    type(note, 'BP 150/95');
    wait(3_999);
    expect(stored('asha')).toBeNull();
    wait(1);
    expect(stored('asha')?.value).toEqual({ category: 'General', note: 'BP 150/95' });

    // Typing that never pauses is still saved every 4 seconds, not only once it stops.
    for (let second = 1; second <= 8; second++) {
      type(note, `BP 150/95, repeat in ${second} hour(s)`);
      wait(1_000);
      if (second === 4 || second === 8) {
        expect(stored('asha')?.value.note).toBe(`BP 150/95, repeat in ${second} hour(s)`);
      }
    }
  });

  it('keeps nothing for a form left as it was', () => {
    openNote();

    wait(12_000);

    expect(stored('asha')).toBeNull();
  });

  it('offers the draft when the form is opened again, and puts it back in the form on request', () => {
    const before = openNote();
    type(before, 'Patient drowsy, GCS 14');
    wait(4_000);
    // The WiFi drops and the window is closed, or HC360 Desktop crashes.
    before.unmount();

    const again = openNote();

    expect(again.result.current.draft.saved?.value).toEqual({ category: 'General', note: 'Patient drowsy, GCS 14' });
    expect(again.result.current.form.getValues('note')).toBe('');
    act(() => again.result.current.draft.restore());
    expect(again.result.current.form.getValues('note')).toBe('Patient drowsy, GCS 14');
    expect(again.result.current.draft.saved).toBeNull();
  });

  it('can be discarded instead', () => {
    const before = openNote();
    type(before, 'Patient drowsy, GCS 14');
    wait(4_000);
    before.unmount();

    const again = openNote();
    act(() => again.result.current.draft.discard());

    expect(again.result.current.draft.saved).toBeNull();
    expect(stored('asha')).toBeNull();
    expect(openNote().result.current.draft.saved).toBeNull();
  });

  it('goes once the form is saved, even when the save came before the next 4-second save', () => {
    const note = openNote();
    type(note, 'Paracetamol 500 mg given');
    wait(4_000);
    // More typing, then Save at once. On success the form drops its draft and takes what the server saved.
    type(note, 'Paracetamol 500 mg given at 10:30');
    act(() => {
      note.result.current.draft.clear();
      note.result.current.form.reset({ category: 'General', note: 'Paracetamol 500 mg given at 10:30' });
    });

    expect(stored('asha')).toBeNull();
    wait(8_000);
    expect(stored('asha')).toBeNull();
    note.unmount();
    expect(openNote().result.current.draft.saved).toBeNull();
  });

  it('keeps what was typed as a draft when the form is filled again from the server after a conflict (REL-04)', () => {
    const note = openNote();
    type(note, 'BP 150/95');
    // Saved before the next 4-second save, and refused: someone else changed the note meanwhile. Theirs is shown.
    act(() => note.result.current.draft.keep());
    act(() => note.result.current.form.reset({ category: 'General', note: 'BP 140/90, repeat at 6 pm' }));
    wait(8_000);

    expect(note.result.current.draft.saved?.value.note).toBe('BP 150/95');
    expect(stored('asha')?.value.note).toBe('BP 150/95');
    act(() => note.result.current.draft.restore());
    expect(note.result.current.form.getValues('note')).toBe('BP 150/95');
    expect(note.result.current.form.formState.defaultValues?.note).toBe('BP 140/90, repeat at 6 pm');
  });

  it('keeps nothing from a form left as it was filled', () => {
    const note = openNote();

    act(() => note.result.current.draft.keep());

    expect(note.result.current.draft.saved).toBeNull();
    expect(stored('asha')).toBeNull();
  });

  it('is wiped when its user signs out, and not offered after the next sign-in', async () => {
    const note = openNote();
    type(note, 'Wound dressing changed');
    wait(4_000);
    note.unmount();
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 204 })));

    await auth.logout();

    expect(stored('asha')).toBeNull();
    expect(onComputer()).not.toContain('hc360:draft');
    await signIn('asha');
    expect(openNote().result.current.draft.saved).toBeNull();
  });

  it('survives a start-up where the server cannot restore the session, and is offered once its user signs in', async () => {
    const note = openNote();
    type(note, 'Wound dressing changed');
    wait(4_000);
    await keptOnComputer('asha');
    note.unmount();
    // The app starts again while the server answers with an error (restarting after an update, its database down).
    closeDrafts();
    useSession.setState({ status: 'unknown', accessToken: null, expiresAt: null, user: null, locked: false });
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{}', { status: 503 })));

    expect(await auth.restore()).toBe(false);

    expect(useSession.getState().status).toBe('anonymous');
    await signIn('asha');
    expect(openNote().result.current.draft.saved?.value.note).toBe('Wound dressing changed');
  });

  it('belongs to one user and one form', async () => {
    const asha = openNote();
    type(asha, "Asha's note");
    wait(4_000);
    await keptOnComputer('asha');
    asha.unmount();

    // Not offered on another admission's note, nor to another nurse on the same computer…
    const other = openNote('nursing-note:adm-2');
    expect(other.result.current.draft.saved).toBeNull();
    other.unmount();
    await signIn('ravi');
    const ravi = openNote();
    expect(ravi.result.current.draft.saved).toBeNull();
    type(ravi, "Ravi's note");
    wait(4_000);
    ravi.unmount();

    // …whose own draft of it leaves Asha's as it was.
    expect(stored('ravi')?.value.note).toBe("Ravi's note");
    await keptOnComputer('ravi');
    await signIn('asha');
    expect(openNote().result.current.draft.saved?.value.note).toBe("Asha's note");
  });

  it('keeps no clinical text on this computer, only what its user’s key opens (audit P10)', async () => {
    const note = openNote();
    type(note, 'Patient drowsy, GCS 14');
    wait(4_000);

    await keptOnComputer('asha');

    expect(stored('asha')?.value.note).toBe('Patient drowsy, GCS 14');
    expect(onComputer()).not.toContain('GCS 14');
  });

  it('is offered after HC360 crashes, once its user signs in again, and to nobody else', async () => {
    const before = openNote();
    type(before, 'Patient drowsy, GCS 14');
    wait(4_000);
    await keptOnComputer('asha');
    // HC360 Desktop crashes: what it held in memory is gone, and Ravi signs in on the same Windows account.
    before.unmount();
    closeDrafts();
    await signIn('ravi');
    expect(openNote().result.current.draft.saved).toBeNull();

    closeDrafts();
    await signIn('asha');

    expect(openNote().result.current.draft.saved?.value.note).toBe('Patient drowsy, GCS 14');
  });

  it('is offered by a form opened before its user’s drafts were', async () => {
    const before = openNote();
    type(before, 'Patient drowsy, GCS 14');
    wait(4_000);
    await keptOnComputer('asha');
    before.unmount();
    closeDrafts();

    // The app starts again on the form, and the server's key comes a moment later.
    const again = openNote();
    expect(again.result.current.draft.saved).toBeNull();
    await act(() => openDrafts('asha', draftKeys.asha));

    expect(useDrafts.getState().openFor).toBe('asha');
    expect(again.result.current.draft.saved?.value.note).toBe('Patient drowsy, GCS 14');
  });
});
