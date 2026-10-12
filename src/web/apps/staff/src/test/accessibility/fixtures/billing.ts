import type { PatientFlags } from '@/modules/emr/api';
import type { BillDetail, BillLineDto, BillListItem, ChargeDto, PatientDues, ShiftDto } from '@/modules/billing/api';
import type { DiscountAuthority } from '@/modules/billing/billTotals';
import type { PackageListItem } from '@/modules/billing/mastersApi';
import type { PatientDetail, VisitListItem } from '@/modules/patients/api';
import { FACILITY_ID, paged, STAFF_USER, type FakeAnswers } from '../../fakeApi';
import { DOCTORS, OPD, PATIENTS } from './frontOffice';

// The OP billing counter with canned data: a patient with a visit to bill, a draft bill and an unpaid one.

const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();

export const ASHA = PATIENTS[0]!;
const VISIT_ID = '0192a000-0000-7000-8000-000000000b01';
const OLD_VISIT_ID = '0192a000-0000-7000-8000-000000000b02';
export const DRAFT_ID = '0192a000-0000-7000-8000-000000000b11';
const UNPAID_ID = '0192a000-0000-7000-8000-000000000b12';

const PATIENT: PatientDetail = {
  id: ASHA.id,
  uhid: ASHA.uhid,
  title: 'Mrs',
  firstName: 'Asha',
  middleName: null,
  lastName: 'Devi',
  fullName: ASHA.fullName,
  gender: 'Female',
  dateOfBirth: ASHA.dateOfBirth,
  isDateOfBirthEstimated: false,
  age: ASHA.age,
  bloodGroup: 'BPositive',
  maritalStatus: 'Married',
  phone: '+919876543210',
  alternatePhone: null,
  email: null,
  address: { line1: '14 Laxmi Road', city: 'Pune', state: 'Maharashtra', pinCode: '411030', country: 'India' },
  guardianName: null,
  guardianRelation: null,
  emergencyContactName: 'Ramesh Devi',
  emergencyContactPhone: '+919876500000',
  emergencyContactRelation: 'Husband',
  occupation: null,
  religion: null,
  nationality: 'Indian',
  preferredLanguage: 'Marathi',
  category: 'General',
  abhaNumberMasked: null,
  abhaAddress: null,
  aadhaarLast4: null,
  photoFileId: null,
  registeredFacilityId: FACILITY_ID,
  registeredAt: minutesAgo(60 * 24 * 40),
  isVip: false,
  isDeceased: false,
  deceasedAt: null,
  mergedIntoPatientId: null,
  notes: null,
  isActive: true,
  version: 3,
};

const visit = (overrides: Partial<VisitListItem> & Pick<VisitListItem, 'id' | 'visitNumber'>): VisitListItem => ({
  patientId: ASHA.id,
  uhid: ASHA.uhid,
  patientName: ASHA.fullName,
  gender: 'Female',
  age: ASHA.age,
  type: 'OutPatient',
  status: 'InProgress',
  departmentId: OPD.id,
  departmentName: OPD.name,
  practitionerId: DOCTORS[0]!.id,
  practitionerName: DOCTORS[0]!.name,
  visitedAt: minutesAgo(70),
  completedAt: null,
  isFollowUp: false,
  isFreeFollowUp: false,
  payerType: 'Self',
  isMlc: false,
  tokenLabel: 'GM-012',
  tokenStatus: 'InService',
  ...overrides,
});

const VISITS: VisitListItem[] = [
  visit({ id: VISIT_ID, visitNumber: 'NHOV260000210' }),
  visit({
    id: OLD_VISIT_ID,
    visitNumber: 'NHOV260000154',
    status: 'Completed',
    visitedAt: minutesAgo(60 * 24 * 9),
    completedAt: minutesAgo(60 * 24 * 9 - 40),
    tokenLabel: null,
    tokenStatus: null,
  }),
];

const charge = (overrides: Partial<ChargeDto> & Pick<ChargeDto, 'id' | 'description'>): ChargeDto => ({
  patientId: ASHA.id,
  visitId: VISIT_ID,
  admissionId: null,
  serviceItemId: `${overrides.id}-service`,
  serviceCode: 'CONS-NEW',
  category: 'Consultation',
  quantity: 1,
  unitPrice: 500,
  grossAmount: 500,
  discountAmount: 0,
  taxAmount: 0,
  netAmount: 500,
  priceSource: 'BasePrice',
  sourceModule: 'Emr',
  sourceReference: 'NHOV260000210',
  practitionerId: DOCTORS[0]!.id,
  practitionerName: DOCTORS[0]!.name,
  departmentId: OPD.id,
  serviceDate: minutesAgo(60),
  status: 'Unbilled',
  billId: null,
  cancelReason: null,
  ...overrides,
});

const CHARGES: ChargeDto[] = [
  charge({ id: 'c1', description: 'Consultation, new patient' }),
  charge({
    id: 'c2',
    description: 'Complete blood count',
    serviceCode: 'LAB-CBC',
    category: 'Laboratory',
    unitPrice: 350,
    grossAmount: 350,
    netAmount: 350,
    sourceModule: 'Lab',
  }),
];

const listItem = (overrides: Partial<BillListItem> & Pick<BillListItem, 'id' | 'status'>): BillListItem => ({
  billNumber: null,
  type: 'OutPatient',
  isFinalBill: true,
  patientId: ASHA.id,
  uhid: ASHA.uhid,
  patientName: ASHA.fullName,
  visitId: VISIT_ID,
  admissionId: null,
  payerType: 'Self',
  payerId: null,
  payerName: null,
  netAmount: 850,
  patientAmount: 850,
  payerAmount: 0,
  paidAmount: 0,
  dueAmount: 850,
  createdAt: minutesAgo(20),
  finalizedAt: null,
  ...overrides,
});

const DRAFTS: BillListItem[] = [listItem({ id: DRAFT_ID, status: 'Draft' })];

const UNPAID: BillListItem = listItem({
  id: UNPAID_ID,
  status: 'PartiallyPaid',
  billNumber: 'NHOB260000154',
  visitId: OLD_VISIT_ID,
  netAmount: 1200,
  patientAmount: 1200,
  paidAmount: 700,
  dueAmount: 500,
  createdAt: minutesAgo(60 * 24 * 9),
  finalizedAt: minutesAgo(60 * 24 * 9 - 30),
});

const DUES: PatientDues = { patientId: ASHA.id, totalDue: 500, advanceBalance: 0, bills: [UNPAID], advances: [] };

const line = (overrides: Partial<BillLineDto> & Pick<BillLineDto, 'id' | 'description'>): BillLineDto => ({
  chargeId: `${overrides.id}-charge`,
  serviceItemId: `${overrides.id}-service`,
  serviceCode: 'CONS-NEW',
  category: 'Consultation',
  sacCode: '999312',
  quantity: 1,
  unitPrice: 500,
  grossAmount: 500,
  packageCoveredAmount: 0,
  tariffDiscountAmount: 0,
  lineDiscountAmount: 0,
  billDiscountShare: 0,
  taxRate: 0,
  taxableAmount: 500,
  cgstAmount: 0,
  sgstAmount: 0,
  netAmount: 500,
  isDiscountAllowed: true,
  isPayerPayable: true,
  practitionerId: DOCTORS[0]!.id,
  practitionerName: DOCTORS[0]!.name,
  departmentId: OPD.id,
  serviceDate: minutesAgo(60),
  isReturn: false,
  ...overrides,
});

const DRAFT: BillDetail = {
  id: DRAFT_ID,
  billNumber: null,
  type: 'OutPatient',
  isFinalBill: true,
  status: 'Draft',
  facilityId: FACILITY_ID,
  patientId: ASHA.id,
  uhid: ASHA.uhid,
  patientName: ASHA.fullName,
  visitId: VISIT_ID,
  admissionId: null,
  ipAccountId: null,
  payerType: 'Self',
  payerId: null,
  payerName: null,
  tariffPlanId: null,
  tariffPlanName: 'General',
  policyId: null,
  preAuthorizationId: null,
  coPayPercent: 0,
  payerCoverageLimit: null,
  billDiscountPercent: null,
  billDiscountValue: null,
  discountReason: null,
  approvedDiscountAmount: 0,
  grossAmount: 850,
  packageCoveredAmount: 0,
  tariffDiscountAmount: 0,
  lineDiscountAmount: 0,
  billDiscountAmount: 0,
  discountAmount: 0,
  taxableAmount: 850,
  cgstAmount: 0,
  sgstAmount: 0,
  taxAmount: 0,
  roundOffAmount: 0,
  netAmount: 850,
  payerAmount: 0,
  patientAmount: 850,
  coPayAmount: 0,
  nonPayableAmount: 0,
  paidAmount: 0,
  refundedAmount: 0,
  creditAmount: 0,
  dueAmount: 850,
  refundableAmount: 0,
  payerSettledAmount: 0,
  payerDisallowedAmount: 0,
  isTaxInvoice: false,
  createdAt: minutesAgo(20),
  finalizedAt: null,
  cancelledAt: null,
  cancelReason: null,
  notes: null,
  lines: [
    line({ id: 'l1', description: 'Consultation, new patient' }),
    line({
      id: 'l2',
      description: 'Complete blood count',
      serviceCode: 'LAB-CBC',
      category: 'Laboratory',
      sacCode: '999316',
      unitPrice: 350,
      grossAmount: 350,
      taxableAmount: 350,
      netAmount: 350,
    }),
  ],
  receipts: [],
  refunds: [],
  creditNotes: [],
  pendingDiscount: null,
  package: null,
  version: 2,
};

const SHIFT: ShiftDto = {
  id: '0192a000-0000-7000-8000-000000000b21',
  userId: STAFF_USER.id,
  userName: STAFF_USER.displayName,
  counter: 'OP counter 1',
  status: 'Open',
  openedAt: minutesAgo(180),
  closedAt: null,
  openingFloat: 2000,
  expectedCash: 6450,
  declaredCash: null,
  variance: null,
  totals: [
    { mode: 'Cash', collected: 4450, refunded: 0, net: 4450 },
    { mode: 'Upi', collected: 3200, refunded: 0, net: 3200 },
  ],
  receiptCount: 14,
  notes: null,
  version: 5,
};

const FLAGS: PatientFlags = {
  allergyStatus: 'Recorded',
  allergies: [
    {
      id: '0192a000-0000-7000-8000-000000000b31',
      allergen: 'Penicillin',
      type: 'Drug',
      severity: 'Anaphylaxis',
      reaction: 'Facial swelling and breathlessness',
    },
  ],
  alerts: [],
};

const PACKAGES: PackageListItem[] = [
  {
    id: '0192a000-0000-7000-8000-000000000b41',
    code: 'PKG-CATARACT',
    name: 'Cataract surgery, day care',
    price: 18000,
    lengthOfStayDays: null,
    tariffPlanId: null,
    usedFor: 'Both',
    isActive: true,
    inclusionCount: 6,
  },
];

const DISCOUNT_AUTHORITY: DiscountAuthority = { canApprove: false, maxPercent: 10, maxAmount: 500 };

export const BILLING_ANSWERS: FakeAnswers = {
  [`GET /api/v1/patients/${ASHA.id}`]: PATIENT,
  [`GET /api/v1/patients/${ASHA.id}/visits`]: paged(VISITS),
  [`GET /api/v1/emr/patients/${ASHA.id}/flags`]: FLAGS,
  [`GET /api/v1/billing/patients/${ASHA.id}/dues`]: DUES,
  'GET /api/v1/billing/charges': paged(CHARGES),
  'GET /api/v1/billing/bills': paged(DRAFTS),
  [`GET /api/v1/billing/bills/${DRAFT_ID}`]: DRAFT,
  'GET /api/v1/billing/shifts/current': SHIFT,
  'GET /api/v1/billing/packages': PACKAGES,
  'GET /api/v1/billing/discount-limits/me': DISCOUNT_AUTHORITY,
  'GET /api/v1/masters/lookups/practitioners': DOCTORS,
  'GET /api/v1/print/letterhead': { name: 'Nana Hospitals, Pune' },
};
