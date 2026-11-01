import type * as Core from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FRONT_OFFICE_ANSWERS, IN_HOSPITAL, WARD } from '@/test/accessibility/fixtures/frontOffice';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { journeyQueries } from './journeys';

// The board of patients in the hospital (Front office › Patients in hospital): each patient with the compact pipeline
// of their visit or stay and the time spent in the stage they are in, as the server derived it. The server is a fake.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

/** A ward nurse: sees visits and the wards. */
const NURSE = {
  ...STAFF_USER,
  roles: ['Nurse'],
  permissions: ['visits.read', 'masters.read', 'inpatient.admissions.read'],
};

/** A lab technician sees visits, not the wards. */
const LAB = { ...STAFF_USER, roles: ['LabTechnician'], permissions: ['visits.read', 'masters.read'] };

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  server = fakeServer(FRONT_OFFICE_ANSWERS);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const boardRequests = () =>
  server.requests.filter((r) => r.path === '/api/v1/journeys/in-hospital').map((r) => r.url.searchParams);

async function openBoard(user = NURSE) {
  const opened = openScreen('/front-office/in-hospital', user);
  await screen.findByText('Sunil Patil', {}, SLOW);
  return opened;
}

const row = (name: string) => screen.getByText(name).closest('tr') as HTMLElement;

describe('patients in hospital', SCREEN_TESTS, () => {
  it('lists each patient with the stage they are in and the time spent in it', async () => {
    await openBoard();

    const asha = within(row('Asha Devi'));
    const visit = asha.getByRole('list', { name: 'Out-patient visit OP2600981' });
    const current = within(visit)
      .getAllByRole('listitem')
      .find((stage) => stage.getAttribute('aria-current') === 'step');
    expect(current).toHaveTextContent('In consultation');
    expect(current).toHaveTextContent('20 min');
    expect(within(visit).getAllByRole('listitem')).toHaveLength(9);
    expect(within(visit).getAllByRole('listitem')[0]).toHaveTextContent(/^Checked in: Done/);
    expect(asha.getByText('NH26000123 · Token GM-012')).toBeInTheDocument();

    // In a bed for two days, under rounds and nursing for the last 44 hours.
    const sunil = within(row('Sunil Patil'));
    expect(sunil.getByRole('list', { name: 'In-patient stay IP2600112' })).toBeInTheDocument();
    expect(sunil.getAllByText('1 d 20 h')).toHaveLength(2);
    expect(sunil.getByText(`NH26000126 · ${WARD.name} · GWM-07`)).toBeInTheDocument();
  });

  it('names a restricted patient by initials, as the server sends them, and says the record is restricted', async () => {
    await openBoard();

    const ravi = within(row('R. S. (restricted)'));
    expect(ravi.getByText('Restricted')).toBeInTheDocument();
    expect(ravi.getByText(/^NH26000124/)).toBeInTheDocument();
    expect(screen.queryByText('Ravi Shankar')).not.toBeInTheDocument();
  });

  it('asks the server for the out-patients or in-patients, the department and the ward chosen', async () => {
    const user = userEvent.setup();
    await openBoard();
    expect(boardRequests()[0]?.get('kind')).toBeNull();

    await user.selectOptions(screen.getByRole('combobox', { name: 'Show' }), 'Inpatient');
    await waitFor(() => expect(boardRequests().at(-1)?.get('kind')).toBe('Inpatient'));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Ward' }), WARD.id);
    await waitFor(() => expect(boardRequests().at(-1)?.get('wardId')).toBe(WARD.id));
    await user.selectOptions(screen.getByRole('combobox', { name: 'Department' }), IN_HOSPITAL[0]!.departmentId!);
    await waitFor(() => expect(boardRequests().at(-1)?.get('departmentId')).toBe(IN_HOSPITAL[0]!.departmentId));

    // Out-patients have no ward: the filter goes, and so does the ward chosen.
    await user.selectOptions(screen.getByRole('combobox', { name: 'Show' }), 'Outpatient');
    await waitFor(() => expect(boardRequests().at(-1)?.get('kind')).toBe('Outpatient'));
    expect(boardRequests().at(-1)?.get('wardId')).toBeNull();
    expect(screen.queryByRole('combobox', { name: 'Ward' })).not.toBeInTheDocument();
  });

  it('offers the ward filter only to staff who may read the wards', async () => {
    await openBoard(LAB);

    expect(screen.queryByRole('combobox', { name: 'Ward' })).not.toBeInTheDocument();
    expect(server.requests.some((r) => r.path === '/api/v1/inpatient/wards')).toBe(false);
  });

  it("opens the patient's page from a row", async () => {
    const user = userEvent.setup();
    const { router } = await openBoard();

    await user.click(screen.getByText('Sunil Patil'));

    await waitFor(() => expect(router.state.location.pathname).toBe(`/patients/${IN_HOSPITAL[2]!.patientId}`));
  });

  it('refreshes every minute, like the other live worklists', () => {
    expect(journeyQueries.inHospital({}).refetchInterval).toBe(60_000);
    expect(journeyQueries.forPatient('0192a000-0000-7000-8000-000000000001').refetchInterval).toBe(60_000);
  });
});
