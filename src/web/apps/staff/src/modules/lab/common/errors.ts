import { isApiError } from '@hc360/core';

/** The machine-readable Problem Details code (e.g. "lab.payment_pending"), or undefined for other errors. */
export function errorCode(error: unknown): string | undefined {
  return isApiError(error) ? error.code : undefined;
}

/** True when the error carries the given Problem Details code. */
export function hasErrorCode(error: unknown, code: string): boolean {
  return errorCode(error) === code;
}

/** The `data` payload of a Problem Details response (for example the unpaid tests of a payment gate refusal). */
export function errorData<T>(error: unknown): T | undefined {
  return isApiError(error) ? (error.data as T | undefined) : undefined;
}

/** One sentence for the user (server detail, or the fallback for non-API errors). */
export function errorMessage(error: unknown, fallback: string): string {
  return isApiError(error) ? error.userMessage : fallback;
}

/**
 * Field errors of a bulk request, keyed by the item index: the server reports `entries[3].value`, the UI shows the
 * message next to the fourth line.
 */
export function indexedFieldErrors(error: unknown, collection: string): Map<number, string> {
  const result = new Map<number, string>();
  if (!isApiError(error)) {
    return result;
  }
  const pattern = new RegExp(`^${collection}\\[(\\d+)\\]`, 'i');
  for (const [field, messages] of Object.entries(error.fieldErrors)) {
    const match = pattern.exec(field);
    if (match?.[1] && messages[0]) {
      result.set(Number(match[1]), messages[0]);
    }
  }
  return result;
}
