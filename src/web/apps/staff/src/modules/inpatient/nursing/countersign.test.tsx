import { useSession } from '@hc360/core';
import type * as Core from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { AwaitingCountersignatureItem, MedicationOrderDto } from '../nursingApi';
import { MedicationOrders } from './MedicationOrders';

// Maker-checker on the ward (slice 5), on whole screens against a fake server. A doctor countersigns the verbal order a
// nurse wrote in their name, from the list of orders to countersign, sending the version they saw; a nurse sees it waiting
// and is offered no countersignature. On the medicines tab a nurse writes the doctor's order as a verbal or telephone
// order with the read-back, a nurse's stop shows as pending with the doses held, and the doctor named confirms or
// declines it.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const ORDER_ID = '0192a000-0000-7000-8000-00000000e101';
const ADMISSION_ID = '0192a000-0000-7000-8000-00000000e201';
const PATIENT_ID = '0192a000-0000-7000-8000-00000000e301';
const WARD_ID = '0192a000-0000-7000-8000-00000000e401';
const AWAITING = '/api/v1/nursing/medication-orders/awaiting-countersignature';
const NOW = Date.now();
const hoursFromNow = (hours: number) => new Date(NOW + hours * 3_600_000).toISOString();

/** A telephone order of insulin (high-alert) the night nurse wrote in Dr. Meera Iyer's name: no dose until she countersigns. */
const order = (more: Partial<MedicationOrderDto> = {}): MedicationOrderDto => ({
  id: ORDER_ID,
  prescriptionId: null,
  drugItemId: '0192a000-0000-7000-8000-00000000e501',
  drugName: 'Inj. Insulin regular',
  genericName: 'Insulin regular',
  strength: '100 IU/ml',
  dose: '6 IU',
  route: 'SC',
  frequency: 'STAT',
  durationDays: 1,
  startAt: hoursFromNow(-1),
  endAt: null,
  instructions: null,
  isControlledDrug: false,
  isHighAlert: true,
  isAsNeeded: false,
  source: 'Manual',
  status: 'Active',
  lastDispensedAt: null,
  stoppedAt: null,
  stopReason: null,
  roundTimes: [],
  mode: 'Telephone',
  isEmergency: false,
  orderedByPractitionerId: STAFF_USER.practitionerId,
  orderedByName: 'Dr. Meera Iyer',
  enteredByName: 'Sr. Kavita Pawar',
  awaitingCountersignature: true,
  countersignDueAt: hoursFromNow(23),
  countersignOverdue: false,
  countersignedAt: null,
  countersignedByName: null,
  stopPending: false,
  stopRequestedAt: null,
  stopRequestedByName: null,
  stopRequestReason: null,
  stopCountersignOverdue: false,
  dosesHeld: true,
  canCountersign: true,
  version: 4,
  ...more,
});

const item = (o: MedicationOrderDto): AwaitingCountersignatureItem => ({
  order: o,
  admissionId: ADMISSION_ID,
  patientId: PATIENT_ID,
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  wardId: WARD_ID,
  wardName: 'Semi-private ward',
  bedLabel: 'SPW-04',
});

/** The night nurse: keeps the MAR, prescribes nothing, has no practitioner record. */
const NURSE: Core.AuthUser = {
  ...STAFF_USER,
  id: '0192a000-0000-7000-8000-00000000e601',
  displayName: 'Sr. Kavita Pawar',
  roles: ['Nurse'],
  practitionerId: null,
  permissions: ['nursing.mar', 'nursing.chart', 'inpatient.admissions.read'],
};

let server: ReturnType<typeof fakeServer>;
const posts = () => server.requests.filter((r) => r.method === 'POST');

/** The medicines tab of Asha Devi's chart, signed in as `user`. */
function openMedicines(user: Core.AuthUser) {
  useSession.setState({
    status: 'authenticated',
    accessToken: 'test-token',
    expiresAt: Date.now() + 8 * 3600_000,
    user,
    locked: false,
  });
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Hc360ThemeProvider>
        <MedicationOrders admissionId={ADMISSION_ID} />
      </Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('orders to countersign', SCREEN_TESTS, () => {
  it('lets the doctor named countersign a verbal order written in their name, on the version shown', async () => {
    server = fakeServer({
      [`GET ${AWAITING}`]: [item(order())],
      'GET /api/v1/inpatient/wards': [],
      [`POST /api/v1/nursing/medication-orders/${ORDER_ID}/countersign`]: order({
        awaitingCountersignature: false,
        countersignedAt: new Date(NOW).toISOString(),
        countersignedByName: 'Dr. Meera Iyer',
        dosesHeld: false,
        canCountersign: false,
        version: 5,
      }),
    });
    const user = userEvent.setup();
    openScreen('/nursing/countersignatures');

    expect(await screen.findByText('Inj. Insulin regular', {}, SLOW)).toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'Orders to countersign' });
    expect(within(table).getByText('Awaiting countersignature')).toBeInTheDocument();
    expect(within(table).getByText('Telephone order written by Sr. Kavita Pawar')).toBeInTheDocument();
    await expectNoAxeViolations();

    await user.click(screen.getByRole('button', { name: 'Actions for Inj. Insulin regular, Asha Devi' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Countersign' }));
    const dialog = await screen.findByRole('dialog', { name: 'Countersign this order?' });
    expect(within(dialog).getByText(/Telephone order written in your name by Sr\. Kavita Pawar/)).toBeInTheDocument();
    expect(within(dialog).getByText(/its doses go on the round/)).toBeInTheDocument();
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Countersign' }));

    await vi.waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]).toMatchObject({
      path: `/api/v1/nursing/medication-orders/${ORDER_ID}/countersign`,
      body: { version: 4 },
    });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('shows a nurse what waits, past its time flagged, and offers her no countersignature', async () => {
    server = fakeServer({
      [`GET ${AWAITING}`]: [item(order({ canCountersign: false, countersignOverdue: true }))],
      'GET /api/v1/inpatient/wards': [],
    });
    const user = userEvent.setup();
    openScreen('/nursing/countersignatures', NURSE);

    expect(await screen.findByText('Countersignature overdue', {}, SLOW)).toBeInTheDocument();
    // A nurse has no list of her own to countersign.
    expect(screen.queryByRole('option', { name: 'Mine to countersign' })).not.toBeInTheDocument();
    await expectNoAxeViolations();
    await user.click(screen.getByRole('button', { name: 'Actions for Inj. Insulin regular, Asha Devi' }));
    expect(await screen.findByRole('menuitem', { name: 'Open the chart' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Countersign' })).not.toBeInTheDocument();
  });
});

describe("the ward's medicines tab", SCREEN_TESTS, () => {
  const ORDERS = `/api/v1/nursing/admissions/${ADMISSION_ID}/medication-orders`;

  it("makes a nurse write the doctor's order as a verbal or telephone order, read back", async () => {
    server = fakeServer({
      [`GET ${ORDERS}`]: [],
      'GET /api/v1/nursing/drugs': [],
      'GET /api/v1/masters/lookups/practitioners': [
        { id: STAFF_USER.practitionerId, code: 'DR001', name: 'Dr. Meera Iyer' },
      ],
      [`POST ${ORDERS}`]: order({ drugItemId: null, drugName: 'Inj. Ondansetron', isHighAlert: true }),
    });
    const user = userEvent.setup();
    openMedicines(NURSE);

    await user.click(await screen.findByRole('button', { name: 'Add ward order' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: 'Add ward medication order' });
    expect(within(dialog).getByText(/You are writing a doctor's verbal or telephone order/)).toBeInTheDocument();
    expect(
      within(dialog).getByText(/Not picked from the drug master: it is handled as high-alert/),
    ).toBeInTheDocument();
    await expectNoAxeViolations();

    await user.type(within(dialog).getByRole('textbox', { name: /^Medicine/ }), 'Inj. Ondansetron');
    await user.type(within(dialog).getByRole('textbox', { name: /^Dose/ }), '4 mg');
    await user.click(within(dialog).getByRole('combobox', { name: /Ordered by/ }));
    await user.click(await screen.findByRole('option', { name: 'Dr. Meera Iyer' }));

    // Neither how it was given nor the read-back may be left out.
    await user.click(within(dialog).getByRole('button', { name: 'Add verbal order' }));
    expect(await within(dialog).findByText(/face to face or on the telephone/)).toBeInTheDocument();
    expect(posts()).toHaveLength(0);
    await user.click(within(dialog).getByRole('radio', { name: 'Telephone' }));
    await user.click(within(dialog).getByRole('button', { name: 'Add verbal order' }));
    expect(await within(dialog).findByText(/Read the order back to the doctor/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('checkbox', { name: /I read the order back to the doctor/ }));
    await user.click(within(dialog).getByRole('button', { name: 'Add verbal order' }));

    await vi.waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]?.body).toMatchObject({
      drugItemId: null,
      drugName: 'Inj. Ondansetron',
      orderedByPractitionerId: STAFF_USER.practitionerId,
      mode: 'Telephone',
      readBack: true,
      emergency: false,
    });
  });

  it("shows a nurse's stop as pending, the doses held, and offers the doctor named to confirm or decline it", async () => {
    const stopping = order({
      awaitingCountersignature: false,
      mode: 'Written',
      dosesHeld: true,
      stopPending: true,
      stopRequestedAt: hoursFromNow(-1),
      stopRequestedByName: 'Sr. Kavita Pawar',
      stopRequestReason: 'Blood sugar 70 mg/dL',
    });
    server = fakeServer({
      [`GET ${ORDERS}`]: [stopping],
      [`POST /api/v1/nursing/medication-orders/${ORDER_ID}/stop/decline`]: { ...stopping, stopPending: false },
    });
    const user = userEvent.setup();
    openMedicines(STAFF_USER);

    expect(await screen.findByText('Stop pending: doses held', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('Stop asked for by Sr. Kavita Pawar: Blood sugar 70 mg/dL')).toBeInTheDocument();
    await expectNoAxeViolations();
    await user.click(screen.getByRole('button', { name: 'Actions for Inj. Insulin regular' }));
    expect(await screen.findByRole('menuitem', { name: 'Confirm stop' })).toBeInTheDocument();
    // While the stop waits it is not asked for again.
    expect(screen.queryByRole('menuitem', { name: 'Stop medicine' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('menuitem', { name: 'Decline stop' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Decline the stop of Inj. Insulin regular?' });
    await user.type(within(dialog).getByRole('textbox'), 'Recheck sugar at 6 am; give the dose');
    await user.click(within(dialog).getByRole('button', { name: 'Decline stop' }));

    await vi.waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]?.body).toEqual({ reason: 'Recheck sugar at 6 am; give the dose', version: 4 });
  });
});
