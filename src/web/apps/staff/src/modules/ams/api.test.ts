import { describe, expect, it } from 'vitest';
import { approvalState } from './api';

describe('approvalState', () => {
  it('names where the approval stands, the urgent ones in red', () => {
    expect(
      approvalState({ status: 'Pending', approvalOverdue: false, reviewDue: false, reviewDecision: null }),
    ).toEqual({
      label: 'Waiting for approval',
      tone: 'warning',
    });
    expect(
      approvalState({ status: 'Pending', approvalOverdue: true, reviewDue: false, reviewDecision: null }).tone,
    ).toBe('danger');
    expect(
      approvalState({ status: 'Approved', approvalOverdue: false, reviewDue: true, reviewDecision: null }).label,
    ).toBe('Review due');
    expect(
      approvalState({ status: 'Approved', approvalOverdue: false, reviewDue: false, reviewDecision: 'DeEscalate' })
        .label,
    ).toBe('Reviewed: Narrow (de-escalate)');
    expect(
      approvalState({ status: 'Rejected', approvalOverdue: false, reviewDue: false, reviewDecision: null }).label,
    ).toBe('Not approved');
  });
});
