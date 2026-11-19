import { useSession } from '@hc360/core';
import type * as Core from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { MedicationOrderDto } from '@/modules/inpatient/nursingApi';
import type { ItemLookup } from '@/modules/inventory/api';
import { fakeServer, paged, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { AdmissionOption } from './api';
import { WardIndentDialog } from './WardIndentDialog';

// The ward indents an e-prescribed medicine for its prescription, so the IP pharmacy issues it for the prescription's own
// line: the chart charts that line, and the prescription's queue does not issue and charge it again.

const IP_STORE = '0192a000-0000-7000-8000-00000000c001';
const CEFTRIAXONE = '0192a000-0000-7000-8000-00000000e001';
const PRESCRIPTION = '0192a000-0000-7000-8000-00000000e101';

/** The ward's nurse: keeps the MAR and raises indents. */
const NURSE: Core.AuthUser = {
  ...STAFF_USER,
  id: '0192a000-0000-7000-8000-00000000f601',
  displayName: 'Sr. Kavita Pawar',
  roles: ['Nurse'],
  practitionerId: null,
  permissions: [
    'pharmacy.ip.indent',
    'inventory.indents.raise',
    'inpatient.admissions.read',
    'nursing.mar',
    'nursing.chart',
  ],
};

const ASHA: AdmissionOption = {
  id: '0192a000-0000-7000-8000-00000000f201',
  admissionNumber: 'NHPIP260000123',
  patientId: '0192a000-0000-7000-8000-00000000f301',
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  gender: 'Female',
  age: '58 Y',
  status: 'Admitted',
  wardName: 'Semi-private ward',
  bedLabel: 'SPW-04',
  consultantName: 'Dr. Meera Iyer',
};

const VIAL: ItemLookup = {
  id: CEFTRIAXONE,
  code: 'DRG-0412',
  name: 'Ceftriaxone 1 g Injection',
  genericName: 'Ceftriaxone',
  strength: '1 g',
  type: 'Drug',
  schedule: 'H',
  isControlled: false,
  isColdChain: false,
  baseUnit: 'VIAL',
  purchaseUnit: 'VIAL',
  purchaseUnitFactor: 1,
  gstRate: 5,
  available: 40,
  mrp: 62,
  nearestExpiry: '2028-03-31',
  isHighAlert: false,
};

/** Inj. Ceftriaxone 1 g IV BD for 5 days, e-prescribed by the consultant and on the chart. */
const PRESCRIBED: MedicationOrderDto = {
  id: '0192a000-0000-7000-8000-00000000d001',
  prescriptionId: PRESCRIPTION,
  drugItemId: CEFTRIAXONE,
  drugName: 'Ceftriaxone 1 g Injection',
  genericName: 'Ceftriaxone',
  strength: '1 g',
  dose: '1 g',
  route: 'IV',
  frequency: 'BD',
  durationDays: 5,
  startAt: '2026-12-01T04:20:00Z',
  endAt: null,
  instructions: null,
  isControlledDrug: false,
  isHighAlert: false,
  isAsNeeded: false,
  source: 'Prescription',
  status: 'Active',
  lastDispensedAt: null,
  stoppedAt: null,
  stopReason: null,
  roundTimes: [],
  mode: 'Written',
  isEmergency: false,
  orderedByPractitionerId: null,
  orderedByName: null,
  enteredByName: null,
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

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
  useSession.setState({
    status: 'authenticated',
    accessToken: 'test-token',
    expiresAt: Date.now() + 8 * 3600_000,
    user: NURSE,
    locked: false,
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('a ward indent', SCREEN_TESTS, () => {
  it('for a medicine e-prescribed on the chart is raised for its prescription', async () => {
    const server = fakeServer({
      'GET /api/v1/inpatient/admissions': paged([ASHA]),
      [`GET /api/v1/nursing/admissions/${ASHA.id}/medication-orders`]: [PRESCRIBED],
      'GET /api/v1/inventory/items/lookup': [VIAL],
      'POST /api/v1/pharmacy/ward-indents': { id: '0192a000-0000-7000-8000-00000000a001', number: 'NHPWI26000045' },
    });
    const user = userEvent.setup();
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Hc360ThemeProvider>
          <WardIndentDialog ipStoreId={IP_STORE} onClose={() => undefined} />
        </Hc360ThemeProvider>
      </QueryClientProvider>,
    );

    await user.type(await screen.findByRole('combobox', { name: /Admitted patient/ }, SLOW), 'Asha');
    await user.click(await screen.findByRole('option', { name: /Asha Devi · NH26000123/ }, SLOW));
    await user.type(screen.getByRole('combobox', { name: 'Add medicine or consumable' }), 'Ceftri');
    await user.click(await screen.findByRole('option', { name: /Ceftriaxone 1 g Injection/ }, SLOW));

    expect(await screen.findByText('For the e-prescription: 1 g BD', {}, SLOW)).toBeInTheDocument();
    await user.type(screen.getByRole('spinbutton', { name: 'Quantity of Ceftriaxone 1 g Injection' }), '10');
    await user.click(screen.getByRole('button', { name: 'Send indent' }));

    await waitFor(() =>
      expect(
        server.requests.filter((r) => r.method === 'POST' && r.path === '/api/v1/pharmacy/ward-indents'),
      ).toHaveLength(1),
    );
    const sent = server.requests.find((r) => r.method === 'POST' && r.path === '/api/v1/pharmacy/ward-indents');
    expect(sent?.body).toMatchObject({
      admissionId: ASHA.id,
      prescriptionId: PRESCRIPTION,
      lines: [{ itemId: CEFTRIAXONE, quantity: 10 }],
    });
  });
});
