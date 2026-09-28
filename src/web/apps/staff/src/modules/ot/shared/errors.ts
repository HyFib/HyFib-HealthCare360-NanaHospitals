import { isApiError } from '@hc360/core';
import type { FieldValues, Path, UseFormSetError } from 'react-hook-form';

/**
 * Error helpers shared by the specialty-care modules. Pure functions only (no Fluent), so they are unit-tested.
 */

/** Server validation keys use brackets ("rows[0].heartRate"); react-hook-form uses dots ("rows.0.heartRate"). */
export function toFormPath(key: string): string {
  return key.replace(/\[(\d+)\]/g, '.$1');
}

/**
 * Puts server validation messages on the matching form fields, including fields inside arrays. Returns true when
 * at least one message was applied. Callers still show `ActionError`, so a message for a field that is not on
 * screen is never lost.
 */
export function applyFormErrors<T extends FieldValues>(error: unknown, setError: UseFormSetError<T>): boolean {
  if (!isApiError(error) || !error.isValidation) {
    return false;
  }
  let applied = false;
  for (const [field, messages] of Object.entries(error.fieldErrors)) {
    const message = messages[0];
    if (message) {
      setError(toFormPath(field) as Path<T>, { type: 'server', message });
      applied = true;
    }
  }
  return applied;
}

/** Machine-readable problem code ("surgery.slot_conflict", "patient.restricted"…). */
export function errorCode(error: unknown): string | undefined {
  return isApiError(error) ? error.code : undefined;
}

/** HTTP status of an API error (0 = server not reachable). */
export function errorStatus(error: unknown): number | undefined {
  return isApiError(error) ? error.status : undefined;
}

/** Someone else saved the record first (optimistic concurrency). */
export function isConcurrencyConflict(error: unknown): boolean {
  return isApiError(error) && error.status === 409 && error.code === 'concurrency';
}

export function isNotFound(error: unknown): boolean {
  return isApiError(error) && error.status === 404;
}

/** A business rule refused the action (HTTP 422) — show it prominently, never as a passing toast. */
export function isRuleViolation(error: unknown): boolean {
  return isApiError(error) && error.status === 422;
}

/** The best single sentence for a user, whatever the error. */
export function errorMessage(error: unknown, fallback = 'Something went wrong.'): string {
  if (isApiError(error)) {
    return error.userMessage;
  }
  return error instanceof Error && error.message ? error.message : fallback;
}
