import type {
  ConsentDto,
  IdentityCheckDto,
  PatientDetail,
  PatientDocumentDto,
  PatientPayer,
  PatientPortalAccessDto,
  VisitListItem,
  WhatsAppConsentDto,
} from '@/modules/patients/api';
import { FACILITY_ID, paged, STAFF_USER, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// The patient record opened as RECORD_ID (Sunil Deshmukh), with what its tabs read: an open visit the desk can still
// close or cancel, consents, WhatsApp choices, portal family links, documents and identity checks.

const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const daysAgo = (days: number) => new Date(now - days * 86_400_000).toISOString().slice(0, 10);
const id = (suffix: string) => `0192a000-0000-7000-8000-0000000073${suffix}`;
const OPEN_VISIT_ID = id('20');

const PATIENT: PatientDetail = {
  id: RECORD_ID,
  uhid: 'NH26000187',
  title: 'Mr',
  firstName: 'Sunil',
  middleName: 'Vasant',
  lastName: 'Deshmukh',
  fullName: 'Sunil Vasant Deshmukh',
  gender: 'Male',
  dateOfBirth: '1971-08-22',
  isDateOfBirthEstimated: false,
  age: '55Y',
  bloodGroup: 'OPositive',
  maritalStatus: 'Married',
  phone: '+919822054321',
  alternatePhone: '+912024451234',
  email: 'sunil.deshmukh@example.in',
  address: {
    line1: 'Flat 302, Shree Apartments',
    line2: 'Karve Road, Kothrud',
    city: 'Pune',
    district: 'Pune',
    state: 'Maharashtra',
    pinCode: '411038',
    country: 'India',
  },
  guardianName: 'Vasant Deshmukh',
  guardianRelation: 'SonOf',
  emergencyContactName: 'Anjali Deshmukh',
  emergencyContactPhone: '+919822054322',
  emergencyContactRelation: 'Wife',
  occupation: 'Bank officer',
  religion: 'Hindu',
  nationality: 'Indian',
  preferredLanguage: 'Marathi',
  category: 'General',
  abhaNumberMasked: '91-XXXX-XXXX-4521',
  abhaAddress: 'sunil.deshmukh@abdm',
  aadhaarLast4: '7310',
  photoFileId: null,
  registeredFacilityId: FACILITY_ID,
  registeredAt: minutesAgo(60 * 24 * 320),
  isVip: false,
  isDeceased: false,
  deceasedAt: null,
  mergedIntoPatientId: null,
  notes: 'Prefers morning appointments.',
  isActive: true,
  version: 4,
};

const visit = (overrides: Partial<VisitListItem> & Pick<VisitListItem, 'id' | 'visitNumber'>): VisitListItem => ({
  patientId: RECORD_ID,
  uhid: PATIENT.uhid,
  patientName: PATIENT.fullName,
  gender: 'Male',
  age: PATIENT.age,
  type: 'OutPatient',
  status: 'Open',
  departmentId: '0192a000-0000-7000-8000-00000000de01',
  departmentName: 'General medicine',
  practitionerId: STAFF_USER.practitionerId,
  practitionerName: 'Dr. Meera Iyer',
  visitedAt: minutesAgo(40),
  completedAt: null,
  isFollowUp: true,
  isFreeFollowUp: false,
  payerType: 'Self',
  isMlc: false,
  tokenLabel: 'GM-015',
  tokenStatus: 'Waiting',
  ...overrides,
});

const VISITS: VisitListItem[] = [
  visit({ id: OPEN_VISIT_ID, visitNumber: 'OP2600990' }),
  visit({
    id: id('21'),
    visitNumber: 'OP2600412',
    status: 'Completed',
    isFollowUp: false,
    visitedAt: minutesAgo(60 * 24 * 21),
    completedAt: minutesAgo(60 * 24 * 21 - 35),
    tokenLabel: null,
    tokenStatus: null,
  }),
];

const PAYER: PatientPayer = {
  visitId: OPEN_VISIT_ID,
  isCurrentVisit: true,
  payerType: 'Self',
  payerId: null,
  payerName: null,
  policyNumber: null,
  validUntil: null,
};

const CONSENTS: ConsentDto[] = [
  {
    id: id('c1'),
    type: 'GeneralTreatment',
    consentVersion: '2.1',
    granted: true,
    capturedAt: minutesAgo(60 * 24 * 320),
    capturedByName: 'Pooja Nair',
    witnessName: null,
    signedByRelation: null,
    signedByName: null,
    documentFileId: null,
    revokedAt: null,
    revokedReason: null,
    revokedByName: null,
    notes: null,
    isActive: true,
  },
  {
    id: id('c2'),
    type: 'DataSharing',
    consentVersion: '1.0',
    granted: true,
    capturedAt: minutesAgo(60 * 24 * 21),
    capturedByName: 'Pooja Nair',
    witnessName: 'Anjali Deshmukh',
    signedByRelation: null,
    signedByName: null,
    documentFileId: null,
    revokedAt: null,
    revokedReason: null,
    revokedByName: null,
    notes: 'For ABHA record sharing.',
    isActive: true,
  },
];

const WHATSAPP: WhatsAppConsentDto = {
  phone: '98******21',
  optedIn: false,
  optedInAt: null,
  source: null,
  capturedByName: null,
  optedOut: false,
  optedOutAt: null,
  reachable: false,
};

const PORTAL: PatientPortalAccessDto = {
  ownPhone: '+91982*****21',
  links: [
    {
      id: id('f1'),
      accountPhone: '+919822054322',
      relation: 'Spouse',
      source: 'FrontDesk',
      linkedAt: minutesAgo(60 * 24 * 60),
      linkedByName: 'Pooja Nair',
      consentGivenBy: 'Patient',
      codeSentTo: null,
      revokedAt: null,
      revokedByName: null,
      revokedReason: null,
      isActive: true,
    },
  ],
};

const DOCUMENTS: PatientDocumentDto[] = [
  {
    id: id('d1'),
    fileId: id('df'),
    type: 'ExternalReport',
    title: 'HbA1c report — Ruby Hall lab',
    documentDate: daysAgo(30),
    source: 'Ruby Hall Clinic laboratory',
    notes: null,
    createdAt: minutesAgo(60 * 24 * 21),
    fileName: 'hba1c.pdf',
    contentType: 'application/pdf',
    sizeBytes: 182_340,
  },
];

const IDENTITY_CHECKS: IdentityCheckDto[] = [
  {
    id: id('22'),
    method: 'AadhaarFingerprint',
    outcome: 'Verified',
    message: 'Identity verified',
    aadhaarLast4: '7310',
    usedVirtualId: false,
    linkedAadhaar: false,
    provider: 'uidai',
    errorCode: null,
    transactionId: 'HC360-20261002-1182',
    responseCode: '4f1e0c2a9b',
    deviceProviderId: 'MANTRA.MSIPL',
    deviceModelId: 'MFS100',
    checkedAt: minutesAgo(60 * 24 * 21),
    checkedByName: 'Pooja Nair',
  },
];

/** Ravi Shankar (../accessibility/fixtures/frontOffice), whose lab report the lab screens open: staff, no open visit. */
const RAVI_ID = '0192a000-0000-7000-8000-000000000002';
const RAVI_PAYER: PatientPayer = {
  visitId: id('23'),
  isCurrentVisit: false,
  payerType: 'Staff',
  payerId: null,
  payerName: null,
  policyNumber: null,
  validUntil: null,
};

export const ANSWERS: FakeAnswers = {
  [`GET /api/v1/patients/${RAVI_ID}/payer`]: { current: RAVI_PAYER },
  [`GET /api/v1/patients/${RECORD_ID}`]: PATIENT,
  [`GET /api/v1/patients/${RECORD_ID}/visits`]: paged(VISITS),
  [`GET /api/v1/patients/${RECORD_ID}/payer`]: { current: PAYER },
  'GET /api/v1/patients/0192a000-0000-7000-8000-000000000006/payer': { current: PAYER },
  [`GET /api/v1/patients/${RECORD_ID}/consents`]: CONSENTS,
  [`GET /api/v1/patients/${RECORD_ID}/whatsapp`]: WHATSAPP,
  [`GET /api/v1/patients/${RECORD_ID}/portal-family`]: PORTAL,
  [`GET /api/v1/patients/${RECORD_ID}/documents`]: DOCUMENTS,
  [`GET /api/v1/patients/${RECORD_ID}/identity-checks`]: IDENTITY_CHECKS,
};
