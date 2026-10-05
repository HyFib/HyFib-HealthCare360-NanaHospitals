import { describe, expect, it } from 'vitest';
import { gatewayRefundText } from './api';
import {
  DISCREPANCY_KIND_HELP,
  DISCREPANCY_KIND_LABEL,
  DISCREPANCY_KINDS,
  headlineAmount,
  openCount,
  resolvedBy,
  retryLabel,
  type ReconciliationSummaryDto,
} from './gatewayApi';

describe('gateway reconciliation wording', () => {
  it('lists the kinds worst first, each with a label and what to do', () => {
    expect(DISCREPANCY_KINDS).toEqual(['UnrecordedPayment', 'UnsettledReceipt', 'AmountMismatch', 'RefundMismatch']);
    for (const kind of DISCREPANCY_KINDS) {
      expect(DISCREPANCY_KIND_LABEL[kind]).toBeTruthy();
      expect(DISCREPANCY_KIND_HELP[kind]).toMatch(/\.$/);
      expect(DISCREPANCY_KIND_HELP[kind]).not.toContain('!');
    }
  });

  it('says what "try again" does for each kind', () => {
    expect(retryLabel({ kind: 'UnrecordedPayment', refundId: null, summary: '' })).toBe('Ask the gateway again');
    expect(retryLabel({ kind: 'UnsettledReceipt', refundId: null, summary: '' })).toBe('Read the settlements again');
    expect(
      retryLabel({
        kind: 'RefundMismatch',
        refundId: 'r1',
        summary: 'Refund NHRF1 of ₹500.00 failed at the gateway (rfnd_1).',
      }),
    ).toBe('Send the refund again');
    expect(
      retryLabel({ kind: 'RefundMismatch', refundId: null, summary: 'The gateway returned ₹500.00 to a patient.' }),
    ).toBe('Check again');
    expect(retryLabel({ kind: 'AmountMismatch', refundId: null, summary: '' })).toBe('Check again');
  });

  it("shows the gateway's amount first, else HC360's", () => {
    expect(headlineAmount({ gatewayAmount: 500, recordedAmount: 450 })).toBe(500);
    expect(headlineAmount({ gatewayAmount: null, recordedAmount: 450 })).toBe(450);
    expect(headlineAmount({ gatewayAmount: null, recordedAmount: null })).toBeNull();
  });

  it('counts the open exceptions across kinds', () => {
    const summary: ReconciliationSummaryDto = {
      enabled: true,
      settlementDays: 3,
      open: [
        { kind: 'UnrecordedPayment', count: 2, amount: 1500 },
        { kind: 'RefundMismatch', count: 1, amount: 200 },
      ],
      lastSettledOn: '2026-10-01',
      lastFetchedAt: null,
    };
    expect(openCount(summary)).toBe(3);
    expect(openCount(undefined)).toBe(0);
  });

  it('names who resolved an exception', () => {
    expect(resolvedBy({ resolvedAutomatically: true, resolvedByName: null })).toBe('HC360 (the cause went away)');
    expect(resolvedBy({ resolvedAutomatically: false, resolvedByName: 'Asha Nair' })).toBe('Asha Nair');
  });

  it('describes where an online refund stands', () => {
    expect(gatewayRefundText('Pending')).toBe('With the bank; it usually takes 5 to 7 working days');
    expect(gatewayRefundText('Processed', '74836287462837')).toBe(
      "Returned to the patient's account (bank reference 74836287462837)",
    );
    expect(gatewayRefundText('Failed', null, 'The bank account is closed')).toContain('The bank account is closed');
  });
});
