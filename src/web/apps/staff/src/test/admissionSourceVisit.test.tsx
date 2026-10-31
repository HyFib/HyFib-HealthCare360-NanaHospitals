import type * as Core from '@hc360/core';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent, { type UserEvent } from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AdmissionCreated, AvailableBedDto } from '@/modules/inpatient/api';
import { toIstInput } from '@/modules/inpatient/time';
import { FRONT_OFFICE_ANSWERS, PATIENTS } from './accessibility/fixtures/frontOffice';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis, type FakeRequest } from './appScreen';
import { problem } from './fakeApi';

// The admission desk links a stay to the OPD or casualty visit it comes from (production-style run, fix 6). From
// review: the link is settled before admitting (the desk waits while the patient's visits load, and is told when they
// cannot be), only visits before the admission are offered (an admission entered as earlier counts back from its own
// time), and a "Not linked" the desk chose stands when the visits load again.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const ASHA = PATIENTS[0]!;
const HOUR = 3_600_000;

const BED: AvailableBedDto = {
  bedId: '0192a000-0000-7000-8000-000000000d71',
  label: 'GW-12',
  wardId: '0192a000-0000-7000-8000-000000000d61',
  wardName: 'General ward (female)',
  wardType: 'General',
  roomNumber: '204',
  status: 'Vacant',
  hasOxygen: true,
  hasVentilator: false,
  hasMonitor: false,
  isEligible: true,
  ineligibleReason: null,
};

const CREATED: AdmissionCreated = {
  id: '0192a000-0000-7000-8000-000000000d81',
  admissionNumber: 'NHIP260000077',
  visitId: '0192a000-0000-7000-8000-000000000d82',
  visitNumber: 'NHIV260000077',
  bedId: BED.bedId,
  bedLabel: BED.label,
};

const opdVisit = (suffix: string, visitNumber: string, hoursAgo: number) => ({
  id: `0192a000-0000-7000-8000-000000000${suffix}`,
  visitNumber,
  type: 'OutPatient',
  status: 'InProgress',
  visitedAt: new Date(Date.now() - hoursAgo * HOUR).toISOString(),
  departmentName: 'General Medicine',
  practitionerName: 'Dr. Anil Kumar',
});

/** Seen in OPD an hour ago, and the day before yesterday. */
const TODAY = opdVisit('d91', 'NHV260000076', 1);
const EARLIER = opdVisit('d92', 'NHV260000071', 30);

let server: ReturnType<typeof fakeServer>;

function serve(visits: (request: FakeRequest) => unknown) {
  server = fakeServer({
    ...FRONT_OFFICE_ANSWERS,
    'GET /api/v1/beds/available': [BED],
    [`GET /api/v1/patients/${ASHA.id}/admissions`]: [],
    [`GET /api/v1/patients/${ASHA.id}/visits`]: visits,
    'POST /api/v1/inpatient/admissions': CREATED,
  });
}

const visitsPage = (items: unknown[]) => () => ({ items, page: 1, pageSize: 200, totalCount: items.length });

/** What the admission sent, once the desk pressed "Admit patient". */
const admitted = () => server.requests.find((r) => r.path === '/api/v1/inpatient/admissions' && r.method === 'POST');

const admitButton = () =>
  screen.getAllByRole('button', { name: 'Admit patient' }).find((b) => b.getAttribute('type') === 'submit')!;

async function pickPatientAndBed(user: UserEvent) {
  const picker = await screen.findByRole('combobox', { name: /^Patient/ }, SLOW);
  await user.type(picker, 'Asha');
  await user.click(await screen.findByRole('option', { name: /Asha/ }, SLOW));
  await user.click(await screen.findByRole('radio', { name: /^Bed GW-12/ }, SLOW));
}

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
  // The admission desk does not print in these tests.
  localStorage.setItem('hc360:inpatient:admit-autoprint', '0');
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('the visit an admission comes from', SCREEN_TESTS, () => {
  it('is settled before admitting: the desk waits while the visits load', async () => {
    serve(visitsPage([TODAY]));
    // The patient's visits answer only when released.
    const answer = globalThis.fetch;
    let release!: () => void;
    const loaded = new Promise<void>((resolve) => (release = resolve));
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes(`/patients/${ASHA.id}/visits`)) {
        await loaded;
      }
      return answer(input, init);
    });
    const user = userEvent.setup();
    openScreen('/inpatient/admit');

    await pickPatientAndBed(user);
    expect(await screen.findByText("Looking up the patient's visits…")).toBeInTheDocument();
    expect(admitButton()).toBeDisabled();

    act(() => release());
    await waitFor(() => expect(admitButton()).toBeEnabled(), SLOW);
    // All her recent visits in one page (the server's largest), whatever other visits she had: the first ten are not enough.
    const lookup = server.requests.find((r) => r.path === `/api/v1/patients/${ASHA.id}/visits`)!;
    expect(lookup.url.searchParams.get('pageSize')).toBe('200');
    expect(screen.getByRole('combobox', { name: 'From visit' })).toHaveValue(TODAY.id);
    await user.click(admitButton());
    await screen.findByRole('dialog', { name: 'Patient admitted' }, SLOW);
    expect(admitted()?.body).toMatchObject({ source: 'OutPatient', sourceVisitId: TODAY.id });
  });

  it('says so when the visits cannot be loaded, and admits without the link', async () => {
    serve(() => problem(500, 'Server error'));
    const user = userEvent.setup();
    openScreen('/inpatient/admit');

    await pickPatientAndBed(user);
    expect(await screen.findByText("The patient's visits could not be loaded", {}, SLOW)).toBeInTheDocument();
    await user.click(admitButton());
    await screen.findByRole('dialog', { name: 'Patient admitted' }, SLOW);
    expect(admitted()?.body).toMatchObject({ source: 'OutPatient', sourceVisitId: null });
  });

  it('offers, for an admission entered as earlier, only the visits before it', async () => {
    serve(visitsPage([TODAY, EARLIER]));
    const user = userEvent.setup();
    openScreen('/inpatient/admit');

    await pickPatientAndBed(user);
    const fromVisit = await screen.findByRole('combobox', { name: 'From visit' }, SLOW);
    await waitFor(() => expect(fromVisit).toHaveValue(TODAY.id));

    // She was admitted 26 hours ago, before today's OPD visit: only the visit before the admission is a source of it.
    await user.click(screen.getByRole('switch', { name: 'Admitted earlier (last 3 days)' }));
    const admittedAt = toIstInput(new Date(Date.now() - 26 * HOUR));
    fireEvent.change(await screen.findByLabelText(/^Admitted at/), { target: { value: admittedAt } });

    await waitFor(() => expect(fromVisit).toHaveValue(EARLIER.id));
    expect(within(fromVisit).queryByRole('option', { name: /NHV260000076/ })).not.toBeInTheDocument();
    await user.click(admitButton());
    await screen.findByRole('dialog', { name: 'Patient admitted' }, SLOW);
    expect(admitted()?.body).toMatchObject({ sourceVisitId: EARLIER.id });
  });

  it('keeps "Not linked" when the visits load again', async () => {
    // The second load brings a visit more (her lab visit just now): a list that changed, not a copy of the first.
    const lab = { ...opdVisit('d93', 'NHV260000079', 0.1), type: 'HealthCheckup' };
    let loads = 0;
    serve(() => visitsPage(++loads === 1 ? [TODAY] : [lab, TODAY])());
    const user = userEvent.setup();
    const { queryClient } = openScreen('/inpatient/admit');

    await pickPatientAndBed(user);
    const fromVisit = await screen.findByRole('combobox', { name: 'From visit' }, SLOW);
    await waitFor(() => expect(fromVisit).toHaveValue(TODAY.id));
    await user.selectOptions(fromVisit, 'Not linked');
    expect(fromVisit).toHaveValue('');

    // Something reloads her visits (another screen saved one): the desk's choice stands.
    const before = server.requests.filter((r) => r.path.endsWith('/visits')).length;
    await act(() => queryClient.invalidateQueries({ queryKey: ['patients', 'visits', ASHA.id] }));
    await waitFor(() =>
      expect(server.requests.filter((r) => r.path.endsWith('/visits')).length).toBeGreaterThan(before),
    );
    expect(fromVisit).toHaveValue('');

    await user.click(admitButton());
    await screen.findByRole('dialog', { name: 'Patient admitted' }, SLOW);
    expect(admitted()?.body).toMatchObject({ source: 'OutPatient', sourceVisitId: null });
  });
});
