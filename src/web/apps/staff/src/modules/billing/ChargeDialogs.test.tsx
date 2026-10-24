import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { ApiError, todayIso } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type * as BillingApi from './api';
import { billingApi, quotePrice, searchServices, type ChargeDto, type PriceQuoteDto, type ServiceItemDto } from './api';
import { AddChargeButton, CancelChargeDialog, type ChargeTarget } from './ChargeDialogs';
import type * as IpApi from './ipApi';
import type { IpAccountDetail, RunningBill, RunningBillLine } from './ipApi';
import { IpRunningBill } from './IpRunningBill';

// The server, the look-ups and the toasts are outside the test; the dialogs' own behaviour is what is tested.
vi.mock('./api', async (importOriginal) => {
  const actual = await importOriginal<typeof BillingApi>();
  return {
    ...actual,
    searchServices: vi.fn(),
    quotePrice: vi.fn(),
    billingApi: { ...actual.billingApi, postCharge: vi.fn(), cancelCharge: vi.fn() },
  };
});
const readRunningBill = vi.hoisted(() => vi.fn());
vi.mock('./ipApi', async (importOriginal) => {
  const actual = await importOriginal<typeof IpApi>();
  return {
    ...actual,
    ipQueries: {
      ...actual.ipQueries,
      runningBill: (id: string) => ({
        queryKey: ['billing', 'ip', 'account', id, 'running-bill'],
        queryFn: readRunningBill,
      }),
    },
  };
});
vi.mock('@/lib/lookups', () => ({
  useDepartments: () => ({ data: [{ id: 'dept-ortho', name: 'Orthopaedics' }] }),
  usePractitioners: () => ({ data: [{ id: 'doc-rao', name: 'Dr. Anil Rao' }] }),
}));
vi.mock('@/lib/EChart', () => ({ EChart: () => null }));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
const permissions = vi.hoisted(() => ({ granted: ['billing.charges.manage'] as string[] }));
vi.mock('@/lib/permissions', () => ({
  usePermission: (permission: string | string[]) =>
    (Array.isArray(permission) ? permission : [permission]).some((p) => permissions.granted.includes(p)),
}));

const UNBILLED_KEY = ['billing', 'charges', 'unbilled', 'patient-1'];

function service(patch: Partial<ServiceItemDto> = {}): ServiceItemDto {
  return {
    id: 'svc-dressing',
    code: 'PROC-DRS',
    name: 'Wound dressing',
    shortName: null,
    category: 'Procedure',
    departmentId: 'dept-surgery',
    basePrice: 400,
    taxRate: 0,
    sacCode: null,
    isDiscountAllowed: true,
    isPriceEditable: false,
    description: null,
    isActive: true,
    version: 1,
    ...patch,
  };
}

function quote(item: ServiceItemDto, patch: Partial<PriceQuoteDto> = {}): PriceQuoteDto {
  return {
    serviceItemId: item.id,
    serviceCode: item.code,
    serviceName: item.name,
    category: item.category,
    unitPrice: 450,
    source: 'TariffPlan',
    tariffPlanId: 'plan-general',
    tariffPlanName: 'General',
    multiplier: 1,
    taxRate: 0,
    isPriceEditable: item.isPriceEditable,
    isDiscountAllowed: true,
    ...patch,
  };
}

const posted = { id: 'charge-new', description: 'Wound dressing', netAmount: 900 } as ChargeDto;

const visitTarget: ChargeTarget = {
  patientId: 'patient-1',
  visitId: 'visit-1',
  admissionId: null,
  label: 'Visit NHOV260000123',
};

function setUp(children: ReactNode) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  // What the billing screen shows: refreshed (invalidated) once a charge changes.
  client.setQueryData(UNBILLED_KEY, { items: [] });
  render(
    <FluentProvider theme={webLightTheme}>
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    </FluentProvider>,
  );
  return client;
}

async function pickService(user: ReturnType<typeof userEvent.setup>, item: ServiceItemDto) {
  vi.mocked(searchServices).mockResolvedValue([item]);
  vi.mocked(quotePrice).mockResolvedValue(quote(item));
  const dialog = await screen.findByRole('alertdialog', { name: 'Add a charge' });
  await user.type(within(dialog).getByRole('combobox', { name: /service/i }), 'dress');
  await user.click(await screen.findByRole('option', { name: /wound dressing/i }));
  return dialog;
}

beforeEach(() => {
  vi.clearAllMocks();
  permissions.granted = ['billing.charges.manage'];
});

describe('AddChargeButton', () => {
  it('is not offered without billing.charges.manage', () => {
    permissions.granted = ['billing.op.create'];
    setUp(<AddChargeButton target={visitTarget} />);

    expect(screen.queryByRole('button', { name: 'Add charge' })).not.toBeInTheDocument();
  });

  it('posts the service against the visit at the tariff price, then refreshes the unbilled services', async () => {
    const user = userEvent.setup();
    vi.mocked(billingApi.postCharge).mockResolvedValue(posted);
    const client = setUp(<AddChargeButton target={visitTarget} />);

    await user.click(screen.getByRole('button', { name: 'Add charge' }));
    const dialog = await pickService(user, service());
    expect(await within(dialog).findByText(/General\): ₹450.00 each/)).toBeInTheDocument();
    // A fixed-price service: the tariff decides, so there is no price to type.
    expect(within(dialog).queryByRole('textbox', { name: /price each/i })).not.toBeInTheDocument();

    const quantity = within(dialog).getByRole('textbox', { name: /quantity/i });
    await user.clear(quantity);
    await user.type(quantity, '2');
    await user.selectOptions(within(dialog).getByRole('combobox', { name: 'Doctor' }), 'doc-rao');
    await user.click(within(dialog).getByRole('button', { name: 'Add charge' }));

    await waitFor(() =>
      expect(billingApi.postCharge).toHaveBeenCalledWith({
        patientId: 'patient-1',
        visitId: 'visit-1',
        admissionId: null,
        serviceItemId: 'svc-dressing',
        quantity: 2,
        unitPrice: null,
        description: null,
        practitionerId: 'doc-rao',
        departmentId: null,
        serviceDate: null,
      }),
    );
    expect(notify.success).toHaveBeenCalledWith('Charge added', 'Wound dressing · ₹900.00');
    await waitFor(() => expect(client.getQueryState(UNBILLED_KEY)?.isInvalidated).toBe(true));
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
  });

  it('sends the price typed for an editable service, the department and an earlier service date', async () => {
    const user = userEvent.setup();
    vi.mocked(billingApi.postCharge).mockResolvedValue(posted);
    setUp(
      <AddChargeButton
        target={{ patientId: 'patient-1', visitId: null, admissionId: 'adm-1', label: 'Admission NHIP26000045' }}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Add charge' }));
    const dialog = await pickService(user, service({ isPriceEditable: true }));
    const price = within(dialog).getByRole('textbox', { name: /price each/i });
    expect(price).toHaveValue('400');
    await user.clear(price);
    await user.type(price, '650');
    await user.selectOptions(within(dialog).getByRole('combobox', { name: 'Department' }), 'dept-ortho');
    await user.type(within(dialog).getByRole('textbox', { name: /description on the bill/i }), 'Dressing, left knee');
    const yesterday = todayIso(-1);
    const date = within(dialog).getByLabelText('Service date');
    await user.clear(date);
    await user.type(date, yesterday);
    await user.click(within(dialog).getByRole('button', { name: 'Add charge' }));

    await waitFor(() => expect(billingApi.postCharge).toHaveBeenCalled());
    expect(vi.mocked(billingApi.postCharge).mock.calls[0]![0]).toEqual({
      patientId: 'patient-1',
      visitId: null,
      admissionId: 'adm-1',
      serviceItemId: 'svc-dressing',
      quantity: 1,
      unitPrice: 650,
      description: 'Dressing, left knee',
      practitionerId: null,
      departmentId: 'dept-ortho',
      serviceDate: `${yesterday}T12:00:00+05:30`,
    });
  });

  it('shows the server problem and keeps the dialog open', async () => {
    const user = userEvent.setup();
    const refusal = new ApiError({
      status: 422,
      title: 'Wound dressing is no longer offered.',
      code: 'billing.service_inactive',
    });
    vi.mocked(billingApi.postCharge).mockRejectedValue(refusal);
    const client = setUp(<AddChargeButton target={visitTarget} />);

    await user.click(screen.getByRole('button', { name: 'Add charge' }));
    const dialog = await pickService(user, service());
    await user.click(within(dialog).getByRole('button', { name: 'Add charge' }));

    await waitFor(() => expect(notify.error).toHaveBeenCalledWith(refusal, 'The charge was not added'));
    expect(notify.success).not.toHaveBeenCalled();
    expect(screen.getByRole('alertdialog', { name: 'Add a charge' })).toBeInTheDocument();
    expect(client.getQueryState(UNBILLED_KEY)?.isInvalidated).toBe(false);
  });
});

describe('CancelChargeDialog', () => {
  const charge = {
    id: 'charge-7',
    description: 'CBC',
    netAmount: 350,
    sourceModule: 'lab',
    onDraft: true,
  };

  it('cancels the charge with the reason given, after saying what it does, then refreshes', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    vi.mocked(billingApi.cancelCharge).mockResolvedValue(undefined);
    const client = setUp(<CancelChargeDialog charge={charge} onClose={onClose} />);

    const dialog = await screen.findByRole('alertdialog', { name: 'Cancel CBC?' });
    expect(dialog).toHaveTextContent('₹350.00 is no longer billed to the patient.');
    expect(dialog).toHaveTextContent('taken off the draft bill');
    expect(dialog).toHaveTextContent('If it came from an order or a dispense, that is not cancelled here');
    const confirm = within(dialog).getByRole('button', { name: 'Cancel charge' });
    expect(confirm).toBeDisabled();

    await user.type(within(dialog).getByRole('textbox'), 'Charged twice');
    await user.click(confirm);

    await waitFor(() => expect(billingApi.cancelCharge).toHaveBeenCalledWith('charge-7', 'Charged twice'));
    expect(notify.success).toHaveBeenCalledWith('Charge cancelled', 'CBC will not be billed.');
    expect(onClose).toHaveBeenCalled();
    await waitFor(() => expect(client.getQueryState(UNBILLED_KEY)?.isInvalidated).toBe(true));
  });

  it('shows the server problem when the charge is already billed', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    const refusal = new ApiError({
      status: 422,
      title: 'This service is on a finalized bill. Cancel the bill or issue a credit note instead.',
      code: 'billing.charge_billed',
    });
    vi.mocked(billingApi.cancelCharge).mockRejectedValue(refusal);
    setUp(
      <CancelChargeDialog charge={{ ...charge, sourceModule: 'billing.manual', onDraft: false }} onClose={onClose} />,
    );

    const dialog = await screen.findByRole('alertdialog', { name: 'Cancel CBC?' });
    expect(dialog).not.toHaveTextContent('order or a dispense');
    await user.type(within(dialog).getByRole('textbox'), 'Service not given');
    await user.click(within(dialog).getByRole('button', { name: 'Cancel charge' }));

    await waitFor(() => expect(notify.error).toHaveBeenCalledWith(refusal, 'The charge was not cancelled'));
    expect(onClose).not.toHaveBeenCalled();
  });
});

describe('IpRunningBill charges', () => {
  function line(id: string, patch: Partial<RunningBillLine> = {}): RunningBillLine {
    return {
      chargeId: id,
      serviceDate: '2026-10-20T10:00:00+05:30',
      serviceCode: 'LAB-CBC',
      description: `Service ${id}`,
      category: 'Laboratory',
      quantity: 1,
      unitPrice: 350,
      netAmount: 350,
      status: 'Unbilled',
      billId: null,
      practitionerName: null,
      isReturn: false,
      ...patch,
    };
  }

  const account = {
    id: 'account-1',
    admissionId: 'adm-1',
    admissionNumber: 'NHIP26000045',
    patientId: 'patient-1',
    status: 'Open',
  } as IpAccountDetail;

  beforeEach(() => {
    readRunningBill.mockResolvedValue({
      byCategory: [],
      byDate: [],
      lines: [line('a'), line('b', { status: 'Billed', billId: 'bill-1' })],
      netTotal: 700,
      returnsNet: 0,
      estimatedPatientPayable: 700,
      depositBalance: 0,
      paidOnBills: 0,
      estimatedBalance: 700,
      estimatedAmount: 0,
      insurance: null,
    } as unknown as RunningBill);
  });

  it('offers adding a charge and cancelling only the lines not yet billed', async () => {
    const user = userEvent.setup();
    setUp(<IpRunningBill account={account} />);

    expect(await screen.findByRole('button', { name: 'Add charge' })).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: 'All services' }));
    expect(await screen.findByRole('button', { name: 'Cancel Service a' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel Service b' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Cancel Service a' }));
    expect(await screen.findByRole('alertdialog', { name: 'Cancel Service a?' })).toBeInTheDocument();
  });

  it('offers neither without billing.charges.manage', async () => {
    permissions.granted = ['billing.ip.manage'];
    const user = userEvent.setup();
    setUp(<IpRunningBill account={account} />);

    await user.click(await screen.findByRole('tab', { name: 'All services' }));
    expect(await screen.findByText('Service a')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add charge' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Cancel / })).not.toBeInTheDocument();
  });

  it('takes no new charge on a closed account', async () => {
    setUp(<IpRunningBill account={{ ...account, status: 'Closed' }} />);

    expect(await screen.findByRole('tab', { name: 'All services' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add charge' })).not.toBeInTheDocument();
  });
});
