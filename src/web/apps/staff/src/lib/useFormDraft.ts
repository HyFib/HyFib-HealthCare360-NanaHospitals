import { draftStorage, draftStorageKey, useDrafts, useSession } from '@hc360/core';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { FieldValues, UseFormReturn } from 'react-hook-form';

interface SavedDraft<T> {
  value: T;
  savedAt: number;
}

function read<T>(key: string | null): SavedDraft<T> | null {
  if (!key) {
    return null;
  }
  try {
    const raw = draftStorage.getItem(key);
    return raw ? (JSON.parse(raw) as SavedDraft<T>) : null;
  } catch {
    return null;
  }
}

function remove(key: string | null): void {
  if (key) {
    draftStorage.removeItem(key);
  }
}

/**
 * Saves a form locally every few seconds while it changes, so a WiFi drop, a crash or an accidental close never
 * loses a half-typed registration or note (SRS DED-10). Drafts are per user, kept on the computer encrypted under the
 * user's key (`draftStorage`), and wiped at sign-out.
 */
export function useFormDraft<T extends FieldValues>(
  form: UseFormReturn<T> | null,
  key: string | null,
  intervalMs = 4000,
) {
  const userId = useSession((s) => s.user?.id);
  const storageKey = key && userId ? draftStorageKey(userId, key) : null;
  const [saved, setSaved] = useState<SavedDraft<T> | null>(() => read<T>(storageKey));
  // The user's drafts open once the server's key has come, which can be after the form: it looks again then.
  const draftsOpen = useDrafts((s) => userId !== undefined && s.openFor === userId);
  const [lookedOpen, setLookedOpen] = useState(draftsOpen);
  if (draftsOpen !== lookedOpen) {
    setLookedOpen(draftsOpen);
    if (draftsOpen && saved === null) {
      setSaved(read<T>(storageKey));
    }
  }
  // A draft on offer stays stored until it is restored or discarded, even while the form shows what it was filled with.
  const offered = useRef(saved !== null);
  useEffect(() => {
    offered.current = saved !== null;
  }, [saved]);

  useEffect(() => {
    if (!form || !storageKey) {
      return;
    }
    let changed = false;
    const subscription = form.watch(() => {
      changed = true;
    });
    const timer = setInterval(() => {
      if (!changed) {
        return;
      }
      changed = false;
      try {
        const values = form.getValues();
        // Back at the saved state (for example after a save reset the form to what the server holds): nothing to keep,
        // so saved content never comes back later as a "draft".
        if (JSON.stringify(values) === JSON.stringify(form.formState.defaultValues ?? {})) {
          if (!offered.current) {
            draftStorage.removeItem(storageKey);
          }
          return;
        }
        draftStorage.setItem(storageKey, JSON.stringify({ value: values, savedAt: Date.now() }));
      } catch {
        // Quota exceeded or private mode: drafts are best effort.
      }
    }, intervalMs);
    return () => {
      subscription.unsubscribe();
      clearInterval(timer);
    };
  }, [form, storageKey, intervalMs]);

  const restore = useCallback(() => {
    if (saved && form) {
      form.reset(saved.value, { keepDefaultValues: true });
    }
    setSaved(null);
  }, [saved, form]);

  const discard = useCallback(() => {
    remove(storageKey);
    setSaved(null);
  }, [storageKey]);

  const clear = useCallback(() => remove(storageKey), [storageKey]);

  /**
   * Keeps what the form holds now as the draft, stored and offered at once. Call it before the form is filled again
   * from the server after a conflict (REL-04): the newer record is shown, and what was typed can be put back on it.
   */
  const keep = useCallback(() => {
    if (!form || !storageKey) {
      return;
    }
    const values = form.getValues();
    if (JSON.stringify(values) === JSON.stringify(form.formState.defaultValues ?? {})) {
      return;
    }
    const raw = JSON.stringify({ value: values, savedAt: Date.now() });
    draftStorage.setItem(storageKey, raw);
    offered.current = true;
    setSaved(JSON.parse(raw) as SavedDraft<T>);
  }, [form, storageKey]);

  return { saved, restore, discard, clear, keep };
}
