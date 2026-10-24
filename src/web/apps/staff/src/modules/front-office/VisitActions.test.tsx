import type * as Core from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { problem, type FakeRequest } from '@/test/fakeApi';
import type { VisitListItem } from '@/modules/patients/api';

// Ending a visit from the Visits screen (and the patient's visits, which share the actions): staff with
// `visits.manage` close or cancel a visit that is still open; cancelling asks why. The server is a fake; the toasts
// are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const visit = (patch: Partial<VisitListItem>): VisitListItem => ({
  id: '0192a000-0000-7000-8000-000000000c01',
  visitNumber: 'NHOV260000211',
  patientId: '0192a000-0000-7000-8000-000000000001',
  uhid: 'NH26000123',
  patientName: 'Asha Devi',
  gender: 'Female',
  age: '54 Y',
  type: 'OutPatient',
  status: 'Open',
  departmentId: null,
  departmentName: 'General medicine',
  practitionerId: null,
  practitionerName: null,
  visitedAt: '2026-10-24T04:00:00Z',
  completedAt: null,
  isFollowUp: false,
  isFreeFollowUp: false,
  payerType: 'Self',
  isMlc: false,
  tokenLabel: 'GM-014',
  tokenStatus: 'Waiting',
  ...patch,
});

const OPEN = visit({});
const IN_PROGRESS = visit({
  id: '0192a000-0000-7000-8000-000000000c02',
  visitNumber: 'NHOV260000212',
  patientName: 'Ravi Kumar',
  status: 'InProgress',
});
const COMPLETED = visit({
  id: '0192a000-0000-7000-8000-000000000c03',
  visitNumber: 'NHOV260000213',
  patientName: 'Meena Joshi',
  status: 'Completed',
});
const CANCELLED = visit({
  id: '0192a000-0000-7000-8000-000000000c04',
  visitNumber: 'NHOV260000214',
  patientName: 'Sunil Patil',
  status: 'Cancelled',
});
/** An admission's visit: the ward discharges or cancels it, not the front desk. */
const ADMITTED = visit({
  id: '0192a000-0000-7000-8000-000000000c05',
  visitNumber: 'NHIV260000031',
  patientName: 'Kavita Rao',
  type: 'InPatient',
  status: 'InProgress',
});

const FRONT_DESK = { ...STAFF_USER, roles: ['FrontOffice'], permissions: ['visits.read', 'visits.manage'] };
const VIEWER = { ...STAFF_USER, roles: ['Nurse'], permissions: ['visits.read'] };

let server: ReturnType<typeof fakeServer>;
let answerCancel: (request: FakeRequest) => unknown;
let answerComplete: (request: FakeRequest) => unknown;

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
  answerCancel = () => null;
  answerComplete = () => null;
  server = fakeServer({
    'GET /api/v1/visits': paged([OPEN, IN_PROGRESS, COMPLETED, CANCELLED, ADMITTED]),
    'GET /api/v1/masters/lookups/departments': [],
    'POST /api/v1/visits/*': (request: FakeRequest) =>
      request.path.endsWith('/cancel') ? answerCancel(request) : answerComplete(request),
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const posted = () => server.requests.filter((r) => r.method === 'POST').map((r) => ({ path: r.path, body: r.body }));

async function openVisits(user = FRONT_DESK) {
  openScreen('/front-office/visits', user);
  await screen.findByText('Asha Devi', {}, SLOW);
}

async function chooseAction(visitNumber: string, action: 'Close visit' | 'Cancel visit') {
  const user = userEvent.setup();
  await user.click(screen.getByRole('button', { name: `Actions for visit ${visitNumber}` }));
  await user.click(await screen.findByRole('menuitem', { name: action }));
  return user;
}

describe('ending a visit', SCREEN_TESTS, () => {
  it('is offered with visits.manage on open and in-progress visits only, never on an admission', async () => {
    await openVisits();
    expect(screen.getByRole('button', { name: 'Actions for visit NHOV260000211' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Actions for visit NHOV260000212' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Actions for visit NHOV260000213' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Actions for visit NHOV260000214' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Actions for visit NHIV260000031' })).not.toBeInTheDocument();
  });

  it('is not offered without visits.manage', async () => {
    await openVisits(VIEWER);
    expect(screen.queryByRole('button', { name: /^Actions for visit/ })).not.toBeInTheDocument();
  });

  it('cancels a visit with the reason given', async () => {
    await openVisits();
    const user = await chooseAction('NHOV260000211', 'Cancel visit');
    const dialog = await screen.findByRole('alertdialog', { name: 'Cancel visit NHOV260000211' });
    expect(within(dialog).getByText(/A cancelled visit cannot be reopened/)).toBeInTheDocument();
    const confirm = within(dialog).getByRole('button', { name: 'Cancel visit' });
    expect(confirm).toBeDisabled();

    await user.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Wrong patient checked in');
    await user.click(confirm);

    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Visit cancelled', 'NHOV260000211 · Asha Devi'));
    expect(posted()).toEqual([
      { path: `/api/v1/visits/${OPEN.id}/cancel`, body: { reason: 'Wrong patient checked in' } },
    ]);
  });

  it("shows the server's refusal in the cancel dialog", async () => {
    answerCancel = () => problem(422, 'This visit is already closed.');
    await openVisits();
    const user = await chooseAction('NHOV260000212', 'Cancel visit');
    const dialog = await screen.findByRole('alertdialog', { name: 'Cancel visit NHOV260000212' });
    await user.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Patient left without being seen');
    await user.click(within(dialog).getByRole('button', { name: 'Cancel visit' }));

    expect(await within(dialog).findByText('This visit is already closed.')).toBeInTheDocument();
    expect(notify.success).not.toHaveBeenCalled();
  });

  it('closes a visit once confirmed, with no body', async () => {
    await openVisits();
    const user = await chooseAction('NHOV260000212', 'Close visit');
    const dialog = await screen.findByRole('alertdialog', { name: 'Close visit NHOV260000212' });
    await user.click(within(dialog).getByRole('button', { name: 'Close visit' }));

    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Visit closed', 'NHOV260000212 · Ravi Kumar'));
    expect(posted()).toEqual([{ path: `/api/v1/visits/${IN_PROGRESS.id}/complete`, body: null }]);
  });

  it('says why a visit could not be closed', async () => {
    answerComplete = () => problem(422, 'A cancelled visit cannot be completed.');
    await openVisits();
    const user = await chooseAction('NHOV260000211', 'Close visit');
    const dialog = await screen.findByRole('alertdialog', { name: 'Close visit NHOV260000211' });
    await user.click(within(dialog).getByRole('button', { name: 'Close visit' }));

    await waitFor(() => expect(notify.error).toHaveBeenCalledWith(expect.anything(), 'Could not close the visit'));
    expect(notify.success).not.toHaveBeenCalled();
  });
});
