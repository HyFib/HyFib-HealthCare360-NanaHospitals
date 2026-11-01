import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PatientDetail } from '@/modules/patients/api';
import { FACILITY_ID, fakeServer, openScreen, SCREEN_TESTS, SLOW, STAFF_USER, stubBrowserApis } from '../appScreen';
import { patientJourneys } from './fixtures/frontOffice';

// A patient's record as a role that neither contacts nor bills patients sees it (SRS PRV-03): the phone numbers, email,
// street address and emergency contact's phone come masked from the server, the page says why, and offers no editing.
// Checked with axe-core for WCAG 2.2 A and AA problems (SRS UX-10).

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const PATIENT_ID = '0192a000-0000-7000-8000-00000000c001';

/** As the server answers a lab technician: the contacts masked. */
const MASKED: PatientDetail = {
  id: PATIENT_ID,
  uhid: 'NH26000456',
  title: 'Ms.',
  firstName: 'Lata',
  middleName: null,
  lastName: 'Kulkarni',
  fullName: 'Lata Kulkarni',
  gender: 'Female',
  dateOfBirth: '1985-03-14',
  isDateOfBirthEstimated: false,
  age: '41 Y',
  bloodGroup: 'BPositive',
  maritalStatus: 'Married',
  phone: '+91******3210',
  alternatePhone: '+91******5544',
  email: 'l***@example.org',
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
  emergencyContactName: 'Ramesh',
  emergencyContactPhone: '+91******7788',
  emergencyContactRelation: 'Brother',
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
  registeredAt: '2026-10-20T04:30:00Z',
  isVip: false,
  isDeceased: false,
  deceasedAt: null,
  mergedIntoPatientId: null,
  notes: null,
  isActive: true,
  version: 7,
  contactsMasked: true,
};

/** Reads patients and registers them, but does not contact or bill them. */
const LAB_USER = { ...STAFF_USER, permissions: ['patients.read', 'patients.register', 'visits.read'] };

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  server = fakeServer({
    [`GET /api/v1/patients/${PATIENT_ID}`]: MASKED,
    [`GET /api/v1/patients/${PATIENT_ID}/payer`]: { current: null },
    [`GET /api/v1/patients/${PATIENT_ID}/journey`]: patientJourneys(PATIENT_ID),
    'GET /api/v1/masters/lookups/departments': [],
    'GET /api/v1/masters/lookups/practitioners': [],
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('patient record', SCREEN_TESTS, () => {
  it('with the contact details masked', async () => {
    openScreen(`/patients/${PATIENT_ID}`, LAB_USER);
    // In the banner and among the details.
    expect(await screen.findAllByText('+91******3210', {}, SLOW)).toHaveLength(2);

    expect(
      screen.getByText(/shown in part: in full they are for the staff who contact or bill patients/),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
    expect(server.unanswered).toEqual([]);
    await expectNoAxeViolations();
  });

  it('with the journey of her visit, her lab order and her operation', async () => {
    openScreen(`/patients/${PATIENT_ID}`, LAB_USER);

    const visit = await screen.findByRole('list', { name: 'Out-patient visit OP2600981' }, SLOW);
    expect(within(visit).getByText('In consultation').closest('li')).toHaveAttribute('aria-current', 'step');
    expect(screen.getByRole('list', { name: 'Lab order NHLB260000123' })).toBeInTheDocument();
    expect(screen.getByRole('list', { name: 'Operation OT2600045' })).toBeInTheDocument();
    expect(server.unanswered).toEqual([]);
    await expectNoAxeViolations();
  });

  it('opened for editing with the contact details masked', async () => {
    openScreen(`/patients/${PATIENT_ID}/edit`, LAB_USER);

    const refusal = await screen.findByText(/cannot edit this patient's details/, {}, SLOW);
    expect(
      within(refusal.closest('.fui-MessageBar') as HTMLElement).getByText(/Ask the front desk/),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Save/ })).not.toBeInTheDocument();
  });
});
