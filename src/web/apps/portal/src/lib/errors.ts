import { isApiError } from '@hc360/core';
import type { MessageKey, Translate } from '@/i18n';

export interface FriendlyError {
  title: string;
  message: string;
  /** Support reference from the server (never contains patient data). */
  reference?: string;
  /** False when retrying the same request cannot help (for example a rule the patient must act on). */
  retryable: boolean;
}

/**
 * Turns any failure into words a patient understands, in their language. Transport and status problems are
 * translated here; business-rule messages from the server (400/409/422, for example "This slot was just taken")
 * are shown as sent, because they carry the specific reason.
 */
export function describeError(error: unknown, t: Translate, title: MessageKey = 'errors.title'): FriendlyError {
  if (!isApiError(error)) {
    return { title: t(title), message: t('errors.generic'), retryable: true };
  }
  const reference = error.referenceId;
  switch (true) {
    case error.status === 0:
      return { title: t('errors.network.title'), message: t('errors.network'), retryable: true };
    case error.status === 429:
      return { title: t(title), message: t('errors.tooMany'), retryable: true, reference };
    case error.status === 401:
      return { title: t(title), message: t('errors.sessionEnded'), retryable: false, reference };
    case error.status === 403:
      return { title: t(title), message: t('errors.forbidden'), retryable: false, reference };
    case error.status === 404:
      return { title: t(title), message: t('errors.notFound'), retryable: false, reference };
    case error.status >= 500:
      return { title: t(title), message: t('errors.server'), retryable: true, reference };
    case error.status === 409 && !error.detail:
      return { title: t(title), message: t('errors.conflict'), retryable: true, reference };
    default:
      return { title: t(title), message: error.userMessage || t('errors.generic'), retryable: false, reference };
  }
}

/** The server's error code (for example "auth.otp_throttled"), when there is one. */
export function errorCode(error: unknown): string | undefined {
  return isApiError(error) ? error.code : undefined;
}

export function fieldError(error: unknown, field: string): string | undefined {
  return isApiError(error) ? error.fieldErrors[field]?.[0] : undefined;
}
