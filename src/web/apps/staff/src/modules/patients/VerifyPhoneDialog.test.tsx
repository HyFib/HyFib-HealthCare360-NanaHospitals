import type * as Core from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FRONT_OFFICE_ANSWERS } from '@/test/accessibility/fixtures/frontOffice';
import { FACILITY_ID, fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { json, type FakeRequest } from '@/test/fakeApi';
import type { PatientDetail, PhoneVerification } from './api';
import { describeVerified } from './phoneChange';

// Verifying a patient's mobile number at the desk (ASVS V6.6.1): the record shows whether its number is verified, and
// "Verify mobile" sends a code to the number on the record; the number is verified when the code the patient reads out is
// entered. Offered on the patient's record and when registration is done. The server is a fake; the toasts are outside
// the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const PATIENT_ID = '0192a000-0000-7000-8000-00000000d301';
const VERIFICATION_ID = '0192a000-0000-7000-8000-00000000d302';
const PATH = `/api/v1/patients/${PATIENT_ID}/phone-verification`;

const PATIENT = {
  id: PATIENT_ID,
  uhid: 'NH26000777',
  title: 'Mrs.',
  firstName: 'Meenakshi',
  middleName: null,
  lastName: 'Sundaram',
  fullName: 'Meenakshi Sundaram',
  gender: 'Female',
  dateOfBirth: '1968-08-14',
  isDateOfBirthEstimated: false,
  age: '58Y',
  bloodGroup: 'Unknown',
  maritalStatus: 'Married',
  phone: '+919876543210',
  alternatePhone: null,
  email: null,
  address: {
    line1: null,
    line2: null,
    city: 'Theni',
    district: null,
    state: 'Tamil Nadu',
    pinCode: null,
    country: 'India',
  },
  guardianName: null,
  guardianRelation: null,
  emergencyContactName: null,
  emergencyContactPhone: null,
  emergencyContactRelation: null,
  occupation: null,
  religion: null,
  nationality: null,
  preferredLanguage: 'Tamil',
  category: 'General',
  abhaNumberMasked: null,
  abhaAddress: null,
  aadhaarLast4: null,
  photoFileId: null,
  registeredFacilityId: FACILITY_ID,
  registeredAt: '2026-11-14T04:30:00Z',
  isVip: false,
  isDeceased: false,
  deceasedAt: null,
  mergedIntoPatientId: null,
  notes: null,
  isActive: true,
  version: 3,
  contactsMasked: false,
  phoneVerifiedAt: null,
} as PatientDetail;

/** The front desk: edits patients' details and sees their contacts in full. */
const DESK = {
  ...STAFF_USER,
  roles: ['FrontOffice'],
  permissions: ['patients.read', 'patients.register', 'patients.contact.read'],
};

/** Reads patients' records, contacts included, but does not edit them. */
const READER = { ...STAFF_USER, roles: ['Nurse'], permissions: ['patients.read', 'patients.contact.read'] };

const sent = (patch: Partial<PhoneVerification> = {}): PhoneVerification => ({
  verificationId: VERIFICATION_ID,
  maskedPhone: '+91******3210',
  expiresAt: new Date(Date.now() + 10 * 60_000).toISOString(),
  attemptsLeft: 5,
  ...patch,
});

const refusal = (status: number, body: Record<string, unknown>) => json({ title: 'Refused', status, ...body }, status);

let server: ReturnType<typeof fakeServer>;
let record: PatientDetail;
let pending: PhoneVerification | null;
let answerSend: () => unknown;
let answerConfirm: (request: FakeRequest) => unknown;

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
  localStorage.clear();
  record = PATIENT;
  pending = null;
  answerSend = () => sent();
  answerConfirm = () => ({ maskedPhone: '+91******3210', verifiedAt: '2026-11-14T05:00:00Z' });
  server = fakeServer({
    ...FRONT_OFFICE_ANSWERS,
    'POST /api/v1/patients/duplicates': [],
    'POST /api/v1/patients': { id: PATIENT_ID, uhid: 'NH26000777' },
    [`GET /api/v1/patients/${PATIENT_ID}`]: () => record,
    [`GET /api/v1/patients/${PATIENT_ID}/payer`]: { current: null },
    [`GET ${PATH}`]: () => pending,
    [`POST ${PATH}`]: () => answerSend(),
    [`POST ${PATH}/${VERIFICATION_ID}/confirm`]: (request: FakeRequest) => answerConfirm(request),
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const posts = (suffix = '') => server.requests.filter((r) => r.method === 'POST' && r.path === PATH + suffix);

async function openProfile(user = DESK) {
  openScreen(`/patients/${PATIENT_ID}`, user);
  const events = userEvent.setup();
  // The number shows in the banner and on the overview.
  await screen.findAllByText('+91 98765 43210', undefined, SLOW);
  return events;
}

describe('verifying the mobile number on the record', SCREEN_TESTS, () => {
  it('sends a code to the number, counts a wrong code and verifies the number with the right one', async () => {
    answerConfirm = (request) => {
      if ((request.body as { code: string }).code !== '482913') {
        return refusal(400, {
          code: 'patient.phone_code_wrong',
          errors: { code: ['The code is incorrect. 4 tries left.'] },
          data: { attemptsLeft: 4 },
        });
      }
      record = { ...PATIENT, phoneVerifiedAt: '2026-11-14T05:00:00Z', version: 4 };
      return { maskedPhone: '+91******3210', verifiedAt: '2026-11-14T05:00:00Z' };
    };
    const user = await openProfile();
    expect(screen.getByText('Not verified')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Verify mobile' }));
    const dialog = await screen.findByRole('dialog', { name: 'Verify mobile number' });
    expect(await within(dialog).findByText(/A 6-digit code goes to \+91 98765 43210/)).toBeInTheDocument();
    expect(posts()).toHaveLength(0);

    await user.click(within(dialog).getByRole('button', { name: 'Send code' }));
    expect(await within(dialog).findByText(/was sent to \+91\*{6}3210/)).toBeInTheDocument();
    expect(posts()).toHaveLength(1);
    expect(within(dialog).getByText(/5 tries left/)).toBeInTheDocument();

    await user.type(within(dialog).getByRole('textbox', { name: /^Code/ }), '111111');
    await user.click(within(dialog).getByRole('button', { name: 'Confirm' }));
    expect(await within(dialog).findByText('The code is incorrect. 4 tries left.')).toBeInTheDocument();

    const code = within(dialog).getByRole('textbox', { name: /^Code/ });
    await user.clear(code);
    await user.type(code, '482913');
    await user.click(within(dialog).getByRole('button', { name: 'Confirm' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith(
        'Mobile number verified',
        describeVerified({ maskedPhone: '+91******3210', verifiedAt: '2026-11-14T05:00:00Z' }),
      ),
    );
    expect(posts(`/${VERIFICATION_ID}/confirm`).map((r) => r.body)).toEqual([{ code: '111111' }, { code: '482913' }]);
    // The record is read again: it shows the number verified, with nothing more to do.
    expect(await screen.findByText('Verified')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Verify mobile' })).not.toBeInTheDocument();
    expect(screen.queryByRole('dialog', { name: 'Verify mobile number' })).not.toBeInTheDocument();
  });

  it('carries on with a code already sent, and shows how long to wait for another', async () => {
    pending = sent({ attemptsLeft: 2 });
    answerSend = () =>
      refusal(422, {
        code: 'patient.phone_code_throttled',
        detail:
          '3 codes have been sent to this number in the last 15 minutes. Wait a few minutes before sending another.',
        data: { retryAfterSeconds: 245 },
      });
    const user = await openProfile();

    await user.click(screen.getByRole('button', { name: 'Verify mobile' }));
    const dialog = await screen.findByRole('dialog', { name: 'Verify mobile number' });
    expect(await within(dialog).findByText(/was sent to \+91\*{6}3210/)).toBeInTheDocument();
    expect(within(dialog).getByText(/2 tries left/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Send a new code' }));
    expect(await within(dialog).findByText(/3 codes have been sent to this number/)).toBeInTheDocument();
    expect(within(dialog).getByText(/Another code can be sent in 4:0\d/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: /^Send a new code \(4:0\d\)$/ })).toBeDisabled();
  });

  it('shows a verified number as verified, with nothing to do', async () => {
    record = { ...PATIENT, phoneVerifiedAt: '2026-11-10T09:00:00Z' };
    await openProfile();
    expect(await screen.findByText('Verified')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Verify mobile' })).not.toBeInTheDocument();
  });

  it('offers no verifying to staff who do not edit patients', async () => {
    await openProfile(READER);
    expect(await screen.findByText('Not verified')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Verify mobile' })).not.toBeInTheDocument();
  });
});

describe('verifying the mobile number at registration', SCREEN_TESTS, () => {
  it('is offered once the patient is registered, and the registration is done without it', async () => {
    const user = userEvent.setup();
    openScreen('/patients/register', DESK);
    await user.type(await screen.findByRole('textbox', { name: /^First name/ }, SLOW), 'Meenakshi');
    await user.type(screen.getByRole('textbox', { name: /^Last name/ }), 'Sundaram');
    await user.click(screen.getByRole('radio', { name: 'Female' }));
    await user.click(screen.getByRole('radio', { name: 'Age only' }));
    await user.type(await screen.findByLabelText(/^Years/), '58');
    await user.type(screen.getByRole('textbox', { name: /^Mobile number/ }), '9876543210');
    await user.keyboard('{Control>}s{/Control}');

    const registered = await screen.findByRole('dialog', { name: 'Patient registered' }, SLOW);
    expect(within(registered).getByText('NH26000777')).toBeInTheDocument();
    expect(within(registered).getByText('Mobile +91 98765 43210')).toBeInTheDocument();
    expect(within(registered).getByText('Not verified')).toBeInTheDocument();
    expect(posts()).toHaveLength(0);

    await user.click(within(registered).getByRole('button', { name: 'Verify mobile' }));
    const dialog = await screen.findByRole('dialog', { name: 'Verify mobile number' });
    await user.click(await within(dialog).findByRole('button', { name: 'Send code' }));
    await user.type(await within(dialog).findByRole('textbox', { name: /^Code/ }), '482913');
    await user.click(within(dialog).getByRole('button', { name: 'Confirm' }));

    expect(await within(registered).findByText('Verified')).toBeInTheDocument();
    expect(posts(`/${VERIFICATION_ID}/confirm`).map((r) => r.body)).toEqual([{ code: '482913' }]);
  });
});
