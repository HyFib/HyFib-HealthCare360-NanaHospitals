import { describe, expect, it } from 'vitest';
import { TALLY_EXPORT_STATUSES } from '../labels';
import type { TallySending, TallyVoucherDto, TallyVoucherStatus } from '../tallyApi';
import {
  ANSWER_LABEL,
  canMarkImported,
  confirmedSummary,
  EXPORT_STATUS_TONE,
  importQuestion,
  needsAttention,
  sendingSummary,
  VOUCHER_STATUS_LABEL,
  VOUCHER_STATUS_TONE,
  whatToDo,
} from './tallyView';

const STATUSES: TallyVoucherStatus[] = [
  'NotSent',
  'InTally',
  'Refused',
  'Unconfirmed',
  'ChangedSinceSent',
  'DiffersInTally',
  'NoLongerInHc360',
  'OnlyInTally',
  'BillOfAnotherDay',
];

function sending(overrides: Partial<TallySending> = {}): TallySending {
  return {
    vouchers: 0,
    inTally: 0,
    notSent: 0,
    refused: 0,
    changedSinceSent: 0,
    differsInTally: 0,
    noLongerInHc360: 0,
    onlyInTally: 0,
    complete: true,
    ...overrides,
  };
}

const voucher = (status: TallyVoucherStatus, number = 'NHRC260000001', amount = 500): TallyVoucherDto => ({
  key: number,
  remoteId: `HC360-${number}`,
  voucherType: 'Receipt',
  number,
  amount,
  status,
  answer: status === 'InTally' ? 'Created' : 'None',
  answerMessage: null,
  answeredAt: null,
  match: 'NotChecked',
  tallyAmount: null,
  tallyNumber: null,
  tallyDetail: null,
  checkedAt: null,
});

describe('Tally screen words', () => {
  it('has a label and a colour for every status the server sends', () => {
    for (const status of STATUSES) {
      expect(VOUCHER_STATUS_LABEL[status]).toBeTruthy();
      expect(VOUCHER_STATUS_TONE[status]).toBeTruthy();
    }
    for (const status of TALLY_EXPORT_STATUSES) {
      expect(EXPORT_STATUS_TONE[status]).toBeTruthy();
    }
    expect(ANSWER_LABEL.Imported).toBe('Imported from the file');
  });

  it('tells the accountant what to do about every voucher that is not in Tally', () => {
    expect(whatToDo(voucher('InTally'))).toBeNull();
    expect(needsAttention(voucher('InTally'))).toBe(false);
    for (const status of STATUSES.filter((s) => s !== 'InTally')) {
      expect(needsAttention(voucher(status))).toBe(true);
      expect(whatToDo(voucher(status))).toMatch(/\.$/);
    }
    expect(whatToDo({ status: 'DiffersInTally', match: 'Missing' })).toBe(
      'Post the day again to put it back in Tally.',
    );
    expect(whatToDo({ status: 'NoLongerInHc360', match: 'Matched' })).toContain('Delete it in Tally');
  });

  it("asks for the bill's own day to be posted again, not for the bill to be deleted", () => {
    const advice = whatToDo({ status: 'BillOfAnotherDay', match: 'OnlyInTally', billDate: '2026-09-24' });
    expect(advice).toContain('Post the day book of');
    expect(advice).toContain('2026');
    expect(advice).toContain('Do not delete it');
    expect(whatToDo({ status: 'BillOfAnotherDay', match: 'OnlyInTally' })).toContain("the bill's own day");
  });

  it('sums up a day in one sentence', () => {
    expect(sendingSummary(sending())).toBe('Nothing was billed, received or paid on this day.');
    expect(sendingSummary(sending({ vouchers: 1, inTally: 1 }))).toBe('All 1 voucher is in Tally.');
    expect(sendingSummary(sending({ vouchers: 312, inTally: 312 }))).toBe('All 312 vouchers are in Tally.');
    expect(
      sendingSummary(
        sending({ vouchers: 312, inTally: 305, refused: 3, changedSinceSent: 4, noLongerInHc360: 1, complete: false }),
      ),
    ).toBe('305 of 312 vouchers in Tally; 3 refused or not confirmed, 4 changed since sent, 1 no longer in HC360.');
    expect(confirmedSummary({ voucherCount: 312, confirmedCount: 310 })).toBe('310 of 312 in Tally');
    expect(confirmedSummary({ voucherCount: 0, confirmedCount: 0 })).toBe('No vouchers');
  });

  it('offers "mark imported" only after the file was downloaded, and not for a day Tally confirmed', () => {
    expect(canMarkImported(undefined)).toBe(false);
    expect(canMarkImported({ status: 'NotConfigured', fileDownloadedAt: null })).toBe(false);
    expect(canMarkImported({ status: 'NotConfigured', fileDownloadedAt: '2026-09-26T04:00:00Z' })).toBe(true);
    expect(canMarkImported({ status: 'Failed', fileDownloadedAt: '2026-09-26T04:00:00Z' })).toBe(true);
    expect(canMarkImported({ status: 'Posted', fileDownloadedAt: '2026-09-26T04:00:00Z' })).toBe(false);
    expect(canMarkImported({ status: 'Queued', fileDownloadedAt: '2026-09-26T04:00:00Z' })).toBe(false);
  });

  it('asks before marking a day imported and lists what to delete in Tally first', () => {
    const question = importQuestion('25 Sept 2026', [
      voucher('NotSent', 'RC1', 500),
      voucher('NotSent', 'RC2', 250),
      voucher('NoLongerInHc360', 'DF-NH-20260925-DR005', 1200),
      voucher('OnlyInTally', 'DF-20260925-001', 900),
      voucher('BillOfAnotherDay', 'NHOP260000042', 300),
    ]);

    expect(question.title).toBe('Mark the day book of 25 Sept 2026 imported into Tally?');
    expect(question.body).toContain('the 2 vouchers of the file you downloaded');
    expect(question.body).toContain('750.00');
    expect(question.deleteFirst).toHaveLength(1);
    expect(question.deleteFirst[0]).toContain('Receipt DF-NH-20260925-DR005');
  });
});
