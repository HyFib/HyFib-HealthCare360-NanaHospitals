import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as IpApi from './ipApi';
import { ipApi, type ClearanceDto, type IpAccountDetail, type SettlementResult } from './ipApi';
import { IpDischargePanel } from './IpPanels';

// The server, the router and the toasts are outside the test; the panel's own behaviour is what is tested.
const readClearance = vi.hoisted(() => vi.fn());
vi.mock('./ipApi', async (importOriginal) => {
  const actual = await importOriginal<typeof IpApi>();
  return {
    ...actual,
    ipQueries: {
      ...actual.ipQueries,
      clearance: (id: string) => ({ queryKey: ['billing', 'ip', id, 'clearance'], queryFn: () => readClearance() }),
    },
    ipApi: { ...actual.ipApi, settle: vi.fn() },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
vi.mock('@/lib/permissions', () => ({ usePermission: (permission: string) => permission === 'billing.ip.manage' }));
vi.mock('@tanstack/react-router', () => ({ useNavigate: () => vi.fn() }));

function account(patch: Partial<IpAccountDetail> = {}): IpAccountDetail {
  return { id: 'account-1', status: 'DischargeInitiated', depositBalance: 10_000, ...patch } as IpAccountDetail;
}

/** A self-paying stay whose interim bills took every service, one of them still to be paid. */
function billedOnInterimBills(patch: Partial<ClearanceDto> = {}): ClearanceDto {
  return {
    cleared: false,
    outstanding: 4_200,
    reason: '₹4,200.00 is due from the patient. An advance of ₹10,000.00 can be adjusted.',
    unbilledCount: 0,
    unbilledAmount: 0,
    hasFinalBill: false,
    patientDue: 4_200,
    depositBalance: 10_000,
    dischargeWithDuesApproved: false,
    billedOnInterimBills: true,
    ...patch,
  };
}

function renderPanel(detail: IpAccountDetail) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrap = (children: ReactNode) => (
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </FluentProvider>
  );
  return render(wrap(<IpDischargePanel account={detail} />));
}

describe('IpDischargePanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('settles a stay whose interim bills took every service without a final bill, once its discharge has begun', async () => {
    readClearance.mockResolvedValue(billedOnInterimBills());
    const settled: SettlementResult = {
      finalBillId: null,
      advanceAdjusted: 4_200,
      remainingDue: 0,
      refundId: 'refund-1',
      refundRequested: 5_800,
    };
    vi.mocked(ipApi.settle).mockResolvedValue(settled);
    renderPanel(account());

    // The final bill is not needed, and there is none to prepare: it would hold nothing.
    expect(await screen.findByText('Not needed')).toBeInTheDocument();
    expect(screen.getByText('Every service is on an interim bill')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /prepare final bill/i })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /settle with the advance/i }));
    await userEvent.click(await screen.findByRole('button', { name: 'Settle' }));

    expect(ipApi.settle).toHaveBeenCalledWith('account-1', expect.any(String));
    // Once the dialog has closed: the refund of the unused advance, and no final bill to open.
    expect(await screen.findByText('Account settled')).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: /open the refund/i })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /open the final bill/i })).not.toBeInTheDocument();
  });

  it('waits for the discharge to begin before settling, as more services may come during the stay', async () => {
    readClearance.mockResolvedValue(billedOnInterimBills());
    renderPanel(account({ status: 'Open' }));

    expect(await screen.findByText('Not needed')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /settle with the advance/i })).toBeDisabled();
  });

  it('asks for the final bill while services wait for one', async () => {
    readClearance.mockResolvedValue(
      billedOnInterimBills({ unbilledCount: 2, unbilledAmount: 1_500, billedOnInterimBills: false, reason: null }),
    );
    renderPanel(account());

    expect(await screen.findByText('Not finalised')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /prepare final bill/i })).toBeEnabled();
    expect(screen.getByRole('button', { name: /settle with the advance/i })).toBeDisabled();
  });
});
