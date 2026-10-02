import { isApiError } from '@hc360/core';

/**
 * Plain-language text for assistant and voice failures (never a raw error). The AI endpoints answer with Problem
 * Details carrying a machine-readable `code`: ai.disabled, ai.speech_disabled, ai.quota, ai.provider_failed; the
 * `ai` rate limit (20 requests a minute per user) answers 429 without one. An automatic retry after a dropped
 * connection gets idempotency.answer_not_kept: the server answered, but AI answers are not stored.
 */
export function assistantErrorMessage(error: unknown): string {
  if (!isApiError(error)) {
    return 'Something went wrong. Please try again, or use the menu instead.';
  }
  switch (error.code) {
    case 'ai.disabled':
      return 'AI help is turned off, so only everyday commands work. Try “today’s appointments” or “go to billing”.';
    case 'ai.speech_disabled':
      return 'Voice input is not set up on the server. Type the command instead, or use HC360 Desktop.';
    case 'ai.quota':
      return 'You have reached your AI usage limit for now. Everyday commands still work; AI help is available again later.';
    case 'ai.provider_failed':
      return 'The AI service could not help this time. Please try again, or type the command.';
    case 'idempotency.answer_not_kept':
      return 'The connection dropped before the answer arrived. Please ask again.';
    default:
      break;
  }
  if (error.isNetworkError) {
    return 'Cannot reach the HealthCare360 server. Check the WiFi connection and try again.';
  }
  if (error.status === 429) {
    return 'Too many requests in a short time. Wait a minute and try again.';
  }
  if (error.isForbidden) {
    return 'You do not have permission to use the assistant. Ask your administrator if you need it.';
  }
  if (error.status === 401) {
    return 'Your session has ended. Please sign in again.';
  }
  if (error.isValidation) {
    // Field messages from the server's validators are written for staff ("Say or type a command.").
    return error.userMessage;
  }
  if (error.status === 413) {
    return 'The recording is too long. Keep it under 30 seconds.';
  }
  if (error.status >= 500) {
    return 'The server had a problem. Please try again in a moment.';
  }
  return 'The assistant could not do that. Please try again.';
}
