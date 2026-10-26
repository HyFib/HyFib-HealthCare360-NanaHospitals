import type * as Core from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { formFromShare, type AbhaShareDetail, type AbhaShareRow } from './abhaShares';

// Scan and share (ABDM M1) at the registration desk: the day's queue of patients who shared their ABHA profile, the
// record a share matched, registering a new patient with the form filled in from their ABHA, and taking a share off
// the queue. The server is a fake; the toasts are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const DETAIL: AbhaShareDetail = {
  id: '0192a000-0000-7000-8000-00000000ab01',
  tokenNumber: 7,
  counter: 'REG-1',
  name: 'Arjun Kumar Rao',
  gender: 'M',
  yearOfBirth: 1990,
  monthOfBirth: 6,
  dayOfBirth: 15,
  abhaAddress: 'arjun.rao@abdm',
  abhaNumber: '91-2345-6789-0123',
  phone: '+919876543210',
  addressLine: '12 MG Road',
  district: 'Bengaluru Urban',
  state: 'Karnataka',
  pincode: '560001',
  status: 'Waiting',
  patient: null,
  matchedBy: null,
};

const row = (patch: Partial<AbhaShareRow> & Pick<AbhaShareRow, 'id' | 'tokenNumber'>): AbhaShareRow => ({
  counter: 'REG-1',
  name: 'Arjun Kumar Rao',
  gender: 'M',
  yearOfBirth: 1990,
  abhaAddress: 'arjun.rao@abdm',
  abhaNumberMasked: 'xx-xxxx-xxxx-0123',
  phoneMasked: '+91******3210',
  status: 'Waiting',
  sharedAt: new Date(Date.now() - 5 * 60_000).toISOString(),
  expiresAt: new Date(Date.now() + 55 * 60_000).toISOString(),
  patient: null,
  matchedBy: null,
  ...patch,
});

const NEW_PATIENT = row({ id: DETAIL.id, tokenNumber: 7 });
const KNOWN = row({
  id: '0192a000-0000-7000-8000-00000000ab02',
  tokenNumber: 8,
  name: 'Asha Devi',
  gender: 'F',
  abhaAddress: 'asha.d@abdm',
  patient: { id: '0192a000-0000-7000-8000-000000000001', uhid: 'NH26000123', fullName: 'Asha Devi' },
  matchedBy: 'ABHA_ADDRESS',
});

const DESK = {
  ...STAFF_USER,
  roles: ['FrontOffice'],
  permissions: ['patients.register', 'patients.contact.read', 'patients.read'],
};
const NO_CONTACT = { ...DESK, permissions: ['patients.register', 'patients.read'] };

describe('the registration form from a shared ABHA profile', () => {
  it('splits the name, takes the full date of birth, the mobile without +91, the ABHA and the address', () => {
    const form = formFromShare(DETAIL);
    expect(form).toMatchObject({
      firstName: 'Arjun',
      middleName: 'Kumar',
      lastName: 'Rao',
      gender: 'Male',
      ageMode: 'dob',
      dateOfBirth: '1990-06-15',
      phone: '9876543210',
      abhaNumber: '91-2345-6789-0123',
      abhaAddress: 'arjun.rao@abdm',
    });
    expect(form.address).toMatchObject({
      line1: '12 MG Road',
      district: 'Bengaluru Urban',
      state: 'Karnataka',
      pinCode: '560001',
    });
  });

  it('gives the age from the year when the ABHA has no full date of birth, and leaves an unknown gender to the desk', () => {
    const form = formFromShare({ ...DETAIL, dayOfBirth: null, gender: 'U', name: 'Arjun' }, new Date('2026-10-26'));
    expect(form).toMatchObject({
      firstName: 'Arjun',
      lastName: '',
      ageMode: 'age',
      ageYears: 36,
      dateOfBirth: '',
      gender: '',
    });
  });
});

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
  server = fakeServer({
    'GET /api/v1/abdm/shares': [NEW_PATIENT, KNOWN],
    [`GET /api/v1/abdm/shares/${DETAIL.id}`]: DETAIL,
    'POST /api/v1/abdm/shares/*': () => null,
    'GET /api/v1/masters/lookups/departments': [],
    // ABDM is off until the hospital turns it on.
    'GET /api/v1/settings/public': { 'features.abdm': true },
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const posted = () => server.requests.filter((r) => r.method === 'POST').map((r) => ({ path: r.path, body: r.body }));

describe('the scan-and-share queue', SCREEN_TESTS, () => {
  it('shows each share by token, the record a share matched, and registers a new patient from their ABHA', async () => {
    openScreen('/front-office/abha-shares', DESK);
    await screen.findByText('0007', {}, SLOW);
    expect(screen.getByText('0008')).toBeInTheDocument();
    expect(screen.getByText('Matched by ABHA address')).toBeInTheDocument();
    expect(screen.getAllByText('+91******3210', { exact: false })).toHaveLength(2);

    await userEvent.setup().click(screen.getByRole('button', { name: 'Register' }));
    expect(await screen.findByDisplayValue('Arjun', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByDisplayValue('arjun.rao@abdm')).toBeInTheDocument();
    expect(screen.getByText(/Filled in from the ABHA shared at token 0007/)).toBeInTheDocument();
  });

  it('marks a matched patient done, with the record it matched', async () => {
    openScreen('/front-office/abha-shares', DESK);
    await screen.findByText('0008', {}, SLOW);
    await userEvent.setup().click(screen.getByRole('button', { name: 'Done' }));
    await waitFor(() =>
      expect(posted()).toEqual([
        { path: `/api/v1/abdm/shares/${KNOWN.id}/registered`, body: { patientId: KNOWN.patient!.id } },
      ]),
    );
  });

  it('takes a share off the queue once confirmed', async () => {
    openScreen('/front-office/abha-shares', DESK);
    await screen.findByText('0007', {}, SLOW);
    const user = userEvent.setup();
    await user.click(screen.getAllByRole('button', { name: 'Take off queue' })[0]!);
    const dialog = await screen.findByRole('alertdialog');
    await user.click(within(dialog).getByRole('button', { name: 'Take off' }));
    await waitFor(() => expect(posted().map((p) => p.path)).toEqual([`/api/v1/abdm/shares/${NEW_PATIENT.id}/dismiss`]));
  });

  it('does not offer registering from a share to staff who may not see contact details', async () => {
    openScreen('/front-office/abha-shares', NO_CONTACT);
    await screen.findByText('0007', {}, SLOW);
    expect(screen.queryByRole('button', { name: 'Register' })).not.toBeInTheDocument();
  });
});
