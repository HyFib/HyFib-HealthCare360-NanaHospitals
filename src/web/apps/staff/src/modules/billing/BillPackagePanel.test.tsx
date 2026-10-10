import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as BillingApi from './api';
import { billingApi, type BillDetail, type BillPackageSummary } from './api';
import { BillPackagePanel } from './BillPackagePanel';
import type * as MastersApi from './mastersApi';
import { packagesForOpBill, type PackageListItem } from './mastersApi';

// The server and the toasts are outside the test; the panel's own behaviour is what is tested.
const listPackages = vi.hoisted(() => vi.fn());
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof BillingApi>();
  return { ...actual, billingApi: { ...actual.billingApi, setPackage: vi.fn() } };
});
vi.mock('./mastersApi', async (importOriginal) => {
  const actual = await importOriginal<typeof MastersApi>();
  return {
    ...actual,
    mastersQueries: {
      ...actual.mastersQueries,
      packages: () => ({ queryKey: ['billing', 'packages', 'list', false], queryFn: () => listPackages() }),
    },
  };
});
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
const permissions = vi.hoisted(() => ({ opCreate: true }));
vi.mock('@/lib/permissions', () => ({ usePermission: () => permissions.opCreate }));

const GENERAL = 'plan-general';

function pkg(id: string, patch: Partial<PackageListItem> = {}): PackageListItem {
  return {
    id,
    code: id.toUpperCase(),
    name: `Package ${id}`,
    price: 5000,
    lengthOfStayDays: null,
    tariffPlanId: null,
    usedFor: 'OutPatient',
    isActive: true,
    inclusionCount: 2,
    ...patch,
  };
}

const cataract: BillPackageSummary = {
  packageId: 'cataract',
  code: 'PKG-CATARACT',
  name: 'Cataract surgery (one eye)',
  price: 5000,
  actuals: 6600,
  covered: 5800,
  beyondPackage: 800,
};

function bill(patch: Partial<BillDetail> = {}): BillDetail {
  return {
    id: 'bill-1',
    billNumber: null,
    type: 'OutPatient',
    status: 'Draft',
    tariffPlanId: GENERAL,
    patientAmount: 6600,
    package: null,
    version: 7,
    ...patch,
  } as BillDetail;
}

function wrap(children: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </FluentProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  permissions.opCreate = true;
  listPackages.mockResolvedValue([
    pkg('stay', { usedFor: 'InPatient', name: 'Normal delivery' }),
    pkg('corporate', { tariffPlanId: 'plan-corporate', name: 'Cataract (corporate)' }),
    pkg('cataract', { name: 'Cataract surgery (one eye)' }),
    pkg('dressing', { usedFor: 'Both', tariffPlanId: GENERAL, name: 'Dressing course', price: 1200 }),
  ]);
});

describe('packagesForOpBill', () => {
  it('offers out-patient packages for the bill tariff or any tariff, but not the one already chosen', async () => {
    const all = (await listPackages()) as PackageListItem[];
    expect(packagesForOpBill(all, bill()).map((p) => p.id)).toEqual(['cataract', 'dressing']);
    expect(packagesForOpBill(all, bill({ package: cataract })).map((p) => p.id)).toEqual(['dressing']);
    expect(packagesForOpBill(all, bill({ tariffPlanId: 'plan-corporate' })).map((p) => p.id)).toEqual([
      'corporate',
      'cataract',
    ]);
  });
});

describe('BillPackagePanel', () => {
  it('bills a draft at the package chosen, after saying what that does', async () => {
    const user = userEvent.setup();
    const onChanged = vi.fn();
    const priced = bill({ package: cataract, patientAmount: 5800, version: 8 });
    vi.mocked(billingApi.setPackage).mockResolvedValue(priced);
    render(wrap(<BillPackagePanel bill={bill()} onChanged={onChanged} />));

    const picker = await screen.findByRole('combobox', { name: 'Bill at a package' });
    expect(
      within(picker)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual(['Bill at a package…', 'Cataract surgery (one eye) · ₹5,000.00', 'Dressing course · ₹1,200.00']);

    await user.selectOptions(picker, 'cataract');
    const dialog = await screen.findByRole('alertdialog', { name: 'Bill at Cataract surgery (one eye)?' });
    expect(dialog).toHaveTextContent('₹5,000.00 is billed for the package');
    await user.click(within(dialog).getByRole('button', { name: 'Apply package' }));

    await waitFor(() => expect(onChanged).toHaveBeenCalledWith(priced));
    expect(billingApi.setPackage).toHaveBeenCalledWith('bill-1', 'cataract', 7);
    expect(notify.success).toHaveBeenCalledWith('Package applied', "The patient's share is now ₹5,800.00.");
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
  });

  it('shows what the package covers, and takes it off after asking', async () => {
    const user = userEvent.setup();
    const onChanged = vi.fn();
    vi.mocked(billingApi.setPackage).mockResolvedValue(bill({ version: 9 }));
    render(wrap(<BillPackagePanel bill={bill({ package: cataract })} onChanged={onChanged} />));

    expect(screen.getByText('Package: Cataract surgery (one eye)')).toBeInTheDocument();
    expect(screen.getByText('Covered by the package').parentElement).toHaveTextContent('₹5,800.00');
    expect(screen.getByText('Billed beyond the package').parentElement).toHaveTextContent('₹800.00');
    expect(await screen.findByRole('combobox', { name: 'Change the package' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Remove package' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Remove the package?' });
    await user.click(within(dialog).getByRole('button', { name: 'Remove package' }));

    await waitFor(() => expect(billingApi.setPackage).toHaveBeenCalledWith('bill-1', null, 7));
    expect(notify.success).toHaveBeenCalledWith('Package removed', expect.any(String));
  });

  it('shows the package of a finalised bill without letting it change', async () => {
    render(wrap(<BillPackagePanel bill={bill({ status: 'Finalized', package: cataract })} onChanged={vi.fn()} />));

    expect(screen.getByText('Package: Cataract surgery (one eye)')).toBeInTheDocument();
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Remove package' })).not.toBeInTheDocument();
    expect(listPackages).not.toHaveBeenCalled();
  });

  it('shows nothing on a bill that has no package and cannot take one', () => {
    permissions.opCreate = false;
    const { container } = render(wrap(<BillPackagePanel bill={bill()} onChanged={vi.fn()} />));

    expect(container.textContent).toBe('');
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    expect(listPackages).not.toHaveBeenCalled();
  });
});
