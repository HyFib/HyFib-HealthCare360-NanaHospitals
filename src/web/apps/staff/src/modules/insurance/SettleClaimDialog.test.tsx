import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ApprovalRequestDto } from '@/modules/approvals/api';
import type * as InsuranceApi from './api';
import { insuranceApi, type ClaimDetail } from './api';
import { SettleClaimDialog } from './ClaimDialogs';

// The server and the toasts are outside the test; the dialog's own behaviour is what is tested.
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof InsuranceApi>();
  return { ...actual, insuranceApi: { ...actual.insuranceApi, settleClaim: vi.fn() } };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

function claim(patch: Partial<ClaimDetail> = {}): ClaimDetail {
  return {
    id: 'claim-1',
    claimNumber: 'NHCL26000042',
    billId: 'bill-1',
    billNumber: 'NHB26000101',
    patientId: 'patient-1',
    uhid: 'NH0000123',
    patientName: 'Ravi Kumar',
    payerId: 'payer-star',
    payerName: 'Star Health',
    tpaId: null,
    policyId: null,
    preAuthorizationId: null,
    claimedAmount: 10_000,
    diagnosisCodes: null,
    procedureCodes: null,
    documentFileIds: [],
    status: 'Approved',
    submittedAt: '2026-11-01T09:00:00Z',
    payerClaimReference: null,
    queryText: null,
    queryResponse: null,
    approvedAmount: 10_000,
    settledAmount: 0,
    deductionAmount: 0,
    outstanding: 10_000,
    utrNumber: null,
    rejectionReason: null,
    settlements: [],
    deductions: [],
    verifiedCoding: null,
    version: 7,
    writeOffAskedAt: null,
    writeOffApprovalAbove: 0,
    ...patch,
  };
}

const asked: ApprovalRequestDto = {
  id: 'approval-1',
  kind: 'insurance.claim_writeoff',
  title: 'Insurance write-off',
  status: 'Pending',
  summary: 'Write off ₹1,500.00 the insurer deducted from claim NHCL26000042',
  version: 1,
} as ApprovalRequestDto;

function renderDialog(detail: ClaimDetail) {
  const onClose = vi.fn();
  const view = render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <FluentProvider theme={webLightTheme}>
        <SettleClaimDialog claim={detail} onClose={onClose} />
      </FluentProvider>
    </QueryClientProvider>,
  );
  return { ...view, onClose };
}

async function settle(received: string, deducted: string) {
  const user = userEvent.setup();
  const amount = screen.getByRole('textbox', { name: /Amount received/ });
  await user.clear(amount);
  await user.type(amount, received);
  await user.type(screen.getByRole('textbox', { name: /Bank UTR/ }), 'UTR000123456789');
  await user.click(screen.getByRole('button', { name: 'Add a deduction' }));
  await user.type(screen.getByRole('textbox', { name: 'Reason' }), 'Non-medical items');
  await user.type(screen.getByRole('textbox', { name: /^Amount$/ }), deducted);
  return user;
}

describe('SettleClaimDialog', () => {
  beforeEach(() => {
    vi.mocked(insuranceApi.settleClaim).mockReset();
    notify.success.mockReset();
  });

  it('says the deductions wait for a second person, sends the claim as seen and reports that it asked', async () => {
    vi.mocked(insuranceApi.settleClaim).mockResolvedValue(asked);
    const { container, onClose } = renderDialog(claim());

    const user = await settle('8500', '1500');

    expect(screen.getByText('The deductions wait for approval')).toBeInTheDocument();
    expect(
      screen.getByText(/Every insurance write-off waits for someone who approves insurance write-offs/),
    ).toBeInTheDocument();
    await expectNoAxeViolations(container);

    await user.click(screen.getByRole('button', { name: 'Record settlement' }));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(insuranceApi.settleClaim).toHaveBeenCalledWith(
      'claim-1',
      expect.objectContaining({
        amount: 8500,
        deductions: [{ reason: 'Non-medical items', amount: 1500 }],
        version: 7,
      }),
      expect.any(String),
    );
    expect(notify.success).toHaveBeenCalledWith(
      'Asked for approval',
      expect.stringMatching(/₹8,500\.00 received is recorded\. The ₹1,500\.00 deducted is written off only when/),
    );
  });

  it('records deductions within what the desk writes off alone at once', async () => {
    vi.mocked(insuranceApi.settleClaim).mockResolvedValue(undefined);
    const { onClose } = renderDialog(claim({ writeOffApprovalAbove: 2_000 }));

    const user = await settle('8500', '1500');

    expect(screen.queryByText('The deductions wait for approval')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Record settlement' }));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(notify.success).toHaveBeenCalledWith('Settlement recorded', 'The claim is fully settled.');
  });

  it('names the amount above which a write-off waits', async () => {
    renderDialog(claim({ writeOffApprovalAbove: 1_000 }));

    await settle('8500', '1500');

    expect(
      screen.getByText(/More than ₹1,000\.00 written off waits for someone who approves insurance write-offs/),
    ).toBeInTheDocument();
  });
});
