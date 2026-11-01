import type * as Core from '@hc360/core';
import type * as Ui from '@hc360/ui';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  FACILITY_ID,
  fakeServer,
  openScreen,
  paged,
  SCREEN_TESTS,
  SLOW,
  STAFF_USER,
  stubBrowserApis,
} from '@/test/appScreen';
import { problem, type FakeRequest } from '@/test/fakeApi';
import type { PatientDetail, PatientListItem } from './api';

// Merging a duplicate registration into the record that stays, from the patient profile: offered to the MRD officer
// (`patients.merge`) on a record not already merged, both records side by side, a reason, and an explicit "cannot be
// undone". The server is a fake; the toasts are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const DUPLICATE_ID = '0192a000-0000-7000-8000-00000000c101';
const SURVIVOR_ID = '0192a000-0000-7000-8000-00000000c102';

const record = (patch: Partial<PatientDetail>): PatientDetail => ({
  id: DUPLICATE_ID,
  uhid: 'NH26000789',
  title: 'Mrs.',
  firstName: 'Asha',
  middleName: null,
  lastName: 'Devi',
  fullName: 'Asha Devi',
  gender: 'Female',
  dateOfBirth: '1972-06-01',
  isDateOfBirthEstimated: false,
  age: '54 Y',
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
  version: 1,
  ...patch,
});

const DUPLICATE = record({});
const SURVIVOR = record({
  id: SURVIVOR_ID,
  uhid: 'NH24000123',
  phone: '+919876500001',
  registeredAt: '2024-02-01T04:30:00Z',
});

const SURVIVOR_ROW: PatientListItem = {
  id: SURVIVOR_ID,
  uhid: SURVIVOR.uhid,
  fullName: SURVIVOR.fullName,
  gender: 'Female',
  dateOfBirth: SURVIVOR.dateOfBirth,
  age: SURVIVOR.age,
  phoneMasked: '+91******0001',
  category: 'General',
  isVip: false,
  isDeceased: false,
  registeredAt: SURVIVOR.registeredAt,
  lastVisitAt: null,
};

/** The MRD officer: reads and merges records. */
const MRD_USER = { ...STAFF_USER, roles: ['MrdOfficer'], permissions: ['patients.read', 'patients.merge'] };
const READER = { ...STAFF_USER, roles: ['Nurse'], permissions: ['patients.read'] };

let server: ReturnType<typeof fakeServer>;
let answerMerge: (request: FakeRequest) => unknown;

function serve(duplicate: PatientDetail = DUPLICATE) {
  server = fakeServer({
    [`GET /api/v1/patients/${DUPLICATE_ID}`]: duplicate,
    [`GET /api/v1/patients/${SURVIVOR_ID}`]: SURVIVOR,
    [`GET /api/v1/patients/${DUPLICATE_ID}/payer`]: { current: null },
    [`GET /api/v1/patients/${SURVIVOR_ID}/payer`]: { current: null },
    'GET /api/v1/patients': paged([SURVIVOR_ROW]),
    [`POST /api/v1/patients/${DUPLICATE_ID}/merge`]: (request: FakeRequest) => answerMerge(request),
  });
}

const merges = () => server.requests.filter((r) => r.method === 'POST' && r.path.endsWith('/merge'));

async function openMenu() {
  const user = userEvent.setup();
  await user.click(await screen.findByRole('button', { name: 'More actions' }, SLOW));
  return user;
}

/** Opens the merge from the profile's menu and chooses the surviving record. */
async function chooseSurvivor() {
  const user = await openMenu();
  await user.click(await screen.findByRole('menuitem', { name: 'Merge duplicate…' }));
  const dialog = await screen.findByRole('alertdialog', { name: 'Merge duplicate record' });
  await user.type(within(dialog).getByRole('combobox', { name: /^Surviving record/ }), 'NH24');
  await user.click(await screen.findByRole('option', { name: /NH24000123/ }));
  await within(dialog).findByRole('region', { name: 'Surviving record: kept' });
  return { user, dialog };
}

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
  answerMerge = () => null;
  serve();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('merging a duplicate record', SCREEN_TESTS, () => {
  it('is offered only to those who may merge, and not on a record already merged', async () => {
    openScreen(`/patients/${DUPLICATE_ID}`, READER);
    await openMenu();
    expect(await screen.findByRole('menuitem', { name: 'Print UHID card' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Merge duplicate…' })).not.toBeInTheDocument();
  });

  it('is not offered on a record already merged', async () => {
    serve(record({ mergedIntoPatientId: SURVIVOR_ID }));
    openScreen(`/patients/${DUPLICATE_ID}`, MRD_USER);
    expect(await screen.findByText('Merged record', {}, SLOW)).toBeInTheDocument();
    await openMenu();
    expect(await screen.findByRole('menuitem', { name: 'Print UHID card' })).toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Merge duplicate…' })).not.toBeInTheDocument();
  });

  it('shows both records, needs a reason and the acknowledgement, then asks for the merge and stays on the record', async () => {
    const { router } = openScreen(`/patients/${DUPLICATE_ID}`, MRD_USER);
    const { user, dialog } = await chooseSurvivor();

    // Side by side, the phone numbers masked as the patient lists show them.
    const duplicate = within(dialog).getByRole('region', { name: 'Duplicate: merged away' });
    const survivor = within(dialog).getByRole('region', { name: 'Surviving record: kept' });
    expect(within(duplicate).getByText('NH26000789')).toBeInTheDocument();
    expect(within(duplicate).getByText('+91******3210')).toBeInTheDocument();
    expect(within(survivor).getByText('NH24000123')).toBeInTheDocument();
    expect(within(survivor).getByText('54 Y / F')).toBeInTheDocument();
    expect(within(survivor).getByText('+91******0001')).toBeInTheDocument();
    expect(within(dialog).getByText(/A merge cannot be undone/)).toBeInTheDocument();

    const confirm = within(dialog).getByRole('button', { name: 'Ask to merge into NH24000123' });
    await user.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Registered twice');
    expect(confirm).toBeDisabled();
    await user.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), ', same Aadhaar');
    expect(confirm).toBeDisabled();
    await user.click(
      within(dialog).getByRole('checkbox', {
        name: 'Merge NH26000789 into NH24000123. I understand this cannot be undone.',
      }),
    );
    await user.click(confirm);

    // Another person who may merge records approves it (maker-checker): until then nothing has changed.
    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith(
        'Merge sent for approval',
        'NH26000789 will be merged into NH24000123 when another person who may merge records approves it.',
      ),
    );
    expect(merges().map((r) => r.body)).toEqual([
      { intoPatientId: SURVIVOR_ID, reason: 'Registered twice, same Aadhaar' },
    ]);
    await waitFor(() =>
      expect(screen.queryByRole('alertdialog', { name: 'Merge duplicate record' })).not.toBeInTheDocument(),
    );
    expect(router.state.location.pathname).toBe(`/patients/${DUPLICATE_ID}`);
  });

  it('does not let a record be merged into itself', async () => {
    server = fakeServer({
      [`GET /api/v1/patients/${DUPLICATE_ID}`]: DUPLICATE,
      [`GET /api/v1/patients/${DUPLICATE_ID}/payer`]: { current: null },
      'GET /api/v1/patients': paged([{ ...SURVIVOR_ROW, id: DUPLICATE_ID, uhid: DUPLICATE.uhid }]),
    });
    openScreen(`/patients/${DUPLICATE_ID}`, MRD_USER);
    const user = await openMenu();
    await user.click(await screen.findByRole('menuitem', { name: 'Merge duplicate…' }));
    const dialog = await screen.findByRole('alertdialog', { name: 'Merge duplicate record' });
    await user.type(within(dialog).getByRole('combobox', { name: /^Surviving record/ }), 'NH26');
    await user.click(await screen.findByRole('option', { name: /NH26000789/ }));

    expect(await within(dialog).findByText('Choose a different record to merge into.')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Ask to merge' })).toBeDisabled();
  });

  it("shows the server's refusal in the dialog and stays on the record", async () => {
    answerMerge = () => problem(422, 'One of these records has already been merged.');
    const { router } = openScreen(`/patients/${DUPLICATE_ID}`, MRD_USER);
    const { user, dialog } = await chooseSurvivor();
    await user.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Registered twice, same Aadhaar');
    await user.click(within(dialog).getByRole('checkbox', { name: /cannot be undone/ }));
    await user.click(within(dialog).getByRole('button', { name: 'Ask to merge into NH24000123' }));

    expect(await within(dialog).findByText('One of these records has already been merged.')).toBeInTheDocument();
    expect(notify.success).not.toHaveBeenCalled();
    expect(router.state.location.pathname).toBe(`/patients/${DUPLICATE_ID}`);
  });
});
