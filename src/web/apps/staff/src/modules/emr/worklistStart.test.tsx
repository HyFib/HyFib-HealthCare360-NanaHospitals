import type * as Core from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import type { WardRound } from '@/modules/inpatient/api';
import type { EmrWorklist, WorklistAppointment, WorklistQueueEntry } from './api';

// Starting a consultation from the doctor's worklist (EMR-01), whole with the shell, against a fake server. One call
// does it: POST /api/v1/emr/encounters starts the visit and puts its queue token in service on the server, in one
// transaction. The screen used to start the token as well, at the same moment and without waiting for it; both calls
// wrote the visit, the one saved second was refused (409, "changed by someone else"), and the note often did not open.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const id = (suffix: string) => `0192a000-0000-7000-8000-00000000e${suffix}`;
const DEPARTMENT = id('001');
const PATIENT = id('002');
const VISIT = id('003');
const TOKEN = id('004');
const ENCOUNTER = id('005');
const APPOINTMENT = id('006');

const WAITING: WorklistQueueEntry = {
  tokenId: TOKEN,
  tokenLabel: 'GM-010',
  tokenStatus: 'Waiting',
  priority: 'Normal',
  visitId: VISIT,
  patientId: PATIENT,
  uhid: 'NHP-0001234',
  patientName: 'Kavita Joshi',
  gender: 'Female',
  age: '46 y',
  chiefComplaint: 'Fever for three days',
  isFollowUp: false,
  issuedAt: '2026-11-19T03:40:00Z',
  waitingMinutes: 12,
  encounterId: null,
  encounterStatus: null,
  hasActiveAllergies: false,
  hasCriticalAlerts: false,
  isRestricted: false,
};

/** Her booking, checked in: the queue holds its visit. */
const BOOKED: WorklistAppointment = {
  id: APPOINTMENT,
  number: 'AP-26-0042',
  startsAt: '2026-11-19T03:30:00Z',
  endsAt: '2026-11-19T03:45:00Z',
  status: 'CheckedIn',
  visitType: 'New',
  isTelehealth: false,
  reason: 'Fever for three days',
  patientId: PATIENT,
  uhid: WAITING.uhid,
  patientName: WAITING.patientName,
  gender: 'Female',
  age: '46 y',
  practitionerId: STAFF_USER.practitionerId!,
  practitionerName: 'Dr. Meera Iyer',
  visitId: VISIT,
  checkedInAt: '2026-11-19T03:40:00Z',
  tokenLabel: WAITING.tokenLabel,
  encounterId: null,
  encounterStatus: null,
  hasActiveAllergies: false,
  hasCriticalAlerts: false,
  isRestricted: false,
};

let server: ReturnType<typeof fakeServer>;
let token: WorklistQueueEntry;

const worklist = (): EmrWorklist => ({
  date: '2026-11-19',
  practitionerId: STAFF_USER.practitionerId,
  departmentId: DEPARTMENT,
  queue: [token],
  appointments: [BOOKED],
  openEncounters: [],
  unsignedNotes: [],
  draftPrescriptions: [],
  resultsToReview: [],
  chartDeficiencies: [],
});

beforeEach(() => {
  stubBrowserApis();
  token = { ...WAITING };
  const round: WardRound = {
    date: '2026-11-19',
    practitionerId: STAFF_USER.practitionerId,
    departmentId: null,
    patients: [],
  };
  server = fakeServer({
    'GET /api/v1/emr/worklist': () => worklist(),
    'GET /api/v1/masters/lookups/departments': [{ id: DEPARTMENT, code: 'GM', name: 'General Medicine' }],
    'GET /api/v1/masters/lookups/practitioners': [
      { id: STAFF_USER.practitionerId, code: 'D001', name: 'Dr. Meera Iyer' },
    ],
    'GET /api/v1/inpatient/rounds': round,
    // F2 / "Call next patient": the server calls her token.
    'POST /api/v1/queue/call-next': () => {
      token = { ...token, tokenStatus: 'Called' };
      return { id: TOKEN, label: token.tokenLabel, status: 'Called', visitId: VISIT, patientId: PATIENT };
    },
    'POST /api/v1/emr/encounters': {
      id: ENCOUNTER,
      patientId: PATIENT,
      visitId: VISIT,
      status: 'InProgress',
      version: 1,
    },
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** The worklist's writes, in order: method and path. */
const writes = () => server.requests.filter((r) => r.method !== 'GET').map((r) => `${r.method} ${r.path}`);

async function expectNoteOpened(router: ReturnType<typeof openScreen>['router']) {
  await waitFor(() => expect(router.state.location.pathname).toBe(`/emr/encounters/${ENCOUNTER}`), SLOW);
  const started = server.requests.filter((r) => r.method === 'POST' && r.path === '/api/v1/emr/encounters');
  expect(started).toHaveLength(1);
  expect(started[0]!.body).toMatchObject({ patientId: PATIENT, visitId: VISIT });
}

const queueTable = async () => screen.findByRole('table', { name: "Today's queue" }, SLOW);

describe('starting a consultation from the worklist (EMR-01)', SCREEN_TESTS, () => {
  it('starts a waiting patient with the encounter call alone, and opens the note', async () => {
    const { router } = openScreen('/emr');

    await userEvent.click(await within(await queueTable()).findByRole('button', { name: 'Start' }, SLOW));

    await expectNoteOpened(router);
    expect(writes()).toEqual(['POST /api/v1/emr/encounters']);
  });

  it('starts a patient called with "Call next patient" with the encounter call alone', async () => {
    const { router } = openScreen('/emr');
    const table = await queueTable();
    await within(table).findByRole('button', { name: 'Call to the room' }, SLOW);

    await userEvent.click(screen.getByRole('button', { name: 'Call next patient (F2)' }));
    await within(table).findByText('Called', {}, SLOW);
    await userEvent.click(within(table).getByRole('button', { name: 'Start' }));

    await expectNoteOpened(router);
    expect(writes()).toEqual(['POST /api/v1/queue/call-next', 'POST /api/v1/emr/encounters']);
  });

  it('starts a booking from the appointments tab with the encounter call alone', async () => {
    const { router } = openScreen('/emr?view=appointments');

    await userEvent.click(
      await screen.findByRole('button', { name: `Start the consultation of ${WAITING.patientName}` }, SLOW),
    );

    await expectNoteOpened(router);
    expect(writes()).toEqual(['POST /api/v1/emr/encounters']);
  });
});
