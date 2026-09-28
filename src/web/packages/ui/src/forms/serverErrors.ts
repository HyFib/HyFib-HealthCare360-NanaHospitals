import { isApiError } from '@hc360/core';
import type { FieldValues, Path, UseFormSetError } from 'react-hook-form';

/** Copies server validation errors (camelCase field paths) onto the form fields. Returns true if applied. */
export function applyServerErrors<T extends FieldValues>(error: unknown, setError: UseFormSetError<T>): boolean {
  if (!isApiError(error) || !error.isValidation) {
    return false;
  }
  for (const [field, messages] of Object.entries(error.fieldErrors)) {
    setError(field as Path<T>, { type: 'server', message: messages[0] });
  }
  return true;
}
