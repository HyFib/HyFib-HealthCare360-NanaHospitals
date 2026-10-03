import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { describe, expect, it } from 'vitest';
import type { TallyDayCheck, TallyVoucherDto } from '../tallyApi';
import { ReconciliationPanel, VoucherList } from './TallyDay';

// The drawer around these is Fluent's (drawers flake in jsdom); the panel and the list are what is tested.
const wrap = (children: ReactNode) => render(<FluentProvider theme={webLightTheme}>{children}</FluentProvider>);

const check: TallyDayCheck = {
  date: '2026-09-25',
  status: 'Posted',
  records: 'Differences',
  lines: [
    {
      group: 'Money received',
      item: 'Cash',
      records: 650,
      dayBook: 700,
      recordsCount: null,
      dayBookCount: null,
      note: 'Records: the cashier day-end report',
      informational: false,
      difference: 50,
      matches: false,
    },
    {
      group: 'Bills',
      item: 'Finalised',
      records: 2180,
      dayBook: 2180,
      recordsCount: 2,
      dayBookCount: 2,
      note: null,
      informational: false,
      difference: 0,
      matches: true,
    },
    {
      group: 'Credit notes',
      item: 'Write-offs',
      records: 0,
      dayBook: 0,
      recordsCount: 0,
      dayBookCount: 0,
      note: null,
      informational: false,
      difference: 0,
      matches: true,
    },
  ],
  notes: ['Cash was short by ₹120.00 at OPD-1 (Asha). The day book has no voucher for it: record it in Tally.'],
  sending: {
    vouchers: 3,
    inTally: 2,
    notSent: 0,
    refused: 1,
    changedSinceSent: 0,
    differsInTally: 0,
    noLongerInHc360: 0,
    onlyInTally: 0,
    complete: false,
  },
  tally: 'Matched',
  tallyCheckedAt: '2026-09-26T01:10:00Z',
  tallyDifferences: 0,
  tallyMessage: 'Tally holds 2 of the 3 voucher(s); 0 differ; 0 other HC360 voucher(s) on the day.',
  canCheckTally: true,
};

function voucher(overrides: Partial<TallyVoucherDto>): TallyVoucherDto {
  return {
    key: 'NHRC260000001',
    remoteId: 'HC360-NHRC260000001',
    voucherType: 'Receipt',
    number: 'NHRC260000001',
    amount: 500,
    status: 'InTally',
    answer: 'Created',
    answerMessage: null,
    answeredAt: '2026-09-26T00:31:00Z',
    match: 'Matched',
    tallyAmount: 500,
    tallyNumber: 'NHRC260000001',
    tallyDetail: null,
    checkedAt: '2026-09-26T01:10:00Z',
    ...overrides,
  };
}

describe('ReconciliationPanel', () => {
  it('shows the figures that differ from the records, the notes and the check against Tally', () => {
    wrap(<ReconciliationPanel check={check} />);

    expect(screen.getByText('Differences')).toBeInTheDocument();
    expect(screen.getByText('2 of 3 vouchers in Tally; 1 refused or not confirmed.')).toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'The day book compared with the records' });
    const cash = within(table).getByText('Money received: Cash').closest('tr')!;
    expect(within(cash).getByText('₹50.00')).toBeInTheDocument();
    expect(within(table).getByText('2 in the records, 2 in the day book')).toBeInTheDocument();
    // A figure with nothing on either side is left out.
    expect(within(table).queryByText('Credit notes: Write-offs')).not.toBeInTheDocument();
    expect(screen.getByText(/Cash was short by/)).toBeInTheDocument();
    expect(screen.getByText('Matched')).toBeInTheDocument();
    expect(screen.getByText(check.tallyMessage!)).toBeInTheDocument();
  });
});

describe('VoucherList', () => {
  const vouchers = [
    voucher({}),
    voucher({
      key: 'NHRC260000002',
      number: 'NHRC260000002',
      status: 'Refused',
      answer: 'Rejected',
      answerMessage: "Ledger 'HDFC Card Settlement' does not exist!",
      match: 'NotChecked',
    }),
    voucher({
      key: 'DF-NH-20260925-DR005',
      number: 'DF-NH-20260925-DR005',
      voucherType: 'Journal',
      status: 'NoLongerInHc360',
      match: 'NotChecked',
    }),
  ];

  it('starts with the vouchers that need attention and says what to do about each', () => {
    wrap(<VoucherList vouchers={vouchers} />);

    expect(screen.getByRole('tab', { name: 'Needs attention (2)' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText("Ledger 'HDFC Card Settlement' does not exist!")).toBeInTheDocument();
    expect(screen.getByText('Refused by Tally')).toBeInTheDocument();
    expect(screen.getByText(/Correct the cause in Tally/)).toBeInTheDocument();
    expect(screen.getByText(/Delete it in Tally: HC360 no longer has this voucher/)).toBeInTheDocument();
    expect(screen.queryByText('NHRC260000001')).not.toBeInTheDocument();
  });

  it('lists every voucher on request', async () => {
    wrap(<VoucherList vouchers={vouchers} />);

    await userEvent.click(screen.getByRole('tab', { name: 'All (3)' }));

    expect(screen.getByText('NHRC260000001')).toBeInTheDocument();
    expect(screen.getByText('Created · Matches')).toBeInTheDocument();
  });

  it('says so when Tally holds every voucher', () => {
    wrap(<VoucherList vouchers={[voucher({})]} />);

    expect(screen.getByRole('tab', { name: 'All (1)' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByText('NHRC260000001')).toBeInTheDocument();
  });
});
