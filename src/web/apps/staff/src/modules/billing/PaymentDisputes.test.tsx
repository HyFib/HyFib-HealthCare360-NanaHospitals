import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type { PagedResult } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as DisputesApi from './disputesApi';
import { deadlineText, disputeApi, gatewayText, nextStep, type PaymentDisputeDto } from './disputesApi';
import { PaymentDisputes } from './PaymentDisputes';

// The server, the router and the toasts are outside the test; the screen's own behaviour is what is tested.
const listCases = vi.hoisted(() => vi.fn());
const paymentLines = vi.hoisted(() => vi.fn());
vi.mock('./disputesApi', async (importOriginal) => {
  const actual = await importOriginal<typeof DisputesApi>();
  return {
    ...actual,
    disputeApi: { open: vi.fn(), describe: vi.fn(), evidence: vi.fn(), won: vi.fn(), acceptLoss: vi.fn() },
    disputeQueries: {
      ...actual.disputeQueries,
      list: (filter: DisputesApi.DisputeFilter) => ({
        queryKey: [...actual.disputeKeys.list(), filter],
        queryFn: () => listCases(filter),
      }),
      paymentLines: (receiptNumber: string) => ({
        queryKey: [...actual.disputeKeys.all, 'payment-lines', receiptNumber],
        queryFn: () => paymentLines(receiptNumber),
        retry: false,
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

beforeEach(() => {
  vi.clearAllMocks();
  // A receipt paid in one card payment unless a test says otherwise: nothing to choose.
  paymentLines.mockResolvedValue([{ id: 'p0', mode: 'Card', amount: 1500, reference: null, hasOpenCase: false }]);
});

function wrap(children: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </FluentProvider>
  );
}

function dispute(patch: Partial<PaymentDisputeDto>): PaymentDisputeDto {
  return {
    id: 'd1',
    number: 'NHCB2600001',
    source: 'Terminal',
    status: 'Open',
    receiptId: 'r1',
    receiptNumber: 'NHRC260000042',
    receiptPaymentId: 'p1',
    billId: 'b1',
    mode: 'Card',
    paymentReference: '627910654321',
    amount: 1500,
    reasonCode: null,
    reason: null,
    respondBy: null,
    daysLeft: null,
    openedAt: '2026-11-02T05:00:00Z',
    openedByName: null,
    evidenceSentOn: null,
    evidenceNote: null,
    evidenceByName: null,
    lossAskedAt: null,
    lossAskedByName: null,
    lossWaiting: false,
    closedAt: null,
    closedByName: null,
    outcome: null,
    reversalNumber: null,
    gatewayDisputeId: null,
    gatewayStatus: null,
    gatewayPhase: null,
    decidedAgainst: false,
    version: 7,
    ...patch,
  };
}

const page = (items: PaymentDisputeDto[]): PagedResult<PaymentDisputeDto> => ({
  items,
  page: 1,
  pageSize: 25,
  totalCount: items.length,
});

describe('chargeback cases', () => {
  it("lists open cases of the channel with their deadline and what is next, and asks for the bank's notice first", async () => {
    listCases.mockResolvedValue(
      page([
        dispute({}),
        dispute({
          id: 'd2',
          number: 'NHCB2600002',
          reasonCode: '10.4',
          respondBy: '2026-11-01',
          daysLeft: -2,
        }),
      ]),
    );
    render(wrap(<PaymentDisputes terminal />));

    expect(await screen.findByText('NHCB2600001')).toBeInTheDocument();
    expect(listCases).toHaveBeenCalledWith({ open: true, terminal: true, page: 1 });
    expect(screen.getByText("Record the bank's notice and its deadline")).toBeInTheDocument();
    expect(screen.getByText(/Late by 2 days/)).toBeInTheDocument();
    expect(screen.getByText('627910654321 · Reason 10.4')).toBeInTheDocument();
  });

  it("records the bank's notice, keeping the words recorded when no new ones are typed", async () => {
    listCases.mockResolvedValue(page([dispute({ reason: 'Card absent fraud', reasonCode: '10.4' })]));
    vi.mocked(disputeApi.describe).mockResolvedValue(dispute({}));
    const user = userEvent.setup();
    render(wrap(<PaymentDisputes terminal />));

    await user.click(await screen.findByRole('button', { name: 'Actions for chargeback NHCB2600001' }));
    await user.click(await screen.findByRole('menuitem', { name: "Record the bank's notice…" }));
    expect(screen.getByText('Recorded: “Card absent fraud”. Leave the note empty to keep it.')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Answer by'), '2026-11-12');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(disputeApi.describe).toHaveBeenCalledWith('d1', {
        reasonCode: '10.4',
        reason: 'Card absent fraud',
        respondBy: '2026-11-12',
        version: 7,
      }),
    );
    expect(notify.success).toHaveBeenCalledWith('Chargeback NHCB2600001', "The bank's notice is recorded.");
  });

  it('asks for the loss to be accepted, which someone else approves', async () => {
    listCases.mockResolvedValue(page([dispute({})]));
    vi.mocked(disputeApi.acceptLoss).mockResolvedValue({} as never);
    const user = userEvent.setup();
    render(wrap(<PaymentDisputes terminal />));

    await user.click(await screen.findByRole('button', { name: 'Actions for chargeback NHCB2600001' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Accept the loss…' }));
    await user.type(screen.getByRole('textbox', { name: /Why the loss is accepted/ }), 'The bank upheld the claim.');
    await user.click(screen.getByRole('button', { name: 'Ask for approval' }));

    await waitFor(() => expect(disputeApi.acceptLoss).toHaveBeenCalledWith('d1', 'The bank upheld the claim.', 7));
    expect(notify.success).toHaveBeenCalledWith(
      'Chargeback NHCB2600001',
      'Someone else who approves reversals decides; the receipt is reversed when they approve.',
    );
  });

  it("shows the gateway's dispute, and keeps its deadline when the notice is recorded", async () => {
    listCases.mockResolvedValue(
      page([
        dispute({
          source: 'Gateway',
          mode: 'PaymentGateway',
          paymentReference: 'pay_EsyWjHrfzb59eR',
          reasonCode: 'pre_arbitration',
          respondBy: '2026-11-09',
          daysLeft: 6,
          gatewayDisputeId: 'disp_AHfqOvkldwsbqt',
          gatewayStatus: 'under_review',
          gatewayPhase: 'pre_arbitration',
        }),
      ]),
    );
    vi.mocked(disputeApi.describe).mockResolvedValue(dispute({}));
    const user = userEvent.setup();
    render(wrap(<PaymentDisputes terminal={false} />));

    expect(
      await screen.findByText('Dispute disp_AHfqOvkldwsbqt · pre arbitration · gateway: under review'),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Actions for chargeback NHCB2600001' }));
    await user.click(await screen.findByRole('menuitem', { name: "Record the bank's notice…" }));
    expect(screen.getByLabelText('Answer by')).toBeDisabled();
    expect(screen.getByText('Set by the gateway for this dispute')).toBeInTheDocument();
    await user.type(
      screen.getByRole('textbox', { name: /What the notice says/ }),
      'Patient says the stay was cut short',
    );
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(disputeApi.describe).toHaveBeenCalledWith('d1', {
        reasonCode: 'pre_arbitration',
        reason: 'Patient says the stay was cut short',
        respondBy: '2026-11-09',
        version: 7,
      }),
    );
  });

  it('asks to accept the loss once the gateway decided against the hospital', async () => {
    listCases.mockResolvedValue(
      page([
        dispute({
          source: 'Gateway',
          respondBy: '2026-11-01',
          daysLeft: null,
          gatewayDisputeId: 'disp_AHfqOvkldwsbqt',
          gatewayStatus: 'lost',
          decidedAgainst: true,
        }),
      ]),
    );
    render(wrap(<PaymentDisputes terminal={false} />));

    expect(await screen.findByText('The bank decided against the hospital: accept the loss')).toBeInTheDocument();
    expect(screen.queryByText(/Answer was due/)).not.toBeInTheDocument();
  });

  it('offers neither a win nor the loss again while accepting the loss waits for a second person', async () => {
    listCases.mockResolvedValue(page([dispute({ lossWaiting: true, lossAskedByName: 'Asha' })]));
    const user = userEvent.setup();
    render(wrap(<PaymentDisputes terminal />));

    expect(await screen.findByText('Accepting the loss waits for a second person')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Actions for chargeback NHCB2600001' }));
    expect(await screen.findByRole('menuitem', { name: 'Evidence sent…' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Won…' })).not.toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Accept the loss…' })).not.toBeInTheDocument();
  });

  it("opens a case from the bank's notice", async () => {
    listCases.mockResolvedValue(page([]));
    vi.mocked(disputeApi.open).mockResolvedValue(dispute({ number: 'NHCB2600003', receiptNumber: 'NHRC260000050' }));
    const user = userEvent.setup();
    render(wrap(<PaymentDisputes terminal={false} />));

    expect(await screen.findByText('No open chargebacks')).toBeInTheDocument();
    expect(listCases).toHaveBeenCalledWith({ open: true, terminal: false, page: 1 });
    await user.click(screen.getByRole('button', { name: 'Open a case…' }));
    await user.type(screen.getByRole('textbox', { name: /Receipt number/ }), 'NHRC260000050');
    await user.type(screen.getByRole('spinbutton', { name: /Amount taken back/ }), '750');
    await user.type(screen.getByRole('textbox', { name: /Reason code/ }), 'fraudulent');
    await user.click(screen.getByRole('button', { name: 'Open the case' }));

    await waitFor(() =>
      expect(disputeApi.open).toHaveBeenCalledWith({
        receiptNumber: 'NHRC260000050',
        paymentId: null,
        amount: 750,
        reasonCode: 'fraudulent',
        reason: null,
        respondBy: null,
      }),
    );
    expect(notify.success).toHaveBeenCalledWith('Chargeback NHCB2600003 opened', 'Receipt NHRC260000050.');
  });

  it('asks which payment the notice is about when the receipt has more than one', async () => {
    listCases.mockResolvedValue(page([]));
    paymentLines.mockResolvedValue([
      { id: 'p1', mode: 'Card', amount: 1000, reference: '627910111111', hasOpenCase: false },
      { id: 'p2', mode: 'Upi', amount: 500, reference: '627910222222', hasOpenCase: false },
      { id: 'p3', mode: 'Card', amount: 250, reference: null, hasOpenCase: true },
    ]);
    vi.mocked(disputeApi.open).mockResolvedValue(dispute({ number: 'NHCB2600004', receiptNumber: 'NHRC260000051' }));
    const user = userEvent.setup();
    render(wrap(<PaymentDisputes terminal />));

    await user.click(await screen.findByRole('button', { name: 'Open a case…' }));
    await user.type(screen.getByRole('textbox', { name: /Receipt number/ }), 'NHRC260000051');
    await user.type(screen.getByRole('spinbutton', { name: /Amount taken back/ }), '500');
    const choice = await screen.findByRole('combobox', { name: /Payment taken back/ });
    expect(paymentLines).toHaveBeenCalledWith('NHRC260000051');
    expect(screen.getByRole('option', { name: 'Card · ₹250.00 · a case is open' })).toBeDisabled();

    // Not opened until the payment is chosen.
    await user.click(screen.getByRole('button', { name: 'Open the case' }));
    expect(disputeApi.open).not.toHaveBeenCalled();
    await user.selectOptions(choice, 'p2');
    await user.click(screen.getByRole('button', { name: 'Open the case' }));

    await waitFor(() =>
      expect(disputeApi.open).toHaveBeenCalledWith(expect.objectContaining({ paymentId: 'p2', amount: 500 })),
    );
  });

  it('says when the receipt is not there', async () => {
    listCases.mockResolvedValue(page([]));
    paymentLines.mockRejectedValue(new Error('Not found'));
    const user = userEvent.setup();
    render(wrap(<PaymentDisputes terminal />));

    await user.click(await screen.findByRole('button', { name: 'Open a case…' }));
    await user.type(screen.getByRole('textbox', { name: /Receipt number/ }), 'NHRC269999999');
    await user.tab();

    expect(await screen.findByText('No receipt NHRC269999999 in this facility.')).toBeInTheDocument();
  });

  it('shows closed cases with how they ended, and only the bill to open', async () => {
    listCases.mockResolvedValue(
      page([
        dispute({
          status: 'Lost',
          outcome: 'Receipt NHRC260000042 reversed (NHRR26000007): the bill owes the money again.',
          closedAt: '2026-11-03T06:00:00Z',
          reversalNumber: 'NHRR26000007',
        }),
      ]),
    );
    const user = userEvent.setup();
    render(wrap(<PaymentDisputes terminal />));

    await screen.findByText('NHCB2600001');
    await user.click(screen.getByRole('tab', { name: 'Closed' }));
    await waitFor(() => expect(listCases).toHaveBeenCalledWith({ open: false, terminal: true, page: 1 }));
    expect(await screen.findByText(/the bill owes the money again/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Actions for chargeback NHCB2600001' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Open the bill' }));
    expect(screen.queryByRole('menuitem', { name: 'Evidence sent…' })).not.toBeInTheDocument();
    expect(navigate).toHaveBeenCalledWith({ to: '/billing/bills/$billId', params: { billId: 'b1' } });
  });
});

describe('deadline and next step in words', () => {
  const format = (iso: string) => iso;

  it('counts the days left, and late days', () => {
    expect(deadlineText({ respondBy: null, daysLeft: null }, format)).toBeNull();
    expect(deadlineText({ respondBy: '2026-11-05', daysLeft: 2 }, format)).toBe('Answer by 2026-11-05 (2 days left)');
    expect(deadlineText({ respondBy: '2026-11-04', daysLeft: 1 }, format)).toBe('Answer by 2026-11-04 (1 day left)');
    expect(deadlineText({ respondBy: '2026-11-03', daysLeft: 0 }, format)).toBe('Answer due today');
    expect(deadlineText({ respondBy: '2026-11-02', daysLeft: -1 }, format)).toBe('Late by 1 day (due 2026-11-02)');
    expect(deadlineText({ respondBy: '2026-11-02', daysLeft: null }, format)).toBe('Answer was due by 2026-11-02');
  });

  it('says what is next', () => {
    expect(nextStep({ status: 'Open', lossWaiting: false, respondBy: null })).toBe(
      "Record the bank's notice and its deadline",
    );
    expect(nextStep({ status: 'Open', lossWaiting: false, respondBy: '2026-11-05' })).toBe(
      'Send the evidence, or accept the loss',
    );
    expect(nextStep({ status: 'Contested', lossWaiting: false, respondBy: '2026-11-05' })).toBe(
      'Waiting for the bank to decide',
    );
    expect(nextStep({ status: 'Open', lossWaiting: true, respondBy: null })).toBe(
      'Accepting the loss waits for a second person',
    );
    expect(nextStep({ status: 'Won', lossWaiting: false, respondBy: null })).toBe('Closed: the money came back');
    expect(nextStep({ status: 'Lost', lossWaiting: false, respondBy: null })).toBe('Closed: the receipt was reversed');
    expect(nextStep({ status: 'Contested', lossWaiting: false, respondBy: '2026-11-05', decidedAgainst: true })).toBe(
      'The bank decided against the hospital: accept the loss',
    );
    expect(nextStep({ status: 'Open', lossWaiting: true, respondBy: null, decidedAgainst: true })).toBe(
      'Accepting the loss waits for a second person',
    );
  });

  it("names the gateway's dispute", () => {
    expect(gatewayText({ gatewayDisputeId: null, gatewayPhase: null, gatewayStatus: null })).toBeNull();
    expect(gatewayText({ gatewayDisputeId: 'disp_AHfqOvkldwsbqt', gatewayPhase: null, gatewayStatus: 'open' })).toBe(
      'Dispute disp_AHfqOvkldwsbqt · gateway: open',
    );
    expect(deadlineText({ respondBy: '2026-11-02', daysLeft: null, decidedAgainst: true }, (iso) => iso)).toBeNull();
  });
});
