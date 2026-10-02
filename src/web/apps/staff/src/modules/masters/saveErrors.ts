import { isApiError } from '@hc360/core';
import { toFormPath, type useNotify } from '@hc360/ui';
import type { FieldValues, Path, UseFormReturn } from 'react-hook-form';

type Notify = ReturnType<typeof useNotify>;

/**
 * Shows a failed save where the administrator will look: each field message beside its field (the first one focused), a
 * duplicate code on the code, the facility rules ("you are signed in to this facility") on "In use", and a change made
 * by someone else as a warning with the record refreshed. Anything else is a toast. Server field paths are mapped with
 * `fieldFor` (the facility form keeps the address parts at the top level).
 */
export function showSaveError<T extends FieldValues>(
  error: unknown,
  {
    form,
    notify,
    what,
    onConflict,
    fieldFor = (path) => path,
  }: {
    form: UseFormReturn<T>;
    notify: Notify;
    /** The record in words: "facility", "department". */
    what: string;
    onConflict: () => void;
    fieldFor?: (serverPath: string) => string;
  },
): void {
  const values = form.getValues() as Record<string, unknown>;
  const place = (field: string, message: string, focus: boolean): boolean => {
    if (!(field in values)) {
      return false;
    }
    form.setError(field as Path<T>, { type: 'server', message }, { shouldFocus: focus });
    return true;
  };

  if (isApiError(error)) {
    if (error.code === 'concurrency') {
      notify.warning(
        `Someone else changed this ${what}`,
        'Close it and open it again to see their version, then make your change.',
      );
      onConflict();
      return;
    }
    if (error.code?.endsWith('.duplicate_code') && place('code', error.userMessage, true)) {
      return;
    }
    if (
      (error.code === 'facility.current' || error.code === 'facility.last_active') &&
      place('isActive', error.userMessage, false)
    ) {
      return;
    }
    if (error.isValidation) {
      let placed = false;
      for (const [path, messages] of Object.entries(error.fieldErrors)) {
        const message = messages[0];
        if (message && place(fieldFor(toFormPath(path)), message, !placed)) {
          placed = true;
        }
      }
      if (placed) {
        return;
      }
    }
  }
  notify.error(error, `Could not save the ${what}`);
}

/** The facility form keeps the address parts at the top level: "address.pinCode" → "pinCode". */
export function facilityField(serverPath: string): string {
  return serverPath.startsWith('address.') ? serverPath.slice('address.'.length) : serverPath;
}
