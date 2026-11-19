import { useSession } from '@hc360/core';
import type * as Core from '@hc360/core';
import { Hc360ThemeProvider } from '@hc360/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { WardMarItem } from '../nursingApi';
import { WardMarDue } from './WardMarDue';

// The ward's medication round. A dose of a medicine the doctor has stopped is never given: the stop cancels it, and one
// still listed says why and offers no "Given", as a held dose does.

const WARD_ID = '0192a000-0000-7000-8000-00000000f401';

/** The ward's nurse: keeps the MAR, prescribes nothing. */
const NURSE: Core.AuthUser = {
  ...STAFF_USER,
  id: '0192a000-0000-7000-8000-00000000f601',
  displayName: 'Sr. Kavita Pawar',
  roles: ['Nurse'],
  practitionerId: null,
  permissions: ['nursing.mar', 'nursing.chart', 'inpatient.admissions.read'],
};

/** Asha Devi's 09:00 dose, 40 minutes late. */
const dose = (more: Partial<WardMarItem>): WardMarItem => ({
  entryId: '0192a000-0000-7000-8000-00000000f101',
  admissionId: '0192a000-0000-7000-8000-00000000f201',
  patientId: '0192a000-0000-7000-8000-00000000f301',
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  bedLabel: 'SPW-04',
  drugName: 'Tab. Paracetamol',
  dose: '650 mg',
  route: 'Oral',
  scheduledAt: new Date(Date.now() - 40 * 60_000).toISOString(),
  isOverdue: true,
  minutesLate: 40,
  version: 3,
  needsWitness: false,
  heldBecause: null,
  ...more,
});

function openRound() {
  useSession.setState({
    status: 'authenticated',
    accessToken: 'test-token',
    expiresAt: Date.now() + 8 * 3600_000,
    user: NURSE,
    locked: false,
  });
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <Hc360ThemeProvider>
        <WardMarDue wardId={WARD_ID} onOpenPatient={() => undefined} />
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

describe("the ward's medication round", SCREEN_TESTS, () => {
  it('offers no "Given" for a dose of a medicine the doctor has stopped', async () => {
    fakeServer({
      [`GET /api/v1/nursing/wards/${WARD_ID}/mar/due`]: [
        dose({}),
        dose({
          entryId: '0192a000-0000-7000-8000-00000000f102',
          drugName: 'Inj. Ceftriaxone',
          dose: '1 g',
          route: 'IV',
          heldBecause: 'medication.not_active',
        }),
      ],
    });
    openRound();

    expect(await screen.findByText('Not to be given: medicine stopped', {}, SLOW)).toBeInTheDocument();
    // Only the paracetamol, whose order goes on, is given; either may be recorded as not given.
    expect(screen.getAllByRole('button', { name: 'Given' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Not given' })).toHaveLength(2);
  });
});
