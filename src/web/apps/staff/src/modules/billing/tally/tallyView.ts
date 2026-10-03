import { formatInr } from '@hc360/core';
import type { TallyExportStatus } from '../labels';
import type {
  TallyCheckStatus,
  TallyExportDto,
  TallyMatch,
  TallySending,
  TallyVoucherAnswer,
  TallyVoucherDto,
  TallyVoucherStatus,
} from '../tallyApi';

/**
 * The words and colours of the Tally screen, and what an accountant should do about each voucher. Pure module: no
 * React and no API client, so it is unit-tested.
 */

export type Tone = 'success' | 'warning' | 'danger' | 'informative' | 'subtle';

export const EXPORT_STATUS_TONE: Record<TallyExportStatus, Tone> = {
  Posted: 'success',
  Imported: 'success',
  Queued: 'warning',
  NotConfigured: 'informative',
  Failed: 'danger',
};

export const CHECK_LABEL: Record<TallyCheckStatus, string> = {
  NotChecked: 'Not checked',
  Matched: 'Matched',
  Differences: 'Differences',
  Failed: 'Could not check',
};

export const CHECK_TONE: Record<TallyCheckStatus, Tone> = {
  NotChecked: 'subtle',
  Matched: 'success',
  Differences: 'danger',
  Failed: 'warning',
};

export const VOUCHER_STATUS_LABEL: Record<TallyVoucherStatus, string> = {
  NotSent: 'Not sent',
  InTally: 'In Tally',
  Refused: 'Refused by Tally',
  Unconfirmed: 'Not confirmed',
  ChangedSinceSent: 'Changed since sent',
  DiffersInTally: 'Differs in Tally',
  NoLongerInHc360: 'No longer in HC360',
  OnlyInTally: 'Only in Tally',
};

export const VOUCHER_STATUS_TONE: Record<TallyVoucherStatus, Tone> = {
  NotSent: 'informative',
  InTally: 'success',
  Refused: 'danger',
  Unconfirmed: 'warning',
  ChangedSinceSent: 'warning',
  DiffersInTally: 'danger',
  NoLongerInHc360: 'danger',
  OnlyInTally: 'danger',
};

export const ANSWER_LABEL: Record<TallyVoucherAnswer, string> = {
  None: 'Not sent yet',
  Created: 'Created',
  Altered: 'Altered',
  Accepted: 'Accepted',
  Rejected: 'Refused',
  Unconfirmed: 'Not confirmed',
  NotSent: 'Not sent',
  Imported: 'Imported from the file',
};

export const MATCH_LABEL: Record<TallyMatch, string> = {
  NotChecked: 'Not checked',
  Matched: 'Matches',
  Missing: 'Missing in Tally',
  AmountDiffers: 'Other amount in Tally',
  Changed: 'Changed in Tally',
  NotInBooks: 'Cancelled in Tally',
  OnlyInTally: 'Only in Tally',
};

/** A voucher an accountant has to act on. */
export function needsAttention(voucher: Pick<TallyVoucherDto, 'status'>): boolean {
  return voucher.status !== 'InTally';
}

/** What to do about a voucher, in one sentence; null when nothing is needed. */
export function whatToDo(voucher: Pick<TallyVoucherDto, 'status' | 'match'>): string | null {
  switch (voucher.status) {
    case 'InTally':
      return null;
    case 'NotSent':
      return 'Post the day to Tally, or import the file and mark the day imported.';
    case 'Refused':
      return 'Correct the cause in Tally (often a ledger that does not exist there), then post the day again.';
    case 'Unconfirmed':
      return 'Post the day again.';
    case 'ChangedSinceSent':
      return 'Post the day again to update Tally.';
    case 'DiffersInTally':
      return voucher.match === 'Missing'
        ? 'Post the day again to put it back in Tally.'
        : "Someone changed it in Tally. Post the day again to restore HC360's version, or correct it in Tally.";
    case 'NoLongerInHc360':
      return 'Delete it in Tally: HC360 no longer has this voucher.';
    case 'OnlyInTally':
      return 'Delete it in Tally: no day book in HC360 has this voucher.';
  }
}

/** "312 of 312 in Tally", for the posting log. */
export function confirmedSummary(day: Pick<TallyExportDto, 'confirmedCount' | 'voucherCount'>): string {
  return day.voucherCount === 0 ? 'No vouchers' : `${day.confirmedCount} of ${day.voucherCount} in Tally`;
}

/** One sentence about a day's vouchers: all in Tally, or what is outstanding. */
export function sendingSummary(sending: TallySending): string {
  if (sending.vouchers === 0 && sending.noLongerInHc360 + sending.onlyInTally === 0) {
    return 'Nothing was billed, received or paid on this day.';
  }

  const parts: string[] = [];
  const add = (count: number, words: string) => {
    if (count > 0) {
      parts.push(`${count} ${words}`);
    }
  };
  add(sending.notSent, 'not sent');
  add(sending.refused, 'refused or not confirmed');
  add(sending.changedSinceSent, 'changed since sent');
  add(sending.differsInTally, 'different in Tally');
  add(sending.noLongerInHc360, 'no longer in HC360');
  add(sending.onlyInTally, 'only in Tally');
  const head =
    sending.inTally === sending.vouchers
      ? `All ${sending.vouchers} voucher${sending.vouchers === 1 ? ' is' : 's are'} in Tally`
      : `${sending.inTally} of ${sending.vouchers} vouchers in Tally`;
  return parts.length === 0 ? `${head}.` : `${head}; ${parts.join(', ')}.`;
}

/** The file of a day can be marked imported once downloaded, unless Tally confirmed the day itself. */
export function canMarkImported(day: Pick<TallyExportDto, 'status' | 'fileDownloadedAt'> | undefined): boolean {
  return !!day?.fileDownloadedAt && day.status !== 'Posted' && day.status !== 'Queued';
}

/** The question before a day is marked imported: what it records, and the vouchers to delete in Tally first. */
export function importQuestion(
  date: string,
  vouchers: Pick<TallyVoucherDto, 'status' | 'number' | 'amount' | 'voucherType'>[],
): { title: string; body: string; deleteFirst: string[] } {
  const current = vouchers.filter((v) => v.status !== 'NoLongerInHc360' && v.status !== 'OnlyInTally');
  const total = current.reduce((sum, v) => sum + v.amount, 0);
  return {
    title: `Mark the day book of ${date} imported into Tally?`,
    body:
      `This records that the ${current.length} voucher${current.length === 1 ? '' : 's'} of the file you downloaded ` +
      `(${formatInr(total)}) are now in Tally. Do this only after Tally has imported the file without errors.`,
    deleteFirst: vouchers
      .filter((v) => v.status === 'NoLongerInHc360')
      .map((v) => `${v.voucherType} ${v.number} (${formatInr(v.amount)})`),
  };
}
