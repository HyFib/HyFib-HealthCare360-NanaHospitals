import type * as Core from '@hc360/core';
import { useSession, type AuthUser } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import type * as Router from '@tanstack/react-router';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { DeathRecordDto } from '@/modules/mrd/vital';
import { againstAdviceMissing, EMPTY_AGAINST_ADVICE } from '@/modules/witness/againstAdvice';
import type { FakeRequest } from '@/test/fakeApi';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { json } from '@/test/fakeApi';
import { ANSWERS } from '@/test/roleAccess/births-deaths';
import { RECORD_ID } from '@/test/roleAccessIds';
import {
  dischargeWaitsForApproval,
  isApprovalRequest,
  type AdmissionDetail,
  type DischargeDeathCertificateDto,
} from './api';
import { CompleteDischargeDialog } from './DischargeDialogs';

// Discharge and death where a second person decides (maker-checker, slice 8), on the screens against a fake server: LAMA
// and DAMA record who signed, the doctor who explained the risks and a witness, each confirming with their own
// credentials; an absconded stay is asked for with the report and waits for the nurse in charge; a death closes with the
// certificate a second doctor confirmed, or, medico-legal and pending post-mortem, is asked for; voiding a confirmed
// death certificate asks a second doctor. The toasts are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
vi.mock('@tanstack/react-router', async (importOriginal) => ({
  ...(await importOriginal<typeof Router>()),
  useNavigate: () => vi.fn(),
}));

const ADMISSION_ID = '0192a000-0000-7000-8000-00000000f801';
const PATIENT_ID = '0192a000-0000-7000-8000-00000000f802';
const STAY = {
  id: ADMISSION_ID,
  admissionNumber: 'IP26000123',
  patientId: PATIENT_ID,
  patientName: 'Sunil Deshmukh',
  status: 'DischargeInitiated',
  isMlc: false,
  mlcNumber: null,
  expectedDischargeDate: null,
  pendingDischarge: null,
  version: 9,
} as unknown as AdmissionDetail;

const NURSE: AuthUser = {
  ...STAFF_USER,
  displayName: 'Sister Anita',
  practitionerId: null,
  roles: ['Nurse'],
  permissions: ['inpatient.admissions.read', 'inpatient.admissions.discharge', 'nursing.chart', 'files.write'],
};
const DOCTOR: AuthUser = {
  ...STAFF_USER,
  roles: ['Doctor'],
  permissions: ['mrd.deaths.certify', 'mrd.deaths.confirm', 'mrd.register', 'clinical.encounters.sign'],
};

const DOCTOR_CONFIRMED = {
  confirmationId: '0192a000-0000-7000-8000-00000000f8c1',
  userId: 'u-rao',
  name: 'Dr. Anil Rao',
  expiresAt: '2099-01-01T00:00:00Z',
};
const WITNESS_CONFIRMED = {
  confirmationId: '0192a000-0000-7000-8000-00000000f8c2',
  userId: 'u-mary',
  name: 'Sister Mary',
  expiresAt: '2099-01-01T00:00:00Z',
};
const witness = (request: FakeRequest) =>
  (request.body as { purpose: string }).purpose === 'against_advice.doctor' ? DOCTOR_CONFIRMED : WITNESS_CONFIRMED;

const asked = (kind: string, summary: string) =>
  json({ id: '0192a000-0000-7000-8000-00000000f8a1', kind, summary, canDecide: false }, 202);
const certificate = (over: Partial<DischargeDeathCertificateDto>): DischargeDeathCertificateDto => ({
  admissionId: ADMISSION_ID,
  deathId: null,
  serialNumber: null,
  settled: false,
  forThisStay: false,
  confirmedBy: null,
  confirmationWaitingSince: null,
  medicoLegalStay: false,
  status: 'Certify the cause of death (MCCD, Form 4) first.',
  ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function show(node: ReactNode, user: AuthUser) {
  useSession.setState({
    status: 'authenticated',
    accessToken: 'test-token',
    expiresAt: Date.now() + 3600_000,
    user,
    locked: false,
  });
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={queryClient}>
      <Hc360ThemeProvider>{node}</Hc360ThemeProvider>
    </QueryClientProvider>,
  );
}

const confirm = async (
  user: ReturnType<typeof userEvent.setup>,
  within_: HTMLElement,
  who: 'Doctor' | 'Witness',
  name: string,
) => {
  await user.type(within(within_).getByRole('textbox', { name: new RegExp(`^${who}'s user name`) }), name);
  await user.type(within(within_).getByLabelText(new RegExp(`^${who}'s password`)), 'not-a-real-password');
  await user.click(within(within_).getByRole('button', { name: `Confirm ${who.toLowerCase()}` }));
};

describe('discharge and death where a second person decides', SCREEN_TESTS, () => {
  it("records DAMA by a nurse only with the doctor's confirmation and a witness", async () => {
    const server = fakeServer({
      [`GET /api/v1/inpatient/admissions/${ADMISSION_ID}/discharge/clearance`]: {
        admissionId: ADMISSION_ID,
        cleared: true,
        outstanding: 0,
        reason: null,
      },
      'POST /api/v1/auth/witness': witness,
      [`POST /api/v1/inpatient/admissions/${ADMISSION_ID}/discharge/complete`]: {
        admissionId: ADMISSION_ID,
        status: 'Dama',
        dischargeInitiatedAt: null,
        dischargedAt: '2026-11-08T06:00:00Z',
        chargesPosted: 0,
      },
    });
    const user = userEvent.setup();
    show(<CompleteDischargeDialog admission={STAY} open onClose={() => undefined} />, NURSE);

    const dialog = await screen.findByRole('dialog', {}, SLOW);
    await user.click(within(dialog).getByRole('radio', { name: /Discharged against medical advice \(DAMA\)/ }));
    await user.click(within(dialog).getByRole('radio', { name: 'Refused to sign' }));
    await user.type(
      within(dialog).getByRole('textbox', { name: /^Who was informed/ }),
      'Family wants to take him home',
    );

    // Not sent until the doctor and the witness have confirmed.
    await user.click(within(dialog).getByRole('button', { name: 'Complete discharge' }));
    expect(await within(dialog).findByText('The declaration is not complete')).toBeInTheDocument();
    expect(within(dialog).getByText(/The doctor who explained the risks confirms/)).toBeInTheDocument();
    expect(server.requests.some((r) => r.path.endsWith('/discharge/complete'))).toBe(false);

    await confirm(user, dialog, 'Doctor', 'anil.rao');
    expect(await within(dialog).findByText('Dr. Anil Rao')).toBeInTheDocument();
    await confirm(user, dialog, 'Witness', 'mary');
    expect(await within(dialog).findByText('Sister Mary')).toBeInTheDocument();
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Complete discharge' }));

    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Discharge completed', expect.any(String)));
    const witnessed = server.requests
      .filter((r) => r.path === '/api/v1/auth/witness')
      .map((r) => (r.body as { purpose: string }).purpose);
    expect(witnessed).toEqual(['against_advice.doctor', 'discharge.against_advice']);
    expect(server.requests.find((r) => r.path.endsWith('/discharge/complete'))?.body).toMatchObject({
      dischargeType: 'Dama',
      againstAdvice: {
        signedBy: 'RefusedToSign',
        signatoryName: null,
        doctorConfirmationId: DOCTOR_CONFIRMED.confirmationId,
        witnessConfirmationId: WITNESS_CONFIRMED.confirmationId,
      },
    });
  });

  it('asks the nurse in charge to close an absconded medico-legal stay, with the police told', async () => {
    const stay = { ...STAY, status: 'Admitted', isMlc: true, mlcNumber: 'MLC/26/0099' } as AdmissionDetail;
    const server = fakeServer({
      [`GET /api/v1/inpatient/admissions/${ADMISSION_ID}/discharge/death-certificate`]: certificate({
        medicoLegalStay: true,
      }),
      [`POST /api/v1/inpatient/admissions/${ADMISSION_ID}/discharge/complete`]: asked(
        'inpatient.absconded',
        'IP26000123: close the stay as absconded',
      ),
    });
    const user = userEvent.setup();
    show(<CompleteDischargeDialog admission={stay} open onClose={() => undefined} />, NURSE);

    const dialog = await screen.findByRole('dialog', {}, SLOW);
    await user.click(within(dialog).getByRole('radio', { name: /^Absconded/ }));
    expect(within(dialog).getByText('Medico-legal case')).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox', { name: /^Who searched/ }), 'Ward staff and security');
    await user.type(within(dialog).getByRole('textbox', { name: /^Police station/ }), 'Adyar PS');
    await user.type(within(dialog).getByRole('textbox', { name: /^Police reference/ }), 'GD 114/26');
    await user.type(
      within(dialog).getByRole('textbox', { name: /^Who was informed/ }),
      'Not in the ward at the 6 pm round',
    );
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.stringContaining('nurse in charge')),
    );
    expect(server.requests.find((r) => r.path.endsWith('/discharge/complete'))?.body).toMatchObject({
      dischargeType: 'Absconded',
      absconded: {
        searchedBy: 'Ward staff and security',
        policeInformed: true,
        policeStation: 'Adyar PS',
        policeReference: 'GD 114/26',
      },
    });
  });

  it('shows why a death waits for its certificate, and asks a second doctor when a medico-legal one waits for the post-mortem', async () => {
    const stay = { ...STAY, status: 'Admitted' } as AdmissionDetail;
    fakeServer({
      [`GET /api/v1/inpatient/admissions/${ADMISSION_ID}/discharge/death-certificate`]: certificate({
        deathId: '0192a000-0000-7000-8000-00000000f8d1',
        serialNumber: 'D-0042',
        forThisStay: true,
        confirmationWaitingSince: '2026-11-08T05:00:00Z',
        status: 'Death certificate D-0042 is not yet confirmed by a second doctor.',
      }),
    });
    show(<CompleteDischargeDialog admission={stay} open onClose={() => undefined} />, NURSE);
    expect(
      await screen.findByText('Death certificate D-0042 waiting for a second doctor', {}, SLOW),
    ).toBeInTheDocument();
    expect(screen.queryByRole('switch', { name: /post-mortem/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Record death' })).toBeInTheDocument();
  });

  it('asks a second doctor to close a medico-legal death pending post-mortem', async () => {
    const stay = { ...STAY, status: 'Admitted', isMlc: true } as AdmissionDetail;
    const server = fakeServer({
      [`GET /api/v1/inpatient/admissions/${ADMISSION_ID}/discharge/death-certificate`]: certificate({
        medicoLegalStay: true,
      }),
      [`POST /api/v1/inpatient/admissions/${ADMISSION_ID}/discharge/complete`]: asked(
        'inpatient.expired_pending_postmortem',
        'IP26000123: close the stay as expired, certificate pending post-mortem',
      ),
    });
    const user = userEvent.setup();
    show(<CompleteDischargeDialog admission={stay} open onClose={() => undefined} />, NURSE);

    expect(await screen.findByText('No death certificate yet', {}, SLOW)).toBeInTheDocument();
    await user.click(screen.getByRole('switch', { name: /the certificate waits for the post-mortem/ }));
    await user.type(
      screen.getByRole('textbox', { name: /^Discharge notes/ }),
      'Declared dead at 06:10; police informed',
    );
    await expectNoAxeViolations();
    await user.click(screen.getByRole('button', { name: 'Ask for approval' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.stringContaining('second doctor')),
    );
    expect(server.requests.find((r) => r.path.endsWith('/discharge/complete'))?.body).toMatchObject({
      dischargeType: 'Expired',
      certificatePendingPostMortem: true,
    });
  });

  it('asks a second doctor to void a confirmed death certificate', async () => {
    const path = `/api/v1/mrd/deaths/${RECORD_ID}`;
    const confirmed = ANSWERS[`GET ${path}`] as DeathRecordDto;
    const server = fakeServer({
      ...ANSWERS,
      [`GET ${path}`]: { ...confirmed, canMarkInError: true },
      [`POST ${path}/entered-in-error`]: asked(
        'mrd.death_certificate_void',
        `Death certificate ${confirmed.serialNumber}: confirmed → entered in error`,
      ),
    });
    const user = userEvent.setup();
    openScreen(`/mrd/deaths/${RECORD_ID}`, DOCTOR);

    await user.click(await screen.findByRole('button', { name: 'Ask to void' }, SLOW));
    const dialog = await screen.findByRole('alertdialog', {
      name: `Ask to void certificate ${confirmed.serialNumber}?`,
    });
    expect(within(dialog).getByText(/another doctor who confirms certificates must agree/)).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Certified on the wrong patient');
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.stringContaining('Another doctor')),
    );
    expect(server.requests.find((r) => r.method === 'POST')?.body).toMatchObject({
      reason: 'Certified on the wrong patient',
    });
  });

  it('works out what waits and what the declaration still lacks', () => {
    expect(dischargeWaitsForApproval('Absconded', false)).toBe(true);
    expect(dischargeWaitsForApproval('Expired', true)).toBe(true);
    expect(dischargeWaitsForApproval('Expired', false)).toBe(false);
    expect(dischargeWaitsForApproval('Dama', false)).toBe(false);
    expect(isApprovalRequest({ id: 'x', kind: 'inpatient.absconded', canDecide: false })).toBe(true);
    expect(isApprovalRequest({ admissionId: 'x', status: 'Dama' })).toBe(false);

    const relative = {
      ...EMPTY_AGAINST_ADVICE,
      signedBy: 'Relative' as const,
      signatoryName: 'Ravi',
      relationship: '',
    };
    expect(againstAdviceMissing(relative, true)).toMatch(/Name the relative/);
    const signed = { ...relative, relationship: 'son' };
    expect(againstAdviceMissing(signed, false)).toMatch(/The doctor who explained the risks/);
    expect(againstAdviceMissing(signed, true)).toMatch(/A witness confirms/);
    expect(againstAdviceMissing({ ...signed, witness: WITNESS_CONFIRMED }, true)).toBeNull();
    expect(
      againstAdviceMissing(
        { ...signed, doctor: DOCTOR_CONFIRMED, witness: { ...WITNESS_CONFIRMED, userId: 'u-rao' } },
        false,
      ),
    ).toMatch(/someone other than the doctor/);
  });
});
