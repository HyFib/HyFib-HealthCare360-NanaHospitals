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
import type { RecoveryRecordDto, RecoveryScore, SafetyPhaseDto, SurgeryDetail } from './api';
import { RecoveryPanel } from './RecoveryPanel';

// The recovery room (PACU) tab against a fake server: the nurse receives the patient after the WHO Sign-out, the
// anaesthetist hands over at her screen, the score says what is missing, the ward waits below 9 and the anaesthetist's
// override needs a reason; the ICU takes the patient at any score. The toasts are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const BOOKING_ID = '0192a000-0000-7000-8000-0000000b1001';
const RECOVERY = `/api/v1/ot/bookings/${BOOKING_ID}/recovery`;
const T = '2026-11-08T05:00:00Z';

const phases = (signOut: boolean): SafetyPhaseDto[] => [
  { phase: 'SignIn', isComplete: true, completedAt: T, items: [] },
  { phase: 'TimeOut', isComplete: true, completedAt: T, items: [] },
  { phase: 'SignOut', isComplete: signOut, completedAt: signOut ? T : null, items: [] },
];

const BOOKING: SurgeryDetail = {
  id: BOOKING_ID,
  bookingNumber: 'NHOT2600077',
  patient: null,
  patientId: '0192a000-0000-7000-8000-0000000b1p01',
  admissionId: null,
  visitId: null,
  orderItemId: null,
  procedureServiceItemId: null,
  procedureCode: null,
  procedureName: 'Inguinal hernia repair',
  laterality: 'Right',
  diagnosis: null,
  surgeonId: null,
  surgeonName: 'Dr. Arjun Rao',
  assistantIds: [],
  anaesthetistId: null,
  anaesthetistName: 'Dr. Kavita Shah',
  nurseId: null,
  nurseName: null,
  anaesthesiaType: 'Spinal',
  priority: 'Elective',
  preferredDate: null,
  theatreId: null,
  theatreName: 'OT 1',
  scheduledStart: T,
  scheduledEnd: T,
  status: 'Completed',
  receivedInPreOpAt: T,
  inRoomAt: T,
  startedAt: T,
  completedAt: T,
  preOpComplete: true,
  preOpChecklist: [],
  safetyChecklist: phases(true),
  notes: null,
  cancelReason: null,
  postponeReason: null,
  postponeCount: 0,
  chargeStatus: 'Posted',
  chargeError: null,
  version: 12,
  inRecovery: true,
};

const score = (total: number | null, wardBlockedBy: string | null, missing: string[] = []): RecoveryScore => ({
  components: [
    { sign: 'Activity', points: missing.length ? null : 2, missing: missing[0] ?? null },
    { sign: 'Respiration', points: 2, missing: null },
    { sign: 'Circulation', points: 2, missing: null },
    { sign: 'Consciousness', points: total === 8 ? 0 : 2, missing: null },
    { sign: 'OxygenSaturation', points: 2, missing: null },
  ],
  total,
  at: T,
  missing,
  meetsWardDischarge: (total ?? 0) >= 9,
  wardBlockedBy,
});

const RECORD: RecoveryRecordDto = {
  id: '0192a000-0000-7000-8000-0000000b1r01',
  surgeryBookingId: BOOKING_ID,
  patientId: BOOKING.patientId,
  receivedAt: T,
  receivedByName: 'Sr. Leela Thomas',
  airway: 'None',
  oxygen: 'FaceMask',
  oxygenLitresPerMinute: 6,
  preAnaesthesiaSystolic: 126,
  notes: null,
  handoverRequired: true,
  handoverAt: null,
  handedOverByName: null,
  handoverConfirmedAtScreen: false,
  handoverNotes: null,
  observations: [],
  score: { ...score(null, 'No observations are recorded yet.'), at: null },
  dischargedAt: null,
  destination: null,
  dischargeScore: null,
  dischargedByName: null,
  overrideReason: null,
  overrideByName: null,
  version: 1,
};

const user = (permissions: string[]): AuthUser => ({
  ...STAFF_USER,
  roles: ['Nurse'],
  practitionerId: null,
  permissions: ['ot.read', ...permissions],
});
const NURSE = user(['ot.checklists.record']);
const ANAESTHETIST = user(['ot.checklists.record', 'ot.anaesthesia.record']);

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

function show(booking: SurgeryDetail, signedIn: AuthUser) {
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
      <Hc360ThemeProvider>
        <RecoveryPanel booking={booking} />
      </Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

describe('the recovery room', SCREEN_TESTS, () => {
  it('waits for the WHO Sign-out, then lets the nurse receive the patient', async () => {
    server = fakeServer({});
    show({ ...BOOKING, status: 'InProgress', safetyChecklist: phases(false), inRecovery: false }, NURSE);
    expect(
      await screen.findByText('The patient comes to recovery after the WHO Sign-out.', {}, SLOW),
    ).toBeInTheDocument();
    cleanup();

    server = fakeServer({ [`POST ${RECOVERY}/receive`]: RECORD });
    const u = userEvent.setup();
    show(BOOKING, NURSE);
    const form = await screen.findByRole('group', { name: 'Receive the patient in recovery' }, SLOW);
    await u.type(within(form).getByRole('spinbutton', { name: /Litres per minute/ }), '6');
    await u.type(within(form).getByRole('spinbutton', { name: /Systolic before anaesthesia/ }), '126');
    await expectNoAxeViolations();
    await u.click(within(form).getByRole('button', { name: 'Receive in recovery' }));

    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Patient received in recovery'));
    expect(server.requests.find((r) => r.path.endsWith('/receive'))?.body).toEqual({
      airway: 'None',
      oxygen: 'FaceMask',
      oxygenLitresPerMinute: 6,
      preAnaesthesiaSystolic: 126,
    });
    expect(await screen.findByText('Sr. Leela Thomas')).toBeInTheDocument();
  });

  it('records the anaesthetist’s handover confirmed at the nurse’s screen, and an observation', async () => {
    server = fakeServer({
      [`GET ${RECOVERY}`]: RECORD,
      'POST /api/v1/auth/witness': {
        confirmationId: '0192a000-0000-7000-8000-0000000b1w01',
        userId: '0192a000-0000-7000-8000-0000000b1u02',
        name: 'Dr. Kavita Shah',
        expiresAt: '2026-11-08T05:05:00Z',
      },
      [`POST ${RECOVERY}/handover`]: {
        ...RECORD,
        handoverAt: T,
        handedOverByName: 'Dr. Kavita Shah',
        handoverConfirmedAtScreen: true,
      },
      [`POST ${RECOVERY}/observations`]: RECORD,
    });
    const u = userEvent.setup();
    show(BOOKING, NURSE);

    const handover = await screen.findByRole('group', { name: 'Record the handover' }, SLOW);
    expect(within(handover).getByRole('button', { name: 'Record the handover (anaesthetist first)' })).toBeDisabled();
    await u.type(within(handover).getByRole('textbox', { name: /Anaesthetist's user name/ }), 'kavita');
    await u.type(within(handover).getByLabelText(/Anaesthetist's password/), 'not-a-real-password');
    await u.click(within(handover).getByRole('button', { name: /Confirm/ }));
    expect(await within(handover).findByText('Dr. Kavita Shah')).toBeInTheDocument();
    await expectNoAxeViolations();
    await u.click(within(handover).getByRole('button', { name: 'Record the handover' }));
    expect(
      await screen.findByText(/Handed over by Dr. Kavita Shah at .*, confirmed at the recovery nurse’s screen/),
    ).toBeInTheDocument();
    expect(server.requests.find((r) => r.path.endsWith('/witness'))?.body).toMatchObject({
      purpose: 'ot.recovery_handover',
      patientId: BOOKING.patientId,
    });
    expect(server.requests.find((r) => r.path.endsWith('/handover'))?.body).toEqual({
      witnessConfirmationId: '0192a000-0000-7000-8000-0000000b1w01',
      notes: null,
    });

    const chart = screen.getByRole('group', { name: 'Record observations' });
    await u.type(within(chart).getByRole('spinbutton', { name: /SpO₂/ }), '97');
    await u.selectOptions(within(chart).getByRole('combobox', { name: 'Consciousness' }), 'Alert');
    await u.type(within(chart).getByRole('spinbutton', { name: /Limbs moved/ }), '4');
    await u.click(within(chart).getByRole('button', { name: 'Record observations' }));
    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Observations recorded'));
    expect(server.requests.find((r) => r.path.endsWith('/observations'))?.body).toMatchObject({
      spO2: 97,
      onOxygen: false,
      consciousness: 'Alert',
      limbsMoving: 4,
      heartRate: null,
      breathing: null,
    });
  });

  it('shows what the score is missing and keeps the ward from the nurse below it', async () => {
    server = fakeServer({
      [`GET ${RECOVERY}`]: {
        ...RECORD,
        handoverAt: T,
        handedOverByName: 'Dr. Kavita Shah',
        score: score(null, 'The score cannot be given yet: record how many limbs the patient moves on command.', [
          'how many limbs the patient moves on command',
        ]),
      },
    });
    show(BOOKING, NURSE);

    expect(await screen.findByText(/^Not complete/, {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('Missing: how many limbs the patient moves on command')).toBeInTheDocument();
    expect(screen.getByText('Not yet fit for the ward on the score')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Discharge to the Ward' })).toBeDisabled();
    expect(screen.queryByRole('group', { name: 'Anaesthetist’s override' })).toBeNull();
    await expectNoAxeViolations();
  });

  it('lets the anaesthetist send the patient to the ward below the score with a reason, and the ICU at any score', async () => {
    const below = {
      ...RECORD,
      handoverAt: T,
      handedOverByName: 'Dr. Kavita Shah',
      score: score(8, 'The modified Aldrete score is 8 of 10.'),
    };
    server = fakeServer({
      [`GET ${RECOVERY}`]: below,
      [`POST ${RECOVERY}/discharge/override`]: {
        ...below,
        dischargedAt: T,
        destination: 'Ward',
        dischargeScore: 8,
        dischargedByName: 'Dr. Kavita Shah',
        overrideReason: 'Spinal regressing',
        overrideByName: 'Dr. Kavita Shah',
      },
    });
    const u = userEvent.setup();
    show(BOOKING, ANAESTHETIST);

    expect(await screen.findByText(/^8 of 10/, {}, SLOW)).toBeInTheDocument();
    const override = screen.getByRole('group', { name: 'Anaesthetist’s override' });
    const send = within(override).getByRole('button', { name: 'Send to the ward below the score' });
    expect(send).toBeDisabled();
    await u.type(within(override).getByRole('textbox', { name: /Reason/ }), 'Spinal regressing');
    await expectNoAxeViolations();

    // The ICU takes the patient at any score: the plain discharge is open again, and the override is not offered.
    await u.selectOptions(screen.getByRole('combobox', { name: 'To' }), 'Icu');
    expect(screen.getByRole('button', { name: 'Discharge to the ICU' })).toBeEnabled();
    expect(screen.queryByRole('group', { name: 'Anaesthetist’s override' })).toBeNull();
    await u.selectOptions(screen.getByRole('combobox', { name: 'To' }), 'Ward');

    await u.click(
      within(screen.getByRole('group', { name: 'Anaesthetist’s override' })).getByRole('button', {
        name: 'Send to the ward below the score',
      }),
    );
    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Discharged from recovery to the Ward'));
    expect(server.requests.find((r) => r.path.endsWith('/override'))?.body).toEqual({ reason: 'Spinal regressing' });
    expect(await screen.findByText(/Override by Dr. Kavita Shah: Spinal regressing/)).toBeInTheDocument();
  });
});
