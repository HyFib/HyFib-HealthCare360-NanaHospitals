import { draftStorageKey, useSession } from '@hc360/core';
import { useCallback, useEffect, useState } from 'react';
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
    const raw = localStorage.getItem(key);
    return raw ? (JSON.parse(raw) as SavedDraft<T>) : null;
  } catch {
    return null;
  }
}

function remove(key: string | null): void {
  if (!key) {
    return;
  }
  try {
    localStorage.removeItem(key);
  } catch {
    // Storage unavailable — nothing to remove.
  }
}

/**
 * Saves a form locally every few seconds while it changes, so a WiFi drop, a crash or an accidental close never
 * loses a half-typed registration or note (SRS DED-10). Drafts are per user and wiped at sign-out.
 */
export function useFormDraft<T extends FieldValues>(
  form: UseFormReturn<T> | null,
  key: string | null,
  intervalMs = 4000,
) {
  const userId = useSession((s) => s.user?.id);
  const storageKey = key && userId ? draftStorageKey(userId, key) : null;
  const [saved, setSaved] = useState<SavedDraft<T> | null>(() => read<T>(storageKey));

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
          localStorage.removeItem(storageKey);
          return;
        }
        localStorage.setItem(storageKey, JSON.stringify({ value: values, savedAt: Date.now() }));
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

  return { saved, restore, discard, clear };
}
