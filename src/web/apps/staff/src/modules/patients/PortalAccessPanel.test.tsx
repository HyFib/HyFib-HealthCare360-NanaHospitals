import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as PatientsApi from './api';
import { portalFamilyApi, type PatientPortalAccessDto, type PortalFamilyLinkDto } from './api';
import { PortalAccessPanel, ShareForm } from './PortalAccessPanel';

// The server, the toasts and the signed-in user are outside the test; the panel's own behaviour is what is tested.
const readAccess = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof PatientsApi>();
  return {
    ...actual,
    portalFamilyApi: { link: vi.fn(), revoke: vi.fn() },
    patientQueries: {
      ...actual.patientQueries,
      portalFamily: (id: string) => ({ queryKey: actual.patientKeys.portalFamily(id), queryFn: () => readAccess(id) }),
    },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
const permissions = vi.hoisted(() => ({ manage: true }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (permission: string) => permission !== 'patients.consents' || permissions.manage,
}));

function renderWithProviders(ui: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <FluentProvider theme={webLightTheme}>{ui}</FluentProvider>
    </QueryClientProvider>,
  );
}

function link(patch: Partial<PortalFamilyLinkDto>): PortalFamilyLinkDto {
  return {
    id: 'l1',
    accountPhone: '+919811122233',
    relation: 'Parent',
    source: 'FrontDesk',
    linkedAt: '2026-10-01T05:00:00Z',
    linkedByName: 'Asha Nair',
    consentGivenBy: 'the patient',
    codeSentTo: null,
    revokedAt: null,
    revokedByName: null,
    revokedReason: null,
    isActive: true,
    ...patch,
  };
}

function access(links: PortalFamilyLinkDto[]): PatientPortalAccessDto {
  return { ownPhone: '+919800000001', links };
}

describe('ShareForm', () => {
  beforeEach(() => {
    vi.mocked(portalFamilyApi.link).mockReset().mockResolvedValue({ id: 'new' });
    Object.values(notify).forEach((spy) => spy.mockClear());
  });

  function renderForm() {
    const onClose = vi.fn();
    renderWithProviders(<ShareForm patientId="p1" ownPhone="+919800000001" onClose={onClose} />);
    return { onClose, share: screen.getByRole('button', { name: 'Share' }) };
  }

  it('shares only once the identities are checked, then closes', async () => {
    const user = userEvent.setup();
    const { onClose, share } = renderForm();
    expect(share).toBeDisabled();

    await user.type(screen.getByRole('textbox', { name: /^Family member’s mobile number/ }), '98111 22233');
    await user.selectOptions(screen.getByRole('combobox', { name: /^The patient is their/ }), 'Parent');
    await user.click(screen.getByRole('radio', { name: 'A guardian, for the patient' }));
    await user.type(screen.getByRole('textbox', { name: /^Guardian’s name/ }), 'Ravi Kumar');
    expect(share).toBeDisabled();
    await user.click(screen.getByRole('checkbox', { name: /checked the identity documents/ }));
    await waitFor(() => expect(share).toBeEnabled());
    await user.click(share);

    await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
    expect(portalFamilyApi.link).toHaveBeenCalledWith('p1', {
      accountPhone: '+919811122233',
      relation: 'Parent',
      consentGivenBy: 'Ravi Kumar, guardian',
      identityChecked: true,
    });
    expect(notify.success).toHaveBeenCalledWith(
      'Records shared',
      '+91 98111 22233 sees the patient’s records the next time they open the patient portal.',
    );
  });

  it('shows the server’s refusal in the dialog', async () => {
    const user = userEvent.setup();
    const { onClose, share } = renderForm();
    vi.mocked(portalFamilyApi.link).mockRejectedValueOnce(
      new ApiError({
        status: 409,
        title: 'Conflict',
        code: 'portal.family_already_linked',
        detail: 'This mobile number can already see the record.',
      }),
    );
    await user.type(screen.getByRole('textbox', { name: /^Family member’s mobile number/ }), '9811122233');
    await user.selectOptions(screen.getByRole('combobox', { name: /^The patient is their/ }), 'Spouse');
    await user.click(screen.getByRole('checkbox', { name: /checked the identity documents/ }));
    await waitFor(() => expect(share).toBeEnabled());
    await user.click(share);

    expect(await screen.findByText('This mobile number can already see the record.')).toBeInTheDocument();
    expect(screen.getByText('The records were not shared')).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();
  });
});

describe('PortalAccessPanel', () => {
  beforeEach(() => {
    readAccess.mockReset();
    vi.mocked(portalFamilyApi.revoke).mockReset().mockResolvedValue(undefined);
    permissions.manage = true;
  });

  it('lists who sees the records, and offers to stop only a link in force', async () => {
    readAccess.mockResolvedValue(
      access([
        link({}),
        link({
          id: 'l2',
          accountPhone: '+919822233344',
          relation: 'Spouse',
          source: 'PortalCode',
          consentGivenBy: null,
          codeSentTo: '+919800000001',
          linkedByName: 'Ravi',
          revokedAt: '2026-10-03T05:00:00Z',
          revokedByName: 'Asha Nair',
          revokedReason: 'Patient asked at the front desk',
          isActive: false,
        }),
      ]),
    );
    renderWithProviders(<PortalAccessPanel patientId="p1" />);

    expect(
      await screen.findByText(
        'Anyone who signs in to the patient portal with the patient’s own number, +91 98000 00001, sees these records.',
      ),
    ).toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'Family members who see the records in the portal' });
    const [, active, ended] = within(table).getAllByRole('row');
    expect(active).toHaveTextContent('+91 98111 22233');
    expect(active).toHaveTextContent('The patient is their parent');
    expect(active).toHaveTextContent('At the front desk, agreed by the patient, recorded by Asha Nair');
    expect(within(active!).getByText('Sees the records')).toBeInTheDocument();
    expect(ended).toHaveTextContent('In the portal, with a code sent to +91 98000 00001');
    expect(ended).not.toHaveTextContent('recorded by Ravi');
    expect(ended).toHaveTextContent('Reason: Patient asked at the front desk');
    expect(within(ended!).getByText('Ended')).toBeInTheDocument();

    expect(screen.getAllByRole('button', { name: /^Stop sharing/ })).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'Share with a family member' })).toBeInTheDocument();
  });

  it('stops sharing with a reason', async () => {
    const user = userEvent.setup();
    readAccess.mockResolvedValue(access([link({})]));
    renderWithProviders(<PortalAccessPanel patientId="p1" />);

    await user.click(await screen.findByRole('button', { name: 'Stop sharing with +91 98111 22233' }));
    const dialog = await screen.findByRole('alertdialog');
    await user.type(within(dialog).getByRole('textbox'), 'Patient asked at the front desk');
    await user.click(within(dialog).getByRole('button', { name: 'Stop sharing' }));

    await waitFor(() => expect(portalFamilyApi.revoke).toHaveBeenCalledWith('l1', 'Patient asked at the front desk'));
    expect(notify.success).toHaveBeenCalledWith(
      'Sharing stopped',
      '+91 98111 22233 no longer sees the patient’s records in the portal.',
    );
  });

  it('only lists the links for staff who may not change them', async () => {
    permissions.manage = false;
    readAccess.mockResolvedValue(access([link({})]));
    renderWithProviders(<PortalAccessPanel patientId="p1" />);

    await screen.findByText('+91 98111 22233');
    expect(screen.queryByRole('button', { name: 'Share with a family member' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Stop sharing/ })).not.toBeInTheDocument();
  });
});
