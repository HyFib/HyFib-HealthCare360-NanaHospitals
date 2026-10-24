import type { AccrualDto, DoctorStatement, PayoutDto, ShareRuleDto } from '@/modules/doctor-fees/api';
import { paged, type FakeAnswers, type FakeRequest } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Doctor fee accounting: share rules (a doctor's own and a hospital-wide one), accruals (unpaid, in a payout, a
// reversal) and payouts in each state (awaiting approval, approved to pay, paid).

const now = Date.now();
const dateAgo = (days: number) => new Date(now - days * 86_400_000).toISOString().slice(0, 10);
const daysAgo = (days: number) => new Date(now - days * 86_400_000).toISOString();

const id = (suffix: string) => `0192a000-0000-7000-8000-00000000b${suffix}`;
const MEERA = { id: '0192a000-0000-7000-8000-0000000000d1', name: 'Dr. Meera Iyer' };
const ANIL = { id: '0192a000-0000-7000-8000-0000000000d2', name: 'Dr. Anil Rao' };

const RULES: ShareRuleDto[] = [
  {
    id: RECORD_ID,
    practitionerId: MEERA.id,
    practitionerName: MEERA.name,
    serviceItemId: null,
    serviceCode: null,
    category: 'Consultation',
    payerType: null,
    method: 'Percentage',
    value: 70,
    effectiveFrom: dateAgo(200),
    effectiveTo: null,
    isActive: true,
    notes: 'OPD consultations',
    version: 2,
  },
  {
    id: id('102'),
    practitionerId: ANIL.id,
    practitionerName: ANIL.name,
    serviceItemId: '0192a000-0000-7000-8000-00000000b901',
    serviceCode: 'PROC-ECHO',
    category: null,
    payerType: 'Insurance',
    method: 'FixedPerUnit',
    value: 600,
    effectiveFrom: dateAgo(90),
    effectiveTo: null,
    isActive: true,
    notes: null,
    version: 1,
  },
  {
    id: id('103'),
    practitionerId: null,
    practitionerName: null,
    serviceItemId: null,
    serviceCode: null,
    category: 'DoctorVisit',
    payerType: null,
    method: 'Percentage',
    value: 60,
    effectiveFrom: dateAgo(365),
    effectiveTo: null,
    isActive: true,
    notes: 'Default for in-patient visits',
    version: 1,
  },
];

const accrual = (
  overrides: Partial<AccrualDto> & Pick<AccrualDto, 'id' | 'billNumber' | 'basisAmount' | 'shareAmount'>,
): AccrualDto => ({
  practitionerId: MEERA.id,
  practitionerName: MEERA.name,
  billId: id('901'),
  serviceCode: 'CONS-GM',
  description: 'Consultation — General medicine',
  category: 'Consultation',
  payerType: 'Self',
  serviceDate: dateAgo(3),
  accruedOn: dateAgo(3),
  method: 'Percentage',
  rate: 70,
  status: 'Accrued',
  payoutId: null,
  isReversal: false,
  ...overrides,
});

const ACCRUALS: AccrualDto[] = [
  accrual({ id: RECORD_ID, billNumber: 'OP2600981', basisAmount: 800, shareAmount: 560 }),
  accrual({
    id: id('202'),
    billNumber: 'OP2600955',
    basisAmount: 800,
    shareAmount: 560,
    status: 'InPayout',
    payoutId: RECORD_ID,
    serviceDate: dateAgo(12),
    accruedOn: dateAgo(12),
  }),
  accrual({
    id: id('203'),
    billNumber: 'OP2600955',
    basisAmount: -800,
    shareAmount: -560,
    status: 'Reversed',
    isReversal: true,
    serviceDate: dateAgo(12),
    accruedOn: dateAgo(11),
  }),
];

const payout = (
  overrides: Partial<PayoutDto> & Pick<PayoutDto, 'id' | 'payoutNumber' | 'status' | 'grossAmount'>,
): PayoutDto => {
  const tdsAmount = Math.round(overrides.grossAmount * 0.1 * 100) / 100;
  return {
    practitionerId: MEERA.id,
    practitionerName: MEERA.name,
    periodFrom: dateAgo(40),
    periodTo: dateAgo(10),
    deductionAmount: 0,
    tdsPercent: 10,
    tdsAmount,
    netAmount: overrides.grossAmount - tdsAmount,
    approvedBy: null,
    approvedAt: null,
    paidAt: null,
    paidReference: null,
    paymentMode: null,
    notes: null,
    accrualCount: 42,
    deductions: [],
    version: 1,
    ...overrides,
  };
};

const PAYOUTS: PayoutDto[] = [
  payout({ id: RECORD_ID, payoutNumber: 'DFP2600031', status: 'Draft', grossAmount: 84_560 }),
  payout({
    id: id('302'),
    payoutNumber: 'DFP2600029',
    status: 'Approved',
    practitionerId: ANIL.id,
    practitionerName: ANIL.name,
    grossAmount: 1_12_400,
    deductionAmount: 2_500,
    netAmount: 1_12_400 - 11_240 - 2_500,
    deductions: [{ id: id('391'), description: 'OPD room rent (October)', amount: 2_500 }],
    approvedBy: 'Accounts — Sunil Mehta',
    approvedAt: daysAgo(2),
    version: 2,
  }),
  payout({
    id: id('303'),
    payoutNumber: 'DFP2600024',
    status: 'Paid',
    grossAmount: 76_200,
    periodFrom: dateAgo(70),
    periodTo: dateAgo(41),
    approvedBy: 'Accounts — Sunil Mehta',
    approvedAt: daysAgo(35),
    paidAt: daysAgo(33),
    paidReference: 'NEFT HDFC0001234 UTR 4567812390',
    paymentMode: 'NetBanking',
    version: 3,
  }),
];

const STATEMENT: DoctorStatement = {
  practitionerId: MEERA.id,
  practitionerName: MEERA.name,
  from: dateAgo(30),
  to: dateAgo(0),
  accrued: 1_120,
  reversed: 560,
  inPayout: 560,
  paidOut: 0,
  unpaid: 560,
  accruals: ACCRUALS,
  payouts: PAYOUTS.filter((p) => p.practitionerId === MEERA.id),
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/doctor-fees/rules': RULES,
  'GET /api/v1/doctor-fees/accruals': paged(ACCRUALS),
  'GET /api/v1/doctor-fees/payouts': paged(PAYOUTS),
  'GET /api/v1/doctor-fees/payouts/*': (request: FakeRequest) => PAYOUTS.find((p) => request.path.endsWith(p.id)),
  'GET /api/v1/doctor-fees/statements': STATEMENT,
};
