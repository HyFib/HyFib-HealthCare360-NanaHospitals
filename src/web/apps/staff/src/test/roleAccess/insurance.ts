import { todayIso } from '@hc360/core';
import type {
  ClaimDetail,
  ClaimListItem,
  NonPayableItemDto,
  PayerDetail,
  PayerListItem,
  PolicyDto,
  PreAuthDetail,
  PreAuthListItem,
  ReceivablesAgeingReport,
} from '@/modules/insurance/api';
import { paged, type FakeAnswers, type FakeRequest } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// The insurance desk: payers, a patient's policy, pre-authorisations and claims in the states that offer their
// actions, receivables ageing, and one submitted pre-authorisation and one submitted claim as the records.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const DAY = 24 * 60;

const ASHA = { patientId: '0192a000-0000-7000-8000-000000000001', uhid: 'NH26000123', patientName: 'Asha Devi' };
const RAVI = { patientId: '0192a000-0000-7000-8000-000000000002', uhid: 'NH26000124', patientName: 'Ravi Shankar' };
const LAKSHMI = { patientId: id('3'), uhid: 'NH26000131', patientName: 'Lakshmi Narayanan' };

const STAR = { payerId: id('1a01'), payerName: 'Star Health and Allied Insurance' };
const MEDI_ASSIST = { payerId: id('1a02'), payerName: 'Medi Assist TPA' };
const PMJAY = { payerId: id('1a03'), payerName: 'Mahatma Jyotiba Phule Jan Arogya Yojana' };

const PAYERS: PayerListItem[] = [
  {
    id: STAR.payerId,
    code: 'STAR',
    name: STAR.payerName,
    category: 'Insurer',
    contactPerson: 'Mr. Prakash Menon',
    phone: '1800 425 2255',
    tariffPlanId: id('7a01'),
    tariffPlanName: 'Insurance tariff 2026',
    creditDays: 30,
    isActive: true,
  },
  {
    id: MEDI_ASSIST.payerId,
    code: 'MEDIASSIST',
    name: MEDI_ASSIST.payerName,
    category: 'Tpa',
    contactPerson: 'Ms. Kavya Rao',
    phone: '080 4690 0000',
    tariffPlanId: id('7a01'),
    tariffPlanName: 'Insurance tariff 2026',
    creditDays: 45,
    isActive: true,
  },
  {
    id: PMJAY.payerId,
    code: 'MJPJAY',
    name: PMJAY.payerName,
    category: 'GovernmentScheme',
    contactPerson: null,
    phone: '155 388',
    tariffPlanId: null,
    tariffPlanName: null,
    creditDays: 60,
    isActive: true,
  },
];

const payerDetail = (p: PayerListItem): PayerDetail => ({
  id: p.id,
  code: p.code,
  name: p.name,
  category: p.category,
  contactPerson: p.contactPerson,
  phone: p.phone,
  email: `claims@${p.code.toLowerCase()}.example.in`,
  address: 'Pune, Maharashtra',
  gstin: p.category === 'GovernmentScheme' ? null : '27AABCS1234F1Z5',
  tariffPlanId: p.tariffPlanId,
  creditDays: p.creditDays,
  defaultCoPayPercent: p.category === 'Insurer' ? 10 : 0,
  portalUrl: null,
  isActive: p.isActive,
  notes: null,
  version: 2,
});

const NON_PAYABLES: NonPayableItemDto[] = [
  {
    id: id('1b01'),
    payerId: null,
    payerName: null,
    serviceItemId: null,
    serviceCode: null,
    serviceName: null,
    category: 'Miscellaneous',
    notes: 'Toiletries, attendant food and telephone are never payable.',
  },
  {
    id: id('1b02'),
    payerId: STAR.payerId,
    payerName: STAR.payerName,
    serviceItemId: id('1b11'),
    serviceCode: 'MISC-DIET',
    serviceName: 'Dietician consultation',
    category: null,
    notes: null,
  },
];

const POLICIES: PolicyDto[] = [
  {
    id: id('1c01'),
    patientId: ASHA.patientId,
    ...STAR,
    tpaId: MEDI_ASSIST.payerId,
    tpaName: MEDI_ASSIST.payerName,
    policyNumberMasked: 'XXXXXXXX4417',
    memberId: 'STAR-88231-02',
    policyHolderName: 'Ramesh Devi',
    relationToHolder: 'Spouse',
    validFrom: todayIso(-200),
    validTo: todayIso(165),
    sumInsured: 500000,
    coPayPercent: 10,
    isActive: true,
    isValidToday: true,
    verifiedAt: null,
    verificationNotes: null,
    cardFileId: null,
    notes: null,
    version: 1,
  },
];

const preAuth = (
  n: string,
  number: string,
  who: typeof ASHA,
  payer: typeof STAR,
  status: PreAuthListItem['status'],
  requestedAmount: number,
  approved = 0,
): PreAuthListItem => ({
  id: n === 'record' ? RECORD_ID : id(n),
  number,
  ...who,
  admissionId: id('ad01'),
  ...payer,
  requestedAmount,
  effectiveApprovedAmount: approved,
  status,
  createdAt: minutesAgo(2 * DAY),
  submittedAt: status === 'Draft' ? null : minutesAgo(DAY),
});

const PREAUTHS: PreAuthListItem[] = [
  preAuth('record', 'PA26-00412', ASHA, STAR, 'Submitted', 85000),
  preAuth('1d02', 'PA26-00409', RAVI, MEDI_ASSIST, 'Query', 240000),
  preAuth('1d03', 'PA26-00415', LAKSHMI, PMJAY, 'Draft', 60000),
  preAuth('1d04', 'PA26-00398', RAVI, STAR, 'PartiallyApproved', 150000, 110000),
];

const PREAUTH: PreAuthDetail = {
  id: RECORD_ID,
  number: 'PA26-00412',
  ...ASHA,
  admissionId: id('ad01'),
  visitId: null,
  policyId: id('1c01'),
  policyNumberMasked: 'XXXXXXXX4417',
  ...STAR,
  tpaId: MEDI_ASSIST.payerId,
  tpaName: MEDI_ASSIST.payerName,
  requestedAmount: 85000,
  diagnosisCodes: 'K80.20',
  procedureCodes: '0FT44ZZ',
  clinicalSummary: 'Symptomatic cholelithiasis; planned laparoscopic cholecystectomy under GA, 2 days stay.',
  expectedStayDays: 2,
  documentFileIds: [],
  status: 'Submitted',
  approvedAmount: 0,
  approvalReference: null,
  finalApprovedAmount: null,
  effectiveApprovedAmount: 0,
  queryText: null,
  queryResponse: null,
  rejectionReason: null,
  submittedAt: minutesAgo(DAY),
  approvedAt: null,
  rejectedAt: null,
  enhancements: [],
  version: 3,
};

const claim = (
  n: string,
  claimNumber: string,
  who: typeof ASHA,
  payer: typeof STAR,
  status: ClaimListItem['status'],
  claimedAmount: number,
  extra: Partial<ClaimListItem> = {},
): ClaimListItem => ({
  id: n === 'record' ? RECORD_ID : id(n),
  claimNumber,
  billId: id(`1e${n === 'record' ? '00' : n.slice(-2)}`),
  billNumber: `IP26-${claimNumber.slice(-5)}`,
  ...who,
  ...payer,
  claimedAmount,
  approvedAmount: null,
  settledAmount: 0,
  deductionAmount: 0,
  status,
  submittedAt: minutesAgo(10 * DAY),
  ...extra,
});

const CLAIMS: ClaimListItem[] = [
  claim('record', 'CL26-00233', ASHA, STAR, 'Submitted', 78450),
  claim('1f02', 'CL26-00219', RAVI, MEDI_ASSIST, 'Query', 212300, { submittedAt: minutesAgo(35 * DAY) }),
  claim('1f03', 'CL26-00201', LAKSHMI, PMJAY, 'Approved', 54000, {
    approvedAmount: 50000,
    submittedAt: minutesAgo(70 * DAY),
  }),
  claim('1f04', 'CL26-00188', RAVI, STAR, 'PartiallySettled', 132000, {
    approvedAmount: 120000,
    settledAmount: 100000,
    deductionAmount: 12000,
    submittedAt: minutesAgo(95 * DAY),
  }),
];

const CLAIM: ClaimDetail = {
  id: RECORD_ID,
  claimNumber: 'CL26-00233',
  billId: id('1e00'),
  billNumber: 'IP26-00233',
  ...ASHA,
  ...STAR,
  tpaId: MEDI_ASSIST.payerId,
  policyId: id('1c01'),
  preAuthorizationId: RECORD_ID,
  claimedAmount: 78450,
  diagnosisCodes: 'K80.20',
  procedureCodes: '0FT44ZZ',
  documentFileIds: [],
  status: 'Submitted',
  submittedAt: minutesAgo(10 * DAY),
  payerClaimReference: 'STAR/CL/2026/558812',
  queryText: null,
  queryResponse: null,
  approvedAmount: null,
  settledAmount: 0,
  deductionAmount: 0,
  outstanding: 78450,
  utrNumber: null,
  rejectionReason: null,
  settlements: [],
  deductions: [],
  verifiedCoding: { diagnosisCodes: 'K80.20', procedureCodes: '0FT44ZZ', verifiedAt: minutesAgo(11 * DAY) },
  version: 2,
};

const AGEING: ReceivablesAgeingReport = {
  asOf: todayIso(),
  payers: [
    {
      ...STAR,
      category: 'Insurer',
      creditDays: 30,
      days0To30: 78450,
      days31To60: 0,
      days61To90: 0,
      over90: 32000,
      total: 110450,
    },
    {
      ...MEDI_ASSIST,
      category: 'Tpa',
      creditDays: 45,
      days0To30: 0,
      days31To60: 212300,
      days61To90: 0,
      over90: 0,
      total: 212300,
    },
    {
      ...PMJAY,
      category: 'GovernmentScheme',
      creditDays: 60,
      days0To30: 0,
      days31To60: 0,
      days61To90: 50000,
      over90: 0,
      total: 50000,
    },
  ],
  days0To30: 78450,
  days31To60: 212300,
  days61To90: 50000,
  over90: 32000,
  total: 372750,
};

/** A list filtered by the status asked for (the desk counts each status this way). */
const byStatus =
  <T extends { status: string }>(rows: T[]) =>
  (request: FakeRequest) => {
    const status = request.url.searchParams.get('status');
    return paged(status ? rows.filter((r) => r.status === status) : rows);
  };

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/insurance/payers': PAYERS,
  ...Object.fromEntries(PAYERS.map((p) => [`GET /api/v1/insurance/payers/${p.id}`, payerDetail(p)])),
  'GET /api/v1/insurance/non-payable-items': NON_PAYABLES,
  'GET /api/v1/insurance/policies': POLICIES,
  'GET /api/v1/insurance/preauths': byStatus(PREAUTHS),
  [`GET /api/v1/insurance/preauths/${RECORD_ID}`]: PREAUTH,
  'GET /api/v1/insurance/claims': byStatus(CLAIMS),
  [`GET /api/v1/insurance/claims/${RECORD_ID}`]: CLAIM,
  'GET /api/v1/insurance/receivables/ageing': AGEING,
};
