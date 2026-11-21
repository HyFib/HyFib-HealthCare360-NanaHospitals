import type * as Core from '@hc360/core';
import { useSession, type AuthUser } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { otQueries, type SafetyPhaseDto, type SurgeryDetail, type SurgicalConsentDto } from './api';
import { PreOpChecklistPanel } from './ChecklistPanels';
import { SurgicalConsentPanel } from './ConsentPanel';

// Informed consent before surgery and anaesthesia, on the screens against a fake server: the theatre shows what the front
// office has recorded and what is missing, "consent signed" cannot be ticked without it, and for an emergency operation the
// operating surgeon goes ahead without it, with the clinical reason and a second doctor who agrees at the screen. The
// toasts are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const BOOKING_ID = '0192a000-0000-7000-8000-0000000c1001';
const SURGEON = '0192a000-0000-7000-8000-0000000c1d01';
const ANAESTHETIST = '0192a000-0000-7000-8000-0000000c1d02';
const T = '2026-11-21T04:00:00Z';
const REASON = 'Ruptured spleen after a road accident, in shock; no relative reachable';

const phases = (): SafetyPhaseDto[] => [
  { phase: 'SignIn', isComplete: false, completedAt: null, items: [] },
  { phase: 'TimeOut', isComplete: false, completedAt: null, items: [] },
  { phase: 'SignOut', isComplete: false, completedAt: null, items: [] },
];

/** Nothing recorded under the patient's consents. */
const MISSING: SurgicalConsentDto = {
  satisfied: false,
  recorded: false,
  since: '2026-11-20T10:00:00Z',
  checks: [
    { kind: 'Procedure', state: 'Missing', consent: null },
    { kind: 'Anaesthesia', state: 'Missing', consent: null },
  ],
  waiver: null,
  procedureConsentId: null,
  anaesthesiaConsentId: null,
  refused: false,
  anaesthesiaConsentRequiredAtSignIn: false,
  saidNo: false,
};

/** Nothing recorded for the operation; the patient withdrew the consent to anaesthesia given since the booking. */
const WITHDRAWN: SurgicalConsentDto = {
  ...MISSING,
  saidNo: true,
  checks: [
    { kind: 'Procedure', state: 'Missing', consent: null },
    {
      kind: 'Anaesthesia',
      state: 'Withdrawn',
      consent: {
        id: '0192a000-0000-7000-8000-0000000c1c02',
        kind: 'Anaesthesia',
        granted: true,
        capturedAt: T,
        revokedAt: T,
        capturedByName: 'Priya R',
        signedFormAttached: false,
        reliedOnBy: null,
      },
    },
  ],
};

/** Both consents recorded at the front office since the booking. */
const RECORDED: SurgicalConsentDto = {
  ...MISSING,
  satisfied: true,
  recorded: true,
  checks: [
    {
      kind: 'Procedure',
      state: 'Recorded',
      consent: {
        id: '0192a000-0000-7000-8000-0000000c1c03',
        kind: 'Procedure',
        granted: true,
        capturedAt: T,
        revokedAt: null,
        capturedByName: 'Priya R',
        signedFormAttached: true,
        reliedOnBy: null,
      },
    },
    {
      kind: 'Anaesthesia',
      state: 'Recorded',
      consent: {
        id: '0192a000-0000-7000-8000-0000000c1c04',
        kind: 'Anaesthesia',
        granted: true,
        capturedAt: T,
        revokedAt: null,
        capturedByName: 'Priya R',
        signedFormAttached: false,
        reliedOnBy: null,
      },
    },
  ],
};

/** The patient refused the operation after it was booked; the consent to anaesthesia is recorded. */
const REFUSED: SurgicalConsentDto = {
  ...RECORDED,
  satisfied: false,
  recorded: false,
  refused: true,
  saidNo: true,
  checks: [
    {
      kind: 'Procedure',
      state: 'Refused',
      consent: {
        id: '0192a000-0000-7000-8000-0000000c1c05',
        kind: 'Procedure',
        granted: false,
        capturedAt: T,
        revokedAt: null,
        capturedByName: 'Priya R',
        signedFormAttached: true,
        reliedOnBy: null,
      },
    },
    ...RECORDED.checks.filter((check) => check.kind === 'Anaesthesia'),
  ],
};

const BOOKING: SurgeryDetail = {
  id: BOOKING_ID,
  bookingNumber: 'NHOT2600077',
  patient: null,
  patientId: '0192a000-0000-7000-8000-0000000c1p01',
  admissionId: null,
  visitId: null,
  orderItemId: null,
  procedureServiceItemId: null,
  procedureCode: null,
  procedureName: 'Laparotomy',
  laterality: 'NotApplicable',
  diagnosis: 'Splenic injury',
  surgeonId: SURGEON,
  surgeonName: 'Dr. Arjun Rao',
  assistantIds: [],
  anaesthetistId: ANAESTHETIST,
  anaesthetistName: 'Dr. Kavita Shah',
  anaesthesiaType: 'GA',
  priority: 'Emergency',
  preferredDate: null,
  theatreId: null,
  theatreName: 'OT 1',
  scheduledStart: '2026-11-21T03:30:00Z',
  scheduledEnd: '2026-11-21T05:00:00Z',
  status: 'InPreOp',
  receivedInPreOpAt: T,
  inRoomAt: null,
  startedAt: null,
  completedAt: null,
  preOpComplete: false,
  preOpChecklist: [
    {
      key: 'consent',
      text: 'Informed consent for surgery and anaesthesia signed',
      allowsNotApplicable: false,
      state: 'Pending',
      remarks: null,
      recordedBy: null,
      recordedAt: null,
    },
    {
      key: 'npo',
      text: 'Nil-by-mouth (NPO) status confirmed and time of last intake recorded',
      allowsNotApplicable: false,
      state: 'Pending',
      remarks: null,
      recordedBy: null,
      recordedAt: null,
    },
  ],
  safetyChecklist: phases(),
  notes: null,
  cancelReason: null,
  postponeReason: null,
  postponeCount: 0,
  chargeStatus: 'NotDue',
  chargeError: null,
  version: 3,
  nurseId: null,
  nurseName: null,
  consent: MISSING,
};

const user = (practitionerId: string | null, permissions: string[]): AuthUser => ({
  ...STAFF_USER,
  roles: ['Doctor'],
  practitionerId,
  permissions: ['ot.read', ...permissions],
});
const SURGEON_USER = user(SURGEON, ['ot.bookings.manage', 'ot.checklists.record', 'ot.surgery.perform']);
const ANAESTHETIST_USER = user(ANAESTHETIST, ['ot.bookings.manage', 'ot.checklists.record', 'ot.surgery.perform']);
const NURSE = user(null, ['ot.checklists.record']);

let server: ReturnType<typeof fakeServer>;

/** The panel as the booking screen shows it: over the booking as last read from the server. */
function LoadedConsentPanel() {
  const booking = useQuery(otQueries.booking(BOOKING_ID));
  return booking.data ? <SurgicalConsentPanel booking={booking.data} /> : null;
}

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

describe('informed consent before surgery and anaesthesia', SCREEN_TESTS, () => {
  it('shows what the front office has not recorded and keeps "consent signed" from being ticked', async () => {
    server = fakeServer({});
    show(
      <>
        <SurgicalConsentPanel booking={{ ...BOOKING, priority: 'Elective' }} />
        <PreOpChecklistPanel booking={{ ...BOOKING, priority: 'Elective' }} canRecord />
      </>,
      NURSE,
    );

    const panel = await screen.findByRole('region', { name: 'Consent to the operation' }, SLOW);
    expect(within(panel).getByText('Consent to the operation (high-risk procedure): not recorded')).toBeInTheDocument();
    expect(within(panel).getByText('Consent to anaesthesia: not recorded')).toBeInTheDocument();
    expect(within(panel).getByText('Consent not recorded')).toBeInTheDocument();
    expect(within(panel).queryByRole('button', { name: /Go ahead without consent/ })).toBeNull();

    const consent = screen.getByRole('radiogroup', { name: 'Informed consent for surgery and anaesthesia signed' });
    expect(within(consent).getByRole('radio', { name: 'Done' })).toBeDisabled();
    expect(
      screen.getByText(/Ticked only once the patient’s consent is recorded at the front office/),
    ).toBeInTheDocument();
    const npo = screen.getByRole('radiogroup', { name: /Nil-by-mouth/ });
    expect(within(npo).getByRole('radio', { name: 'Done' })).toBeEnabled();
    await expectNoAxeViolations();
  });

  it('reads the booking again, when asked, once the front office has recorded the consent', async () => {
    let consent = MISSING;
    server = fakeServer({
      [`GET /api/v1/ot/bookings/${BOOKING_ID}`]: () => ({ ...BOOKING, priority: 'Elective', consent }),
    });
    const u = userEvent.setup();
    show(<LoadedConsentPanel />, NURSE);

    const panel = await screen.findByRole('region', { name: 'Consent to the operation' }, SLOW);
    expect(within(panel).getByText('Consent not recorded')).toBeInTheDocument();

    // The front office records both consents on its own screen meanwhile; the theatre asks again.
    consent = RECORDED;
    await u.click(within(panel).getByRole('button', { name: 'Check again' }));

    await waitFor(() => expect(within(panel).queryByText('Consent not recorded')).toBeNull());
    expect(within(panel).getByText(/^Consent to the operation \(high-risk procedure\): recorded/)).toBeInTheDocument();
    expect(server.requests.filter((r) => r.method === 'GET' && r.path.endsWith(BOOKING_ID))).toHaveLength(2);
  });

  it('lets the operating surgeon of an emergency operation go ahead without consent, a second doctor agreeing at the screen', async () => {
    server = fakeServer({
      'POST /api/v1/auth/witness': {
        confirmationId: '0192a000-0000-7000-8000-0000000c1w01',
        userId: '0192a000-0000-7000-8000-0000000c1u02',
        name: 'Dr. Kavita Shah',
        expiresAt: '2026-11-21T04:05:00Z',
      },
      [`POST /api/v1/ot/bookings/${BOOKING_ID}/consent-waiver`]: null,
    });
    const u = userEvent.setup();
    show(<SurgicalConsentPanel booking={BOOKING} />, SURGEON_USER);

    await u.click(await screen.findByRole('button', { name: 'Go ahead without consent (emergency)' }, SLOW));
    const form = screen.getByRole('group', { name: 'Go ahead without consent' });
    expect(within(form).getByText('The emergency doctrine')).toBeInTheDocument();
    const go = within(form).getByRole('button', { name: 'Go ahead without consent (second doctor first)' });
    expect(go).toBeDisabled();

    const reason = within(form).getByRole('textbox', { name: /Why the operation cannot wait for consent/ });
    await u.type(reason, 'Shock');
    expect(within(form).getByText(/Give the clinical reason \(at least 10 characters\)/)).toBeInTheDocument();
    await u.clear(reason);
    await u.type(reason, REASON);

    await u.type(within(form).getByRole('textbox', { name: /Doctor's user name/ }), 'kavita');
    await u.type(within(form).getByLabelText(/Doctor's password/), 'not-a-real-password');
    await u.click(within(form).getByRole('button', { name: 'Confirm doctor' }));
    expect(await within(form).findByText('Dr. Kavita Shah')).toBeInTheDocument();
    await expectNoAxeViolations();

    await u.click(within(form).getByRole('button', { name: 'Go ahead without consent' }));
    await waitFor(() => expect(notify.warning).toHaveBeenCalledWith('Going ahead without consent', expect.any(String)));
    expect(server.requests.find((r) => r.path.endsWith('/witness'))?.body).toMatchObject({
      purpose: 'ot.consent_waiver',
      patientId: BOOKING.patientId,
    });
    expect(server.requests.find((r) => r.path.endsWith('/consent-waiver'))?.body).toEqual({
      reason: REASON,
      concurringDoctorConfirmationId: '0192a000-0000-7000-8000-0000000c1w01',
    });
  });

  it('leaves the emergency path to the operating surgeon', async () => {
    server = fakeServer({});
    show(<SurgicalConsentPanel booking={BOOKING} />, ANAESTHETIST_USER);

    const panel = await screen.findByRole('region', { name: 'Consent to the operation' }, SLOW);
    expect(within(panel).queryByRole('button', { name: /Go ahead without consent/ })).toBeNull();
    expect(
      within(panel).getByText(/the operating surgeon \(Dr. Arjun Rao\) may go ahead without consent/),
    ).toBeInTheDocument();
  });

  it('reads nothing back over an operation under way: its details say what the incision relied on', async () => {
    server = fakeServer({});
    const underWay: SurgeryDetail = { ...BOOKING, status: 'InProgress', startedAt: T, preOpComplete: true };
    show(
      <>
        <SurgicalConsentPanel booking={underWay} />
        <PreOpChecklistPanel booking={underWay} canRecord />
      </>,
      NURSE,
    );

    await screen.findByRole('list', { name: 'Pre-operative checklist' }, SLOW);
    expect(screen.queryByRole('region', { name: 'Consent to the operation' })).toBeNull();
    expect(screen.queryByText(/Ticked only once the patient’s consent is recorded/)).toBeNull();
  });

  it('says who recorded each consent and whether the signed form is attached', async () => {
    server = fakeServer({});
    show(<SurgicalConsentPanel booking={{ ...BOOKING, priority: 'Elective', consent: RECORDED }} />, NURSE);

    const panel = await screen.findByRole('region', { name: 'Consent to the operation' }, SLOW);
    expect(within(panel).getByText('Recorded by Priya R; signed form attached')).toBeInTheDocument();
    expect(within(panel).getByText('Recorded by Priya R; no signed form attached')).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('says a consent another operation relied on is taken again for this one', async () => {
    server = fakeServer({});
    const [procedure, anaesthesia] = RECORDED.checks;
    const reliedOn: SurgicalConsentDto = {
      ...RECORDED,
      satisfied: false,
      recorded: false,
      checks: [
        {
          kind: 'Procedure',
          state: 'OtherOperation',
          consent: procedure?.consent ? { ...procedure.consent, reliedOnBy: 'NHOT2600012' } : null,
        },
        ...(anaesthesia ? [anaesthesia] : []),
      ],
    };
    show(<SurgicalConsentPanel booking={{ ...BOOKING, priority: 'Elective', consent: reliedOn }} />, NURSE);

    const panel = await screen.findByRole('region', { name: 'Consent to the operation' }, SLOW);
    expect(
      within(panel).getByText(/relied on by another operation \(NHOT2600012\): take it again for this operation$/),
    ).toBeInTheDocument();
    expect(within(panel).getByText('Consent not recorded')).toBeInTheDocument();
  });

  it('warns of a refusal recorded before the booking, on the panel and above the reason of the waiver form', async () => {
    const earlier: SurgicalConsentDto = {
      ...MISSING,
      checks: [
        {
          kind: 'Procedure',
          state: 'Missing',
          consent: null,
          earlierRefusal: {
            consentId: '0192a000-0000-7000-8000-0000000c1c06',
            recordedAt: '2026-11-20T04:00:00Z',
            recordedByName: 'Dr. Meera Iyer',
          },
        },
        MISSING.checks[1]!,
      ],
    };
    const warning =
      /The patient refused this on 20 Nov 2026, .* \(recorded by Dr\. Meera Iyer\), before this operation was booked\./;
    server = fakeServer({});
    const u = userEvent.setup();
    show(<SurgicalConsentPanel booking={{ ...BOOKING, consent: earlier }} />, SURGEON_USER);

    const panel = await screen.findByRole('region', { name: 'Consent to the operation' }, SLOW);
    expect(within(panel).getAllByText(warning)).toHaveLength(1);
    // It blocks nothing: the line still says not recorded and the surgeon can still open the waiver.
    expect(
      within(panel).getByText(/^Consent to the operation \(high-risk procedure\): not recorded/),
    ).toBeInTheDocument();
    await expectNoAxeViolations();
    await u.click(within(panel).getByRole('button', { name: 'Go ahead without consent (emergency)' }));
    const form = screen.getByRole('group', { name: 'Go ahead without consent' });
    expect(within(form).getByText('Refused before this operation was booked')).toBeInTheDocument();
    expect(within(form).getByText(warning)).toBeInTheDocument();
  });

  it('shows no earlier-refusal warning when there is none', async () => {
    server = fakeServer({});
    show(<SurgicalConsentPanel booking={BOOKING} />, SURGEON_USER);
    const panel = await screen.findByRole('region', { name: 'Consent to the operation' }, SLOW);
    expect(within(panel).queryByText(/The patient refused this on/)).toBeNull();
  });

  it('never offers the emergency path against the patient’s refusal, and says why', async () => {
    server = fakeServer({});
    show(<SurgicalConsentPanel booking={{ ...BOOKING, consent: REFUSED }} />, SURGEON_USER);

    const panel = await screen.findByRole('region', { name: 'Consent to the operation' }, SLOW);
    expect(
      within(panel).getByText(/^Consent to the operation \(high-risk procedure\): refused by the patient/),
    ).toBeInTheDocument();
    expect(within(panel).getByText('Consent refused by the patient')).toBeInTheDocument();
    expect(within(panel).getByText(/an emergency does not override a refusal/)).toBeInTheDocument();
    expect(within(panel).queryByRole('button', { name: /Go ahead without consent/ })).toBeNull();
    expect(within(panel).queryByText(/may go ahead without consent/)).toBeNull();
    await expectNoAxeViolations();
  });

  it.each([
    ['refusal', REFUSED, 'Consent refused by the patient'],
    ['withdrawal', WITHDRAWN, 'Consent withdrawn by the patient'],
  ])(
    'shows a %s on the waiver form when the front office records it meanwhile, and holds the waiver',
    async (_, no, title) => {
      let consent = MISSING;
      server = fakeServer({
        [`GET /api/v1/ot/bookings/${BOOKING_ID}`]: () => ({ ...BOOKING, consent }),
        'POST /api/v1/auth/witness': {
          confirmationId: '0192a000-0000-7000-8000-0000000c1w01',
          userId: '0192a000-0000-7000-8000-0000000c1u02',
          name: 'Dr. Kavita Shah',
          expiresAt: '2026-11-21T04:05:00Z',
        },
      });
      const u = userEvent.setup();
      show(<LoadedConsentPanel />, SURGEON_USER);

      await u.click(await screen.findByRole('button', { name: 'Go ahead without consent (emergency)' }, SLOW));
      const form = screen.getByRole('group', { name: 'Go ahead without consent' });
      await u.type(within(form).getByRole('textbox', { name: /Why the operation cannot wait for consent/ }), REASON);
      await u.type(within(form).getByRole('textbox', { name: /Doctor's user name/ }), 'kavita');
      await u.type(within(form).getByLabelText(/Doctor's password/), 'not-a-real-password');
      await u.click(within(form).getByRole('button', { name: 'Confirm doctor' }));
      expect(await within(form).findByText('Dr. Kavita Shah')).toBeInTheDocument();
      expect(within(form).getByRole('button', { name: 'Go ahead without consent' })).toBeEnabled();

      // The patient comes round and says no; the front office records it, and the theatre asks again.
      consent = no;
      await u.click(screen.getByRole('button', { name: 'Check again' }));

      expect(await within(form).findByText(title)).toBeInTheDocument();
      expect(within(form).getByRole('button', { name: 'Go ahead without consent' })).toBeDisabled();
      expect(server.requests.some((r) => r.path.endsWith('/consent-waiver'))).toBe(false);
    },
  );

  it('never offers the emergency path against a consent the patient withdrew, and says why', async () => {
    server = fakeServer({});
    show(
      <>
        <SurgicalConsentPanel booking={{ ...BOOKING, consent: WITHDRAWN }} />
        <PreOpChecklistPanel booking={{ ...BOOKING, consent: WITHDRAWN }} canRecord />
      </>,
      SURGEON_USER,
    );

    const panel = await screen.findByRole('region', { name: 'Consent to the operation' }, SLOW);
    expect(within(panel).getByText(/^Consent to anaesthesia: withdrawn/)).toBeInTheDocument();
    expect(within(panel).getByText('Consent withdrawn by the patient')).toBeInTheDocument();
    expect(within(panel).getByText(/an emergency does not override a withdrawal/)).toBeInTheDocument();
    expect(within(panel).queryByRole('button', { name: /Go ahead without consent/ })).toBeNull();
    expect(within(panel).queryByText(/may go ahead without consent/)).toBeNull();
    expect(
      screen.getByText('The patient withdrew consent: ticked only once a new consent is recorded at the front office.'),
    ).toBeInTheDocument();
    await expectNoAxeViolations();
  });

  it('says a waiver no longer stands once the patient withdraws consent, and keeps "consent signed" from being ticked', async () => {
    server = fakeServer({});
    // Waived while the patient could not consent; he came round and withdrew the consent he then gave.
    const waivedThenWithdrawn: SurgicalConsentDto = {
      ...WITHDRAWN,
      waiver: {
        at: T,
        surgeonUserId: '0192a000-0000-7000-8000-0000000c1u01',
        surgeonName: 'Dr. Arjun Rao',
        concurringUserId: '0192a000-0000-7000-8000-0000000c1u02',
        concurringName: 'Dr. Kavita Shah',
        reason: REASON,
      },
    };
    show(
      <>
        <SurgicalConsentPanel booking={{ ...BOOKING, consent: waivedThenWithdrawn }} />
        <PreOpChecklistPanel booking={{ ...BOOKING, consent: waivedThenWithdrawn }} canRecord />
      </>,
      NURSE,
    );

    const panel = await screen.findByRole('region', { name: 'Consent to the operation' }, SLOW);
    expect(within(panel).getByText(/Waived by the operating surgeon Dr. Arjun Rao/)).toHaveTextContent(
      'The patient has withdrawn consent since: an emergency does not override a withdrawal, so the waiver no longer stands.',
    );
    expect(within(panel).getByText('Consent withdrawn by the patient')).toBeInTheDocument();
    const consent = screen.getByRole('radiogroup', { name: 'Informed consent for surgery and anaesthesia signed' });
    expect(within(consent).getByRole('radio', { name: 'Done' })).toBeDisabled();
    expect(screen.queryByText(/Consent was waived in an emergency by the operating surgeon/)).toBeNull();
  });

  it('says a withdrawal since the booking of a consent recorded before it', async () => {
    server = fakeServer({});
    // Recorded the day before this operation was booked, withdrawn since: the withdrawal stops this operation too.
    const [, withdrawnCheck] = WITHDRAWN.checks;
    const older: SurgicalConsentDto = {
      ...WITHDRAWN,
      checks: [
        { kind: 'Procedure', state: 'Missing', consent: null },
        {
          kind: 'Anaesthesia',
          state: 'Withdrawn',
          consent: withdrawnCheck?.consent ? { ...withdrawnCheck.consent, capturedAt: '2026-11-19T10:00:00Z' } : null,
        },
      ],
    };
    show(<SurgicalConsentPanel booking={{ ...BOOKING, priority: 'Elective', consent: older }} />, NURSE);

    const panel = await screen.findByRole('region', { name: 'Consent to the operation' }, SLOW);
    expect(
      within(panel).getByText(
        /^Consent to anaesthesia: withdrawn .+ \(recorded .+, before this operation was booked\)$/,
      ),
    ).toBeInTheDocument();
  });

  it('says the consent to anaesthesia stays needed after a switch to local anaesthesia', async () => {
    server = fakeServer({});
    const [procedure] = RECORDED.checks;
    const heldAtSignIn: SurgicalConsentDto = {
      ...RECORDED,
      satisfied: false,
      recorded: false,
      anaesthesiaConsentRequiredAtSignIn: true,
      checks: [...(procedure ? [procedure] : []), { kind: 'Anaesthesia', state: 'Missing', consent: null }],
    };
    show(
      <SurgicalConsentPanel
        booking={{ ...BOOKING, priority: 'Elective', anaesthesiaType: 'Local', consent: heldAtSignIn }}
      />,
      NURSE,
    );

    const panel = await screen.findByRole('region', { name: 'Consent to the operation' }, SLOW);
    expect(within(panel).getByText('Consent to anaesthesia: not recorded')).toBeInTheDocument();
    expect(
      within(panel).getByText('Still needed: the WHO Sign-in was done under anaesthesia given by the anaesthetist.'),
    ).toBeInTheDocument();
  });

  it('shows a waiver with the surgeon, the doctor who agreed and the reason', async () => {
    server = fakeServer({});
    const waived: SurgicalConsentDto = {
      ...MISSING,
      satisfied: true,
      waiver: {
        at: T,
        surgeonUserId: '0192a000-0000-7000-8000-0000000c1u01',
        surgeonName: 'Dr. Arjun Rao',
        concurringUserId: '0192a000-0000-7000-8000-0000000c1u02',
        concurringName: 'Dr. Kavita Shah',
        reason: REASON,
      },
    };
    show(
      <>
        <SurgicalConsentPanel booking={{ ...BOOKING, consent: waived }} />
        <PreOpChecklistPanel booking={{ ...BOOKING, consent: waived }} canRecord />
      </>,
      NURSE,
    );

    const panel = await screen.findByRole('region', { name: 'Consent to the operation' }, SLOW);
    expect(within(panel).getByText('Going ahead without consent (emergency)')).toBeInTheDocument();
    expect(within(panel).getByText(/Waived by the operating surgeon Dr. Arjun Rao/)).toHaveTextContent(
      `Dr. Kavita Shah agreeing at the surgeon's screen. Reason: ${REASON}`,
    );
    expect(within(panel).queryByText('Consent not recorded')).toBeNull();
    const consent = screen.getByRole('radiogroup', { name: 'Informed consent for surgery and anaesthesia signed' });
    expect(within(consent).getByRole('radio', { name: 'Done' })).toBeEnabled();
    await expectNoAxeViolations();
  });
});
