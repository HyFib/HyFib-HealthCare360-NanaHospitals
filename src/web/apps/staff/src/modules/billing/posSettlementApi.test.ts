import { describe, expect, it } from 'vitest';
import {
  counterDocumentText,
  lineQuery,
  matchText,
  references,
  toLookAtCount,
  toLookAtHint,
  type PosSettlementSummaryDto,
} from './posSettlementApi';

describe('card and UPI settlements', () => {
  it('asks for a payout by its day and UTR, and for one without a UTR by noUtr', () => {
    expect(lineQuery({ settledOn: '2026-10-07', utr: 'AXISN1' })).toMatchObject({
      settledOn: '2026-10-07',
      utr: 'AXISN1',
      noUtr: undefined,
    });
    expect(lineQuery({ settledOn: '2026-10-07', utr: null })).toMatchObject({ utr: undefined, noUtr: true });
    expect(lineQuery({ attention: true, page: 2 })).toEqual({
      pageSize: 25,
      page: 2,
      attention: true,
      settledOn: undefined,
      utr: undefined,
      noUtr: undefined,
    });
  });

  it('lists the references a bank statement and a charge slip show, never more of the card than its last digits', () => {
    expect(references({ rrn: '627910123456', approvalCode: 'A1B2C3', cardLast4: '1234', terminalId: '72001234' })).toBe(
      'RRN 627910123456 · Approval A1B2C3 · Card ending 1234 · TID 72001234',
    );
    expect(references({ rrn: null, approvalCode: null, cardLast4: null, terminalId: null })).toBe('');
  });

  it('says what a transaction matched and how, or what to do about it', () => {
    const billing = { type: 'Sale', counter: null, counterDocument: null } as const;
    expect(
      matchText({
        ...billing,
        match: 'Matched',
        matchBasis: 'Rrn',
        receiptNumber: 'NHRC260000012',
        refundNumber: null,
        recordedAmount: 500,
      }),
    ).toBe('Receipt NHRC260000012 (by the RRN)');
    expect(
      matchText({
        ...billing,
        match: 'AmountDiffers',
        matchBasis: 'Rrn',
        receiptNumber: 'NHRC260000013',
        refundNumber: null,
        recordedAmount: 450,
      }),
    ).toBe('Receipt NHRC260000013 (by the RRN) records another amount');
    expect(
      matchText({
        ...billing,
        match: 'NoReceipt',
        matchBasis: null,
        receiptNumber: null,
        refundNumber: null,
        recordedAmount: null,
      }),
    ).toContain('Record the payment on its bill, then match again.');
  });

  it("names the pharmacy's invoice or the canteen's bill a transaction matched, and a refund on one", () => {
    const counter = { match: 'Matched', receiptNumber: null, refundNumber: null, recordedAmount: 240 } as const;
    expect(
      matchText({ ...counter, type: 'Sale', matchBasis: 'Rrn', counter: 'pharmacy', counterDocument: 'PH/0042' }),
    ).toBe('Pharmacy invoice PH/0042 (by the RRN)');
    expect(
      matchText({
        ...counter,
        type: 'Sale',
        match: 'AmountDiffers',
        matchBasis: 'ApprovalCode',
        counter: 'canteen',
        counterDocument: 'CB-17',
      }),
    ).toBe('Canteen bill CB-17 (by the approval code and amount) records another amount');
    expect(
      matchText({ ...counter, type: 'Refund', matchBasis: 'Rrn', counter: 'pharmacy', counterDocument: 'PH/0042' }),
    ).toBe('Refund on pharmacy invoice PH/0042 (by the RRN)');
    expect(counterDocumentText('stores', 'S-1')).toBe('Stores S-1');
  });

  it('counts what to look at across the reasons', () => {
    const summary: PosSettlementSummaryDto = {
      settlementDays: 3,
      toLookAt: [
        { match: 'NoReceipt', count: 2, amount: 1_500 },
        { match: 'Review', count: 1, amount: 100 },
      ],
      unsettled: 0,
      unsettledAmount: 0,
      lastSettledOn: null,
      lastImport: null,
      otherTerminals: [],
    };

    expect(toLookAtCount(summary)).toBe(3);
    expect(toLookAtCount(undefined)).toBe(0);
    expect(toLookAtHint(summary)).toBe('2 without a receipt, 1 chargeback');
    expect(toLookAtHint({ ...summary, toLookAt: [{ match: 'NoRefund', count: 2, amount: 300 }] })).toBe(
      '2 refunds not in HC360',
    );
  });
});
