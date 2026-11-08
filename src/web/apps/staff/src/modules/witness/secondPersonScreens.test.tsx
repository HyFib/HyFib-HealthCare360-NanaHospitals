import type * as Core from '@hc360/core';
import type { AuthUser } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Body } from '@/modules/mortuary/api';
import type { MtpCaseDto } from '@/modules/mtp/api';
import { fakeServer, json, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { ANSWERS as MORTUARY } from '@/test/roleAccess/mortuary';
import { ANSWERS as MTP, MTP_CASE } from '@/test/roleAccess/mtp';
import { RECORD_ID } from '@/test/roleAccessIds';

// Where a second person confirms at the screen (maker-checker, slice 9), against a fake server: a body released from the
// mortuary on the police's clearance is witnessed by a second member of the mortuary staff, and the second doctor's
// opinion on a termination from 20 weeks is the second doctor's own, confirmed with their own credentials. The toasts
// are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const BODY_ID = '0192a000-0000-7000-8000-0000000000b1';
const UNCLAIMED: Body = {
  id: BODY_ID,
  number: 'NHMB2600001',
  patientId: null,
  uhid: null,
  source: 'Police',
  name: 'Unknown male, about 40',
  identified: false,
  sex: 'Male',
  ageYears: 40,
  diedAt: null,
  receivedAt: '2026-11-04T06:00:00Z',
  receivedByName: 'Sister Meena',
  broughtBy: 'HC 1123, B2 police station',
  isMlc: true,
  mlcNumber: 'MLC 77/2026',
  policeStation: 'B2 Ashok Nagar',
  identificationMarks: 'Scar on left forearm',
  belongings: null,
  slotCode: 'A-1',
  status: 'InStorage',
  unclaimed: true,
  releasedAt: null,
  releasedTo: null,
  receivedBy: null,
  relationship: null,
  idProof: null,
  policeClearance: null,
  belongingsHandedOver: false,
  releaseNotes: null,
  releasedByName: null,
  version: 4,
};

const MORTUARY_NURSE: AuthUser = {
  ...STAFF_USER,
  displayName: 'Sister Meena',
  practitionerId: null,
  roles: ['Nurse'],
  permissions: ['mortuary.manage'],
};
const GYNAECOLOGIST: AuthUser = {
  ...STAFF_USER,
  displayName: 'Dr. Kavitha Raman',
  roles: ['Doctor'],
  permissions: ['mtp.read', 'mtp.certify', 'patients.sensitive.mtp'],
};

const WITNESS = {
  confirmationId: '0192a000-0000-7000-8000-0000000009c1',
  userId: 'u-mary',
  name: 'Sister Mary',
  expiresAt: '2099-01-01T00:00:00Z',
};
const SECOND_DOCTOR = {
  confirmationId: '0192a000-0000-7000-8000-0000000009c2',
  userId: 'u-anita',
  name: 'Dr. Anita Rao',
  expiresAt: '2099-01-01T00:00:00Z',
};

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const confirm = async (
  user: ReturnType<typeof userEvent.setup>,
  inside: HTMLElement,
  who: 'Witness' | 'Doctor',
  name: string,
) => {
  await user.type(within(inside).getByRole('textbox', { name: new RegExp(`^${who}'s user name`) }), name);
  await user.type(within(inside).getByLabelText(new RegExp(`^${who}'s password`)), 'not-a-real-password');
  await user.click(within(inside).getByRole('button', { name: `Confirm ${who.toLowerCase()}` }));
};

describe('a second person at the screen (slice 9)', SCREEN_TESTS, () => {
  it('releases an unclaimed body on the police clearance only with a second member of staff witnessing', async () => {
    const server = fakeServer({
      ...MORTUARY,
      [`GET /api/v1/mortuary/bodies/${BODY_ID}`]: UNCLAIMED,
      'POST /api/v1/auth/witness': WITNESS,
      [`POST /api/v1/mortuary/bodies/${BODY_ID}/release`]: {
        ...UNCLAIMED,
        status: 'Released',
        releasedTo: 'Municipality',
        releaseWitnessName: WITNESS.name,
        version: 5,
      },
    });
    const user = userEvent.setup();
    openScreen('/mortuary', MORTUARY_NURSE);

    await user.click(await screen.findByRole('button', { name: 'Release' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: /Release Unknown male/ }, SLOW);
    await user.selectOptions(within(dialog).getByRole('combobox', { name: /^Released to/ }), 'Municipality');
    await user.type(within(dialog).getByRole('textbox', { name: /^Received by/ }), 'Corporation sanitary inspector');
    await user.type(within(dialog).getByRole('textbox', { name: /^Police clearance/ }), 'Letter 91/2026');

    // Not sent until the witness has confirmed, and the screen says why.
    await user.click(within(dialog).getByRole('button', { name: 'Release and print the slip' }));
    expect(
      await within(dialog).findByText(/A second member of the mortuary staff witnesses this release/),
    ).toBeInTheDocument();
    expect(server.requests.some((r) => r.path.endsWith('/release'))).toBe(false);

    await confirm(user, dialog, 'Witness', 'mary');
    expect(await within(dialog).findByText('Sister Mary')).toBeInTheDocument();
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Release and print the slip' }));

    await waitFor(() => expect(notify.success).toHaveBeenCalledWith('Body released', 'NHMB2600001'));
    expect(server.requests.find((r) => r.path === '/api/v1/auth/witness')?.body).toMatchObject({
      purpose: 'mortuary.release',
      patientId: null,
    });
    expect(server.requests.find((r) => r.path.endsWith('/release'))?.body).toMatchObject({
      to: 'Municipality',
      policeClearance: 'Letter 91/2026',
      witnessConfirmationId: WITNESS.confirmationId,
      version: 4,
    });
  });

  it('asks the second doctor for their own opinion from 20 weeks and holds the termination until then', async () => {
    const awaiting: MtpCaseDto = { ...MTP_CASE, opinions: MTP_CASE.opinions.slice(0, 1), awaitingSecondOpinion: true };
    const server = fakeServer({
      ...MTP,
      [`GET /api/v1/mtp/cases/${RECORD_ID}`]: awaiting,
      'POST /api/v1/auth/witness': SECOND_DOCTOR,
      [`POST /api/v1/mtp/cases/${RECORD_ID}/second-opinion`]: json({
        ...awaiting,
        awaitingSecondOpinion: false,
        opinions: [
          ...awaiting.opinions,
          {
            doctorName: 'Dr. Anita Rao',
            registrationNumber: 'TNMC 51877',
            opinionOn: awaiting.consentOn,
            userId: 'u-anita',
            recordedBy: 'u-kavitha',
            confirmed: true,
          },
        ],
        version: 4,
      }),
    });
    const user = userEvent.setup();
    openScreen(`/mtp/${RECORD_ID}`, GYNAECOLOGIST);

    expect(await screen.findByText("Waiting for the second doctor's opinion", {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Record termination' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Record second opinion' }));

    const dialog = await screen.findByRole('dialog', { name: /Second doctor's opinion/ });
    await user.click(within(dialog).getByRole('button', { name: 'Record second opinion' }));
    expect(
      await within(dialog).findByText(/The second doctor confirms here with their own user name and password\./),
    ).toBeInTheDocument();
    expect(server.requests.some((r) => r.path.endsWith('/second-opinion'))).toBe(false);

    await confirm(user, dialog, 'Doctor', 'anita.rao');
    expect(await within(dialog).findByText('Dr. Anita Rao')).toBeInTheDocument();
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Record second opinion' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Second opinion recorded', 'Dr. Anita Rao (TNMC 51877)'),
    );
    expect(server.requests.find((r) => r.path === '/api/v1/auth/witness')?.body).toMatchObject({
      purpose: 'mtp.second_opinion',
      patientId: awaiting.patientId,
    });
    expect(server.requests.find((r) => r.path.endsWith('/second-opinion'))?.body).toMatchObject({
      doctorConfirmationId: SECOND_DOCTOR.confirmationId,
      version: MTP_CASE.version,
    });
  });
});
