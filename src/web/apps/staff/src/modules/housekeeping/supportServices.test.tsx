import type * as Core from '@hc360/core';
import { useSession, type AuthUser } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, json, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { AmbulanceTrip } from '@/modules/ambulance/api';
import { WaiveDialog } from '@/modules/ambulance/WaiveDialog';
import { RecordAccidentDialog } from '@/modules/bmw/AccidentsPanel';
import type { CanteenSaleDto } from '@/modules/canteen/api';
import { SaleDrawer } from '@/modules/canteen/SaleDrawer';
import { ChecklistDialog, VerifyDialog } from './HousekeepingPanels';

// Support services where a second person decides, on the screens against a fake server: a canteen bill's cancellation is
// asked for and the bill stands until approved; an ambulance trip's waiver is asked for, the charge standing; an accident
// is reported item by item for Form I; a cleaning is recorded against its checklist and verified by someone else, one
// sent back saying what was not clean. The toasts are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const SALE_ID = '0192a000-0000-7000-8000-00000000c501';
const TRIP_ID = '0192a000-0000-7000-8000-00000000c601';
const today = new Date(Date.now() + 5.5 * 3600_000).toISOString().slice(0, 10);

const CANTEEN: AuthUser = {
  ...STAFF_USER,
  roles: ['CanteenManager'],
  permissions: ['canteen.sales.create', 'canteen.sales.cancel'],
};

const sale = (over: Partial<CanteenSaleDto> = {}): CanteenSaleDto => ({
  id: SALE_ID,
  billNumber: 'NHCN260000123',
  soldAt: new Date().toISOString(),
  businessDate: today,
  customerType: 'Visitor',
  customerName: 'Ramesh',
  employeeCode: null,
  employeeName: null,
  taxableAmount: 114.29,
  cgstAmount: 2.86,
  sgstAmount: 2.86,
  grossAmount: 120,
  roundOff: 0,
  netAmount: 120,
  paymentMode: 'Card',
  paymentReference: 'RRN778899',
  status: 'Completed',
  cashierName: 'Meena',
  cancelledAt: null,
  cancelReason: null,
  cancelRequestedByName: null,
  cancelledByName: null,
  refundMode: null,
  refundAmount: null,
  version: 4,
  lines: [
    {
      menuItemId: 'm1',
      itemName: 'Lemon rice',
      hsnSac: null,
      quantity: 2,
      unitPrice: 60,
      gstRate: 5,
      taxableAmount: 114.29,
      cgstAmount: 2.86,
      sgstAmount: 2.86,
      lineTotal: 120,
    },
  ],
  ...over,
});

const trip = {
  id: TRIP_ID,
  number: 'NHAT26000045',
  purpose: 'ScheduledTransport',
  patientId: '0192a000-0000-7000-8000-00000000c602',
  uhid: 'NH26000099',
  patientName: 'Kamala',
  status: 'Completed',
  kilometres: 24,
  chargeWaivedReason: null,
  version: 7,
} as unknown as AmbulanceTrip;

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function show(node: ReactNode, user: AuthUser = STAFF_USER) {
  useSession.setState({
    status: 'authenticated',
    accessToken: 'test-token',
    expiresAt: Date.now() + 3600_000,
    user,
    locked: false,
  });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <Hc360ThemeProvider>{node}</Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

describe('support services where a second person decides', SCREEN_TESTS, () => {
  it('asks for a canteen bill to be cancelled, saying the money goes back the way it was paid once someone else approves', async () => {
    const server = fakeServer({
      [`GET /api/v1/canteen/sales/${SALE_ID}`]: sale(),
      [`POST /api/v1/canteen/sales/${SALE_ID}/cancel`]: json(
        {
          id: '0192a000-0000-7000-8000-00000000c5a1',
          kind: 'canteen.sale_cancel',
          summary: 'Cancel canteen bill NHCN260000123',
          canDecide: false,
        },
        202,
      ),
    });
    const user = userEvent.setup();
    show(<SaleDrawer saleId={SALE_ID} onClose={() => undefined} />, CANTEEN);

    await user.click(await screen.findByRole('button', { name: 'Cancel bill' }, SLOW));
    const dialog = await screen.findByRole('alertdialog', { name: /Cancel bill NHCN260000123/ });
    expect(
      within(dialog).getByText(/waits for someone who approves canteen cancellations, never the cashier/),
    ).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox'), 'Billed twice by mistake');
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Ask to cancel' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Cancellation asked for', expect.stringContaining('the bill stands')),
    );
    expect(server.requests.find((r) => r.method === 'POST')?.body).toEqual({ reason: 'Billed twice by mistake' });
  });

  it('shows a cancellation waiting, and offers no second one', async () => {
    fakeServer({
      [`GET /api/v1/canteen/sales/${SALE_ID}`]: sale({
        pendingCancellation: {
          approvalId: '0192a000-0000-7000-8000-00000000c5a1',
          summary: 'Cancel canteen bill NHCN260000123',
          requestedByName: 'Meena',
          requestedAt: new Date().toISOString(),
          askedByYou: false,
        },
        ownSale: true,
      }),
    });
    show(<SaleDrawer saleId={SALE_ID} onClose={() => undefined} />, CANTEEN);

    expect(await screen.findByText(/Asked for by Meena/, {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/you sold it, so you cannot approve it/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel bill' })).not.toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it("asks for an ambulance trip's charge to be waived, the charge standing until someone who approves waivers decides", async () => {
    const server = fakeServer({
      [`POST /api/v1/ambulance/trips/${TRIP_ID}/waive`]: json(
        {
          id: '0192a000-0000-7000-8000-00000000c6a1',
          kind: 'ambulance.charge_waiver',
          summary: 'Waive ambulance trip NHAT26000045',
          canDecide: false,
        },
        202,
      ),
    });
    const user = userEvent.setup();
    show(<WaiveDialog trip={trip} onClose={() => undefined} />);

    const dialog = await screen.findByRole('alertdialog', { name: /Waive the charge of NHAT26000045/ }, SLOW);
    expect(within(dialog).getByText(/the charge stays on the patient's bill until then/)).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox'), 'Free transport under the state scheme');
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Ask to waive' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith(
        'Waiver asked for',
        expect.stringContaining('The charge stays on the bill'),
      ),
    );
    expect(server.requests.find((r) => r.method === 'POST')?.body).toEqual({
      reason: 'Free transport under the state scheme',
      version: 7,
    });
  });

  it('reports an accident item by item for Form I', async () => {
    const server = fakeServer({
      'POST /api/v1/bmw/accidents': json({ id: '0192a000-0000-7000-8000-00000000c701' }, 201),
    });
    const user = userEvent.setup();
    show(<RecordAccidentDialog onClose={() => undefined} />);

    const dialog = await screen.findByRole('dialog', { name: 'Report an accident (Form I)' }, SLOW);
    await user.click(within(dialog).getByRole('button', { name: 'Report' }));
    expect(await within(dialog).findByText('Say where it happened.')).toBeInTheDocument();
    expect(server.requests.some((r) => r.method === 'POST')).toBe(false);

    await user.type(within(dialog).getByRole('textbox', { name: /^Where/ }), 'Ward 3 dressing room');
    await user.type(within(dialog).getByRole('textbox', { name: /^Sequence of events/ }), 'Recapping a used needle');
    await user.type(within(dialog).getByRole('textbox', { name: /^The waste involved/ }), 'A used 24G needle');
    await user.type(within(dialog).getByRole('textbox', { name: /^Effects on people/ }), 'Possible exposure');
    await user.type(within(dialog).getByRole('textbox', { name: /^Emergency measures/ }), 'Washed under running water');
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Report' }));

    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Accident reported', expect.any(String)));
    expect(server.requests.find((r) => r.method === 'POST')?.body).toMatchObject({
      kind: 'NeedleStick',
      place: 'Ward 3 dressing room',
      wasteInvolved: 'A used 24G needle',
      personsAffected: 1,
    });
  });

  it('records a cleaning only with every item answered, one not done saying why', async () => {
    const onSave = vi.fn(() => Promise.resolve());
    const user = userEvent.setup();
    show(
      <ChecklistDialog
        title="ICU bay 2: cleaning 1"
        items={['Floor mopped', 'Bins emptied']}
        onSave={onSave}
        onClose={() => undefined}
      />,
    );

    const dialog = await screen.findByRole('dialog', { name: 'ICU bay 2: cleaning 1' }, SLOW);
    await user.click(within(dialog).getByRole('checkbox', { name: 'Floor mopped' }));
    await user.click(within(dialog).getByRole('button', { name: 'Record' }));
    expect(await within(dialog).findByText('Say why "Bins emptied" was not done.')).toBeInTheDocument();
    expect(onSave).not.toHaveBeenCalled();

    await user.type(within(dialog).getByRole('textbox', { name: 'Why "Bins emptied" was not done' }), 'No liners');
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Record' }));
    await waitFor(() =>
      expect(onSave).toHaveBeenCalledWith(
        [
          { item: 'Floor mopped', done: true, note: null },
          { item: 'Bins emptied', done: false, note: 'No liners' },
        ],
        null,
      ),
    );
  });

  it('sends a cleaning back only saying what was not clean', async () => {
    const onSave = vi.fn(() => Promise.resolve());
    const user = userEvent.setup();
    show(<VerifyDialog what="ICU bay 2, cleaning 1, done by Ravi" onSave={onSave} onClose={() => undefined} />);

    const dialog = await screen.findByRole('dialog', { name: 'Verify the cleaning' }, SLOW);
    await user.click(within(dialog).getByRole('radio', { name: 'Not clean: clean again' }));
    await user.click(within(dialog).getByRole('button', { name: 'Record' }));
    expect(await within(dialog).findByText('Say what was not clean.')).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox', { name: /What was not clean/ }), 'Bins still full');
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Record' }));
    await waitFor(() => expect(onSave).toHaveBeenCalledWith(false, 'Bins still full'));
  });
});
