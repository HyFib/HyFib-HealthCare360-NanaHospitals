import type * as Core from '@hc360/core';
import { useSession, type AuthUser } from '@hc360/core';
import type * as Ui from '@hc360/ui';
import type * as Router from '@tanstack/react-router';
import type * as Lookups from '@/lib/lookups';
import { Hc360ThemeProvider } from '@hc360/ui';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { allergyChangeWaits } from '@/modules/emr/allergyStatus';
import type { AllergyDto, PatientAlertDto } from '@/modules/emr/api';
import { AlertsTab, AllergiesTab } from '@/modules/emr/FlagsPanels';
import type { AdmissionDetail } from '@/modules/inpatient/api';
import { EditAdmissionDrawer } from '@/modules/inpatient/EditAdmissionDrawer';
import { mlcChangeWaits } from '@/modules/inpatient/mlcFlag';
import { FACILITY_ID, fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '@/test/appScreen';
import { json } from '@/test/fakeApi';
import type { PatientDetail, SensitiveCategoriesDto } from './api';
import { SensitiveCategoriesCard } from './SensitiveCategoriesCard';
import { asksForApproval, fromPatientDetail, recordChangesAsked } from './validation';

// The patient's record where a second person decides (maker-checker, slice 7), on the screens against a fake server: the
// registration edit says what waits and asks for approval with a reason; the confidential-record card asks to take a
// category out and shows it waiting; an allergy taken out of the checks or lowered asks for approval; DNR is offered
// only to doctors, and taking it off asks a second doctor; clearing a stay's MLC flag asks the MLC register. The toasts
// are outside the test.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));
vi.mock('@tanstack/react-router', async (importOriginal) => ({
  ...(await importOriginal<typeof Router>()),
  // The cards and dialogs below are rendered on their own, outside the app's router; the edit screen uses the real one.
  useNavigate: () => vi.fn(),
}));
vi.mock('@/lib/lookups', async (importOriginal) => ({
  ...(await importOriginal<typeof Lookups>()),
  useDepartments: () => ({ data: [{ id: 'dept-gm', name: 'General medicine' }] }),
  usePractitioners: () => ({ data: [{ id: 'doc-rao', name: 'Dr. Anil Rao' }] }),
}));

const PATIENT_ID = '0192a000-0000-7000-8000-00000000f701';
const APPROVAL = {
  id: '0192a000-0000-7000-8000-00000000f7a1',
  kind: 'patients.identity',
  summary: 'Identity of NH26000777: name Lakshmi Rao → Lakshmi Raao',
};

const PATIENT = {
  id: PATIENT_ID,
  uhid: 'NH26000777',
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
  identityCorrectableUntil: null,
  pendingChanges: [
    {
      approvalId: '0192a000-0000-7000-8000-00000000f7a2',
      kind: 'patients.restriction_lift',
      title: 'VIP restriction lifted',
      summary: 'Lift the VIP restriction on NH26000777',
      requestedBy: 'Hospital Admin',
      requestedAt: '2026-11-08T04:30:00Z',
    },
  ],
} as PatientDetail;

/** The front desk: edits patients' details and sees their contacts in full. */
const DESK: AuthUser = {
  ...STAFF_USER,
  roles: ['FrontOffice'],
  permissions: ['patients.read', 'patients.register', 'patients.contact.read'],
};
const DOCTOR: AuthUser = {
  ...STAFF_USER,
  roles: ['Doctor'],
  permissions: [
    'patients.read',
    'patients.sensitive.manage',
    'clinical.emr.read',
    'clinical.allergies.manage',
    'clinical.alerts.manage',
    'clinical.encounters.sign',
  ],
};
const NURSE: AuthUser = {
  ...STAFF_USER,
  roles: ['Nurse'],
  permissions: ['patients.read', 'clinical.emr.read', 'clinical.allergies.manage', 'clinical.alerts.manage'],
};

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  vi.clearAllMocks();
  stubBrowserApis();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** A card or dialog on its own, signed in as `user`, with the queries it reads. */
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

const accepted = (body: object) => json(body, 202);

describe("the patient's record where a second person decides", SCREEN_TESTS, () => {
  it('shows what waits on the registration edit, and asks for approval of an identity change with a reason', async () => {
    server = fakeServer({
      [`GET /api/v1/patients/${PATIENT_ID}`]: PATIENT,
      [`GET /api/v1/patients/${PATIENT_ID}/phone-change`]: null,
      [`PUT /api/v1/patients/${PATIENT_ID}`]: accepted(APPROVAL),
    });
    const user = userEvent.setup();
    openScreen(`/patients/${PATIENT_ID}/edit`, DESK);

    // The change already waiting, with who asked.
    expect(await screen.findByText('Change waiting for approval', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText(/asked for by Hospital Admin/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeInTheDocument();

    // A corrected surname, past the desk's correction window: it asks, and needs a reason.
    const lastName = screen.getByRole('textbox', { name: /^Last name/ });
    await user.clear(lastName);
    await user.type(lastName, 'Raao');
    const ask = await screen.findByRole('button', { name: 'Ask for approval' });
    expect(
      screen.getByText(/The name: someone who approves identity changes \(medical records\) decides it/),
    ).toBeInTheDocument();
    await expectNoAxeViolations();
    await user.click(ask);
    expect(await screen.findByText(/Say why: the change goes to someone else for approval/)).toBeInTheDocument();
    expect(server.requests.some((r) => r.method === 'PUT')).toBe(false);

    await user.type(screen.getByRole('textbox', { name: /^Reason for the change/ }), 'Spelling as on the Aadhaar card');
    await user.click(screen.getByRole('button', { name: 'Ask for approval' }));
    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.stringContaining(APPROVAL.summary)),
    );
    const put = server.requests.find((r) => r.method === 'PUT');
    expect(put?.body).toMatchObject({ lastName: 'Raao', changeReason: 'Spelling as on the Aadhaar card', version: 7 });
  });

  it('works out what an edit asks for: the identity only after the correction window, a restriction lifted, the deceased status', () => {
    const values = fromPatientDetail(PATIENT);
    const none = recordChangesAsked(values, PATIENT, {
      removeAbha: false,
      removeAadhaar: false,
      isDeceased: false,
      deceasedAtChanged: false,
    });
    expect(none).toEqual({ identity: [], liftsRestriction: false, deceased: false });

    const renamed = recordChangesAsked({ ...values, firstName: 'Laxmi', aadhaarNumber: '234123412346' }, PATIENT, {
      removeAbha: false,
      removeAadhaar: false,
      isDeceased: false,
      deceasedAtChanged: false,
    });
    expect(renamed.identity).toEqual(['name', 'Aadhaar']);
    expect(asksForApproval(renamed, false)).toBe(true);
    expect(asksForApproval(renamed, true)).toBe(false);

    const vip = { ...PATIENT, isVip: true };
    expect(
      recordChangesAsked({ ...fromPatientDetail(vip), isVip: false }, vip, {
        removeAbha: false,
        removeAadhaar: false,
        isDeceased: false,
        deceasedAtChanged: false,
      }).liftsRestriction,
    ).toBe(true);
    expect(
      asksForApproval(
        recordChangesAsked(values, PATIENT, {
          removeAbha: false,
          removeAadhaar: false,
          isDeceased: true,
          deceasedAtChanged: false,
        }),
        true,
      ),
    ).toBe(true);
  });

  it('asks another doctor to take the record out of a sensitive category, and shows the removal waiting', async () => {
    const current: SensitiveCategoriesDto = { categories: ['Hiv', 'MentalHealth'], version: 4 };
    let sensitive: SensitiveCategoriesDto = current;
    server = fakeServer({
      [`GET /api/v1/patients/${PATIENT_ID}/sensitive-categories`]: () => sensitive,
      [`PUT /api/v1/patients/${PATIENT_ID}/sensitive-categories`]: () => {
        sensitive = {
          ...current,
          waitingRemoval: ['Hiv'],
          waiting: {
            approvalId: '0192a000-0000-7000-8000-00000000f7a3',
            kind: 'patients.sensitive_remove',
            title: 'Sensitive category removed',
            summary: 'Take NH26000777 out of: HIV',
            requestedBy: 'Dr. Meera Iyer',
            requestedAt: '2026-11-08T05:00:00Z',
          },
        };
        return accepted({
          id: '0192a000-0000-7000-8000-00000000f7a3',
          kind: 'patients.sensitive_remove',
          summary: 'Take NH26000777 out of: HIV',
        });
      },
    });
    const user = userEvent.setup();
    show(<SensitiveCategoriesCard patientId={PATIENT_ID} />, DOCTOR);

    await user.click(await screen.findByRole('button', { name: 'Change categories' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: 'Sensitive categories' });
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeInTheDocument();
    await user.click(within(dialog).getByRole('checkbox', { name: 'HIV' }));
    await user.type(within(dialog).getByRole('textbox', { name: /^Reason/ }), 'Repeat test negative');
    expect(within(dialog).getByText(/another doctor who manages the categories approves it first/)).toBeInTheDocument();
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.stringContaining('Another doctor')),
    );
    expect(await screen.findByText(/Taking the record out of HIV, asked for by Dr\. Meera Iyer/)).toBeInTheDocument();
    expect(screen.getByText('Sensitive: HIV, Mental health')).toBeInTheDocument();
    expect(server.requests.find((r) => r.method === 'PUT')?.body).toMatchObject({
      categories: ['MentalHealth'],
      version: 4,
    });
  });

  it('asks a prescriber to take an allergy out of the checks, and shows it waiting', async () => {
    const allergy: AllergyDto = {
      id: '0192a000-0000-7000-8000-00000000f7b1',
      patientId: PATIENT_ID,
      allergen: 'Penicillin',
      type: 'Drug',
      reaction: 'Rash',
      severity: 'Severe',
      status: 'Active',
      statusReason: null,
      onsetDate: null,
      recordedAt: '2026-10-01T05:00:00Z',
      recordedByUserId: null,
      notes: null,
      version: 3,
    };
    server = fakeServer({
      [`GET /api/v1/emr/patients/${PATIENT_ID}/allergies`]: [
        allergy,
        {
          ...allergy,
          id: '0192a000-0000-7000-8000-00000000f7b2',
          allergen: 'Peanuts',
          type: 'Food',
          pendingChange: {
            approvalId: '0192a000-0000-7000-8000-00000000f7a4',
            summary: 'Allergy to Peanuts of NH26000777: active → resolved',
            requestedBy: 'Sister Anita',
            requestedAt: '2026-11-08T05:00:00Z',
          },
        },
      ],
      [`GET /api/v1/emr/patients/${PATIENT_ID}/allergies/status`]: {
        status: 'Recorded',
        recordedAt: '2026-10-01T05:00:00Z',
        recordedByName: 'Dr. Rao',
      },
      [`PUT /api/v1/emr/allergies/${allergy.id}`]: accepted({
        id: '0192a000-0000-7000-8000-00000000f7a5',
        kind: 'clinical.allergy_change',
        summary: 'Allergy to Penicillin of NH26000777: severity severe → mild',
      }),
    });
    const user = userEvent.setup();
    show(<AllergiesTab patientId={PATIENT_ID} />, NURSE);

    expect(await screen.findByText(/Change asked for approval: Allergy to Peanuts/, {}, SLOW)).toBeInTheDocument();
    await user.click(screen.getByText('Penicillin'));
    const dialog = await screen.findByRole('dialog', { name: /Penicillin/ });
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeInTheDocument();
    await user.click(within(dialog).getByRole('radio', { name: 'Mild' }));
    expect(within(dialog).getByText(/A lower severity softens the alert prescribers see/)).toBeInTheDocument();
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));
    expect(await within(dialog).findByText(/Say why: a lower severity goes to a prescriber/)).toBeInTheDocument();
    await user.type(
      within(dialog).getByRole('textbox', { name: /^Why the severity is lower/ }),
      'Only a mild rash in 2019',
    );
    await expectNoAxeViolations();
    await user.click(within(dialog).getByRole('button', { name: 'Ask for approval' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith(
        'Asked for approval',
        expect.stringContaining('A prescriber who did not ask'),
      ),
    );
    expect(server.requests.find((r) => r.method === 'PUT')?.body).toMatchObject({
      severity: 'Mild',
      status: 'Active',
      changeReason: 'Only a mild rash in 2019',
      version: 3,
    });
    expect(allergyChangeWaits({ status: 'Active', severity: 'Mild' }, { status: 'Active', severity: 'Severe' })).toBe(
      false,
    );
    expect(
      allergyChangeWaits({ status: 'Inactive', severity: 'Severe' }, { status: 'Inactive', severity: 'Mild' }),
    ).toBe(false);
    expect(
      allergyChangeWaits({ status: 'Active', severity: 'Mild' }, { status: 'EnteredInError', severity: 'Mild' }),
    ).toBe(true);
  });

  it('offers DNR only to doctors, and asks a second doctor to take it off', async () => {
    const dnr: PatientAlertDto = {
      id: '0192a000-0000-7000-8000-00000000f7c1',
      patientId: PATIENT_ID,
      type: 'Dnr',
      title: 'Do not resuscitate (DNR)',
      detail: 'Discussed with the son; consent form 12',
      severity: 'Critical',
      effectiveFrom: '2026-11-01T05:00:00Z',
      expiresOn: null,
      isActive: true,
      isCurrent: true,
      deactivatedAt: null,
      deactivationReason: null,
    };
    server = fakeServer({
      [`GET /api/v1/emr/patients/${PATIENT_ID}/alerts`]: [dnr],
      [`POST /api/v1/emr/alerts/${dnr.id}/deactivate`]: accepted({
        id: '0192a000-0000-7000-8000-00000000f7a6',
        kind: 'clinical.dnr_lift',
        summary: 'Take Do Not Resuscitate off NH26000777',
      }),
    });
    const user = userEvent.setup();
    show(<AlertsTab patientId={PATIENT_ID} />, DOCTOR);

    await user.click(await screen.findByRole('button', { name: 'Ask to take off' }, SLOW));
    const confirm = await screen.findByRole('alertdialog', { name: 'Ask to take Do Not Resuscitate off?' });
    expect(within(confirm).getByText(/a second doctor agrees first/)).toBeInTheDocument();
    await user.type(within(confirm).getByRole('textbox'), 'Family changed their mind after the meeting');
    await expectNoAxeViolations();
    await user.click(within(confirm).getByRole('button', { name: 'Ask for approval' }));
    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.stringContaining('A second doctor')),
    );

    // A nurse is offered neither DNR nor taking it off.
    vi.clearAllMocks();
    cleanup();
    server = fakeServer({ [`GET /api/v1/emr/patients/${PATIENT_ID}/alerts`]: [dnr] });
    show(<AlertsTab patientId={PATIENT_ID} />, NURSE);
    expect(await screen.findByText('Do not resuscitate (DNR)', {}, SLOW)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Ask to take off' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Add alert' }));
    const add = await screen.findByRole('dialog', { name: 'Add patient alert' });
    await user.click(within(add).getByRole('combobox', { name: /^Alert/ }));
    expect(screen.queryByRole('option', { name: /Do not resuscitate/ })).not.toBeInTheDocument();
  });

  it("asks the MLC register to clear a stay's medico-legal flag, saying free treatment stays until then", async () => {
    const admission = {
      id: '0192a000-0000-7000-8000-00000000f7d1',
      admissionNumber: 'IP26000099',
      patientId: PATIENT_ID,
      departmentId: 'dept-gm',
      consultantPractitionerId: 'doc-rao',
      provisionalDiagnosis: 'Observation',
      expectedDischargeDate: null,
      policyNumberMasked: null,
      isMlc: true,
      mlcNumber: 'MLC/26/0007',
      attendantName: null,
      attendantRelation: null,
      attendantPhone: null,
      notes: null,
      version: 11,
    } as unknown as AdmissionDetail;
    server = fakeServer({
      [`PUT /api/v1/inpatient/admissions/${admission.id}`]: accepted({
        id: '0192a000-0000-7000-8000-00000000f7a7',
        kind: 'inpatient.mlc_clear',
        summary: 'Medico-legal flag of IP26000099: MLC no. MLC/26/0007 → not a medico-legal case',
      }),
    });
    const user = userEvent.setup();
    show(<EditAdmissionDrawer admission={admission} open onClose={() => undefined} />, DESK);

    const drawer = await screen.findByRole('dialog', {}, SLOW);
    expect(within(drawer).getByRole('button', { name: 'Save' })).toBeInTheDocument();
    await user.click(within(drawer).getByRole('switch', { name: 'Medico-legal case (MLC)' }));
    expect(within(drawer).getByText(/ends free\s+treatment under BNSS s\.397/)).toBeInTheDocument();
    await user.click(within(drawer).getByRole('button', { name: 'Ask for approval' }));
    expect(await within(drawer).findByText(/Say why the stay is no longer medico-legal/)).toBeInTheDocument();
    await user.type(
      within(drawer).getByRole('textbox', { name: /^Why the stay is no longer medico-legal/ }),
      'Entered against the wrong stay',
    );
    await expectNoAxeViolations();
    await user.click(within(drawer).getByRole('button', { name: 'Ask for approval' }));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('Asked for approval', expect.stringContaining('MLC register')),
    );
    expect(server.requests.find((r) => r.method === 'PUT')?.body).toMatchObject({
      isMlc: false,
      changeReason: 'Entered against the wrong stay',
      version: 11,
    });
    expect(mlcChangeWaits({ isMlc: false, mlcNumber: null }, { isMlc: true, mlcNumber: 'X' })).toBe(false);
    expect(mlcChangeWaits({ isMlc: true, mlcNumber: null }, { isMlc: true, mlcNumber: 'X' })).toBe(false);
    expect(mlcChangeWaits({ isMlc: true, mlcNumber: 'X' }, { isMlc: true, mlcNumber: 'Y' })).toBe(true);
  });
});
