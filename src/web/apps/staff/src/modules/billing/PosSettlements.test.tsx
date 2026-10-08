import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type { PagedResult } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as PosApi from './posSettlementApi';
import { posSettlementApi, type PosSettlementImportResult, type PosSettlementLineDto } from './posSettlementApi';
import { ImportReportDialog, PosToLookAt } from './PosSettlements';

// The server, the router and the toasts are outside the test; the screen's own behaviour is what is tested.
const listLines = vi.hoisted(() => vi.fn());
vi.mock('./posSettlementApi', async (importOriginal) => {
  const actual = await importOriginal<typeof PosApi>();
  return {
    ...actual,
    posSettlementApi: { importReport: vi.fn(), review: vi.fn(), match: vi.fn(), trainingFile: vi.fn() },
    posSettlementQueries: {
      ...actual.posSettlementQueries,
      lines: (filter: PosApi.PosLineFilter) => ({
        queryKey: [...actual.posSettlementKeys.lines(), filter],
        queryFn: () => listLines(filter),
      }),
    },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
vi.mock('@tanstack/react-router', () => ({ useNavigate: () => vi.fn() }));

// Fluent's MessageBar measures itself; jsdom has no ResizeObserver.
beforeAll(() => {
  vi.stubGlobal(
    'ResizeObserver',
    class {
      observe() {}
      unobserve() {}
      disconnect() {}
    },
  );
});

beforeEach(() => {
  vi.clearAllMocks();
});

function wrap(children: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </FluentProvider>
  );
}

const imported: PosSettlementImportResult = {
  import: {
    id: 'i1',
    acquirer: 'HDFC Bank',
    fileName: 'mis-07-10.csv',
    fileSizeBytes: 2_048,
    rows: 4,
    added: 3,
    alreadyImported: 1,
    ignored: 1,
    firstSettledOn: '2026-10-07',
    lastSettledOn: '2026-10-07',
    importedAt: '2026-10-08T05:00:00Z',
    importedByName: 'Accounts',
  },
  columns: ['RRN from "RRN"', 'Amount from "Txn Amount"'],
  problems: ['Row 7 has neither an RRN nor an approval code, so it cannot be matched and was left out.'],
  matched: 1,
  toLookAt: 2,
  message:
    "Imported 3 new transactions; 1 of the report's 4 were already imported. 1 matches HC360's records; 2 transactions to look at.",
};

function line(patch: Partial<PosSettlementLineDto>): PosSettlementLineDto {
  return {
    id: 'l1',
    acquirer: 'HDFC Bank',
    type: 'Sale',
    mode: 'Upi',
    terminalId: '72001234',
    transactionAt: '2026-10-06T05:32:10Z',
    transactionOn: '2026-10-06',
    amount: 999,
    charges: 0,
    tax: 0,
    net: 999,
    rrn: '627910654321',
    approvalCode: null,
    cardLast4: null,
    settledOn: '2026-10-07',
    utr: 'AXISN26280012345',
    match: 'NoReceipt',
    matchBasis: null,
    recordedAmount: null,
    receiptId: null,
    receiptNumber: null,
    billId: null,
    refundId: null,
    refundNumber: null,
    needsAttention: true,
    reviewedAt: null,
    reviewedByName: null,
    reviewNote: null,
    version: 4,
    ...patch,
  };
}

const page = (items: PosSettlementLineDto[]): PagedResult<PosSettlementLineDto> => ({
  items,
  page: 1,
  pageSize: 25,
  totalCount: items.length,
});

describe('import a settlement report', () => {
  it('needs a file and the acquirer, then shows what was imported and the rows left out', async () => {
    vi.mocked(posSettlementApi.importReport).mockResolvedValue(imported);
    const user = userEvent.setup();
    render(wrap(<ImportReportDialog open defaultAcquirer="HDFC Bank" onClose={vi.fn()} />));

    const importButton = screen.getByRole('button', { name: 'Import' });
    expect(importButton).toBeDisabled();
    const file = new File(['RRN,Txn Amount\n'], 'mis-07-10.csv', { type: 'text/csv' });
    await user.upload(screen.getByLabelText('Settlement report'), file);
    expect(screen.getByText('mis-07-10.csv')).toBeInTheDocument();
    await user.click(importButton);

    expect(posSettlementApi.importReport).toHaveBeenCalledWith(file, 'HDFC Bank', null);
    expect(await screen.findByText(imported.message, { exact: false })).toBeInTheDocument();
    expect(
      within(screen.getByRole('list', { name: 'Rows left out' })).getByText(imported.problems[0]!),
    ).toBeInTheDocument();
    expect(screen.getByText('Columns read: RRN from "RRN", Amount from "Txn Amount"')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Import' })).not.toBeInTheDocument();
  });

  it('sends the day the bank was paid only when one is chosen', async () => {
    vi.mocked(posSettlementApi.importReport).mockResolvedValue(imported);
    const user = userEvent.setup();
    render(wrap(<ImportReportDialog open onClose={vi.fn()} />));

    await user.upload(screen.getByLabelText('Settlement report'), new File(['x'], 'report.xlsx'));
    expect(screen.getByRole('button', { name: 'Import' })).toBeDisabled();
    await user.type(screen.getByRole('textbox', { name: /Acquirer/ }), 'Pine Labs');
    await user.type(screen.getByLabelText(/Day the bank was paid/), '2026-10-07');
    await user.click(screen.getByRole('button', { name: 'Import' }));

    expect(posSettlementApi.importReport).toHaveBeenCalledWith(expect.any(File), 'Pine Labs', '2026-10-07');
  });
});

describe('transactions to look at', () => {
  it('shows why each does not match, without a patient, and keeps a reviewer’s note', async () => {
    listLines.mockResolvedValue(page([line({})]));
    vi.mocked(posSettlementApi.review).mockResolvedValue(
      line({ reviewedAt: '2026-10-08T06:00:00Z', needsAttention: false }),
    );
    const user = userEvent.setup();
    render(wrap(<PosToLookAt page={1} onPage={vi.fn()} />));

    expect(await screen.findByText('Settled, no receipt')).toBeInTheDocument();
    expect(screen.getByText(/RRN 627910654321 · TID 72001234/)).toBeInTheDocument();
    expect(listLines).toHaveBeenCalledWith({ attention: true, page: 1 });

    await user.click(screen.getByRole('button', { name: /Actions for the transaction/ }));
    await user.click(await screen.findByRole('menuitem', { name: 'Mark reviewed…' }));
    await user.type(screen.getByRole('textbox', { name: /What you found or did/ }), 'Paid at the canteen counter.');
    await user.click(screen.getByRole('button', { name: 'Mark reviewed' }));

    await waitFor(() => expect(posSettlementApi.review).toHaveBeenCalledWith('l1', 'Paid at the canteen counter.', 4));
    expect(notify.success).toHaveBeenCalledWith('Marked reviewed', 'Your note is kept with the transaction.');
  });

  it('matches again and says what happened', async () => {
    listLines.mockResolvedValue(page([]));
    vi.mocked(posSettlementApi.match).mockResolvedValue({
      checked: 2,
      matched: 1,
      toLookAt: 1,
      message: '1 transaction now matches; 1 transaction still to look at.',
    });
    const user = userEvent.setup();
    render(wrap(<PosToLookAt page={1} onPage={vi.fn()} />));

    expect(await screen.findByText('Nothing to look at')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Match again' }));

    await waitFor(() =>
      expect(notify.info).toHaveBeenCalledWith(
        'Matched again',
        '1 transaction now matches; 1 transaction still to look at.',
      ),
    );
  });
});
