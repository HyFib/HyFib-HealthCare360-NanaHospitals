import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type { PagedResult } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { GatewayExceptions } from './GatewayExceptions';
import type * as GatewayApi from './gatewayApi';
import { gatewayApi, type GatewayDiscrepancyDto } from './gatewayApi';

// The server, the router and the toasts are outside the test; the list's own behaviour is what is tested.
const listExceptions = vi.hoisted(() => vi.fn());
vi.mock('./gatewayApi', async (importOriginal) => {
  const actual = await importOriginal<typeof GatewayApi>();
  return {
    ...actual,
    gatewayApi: { fetch: vi.fn(), recordReceipt: vi.fn(), retry: vi.fn(), resolve: vi.fn() },
    gatewayQueries: {
      ...actual.gatewayQueries,
      exceptions: (filters: GatewayApi.ExceptionFilters) => ({
        queryKey: [...actual.gatewayKeys.exceptions(), filters],
        queryFn: () => listExceptions(filters),
      }),
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

function exception(patch: Partial<GatewayDiscrepancyDto>): GatewayDiscrepancyDto {
  return {
    id: 'e1',
    kind: 'UnrecordedPayment',
    rank: 1,
    status: 'Open',
    key: 'pay_A1B2C3D4E5',
    orderId: 'plink_A1B2C3D4E5',
    summary:
      '₹1,500.00 was paid online (pay_A1B2C3D4E5), but the receipt could not be recorded: This in-patient account is closed.',
    recordedAmount: null,
    gatewayAmount: 1500,
    gatewayAt: '2026-10-01T05:00:00Z',
    billId: 'b1',
    billNumber: 'NHOB260000042',
    ipAccountId: null,
    receiptId: null,
    receiptNumber: null,
    refundId: null,
    refundNumber: null,
    appointmentId: null,
    appointmentNumber: null,
    gatewayOrderRecordId: 'o1',
    canRecordReceipt: true,
    detectedAt: '2026-10-01T05:10:00Z',
    lastSeenAt: '2026-10-01T05:10:00Z',
    resolvedAt: null,
    resolvedByName: null,
    resolution: null,
    resolvedAutomatically: false,
    version: 7,
    ...patch,
  };
}

const page = (items: GatewayDiscrepancyDto[]): PagedResult<GatewayDiscrepancyDto> => ({
  items,
  page: 1,
  pageSize: 25,
  totalCount: items.length,
});

const failedRefund = exception({
  id: 'e2',
  kind: 'RefundMismatch',
  rank: 4,
  key: 'rfnd_K1L2M3N4O5',
  summary:
    'Refund NHRF26000003 of ₹200.00 failed at the gateway (rfnd_K1L2M3N4O5): the patient has not got the money back.',
  gatewayAmount: 200,
  recordedAmount: 200,
  billId: null,
  billNumber: null,
  refundId: 'r1',
  refundNumber: 'NHRF26000003',
  gatewayOrderRecordId: null,
  canRecordReceipt: false,
});

function renderList(onFilters = vi.fn()) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>
        <GatewayExceptions filters={{}} onFilters={onFilters} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return { onFilters };
}

async function openActions(key: string) {
  await userEvent.click(await screen.findByRole('button', { name: `Actions for ${key}` }));
}

describe('GatewayExceptions', () => {
  beforeEach(() => {
    listExceptions.mockReset().mockResolvedValue(page([exception({}), failedRefund]));
    Object.values(gatewayApi).forEach((fn) => vi.mocked(fn).mockReset());
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  it('lists what happened with the documents by number, worst first as the server sends them', async () => {
    renderList();

    await screen.findByText('Bill NHOB260000042');
    const rows = screen.getAllByRole('row');
    expect(rows[1]).toHaveTextContent('Paid, no receipt');
    expect(rows[1]).toHaveTextContent('Bill NHOB260000042');
    expect(rows[2]).toHaveTextContent('Refund differs');
    expect(rows[2]).toHaveTextContent('Refund NHRF26000003');
    expect(listExceptions).toHaveBeenCalledWith(expect.objectContaining({ status: 'Open' }));
  });

  it('records the receipt for money the gateway took and says what was recorded', async () => {
    vi.mocked(gatewayApi.recordReceipt).mockResolvedValue({
      message: 'Receipt NHRC260000099 recorded.',
      discrepancy: exception({ status: 'Resolved' }),
    });
    renderList();

    await openActions('pay_A1B2C3D4E5');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Record the receipt' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Receipt recorded', 'Receipt NHRC260000099 recorded.'),
    );
    expect(gatewayApi.recordReceipt).toHaveBeenCalledWith('e1');
  });

  it('offers to send a failed refund again, and no receipt to record for it', async () => {
    vi.mocked(gatewayApi.retry).mockResolvedValue({
      message: 'Refund NHRF26000003 sent again as rfnd_Again00001.',
      discrepancy: exception({ ...failedRefund, status: 'Resolved' }),
    });
    renderList();

    await openActions('rfnd_K1L2M3N4O5');
    const menu = await screen.findByRole('menu');
    expect(within(menu).queryByRole('menuitem', { name: 'Record the receipt' })).not.toBeInTheDocument();
    await userEvent.click(within(menu).getByRole('menuitem', { name: 'Send the refund again' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Sorted', 'Refund NHRF26000003 sent again as rfnd_Again00001.'),
    );
    expect(gatewayApi.retry).toHaveBeenCalledWith('e2');
  });

  it('resolves an exception only with a note of what was done, with the version it showed', async () => {
    vi.mocked(gatewayApi.resolve).mockResolvedValue(exception({ status: 'Resolved' }));
    renderList();

    await openActions('pay_A1B2C3D4E5');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Mark resolved…' }));
    const dialog = await screen.findByRole('alertdialog', {}, { timeout: 5000 });
    const confirm = within(dialog).getByRole('button', { name: 'Mark resolved' });
    expect(confirm).toBeDisabled();
    await userEvent.type(within(dialog).getByRole('textbox'), 'Refunded by NEFT, UTR 1234');
    await userEvent.click(confirm);

    await waitFor(() => expect(gatewayApi.resolve).toHaveBeenCalledWith('e1', 'Refunded by NEFT, UTR 1234', 7));
    expect(notify.success).toHaveBeenCalledWith('Exception resolved', 'Your note is kept with it.');
  });

  it('tells the accountant when there is nothing to look at', async () => {
    listExceptions.mockResolvedValue(page([]));
    renderList();

    expect(await screen.findByText('Nothing to look at')).toBeInTheDocument();
  });
});
