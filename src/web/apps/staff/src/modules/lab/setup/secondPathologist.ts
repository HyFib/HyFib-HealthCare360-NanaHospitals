import { isApiError } from '@hc360/core';
import type { ApprovalRequestDto } from '@/modules/approvals/api';

/** The answer was a change asked for (202), not made: it waits in the approvals inbox. */
export function isApprovalRequest(value: unknown): value is ApprovalRequestDto {
  return typeof value === 'object' && value !== null && 'kind' in value && 'status' in value && 'summary' in value;
}

/** The server refused the change for want of a reason: it loosens a safety setting a second pathologist decides. */
export function asksForReason(error: unknown): boolean {
  return isApiError(error) && Object.keys(error.fieldErrors).some((field) => field.toLowerCase() === 'reason');
}

/** What the person who asked is told once a loosening waits for a second pathologist. */
export const waitsForSecondPathologist =
  'Another pathologist approves it in the approvals inbox. Until then the setting in force stays.';
