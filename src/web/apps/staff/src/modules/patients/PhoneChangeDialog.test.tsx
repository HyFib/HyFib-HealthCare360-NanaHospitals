import type * as Core from '@hc360/core';
import { ApiError } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FACILITY_ID, fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { json, type FakeRequest } from '@/test/fakeApi';
import type { PatientDetail, PhoneChange } from './api';
import {
  describeChange,
  formatWait,
  newPhoneProblem,
  NEW_PHONE_MESSAGE,
  phoneChangeProblem,
  SAME_PHONE_MESSAGE,
} from './phoneChange';

// Changing a patient's registered mobile number from the edit screen: the number is read-only there, "Change number"
// sends a code by SMS to the new number, and the number changes when the code the patient reads out is entered. The
// server is a fake; the toasts are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const PATIENT_ID = '0192a000-0000-7000-8000-00000000d201';
const CHANGE_ID = '0192a000-0000-7000-8000-00000000d202';
const PATH = `/api/v1/patients/${PATIENT_ID}/phone-change`;

const PATIENT: PatientDetail = {
  id: PATIENT_ID,
  uhid: 'NH26000555',
  title: 'Mrs.',
  firstName: 'Lakshmi',
  middleName: null,
  lastName: 'Rao',
  fullName: 'Lakshmi Rao',
  gender: 'Female',
  dateOfBirth: '1970-03-01',
  isDateOfBirthEstimated: false,
  age: '56 Y',
  bloodGroup: 'Unknown',
  maritalStatus: 'Married',
  phone: '+919876543210',
  alternatePhone: null,
  email: null,
  address: {
    line1: null,
    line2: null,
    city: 'Pune',
    district: null,
    state: 'Maharashtra',
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
  preferredLanguage: null,
  category: 'General',
  abhaNumberMasked: null,
  abhaAddress: null,
  aadhaarLast4: null,
  photoFileId: null,
  registeredFacilityId: FACILITY_ID,
  registeredAt: '2026-10-22T04:30:00Z',
  isVip: false,
  isDeceased: false,
  deceasedAt: null,
  mergedIntoPatientId: null,
  notes: null,
  isActive: true,
  version: 7,
  contactsMasked: false,
} as PatientDetail;

/** The front desk: edits patients' details and sees their contacts in full. */
const DESK = {
  ...STAFF_USER,
  roles: ['FrontOffice'],
  permissions: ['patients.read', 'patients.register', 'patients.contact.read'],
};

const sent = (patch: Partial<PhoneChange> = {}): PhoneChange => ({
  changeId: CHANGE_ID,
  maskedPhone: '+91******1122',
  expiresAt: new Date(Date.now() + 10 * 60_000).toISOString(),
  attemptsLeft: 5,
  ...patch,
});

const refusal = (status: number, body: Record<string, unknown>) => json({ title: 'Refused', status, ...body }, status);

let server: ReturnType<typeof fakeServer>;
let record: PatientDetail;
let pending: PhoneChange | null;
let answerSend: (request: FakeRequest) => unknown;
let answerConfirm: (request: FakeRequest) => unknown;

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
  record = PATIENT;
  pending = null;
  answerSend = () => sent();
  answerConfirm = () => ({ maskedPhone: '+91******1122', familyLinksMoved: 0, oldNumberSignedOut: false });
  server = fakeServer({
    [`GET /api/v1/patients/${PATIENT_ID}`]: () => record,
    [`GET ${PATH}`]: () => pending,
    [`POST ${PATH}`]: (request: FakeRequest) => answerSend(request),
    [`POST ${PATH}/${CHANGE_ID}/confirm`]: (request: FakeRequest) => answerConfirm(request),
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const posts = (suffix = '') => server.requests.filter((r) => r.method === 'POST' && r.path === PATH + suffix);
const detailReads = () =>
  server.requests.filter((r) => r.method === 'GET' && r.path === `/api/v1/patients/${PATIENT_ID}`);

async function openDialog() {
  openScreen(`/patients/${PATIENT_ID}/edit`, DESK);
  const user = userEvent.setup();
  const mobile = await screen.findByRole('textbox', { name: /^Mobile number/ }, SLOW);
  expect(mobile).toHaveAttribute('readonly');
  expect(mobile).toHaveValue('+919876543210');
  await user.click(screen.getByRole('button', { name: 'Change number' }));
  const dialog = await screen.findByRole('dialog', { name: 'Change mobile number' });
  return { user, dialog };
}

describe('changing the mobile number', SCREEN_TESTS, () => {
  it('sends a code to the new number, counts a wrong code and changes the number with the right one', async () => {
    let confirmed = false;
    answerConfirm = (request) => {
      const body = request.body as { code: string };
      if (body.code !== '482913') {
        return refusal(400, {
          code: 'patient.phone_code_wrong',
          errors: { code: ['The code is incorrect. 4 tries left.'] },
          data: { attemptsLeft: 4 },
        });
      }
      confirmed = true;
      record = { ...PATIENT, phone: '+919900011122', version: 8 };
      return { maskedPhone: '+91******1122', familyLinksMoved: 1, oldNumberSignedOut: true };
    };
    const { user, dialog } = await openDialog();
    expect(within(dialog).getByText(/Nothing is sent to the old number \(\+91 98765 43210\)/)).toBeInTheDocument();

    // A landline is refused before anything is sent, under the field.
    await user.type(within(dialog).getByRole('textbox', { name: /^New mobile number/ }), '020 2612 3456');
    await user.click(within(dialog).getByRole('button', { name: 'Send code' }));
    expect(await within(dialog).findByText(NEW_PHONE_MESSAGE)).toBeInTheDocument();
    expect(posts()).toHaveLength(0);

    const number = within(dialog).getByRole('textbox', { name: /^New mobile number/ });
    await user.clear(number);
    await user.type(number, '99000 11122');
    await user.click(within(dialog).getByRole('button', { name: 'Send code' }));
    expect(await within(dialog).findByText(/sent by SMS to \+91\*{6}1122/)).toBeInTheDocument();
    expect(posts().map((r) => r.body)).toEqual([{ phone: '+919900011122' }]);
    expect(within(dialog).getByText(/5 tries left/)).toBeInTheDocument();

    const confirm = within(dialog).getByRole('button', { name: 'Confirm' });
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByRole('textbox', { name: /^Code/ }), '111111');
    await user.click(confirm);
    expect(await within(dialog).findByText('The code is incorrect. 4 tries left.')).toBeInTheDocument();
    expect(within(dialog).getByText(/4 tries left$/)).toBeInTheDocument();

    const code = within(dialog).getByRole('textbox', { name: /^Code/ });
    await user.clear(code);
    await user.type(code, '48a2913');
    await user.click(within(dialog).getByRole('button', { name: 'Confirm' }));
    await waitFor(() => expect(confirmed).toBe(true));
    expect(posts(`/${CHANGE_ID}/confirm`).map((r) => r.body)).toEqual([{ code: '111111' }, { code: '482913' }]);
    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith(
        'Mobile number changed',
        describeChange({ maskedPhone: '+91******1122', familyLinksMoved: 1, oldNumberSignedOut: true }),
      ),
    );

    // The record is read again: the form shows the new number.
    await waitFor(() => expect(screen.getByRole('textbox', { name: /^Mobile number/ })).toHaveValue('+919900011122'));
    expect(detailReads().length).toBeGreaterThanOrEqual(2);
    expect(screen.queryByRole('dialog', { name: 'Change mobile number' })).not.toBeInTheDocument();
  });

  it('shows how long to wait when the number has had three codes, and the number refused under its field', async () => {
    answerSend = (request) =>
      (request.body as { phone: string }).phone === '+919876543210'
        ? refusal(400, { code: 'patient.phone_unchanged', errors: { phone: [SAME_PHONE_MESSAGE] } })
        : refusal(422, {
            code: 'patient.phone_code_throttled',
            detail:
              '3 codes have been sent to this number in the last 15 minutes. Wait a few minutes before sending another.',
            data: { retryAfterSeconds: 245 },
          });
    const { user, dialog } = await openDialog();

    await user.type(within(dialog).getByRole('textbox', { name: /^New mobile number/ }), '9900011122');
    await user.click(within(dialog).getByRole('button', { name: 'Send code' }));
    expect(await within(dialog).findByText(/3 codes have been sent to this number/)).toBeInTheDocument();
    expect(within(dialog).getByText(/Another code can be sent in 4:0\d/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: /^Send code \(4:0\d\)$/ })).toBeDisabled();
  });

  it('carries on with a code already sent, and a code that can no longer be used goes back to the number', async () => {
    pending = sent({ attemptsLeft: 2 });
    answerConfirm = () =>
      refusal(409, {
        code: 'patient.phone_changed_meanwhile',
        detail:
          'The patient’s mobile number was changed to +91******0000 after this code was sent. Send a new code if it should still change.',
      });
    const { user, dialog } = await openDialog();

    expect(await within(dialog).findByText(/sent by SMS to \+91\*{6}1122/)).toBeInTheDocument();
    expect(within(dialog).getByText(/2 tries left/)).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox', { name: /^Code/ }), '482913');
    await user.click(within(dialog).getByRole('button', { name: 'Confirm' }));

    expect(
      await within(dialog).findByText(/was changed to \+91\*{6}0000 after this code was sent/),
    ).toBeInTheDocument();
    expect(within(dialog).getByRole('textbox', { name: /^New mobile number/ })).toHaveValue('');
    expect(notify.success).not.toHaveBeenCalled();
  });

  it('is not offered while other changes are unsaved', async () => {
    openScreen(`/patients/${PATIENT_ID}/edit`, DESK);
    const user = userEvent.setup();
    const occupation = await screen.findByRole('textbox', { name: /^Occupation/ }, SLOW);
    expect(screen.getByRole('button', { name: 'Change number' })).toBeEnabled();
    await user.type(occupation, 'Teacher');
    expect(screen.getByRole('button', { name: 'Change number' })).toBeDisabled();
    expect(screen.getByText('Save or cancel your other changes first, then change the number.')).toBeInTheDocument();
  });
});

describe('the rules the dialog checks', () => {
  it('takes only an Indian mobile number that the record does not have', () => {
    expect(newPhoneProblem('98765 43210', '+919876543210')).toBe(SAME_PHONE_MESSAGE);
    expect(newPhoneProblem('+91 99000 11122', '+919876543210')).toBeNull();
    expect(newPhoneProblem('9900011122', null)).toBeNull();
    expect(newPhoneProblem('020 2612 3456', null)).toBe(NEW_PHONE_MESSAGE);
    expect(newPhoneProblem('+44 7700 900123', null)).toBe(NEW_PHONE_MESSAGE);
  });

  it("reads the server's refusals", () => {
    const wrong = new ApiError({
      status: 400,
      code: 'patient.phone_code_wrong',
      errors: { code: ['The code is incorrect, and that was the last try. Send a new code.'] },
      data: { attemptsLeft: 0 },
    });
    expect(phoneChangeProblem(wrong)).toMatchObject({ field: 'code', attemptsLeft: 0, spent: true });
    const throttled = new ApiError({
      status: 422,
      code: 'patient.phone_code_throttled',
      detail: 'Wait.',
      data: { retryAfterSeconds: 61.2 },
    });
    expect(phoneChangeProblem(throttled)).toEqual({
      field: null,
      message: 'Wait.',
      retryAfterSeconds: 62,
      spent: false,
    });
    const expired = new ApiError({ status: 422, code: 'patient.phone_code_expired', detail: 'Send a new code.' });
    expect(phoneChangeProblem(expired)).toMatchObject({ field: null, spent: true });
    expect(phoneChangeProblem(new Error('offline'))).toMatchObject({ field: null, spent: false });
  });

  it('writes a wait as minutes and seconds', () => {
    expect(formatWait(245)).toBe('4:05');
    expect(formatWait(59.1)).toBe('1:00');
    expect(formatWait(-3)).toBe('0:00');
  });
});
