import { useSession } from '@hc360/core';
import type * as Core from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { MedicationOrderDto } from '../nursingApi';
import type { WardSafetyAlert } from '../wardOrderSafety';
import { MedicationOrders } from './MedicationOrders';

// The prescribing safety check of a ward order, on the medicines tab against a fake server. The doctor writes amoxicillin
// and clavulanic acid for a patient with anaphylaxis to penicillins: the check stops it, and the order is written only with
// the doctor's clinical reason, which goes with it. A warning is reviewed before the order is written.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const ADMISSION_ID = '0192a000-0000-7000-8000-00000000c201';
const ORDERS = `/api/v1/nursing/admissions/${ADMISSION_ID}/medication-orders`;
const DRUG = 'Tab. Amoxicillin + Clavulanic acid 625 mg';

const ALLERGY: WardSafetyAlert = {
  key: 'class-allergy:penicillins:0192a000-0000-7000-8000-00000000c301',
  kind: 'ClassAllergy',
  severity: 'Major',
  isBlocking: true,
  message: `ALLERGY: ${DRUG} belongs to Penicillins; the patient is allergic to Penicillins (ANAPHYLAXIS).`,
  detail: null,
};

const DUPLICATE: WardSafetyAlert = {
  key: 'duplicate:amoxicillin:0192a000-0000-7000-8000-00000000c301:0192a000-0000-7000-8000-00000000c302',
  kind: 'DuplicateTherapy',
  severity: 'Moderate',
  isBlocking: false,
  message: `Duplicate therapy: amoxicillin is in both ${DRUG} and Cap. Mox 500 (already prescribed on the ward chart).`,
  detail: null,
};

/** The order as the server answers once it is written. */
const written: MedicationOrderDto = {
  id: '0192a000-0000-7000-8000-00000000c401',
  prescriptionId: null,
  drugItemId: null,
  drugName: DRUG,
  genericName: null,
  strength: null,
  dose: '1 tablet',
  route: 'Oral',
  frequency: 'BD',
  durationDays: 5,
  startAt: new Date().toISOString(),
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
  roundTimes: ['09:00:00', '21:00:00'],
  mode: 'Written',
  isEmergency: false,
  orderedByPractitionerId: STAFF_USER.practitionerId,
  orderedByName: 'Dr. Meera Iyer',
  enteredByName: 'Dr. Meera Iyer',
  awaitingCountersignature: false,
  countersignDueAt: null,
  countersignOverdue: false,
  countersignedAt: null,
  countersignedByName: null,
  stopPending: false,
  stopRequestedAt: null,
  stopRequestedByName: null,
  stopRequestReason: null,
  stopCountersignOverdue: false,
  dosesHeld: false,
  canCountersign: false,
  version: 1,
};

let server: ReturnType<typeof fakeServer>;
const posts = () => server.requests.filter((r) => r.method === 'POST');

/** The medicines tab, signed in as the doctor, who writes in his own name; the form open with the order filled in. */
async function writeOrder() {
  useSession.setState({
    status: 'authenticated',
    accessToken: 'test-token',
    expiresAt: Date.now() + 8 * 3600_000,
    user: STAFF_USER,
    locked: false,
  });
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Hc360ThemeProvider>
        <MedicationOrders admissionId={ADMISSION_ID} />
      </Hc360ThemeProvider>
    </QueryClientProvider>,
  );
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'Add ward order' }, SLOW));
  const dialog = await screen.findByRole('dialog', { name: 'Add ward medication order' });
  await user.type(within(dialog).getByRole('textbox', { name: /^Medicine/ }), DRUG);
  await user.type(within(dialog).getByRole('textbox', { name: /^Dose/ }), '1 tablet');
  await user.click(within(dialog).getByRole('button', { name: 'Add to chart' }));
  return { user, dialog };
}

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("a ward order's safety check", SCREEN_TESTS, () => {
  it('shows the alert that stops the order and writes it only with the clinical reason', async () => {
    server = fakeServer({
      [`GET ${ORDERS}`]: [],
      'GET /api/v1/nursing/drugs': [],
      [`POST ${ORDERS}/check`]: { isBlocked: true, alerts: [ALLERGY] },
      [`POST ${ORDERS}`]: written,
    });
    const { user, dialog } = await writeOrder();

    // The check stops it: the allergy is shown in the form, and nothing is written yet.
    expect(await within(dialog).findByText('Stop: 1 problem blocks this order', {}, SLOW)).toBeInTheDocument();
    expect(within(dialog).getByText(ALLERGY.message)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Add to chart' })).toBeDisabled();
    expect(posts()).toHaveLength(1);
    await expectNoAxeViolations();

    await user.type(
      within(dialog).getByRole('textbox', { name: /Clinical reason for overriding/ }),
      'Skin test negative',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Add to chart' }));

    await vi.waitFor(() => expect(posts()).toHaveLength(2));
    expect(posts()[0]?.path).toBe(`${ORDERS}/check`);
    expect(posts()[1]).toMatchObject({ path: ORDERS, body: { drugName: DRUG, overrideReason: 'Skin test negative' } });
  });

  it('has a warning reviewed before the order is written, with no reason sent', async () => {
    server = fakeServer({
      [`GET ${ORDERS}`]: [],
      'GET /api/v1/nursing/drugs': [],
      [`POST ${ORDERS}/check`]: { isBlocked: false, alerts: [DUPLICATE] },
      [`POST ${ORDERS}`]: written,
    });
    const { user, dialog } = await writeOrder();

    expect(await within(dialog).findByText('1 warning to review', {}, SLOW)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Add to chart' })).toBeDisabled();
    await user.click(
      within(dialog).getByRole('checkbox', { name: 'I have reviewed these warnings and want to continue' }),
    );
    await user.click(within(dialog).getByRole('button', { name: 'Add to chart' }));

    await vi.waitFor(() => expect(posts()).toHaveLength(2));
    expect(posts()[1]).toMatchObject({ path: ORDERS, body: { drugName: DRUG, overrideReason: null } });
  });
});
