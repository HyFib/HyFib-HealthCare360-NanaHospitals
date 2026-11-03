import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type { PagedResult } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as PosApi from './posSettlementApi';
import {
  posSettlementApi,
  type PosSettlementImportDto,
  type PosSettlementImportResult,
  type PosSettlementLineDto,
  type UnsettledPosReceiptDto,
} from './posSettlementApi';
import { ImportReportDialog, PosImports, PosToLookAt, PosUnsettled } from './PosSettlements';

// The server, the router and the toasts are outside the test; the screen's own behaviour is what is tested.
const listLines = vi.hoisted(() => vi.fn());
const listImports = vi.hoisted(() => vi.fn());
const listUnsettled = vi.hoisted(() => vi.fn());
vi.mock('./posSettlementApi', async (importOriginal) => {
  const actual = await importOriginal<typeof PosApi>();
  return {
    ...actual,
    posSettlementApi: {
      importReport: vi.fn(),
      review: vi.fn(),
      match: vi.fn(),
      trainingFile: vi.fn(),
      withdraw: vi.fn(),
    },
    posSettlementQueries: {
      ...actual.posSettlementQueries,
      lines: (filter: PosApi.PosLineFilter) => ({
        queryKey: [...actual.posSettlementKeys.lines(), filter],
        queryFn: () => listLines(filter),
      }),
      imports: (pageNumber: number) => ({
        queryKey: [...actual.posSettlementKeys.imports(), pageNumber],
        queryFn: () => listImports(pageNumber),
      }),
      unsettled: () => ({ queryKey: actual.posSettlementKeys.unsettled(), queryFn: () => listUnsettled() }),
    },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
const navigate = vi.hoisted(() => vi.fn());
vi.mock('@tanstack/react-router', () => ({ useNavigate: () => navigate }));

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
    withdrawnOn: null,
    withdrawnByName: null,
    withdrawReason: null,
    withdrawalAskedAt: null,
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
    counter: null,
    counterDocument: null,
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

const reports = (items: PosSettlementImportDto[]): PagedResult<PosSettlementImportDto> => ({
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

  it("names the pharmacy's invoice a transaction matched, with no bill to open", async () => {
    listLines.mockResolvedValue(
      page([
        line({
          match: 'AmountDiffers',
          matchBasis: 'Rrn',
          recordedAmount: 990,
          counter: 'pharmacy',
          counterDocument: 'PH/0042',
        }),
      ]),
    );
    const user = userEvent.setup();
    render(wrap(<PosToLookAt page={1} onPage={vi.fn()} />));

    expect(await screen.findByText('Pharmacy invoice PH/0042 (by the RRN) records another amount')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /Actions for the transaction/ }));
    expect(await screen.findByRole('menuitem', { name: 'Mark reviewed…' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Open the bill' })).not.toBeInTheDocument();
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

function unsettled(patch: Partial<UnsettledPosReceiptDto>): UnsettledPosReceiptDto {
  return {
    receiptId: 'r1',
    receiptNumber: 'NHRC260000012',
    paymentId: 'p1',
    billId: 'b1',
    receivedAt: '2026-10-01T05:00:00Z',
    mode: 'Card',
    amount: 500,
    reference: '627410000001',
    cardLast4: '1234',
    terminalId: null,
    daysWaiting: 7,
    counter: null,
    cancelled: false,
    ...patch,
  };
}

describe('payments not settled', () => {
  it("lists the pharmacy's and the canteen's with Billing's receipts, and opens only a receipt's bill", async () => {
    listUnsettled.mockResolvedValue([
      unsettled({}),
      unsettled({
        receiptId: null,
        receiptNumber: 'PH/0042',
        paymentId: 'p2',
        billId: null,
        mode: 'Upi',
        amount: 240,
        reference: '627510000002',
        cardLast4: null,
        counter: 'pharmacy',
      }),
      unsettled({
        receiptId: null,
        receiptNumber: 'CB-17',
        paymentId: 'p3',
        billId: null,
        amount: 90,
        reference: null,
        cardLast4: null,
        counter: 'canteen',
        cancelled: true,
      }),
    ]);
    const user = userEvent.setup();
    render(wrap(<PosUnsettled settlementDays={3} />));

    expect(await screen.findByText('NHRC260000012')).toBeInTheDocument();
    expect(screen.getByText('Pharmacy invoice PH/0042')).toBeInTheDocument();
    expect(screen.getByText('Canteen bill CB-17')).toBeInTheDocument();
    // Only the cancelled bill says it was cancelled, and a payment typed without its reference says so.
    expect(screen.getAllByText(/Cancelled since: it settles unless it was voided on the terminal/)).toHaveLength(1);
    expect(screen.getByText('No reference recorded')).toBeInTheDocument();

    await user.click(screen.getByText('Pharmacy invoice PH/0042'));
    expect(navigate).not.toHaveBeenCalled();
    await user.click(screen.getByText('NHRC260000012'));
    expect(navigate).toHaveBeenCalledWith({ to: '/billing/bills/$billId', params: { billId: 'b1' } });
  });
});

describe('reports imported', () => {
  it('withdraws one imported in error only through a second person, saying why', async () => {
    listImports.mockResolvedValue(
      reports([
        imported.import,
        { ...imported.import, id: 'i2', fileName: 'mis-06-10.csv', withdrawalAskedAt: '2026-10-08T06:00:00Z' },
        {
          ...imported.import,
          id: 'i3',
          fileName: 'mis-05-10.csv',
          withdrawnOn: '2026-10-08',
          withdrawnByName: 'Finance Head',
          withdrawReason: 'The report of another branch',
        },
      ]),
    );
    vi.mocked(posSettlementApi.withdraw).mockResolvedValue({} as never);
    const user = userEvent.setup();
    render(wrap(<PosImports page={1} onPage={vi.fn()} />));

    // One waiting for a decision, and one withdrawn, offer nothing more; the withdrawn one says who approved it and why.
    expect(await screen.findByText('Withdrawal waiting for approval')).toBeInTheDocument();
    expect(screen.getByText(/^Withdrawn on /)).toBeInTheDocument();
    expect(screen.getByText(/Approved by Finance Head: The report of another branch/)).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Withdraw…' })).toHaveLength(1);

    await user.click(screen.getByRole('button', { name: 'Withdraw…' }));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(/its 3 transaction\(s\) stop matching receipts and refunds/)).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox'), 'The report of another branch');
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    await waitFor(() => expect(posSettlementApi.withdraw).toHaveBeenCalledWith('i1', 'The report of another branch'));
    expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.stringContaining('Someone else'));
  });
});
