import type * as Core from '@hc360/core';
import { useSession, type AuthUser } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { json } from '@/test/fakeApi';
import type { SafetyPhaseDto, SurgeryDetail, SurgeryMaterialDto } from './api';
import { PlanDialog } from './BookingDialogs';
import { PreOpChecklistPanel } from './ChecklistPanels';
import { CountsPanel } from './CountsPanel';
import { MaterialsPanel } from './MaterialsPanel';

// The theatre where a second person or a second look decides (maker-checker slice 10), on the screens against a fake
// server: a side changed after the Sign-in says the checklist is redone and asks the surgeon for a reason; site marking is
// never "not applicable" with a side booked; a count is witnessed by a second nurse and a count that does not tally is
// settled, a retained item graded for the incident report; an implant is corrected, not removed, after the operation. The
// toasts are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const BOOKING_ID = '0192a000-0000-7000-8000-0000000a1001';
const SURGEON = '0192a000-0000-7000-8000-0000000a1d01';
const ANAESTHETIST = '0192a000-0000-7000-8000-0000000a1d02';
const T = '2026-11-08T04:00:00Z';

const item = (key: string, text: string, state: 'Pending' | 'Done' = 'Pending', allowsNotApplicable = false) => ({
  key,
  text,
  allowsNotApplicable,
  state,
  remarks: null,
  recordedBy: state === 'Done' ? 'Sr. Anita Joseph' : null,
  recordedAt: state === 'Done' ? T : null,
});

function phases(signIn: boolean, timeOut: boolean, signOut = false): SafetyPhaseDto[] {
  return [
    { phase: 'SignIn', isComplete: signIn, completedAt: signIn ? T : null, items: [] },
    { phase: 'TimeOut', isComplete: timeOut, completedAt: timeOut ? T : null, items: [] },
    {
      phase: 'SignOut',
      isComplete: signOut,
      completedAt: signOut ? T : null,
      items: [item('counts_correct', 'Instrument, sponge and needle counts are correct', 'Pending', true)],
    },
  ];
}

const BOOKING: SurgeryDetail = {
  id: BOOKING_ID,
  bookingNumber: 'NHOT2600042',
  patient: null,
  patientId: '0192a000-0000-7000-8000-0000000a1p01',
  admissionId: null,
  visitId: null,
  orderItemId: null,
  procedureServiceItemId: null,
  procedureCode: null,
  procedureName: 'Inguinal hernia repair',
  laterality: 'Left',
  diagnosis: 'Left inguinal hernia',
  surgeonId: SURGEON,
  surgeonName: 'Dr. Arjun Rao',
  assistantIds: [],
  anaesthetistId: ANAESTHETIST,
  anaesthetistName: 'Dr. Kavita Shah',
  anaesthesiaType: 'Spinal',
  priority: 'Elective',
  preferredDate: null,
  theatreId: null,
  theatreName: 'OT 1',
  scheduledStart: '2026-11-08T03:30:00Z',
  scheduledEnd: '2026-11-08T05:00:00Z',
  status: 'InPreOp',
  receivedInPreOpAt: T,
  inRoomAt: T,
  startedAt: null,
  completedAt: null,
  preOpComplete: true,
  preOpChecklist: [],
  safetyChecklist: phases(true, true),
  notes: null,
  cancelReason: null,
  postponeReason: null,
  postponeCount: 0,
  chargeStatus: 'NotDue',
  chargeError: null,
  version: 9,
  nurseId: null,
  nurseName: null,
  siteLevel: null,
  siteMarkingRequired: true,
  counts: [],
  countTallies: [],
  countDiscrepancies: [],
  checklistVoids: [],
};

const user = (practitionerId: string | null, permissions: string[]): AuthUser => ({
  ...STAFF_USER,
  roles: ['Doctor'],
  practitionerId,
  permissions: ['ot.read', ...permissions],
});
const SURGEON_USER = user(SURGEON, [
  'ot.bookings.manage',
  'ot.checklists.record',
  'ot.surgery.perform',
  'ot.materials.record',
]);
const OTHER_DOCTOR = user('0192a000-0000-7000-8000-0000000a1d09', ['ot.bookings.manage', 'ot.surgery.perform']);
const NURSE = user(null, ['ot.checklists.record', 'ot.materials.record']);

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function show(node: ReactNode, signedIn: AuthUser) {
  useSession.setState({
    status: 'authenticated',
    accessToken: 'test-token',
    expiresAt: Date.now() + 3600_000,
    user: signedIn,
    locked: false,
  });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <Hc360ThemeProvider>{node}</Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

describe('the theatre where a second person or a second look decides', SCREEN_TESTS, () => {
  it('says a side changed after the Sign-in redoes the checklist, and asks the surgeon for the reason', async () => {
    server = fakeServer({ [`PUT /api/v1/ot/bookings/${BOOKING_ID}`]: null });
    const u = userEvent.setup();
    show(<PlanDialog booking={BOOKING} onClose={vi.fn()} />, SURGEON_USER);

    const dialog = await screen.findByRole('dialog', {}, SLOW);
    expect(within(dialog).getByRole('button', { name: 'Save plan' })).toBeInTheDocument();
    await u.click(within(dialog).getByRole('radio', { name: 'Right' }));

    expect(await within(dialog).findByText('The WHO checklist will be done again')).toBeInTheDocument();
    expect(within(dialog).getByText(/The WHO Sign-in and WHO Time-out are done/)).toBeInTheDocument();
    const save = within(dialog).getByRole('button', { name: 'Save and redo the checklist' });
    await expectNoAxeViolations();
    await u.click(save);
    expect(await within(dialog).findByText(/Give the reason for changing the side or procedure/)).toBeInTheDocument();
    expect(server.requests.some((r) => r.method === 'PUT')).toBe(false);

    await u.type(within(dialog).getByRole('textbox', { name: /^Reason for the change/ }), 'Consent and MRI say right');
    await u.click(save);
    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Surgical plan saved: do the WHO checklist again'));
    expect(server.requests.find((r) => r.method === 'PUT')?.body).toMatchObject({
      laterality: 'Right',
      changeReason: 'Consent and MRI say right',
      version: 9,
    });
  });

  it('does not let a doctor outside the case change the side after the Sign-in', async () => {
    server = fakeServer({});
    const u = userEvent.setup();
    show(<PlanDialog booking={BOOKING} onClose={vi.fn()} />, OTHER_DOCTOR);

    const dialog = await screen.findByRole('dialog', {}, SLOW);
    await u.click(within(dialog).getByRole('radio', { name: 'Right' }));
    expect(
      await within(dialog).findByText(/Only the operating surgeon or the anaesthetist of this case/),
    ).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Save and redo the checklist' })).toBeDisabled();
    expect(within(dialog).queryByRole('textbox', { name: /^Reason for the change/ })).toBeNull();
  });

  it('offers no "not applicable" for site marking with a side booked, and leaves the mark to the surgeon', async () => {
    server = fakeServer({});
    const booking: SurgeryDetail = {
      ...BOOKING,
      status: 'InPreOp',
      preOpComplete: false,
      safetyChecklist: phases(false, false),
      preOpChecklist: [
        item('consent', 'Informed consent for surgery and anaesthesia signed'),
        item('site_marked', 'Operative site marked by the operating surgeon', 'Pending', true),
        item('blood_arranged', 'Blood grouped, cross-matched and reserved', 'Pending', true),
      ],
    };
    show(<PreOpChecklistPanel booking={booking} canRecord />, NURSE);

    const site = await screen.findByRole(
      'radiogroup',
      { name: 'Operative site marked by the operating surgeon' },
      SLOW,
    );
    expect(within(site).queryByRole('radio', { name: 'Not applicable' })).toBeNull();
    expect(within(site).getByRole('radio', { name: 'Done' })).toBeDisabled();
    expect(
      screen.getByText(/the surgeon marks the site and records it here from their own sign-in/),
    ).toBeInTheDocument();
    const blood = screen.getByRole('radiogroup', { name: 'Blood grouped, cross-matched and reserved' });
    expect(within(blood).getByRole('radio', { name: 'Not applicable' })).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('records a count witnessed by a second nurse at the screen', async () => {
    server = fakeServer({
      'POST /api/v1/auth/witness': {
        confirmationId: '0192a000-0000-7000-8000-0000000a1w01',
        userId: '0192a000-0000-7000-8000-0000000a1u02',
        name: 'Sr. Leela Thomas',
        expiresAt: '2026-11-08T04:05:00Z',
      },
      [`POST /api/v1/ot/bookings/${BOOKING_ID}/counts`]: null,
    });
    const u = userEvent.setup();
    show(<CountsPanel booking={{ ...BOOKING, status: 'InProgress', startedAt: T }} />, NURSE);

    const form = await screen.findByRole('group', { name: 'Record a count' }, SLOW);
    await u.type(within(form).getByRole('spinbutton', { name: /How many were counted/ }), '12');
    expect(within(form).getByRole('button', { name: 'Record the count (witness first)' })).toBeDisabled();
    await u.type(within(form).getByRole('textbox', { name: /Witness's user name/ }), 'leela');
    await u.type(within(form).getByLabelText(/Witness's password/), 'not-a-real-password');
    await u.click(within(form).getByRole('button', { name: /Confirm/ }));
    expect(await within(form).findByText('Sr. Leela Thomas')).toBeInTheDocument();
    await expectNoAxeViolations();

    await u.click(within(form).getByRole('button', { name: 'Record the count' }));
    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Initial count of swabs recorded'));
    expect(server.requests.find((r) => r.path.endsWith('/witness'))?.body).toMatchObject({
      purpose: 'ot.count',
      patientId: BOOKING.patientId,
    });
    expect(server.requests.find((r) => r.path.endsWith('/counts'))?.body).toEqual({
      category: 'Swabs',
      stage: 'Initial',
      number: 12,
      witnessConfirmationId: '0192a000-0000-7000-8000-0000000a1w01',
      remarks: null,
    });
  });

  it('settles a count that did not tally as a retained item, graded for the incident report', async () => {
    const discrepancyId = '0192a000-0000-7000-8000-0000000a1c01';
    server = fakeServer({
      [`POST /api/v1/ot/bookings/${BOOKING_ID}/counts/discrepancies/${discrepancyId}/resolve`]: {
        discrepancyId,
        resolution: 'RetainedItem',
        incidentId: '0192a000-0000-7000-8000-0000000a1i01',
        incidentNumber: 'NHIR2600017',
      },
    });
    const u = userEvent.setup();
    const booking: SurgeryDetail = {
      ...BOOKING,
      status: 'InProgress',
      startedAt: T,
      countTallies: [
        { category: 'Swabs', initial: 20, added: 0, final: 19, expected: 20, isFinal: true, tallies: false },
      ],
      countDiscrepancies: [
        {
          id: discrepancyId,
          category: 'Swabs',
          expected: 20,
          counted: 19,
          finalCountId: '0192a000-0000-7000-8000-0000000a1n01',
          openedAt: T,
          resolution: null,
          resolutionNote: null,
          xrayResult: null,
          incidentId: null,
          incidentNumber: null,
          resolvedByUserId: null,
          resolvedByName: null,
          resolvedAt: null,
          isOpen: true,
        },
      ],
    };
    show(<CountsPanel booking={booking} />, NURSE);

    expect(await screen.findByText('A count did not tally', {}, SLOW)).toBeInTheDocument();
    const settle = screen.getByRole('group', { name: 'Settle the swabs count' });
    await u.click(within(settle).getByRole('radio', { name: 'Retained item: report a never event' }));
    await u.type(within(settle).getByRole('textbox', { name: /What was done/ }), 'Swab seen on X-ray; re-exploration');
    const report = within(settle).getByRole('button', { name: 'Report and settle' });
    expect(report).toBeDisabled();
    await u.selectOptions(within(settle).getByRole('combobox', { name: /Harm to the patient/ }), 'Moderate');
    await expectNoAxeViolations();
    await u.click(report);

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Count settled; incident NHIR2600017 raised for the quality team'),
    );
    expect(server.requests.find((r) => r.method === 'POST')?.body).toEqual({
      resolution: 'RetainedItem',
      note: 'Swab seen on X-ray; re-exploration',
      xrayResult: null,
      harm: 'Moderate',
    });
  });

  it('corrects an implant after the operation instead of removing it, and asks the OT in-charge', async () => {
    const mesh: SurgeryMaterialDto = {
      id: '0192a000-0000-7000-8000-0000000a1m01',
      kind: 'Implant',
      supplyItemId: null,
      itemName: 'Polypropylene mesh',
      manufacturer: 'Ethicon',
      lotNumber: 'PM2611',
      serialNumber: null,
      expiryDate: null,
      quantity: 2,
      unitPrice: 4500,
      amount: 9000,
      recordedByName: 'Sr. Anita Joseph',
      recordedAt: T,
      chargeStatus: 'Posted',
      chargeError: null,
      brand: 'Prolene',
      specification: 'Polypropylene, 15 x 15 cm',
      mrp: 5200,
      ceilingPrice: null,
    };
    server = fakeServer({
      [`GET /api/v1/ot/bookings/${BOOKING_ID}/materials`]: [mesh],
      [`POST /api/v1/ot/bookings/${BOOKING_ID}/materials/${mesh.id}/corrections`]: json(
        {
          id: '0192a000-0000-7000-8000-0000000a1a01',
          kind: 'ot.material_correction',
          summary: 'Polypropylene mesh: quantity 2 → 1',
        },
        202,
      ),
    });
    const u = userEvent.setup();
    show(<MaterialsPanel booking={{ ...BOOKING, status: 'InProgress', startedAt: T }} />, NURSE);

    await screen.findByText('Polypropylene mesh', {}, SLOW);
    expect(screen.queryByRole('button', { name: 'Remove Polypropylene mesh' })).toBeNull();
    await u.click(screen.getByRole('button', { name: 'Correct' }));
    const dialog = await screen.findByRole('dialog', { name: 'Correct Polypropylene mesh' });
    const quantity = within(dialog).getByRole('spinbutton', { name: /Quantity/ });
    await u.clear(quantity);
    await u.type(quantity, '1');
    await u.type(within(dialog).getByRole('textbox', { name: /Reason for the correction/ }), 'One piece used');
    await expectNoAxeViolations();
    await u.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Correction asked for', expect.stringContaining('OT in-charge')),
    );
    expect(server.requests.find((r) => r.method === 'POST')?.body).toEqual({
      lotNumber: 'PM2611',
      serialNumber: null,
      expiryDate: null,
      quantity: 1,
      reason: 'One piece used',
    });
  });
});
