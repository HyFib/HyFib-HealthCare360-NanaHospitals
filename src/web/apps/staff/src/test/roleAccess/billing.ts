import type {
  BillDetail,
  DiscountApprovalDto,
  DiscountLimitDto,
  GatewayOrderDto,
  PatientDues,
  ReceiptDto,
  ReceiptListItem,
  RefundDto,
  ShiftDto,
} from '@/modules/billing/api';
import type { GatewayDiscrepancyDto, ReconciliationSummaryDto, SettlementDayDto } from '@/modules/billing/gatewayApi';
import type {
  ClearanceDto,
  IpAccountDetail,
  IpAccountListItem,
  PackageStatus,
  RunningBill,
} from '@/modules/billing/ipApi';
import type { HbpProcedure } from '@/modules/billing/hbpApi';
import type { PackageDetail, TariffPlanDetail, TariffPlanListItem, TariffPriceDto } from '@/modules/billing/mastersApi';
import type {
  PosSettlementDayDto,
  PosSettlementImportDto,
  PosSettlementLineDto,
  PosSettlementSummaryDto,
  UnsettledPosReceiptDto,
} from '@/modules/billing/posSettlementApi';
import type { AccountingVoucher, TallyDayCheck, TallyDayVouchers, TallyExportDto } from '@/modules/billing/tallyApi';
import { ASHA, DRAFT, DRAFT_ID } from '../accessibility/fixtures/billing';
import { paged, STAFF_USER, type FakeAnswers, type FakeRequest } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';
import { IN_PATIENTS } from './inpatient';

// Billing: Asha Devi's OP bill opened as RECORD_ID (finalized, part paid: collect, refund, credit note, cancel); the
// refunds in each state that has its own action (awaiting approval, approved to pay, paid); a discount waiting for a
// decision on the draft bill of ../accessibility/fixtures/billing; receipts, counter shifts, payment links, the
// in-patient accounts (RECORD_ID is Sunil Deshmukh's, open), tariffs and a package, the gateway and card-terminal
// reconciliations, and the Tally postings.

const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const dateIn = (days: number) => new Date(now + days * 86_400_000).toISOString().slice(0, 10);
const id = (suffix: string) => `0192a000-0000-7000-8000-0000000077${suffix}`;
const SUNIL = IN_PATIENTS[0]!;

// ------------------------------------------------------------------ the bill

const BILL: BillDetail = {
  ...DRAFT,
  id: RECORD_ID,
  billNumber: 'NHOB260000198',
  status: 'PartiallyPaid',
  isTaxInvoice: true,
  paidAmount: 500,
  dueAmount: 350,
  refundableAmount: 500,
  createdAt: minutesAgo(90),
  finalizedAt: minutesAgo(85),
  receipts: [
    {
      id: id('01'),
      receiptNumber: 'NHOR260001342',
      purpose: 'BillPayment',
      amount: 500,
      modes: 'Upi',
      receivedAt: minutesAgo(84),
    },
  ],
  version: 4,
};

// ------------------------------------------------------------------ receipts

const receipt = (
  overrides: Partial<ReceiptListItem> & Pick<ReceiptListItem, 'id' | 'receiptNumber' | 'amount'>,
): ReceiptListItem => ({
  purpose: 'BillPayment',
  patientId: ASHA.id,
  uhid: ASHA.uhid,
  patientName: ASHA.fullName,
  billId: RECORD_ID,
  modes: 'Upi',
  counter: 'OP counter 1',
  receivedByName: STAFF_USER.displayName,
  receivedAt: minutesAgo(84),
  ...overrides,
});

const RECEIPTS: ReceiptListItem[] = [
  receipt({ id: id('01'), receiptNumber: 'NHOR260001342', amount: 500 }),
  receipt({
    id: id('02'),
    receiptNumber: 'NHOR260001350',
    amount: 20_000,
    purpose: 'Deposit',
    patientId: SUNIL.patientId,
    uhid: SUNIL.uhid,
    patientName: SUNIL.patientName,
    billId: null,
    modes: 'Cash + Card',
    counter: 'IP counter',
    receivedAt: minutesAgo(60 * 24 * 3),
  }),
];

const RECEIPT: ReceiptDto = {
  id: id('01'),
  receiptNumber: 'NHOR260001342',
  purpose: 'BillPayment',
  patientId: ASHA.id,
  uhid: ASHA.uhid,
  patientName: ASHA.fullName,
  billId: RECORD_ID,
  billNumber: 'NHOB260000198',
  ipAccountId: null,
  amount: 500,
  source: 'Counter',
  counter: 'OP counter 1',
  receivedByName: STAFF_USER.displayName,
  receivedAt: minutesAgo(84),
  notes: null,
  payments: [
    {
      mode: 'Upi',
      amount: 500,
      reference: '429811736520',
      cardLast4: null,
      cardNetwork: null,
      chequeNumber: null,
      chequeBank: null,
      chequeDate: null,
    },
  ],
};

// ------------------------------------------------------------------ refunds

const refund = (overrides: Partial<RefundDto> & Pick<RefundDto, 'id' | 'refundNumber' | 'status'>): RefundDto => ({
  purpose: 'Bill',
  patientId: ASHA.id,
  uhid: ASHA.uhid,
  patientName: ASHA.fullName,
  billId: RECORD_ID,
  ipAccountId: null,
  amount: 350,
  reason: 'Blood test not done: sample could not be collected',
  requestedByName: 'Pooja Nair',
  requestedAt: minutesAgo(45),
  decidedBy: null,
  decidedAt: null,
  decisionRemarks: null,
  mode: null,
  reference: null,
  paidAt: null,
  advanceId: null,
  gatewayStatus: null,
  gatewayReference: null,
  gatewayError: null,
  ...overrides,
});

const REFUNDS: RefundDto[] = [
  refund({ id: RECORD_ID, refundNumber: 'NHRF260000071', status: 'Requested' }),
  refund({
    id: id('12'),
    refundNumber: 'NHRF260000069',
    status: 'Approved',
    purpose: 'Deposit',
    patientId: SUNIL.patientId,
    uhid: SUNIL.uhid,
    patientName: SUNIL.patientName,
    billId: null,
    ipAccountId: RECORD_ID,
    amount: 4_250,
    reason: 'Deposit left over after the final bill',
    requestedAt: minutesAgo(60 * 5),
    decidedBy: 'Accounts Manager',
    decidedAt: minutesAgo(60 * 4),
  }),
  refund({
    id: id('13'),
    refundNumber: 'NHRF260000064',
    status: 'Paid',
    amount: 500,
    reason: 'Consultation cancelled by the doctor',
    requestedAt: minutesAgo(60 * 30),
    decidedBy: 'Accounts Manager',
    decidedAt: minutesAgo(60 * 29),
    mode: 'Upi',
    reference: 'UPI-4410923',
    paidAt: minutesAgo(60 * 28),
  }),
];

// ------------------------------------------------------------------ discounts

const APPROVALS: DiscountApprovalDto[] = [
  {
    id: RECORD_ID,
    billId: DRAFT_ID,
    billNumber: null,
    patientId: ASHA.id,
    uhid: ASHA.uhid,
    patientName: ASHA.fullName,
    requestedAmount: 170,
    discountBase: 850,
    billDiscountPercent: 20,
    billDiscountAmount: null,
    reason: 'Hospital staff relative',
    status: 'Pending',
    requestedByName: 'Pooja Nair',
    requestedAt: minutesAgo(12),
    decidedBy: null,
    decidedAt: null,
    remarks: null,
  },
];

const LIMITS: DiscountLimitDto[] = [
  {
    id: id('21'),
    userId: '0192a000-0000-7000-8000-0000000000a7',
    userName: 'Pooja Nair',
    maxPercent: 10,
    maxAmount: 500,
    notes: 'OP counter',
    version: 1,
  },
];

// ------------------------------------------------------------------ counter shifts

const shift = (overrides: Partial<ShiftDto> & Pick<ShiftDto, 'id' | 'status'>): ShiftDto => ({
  userId: STAFF_USER.id,
  userName: STAFF_USER.displayName,
  counter: 'OP counter 1',
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
  ...overrides,
});

const SHIFTS: ShiftDto[] = [
  shift({ id: RECORD_ID, status: 'Open' }),
  shift({
    id: id('32'),
    status: 'Closed',
    userId: '0192a000-0000-7000-8000-0000000000a7',
    userName: 'Pooja Nair',
    counter: 'OP counter 2',
    openedAt: minutesAgo(60 * 9),
    closedAt: minutesAgo(60 * 1),
    expectedCash: 12_300,
    declaredCash: 12_250,
    variance: -50,
    receiptCount: 31,
    notes: 'Short by ₹50; noted in the register.',
  }),
];

// ------------------------------------------------------------------ payment links

const order = (
  overrides: Partial<GatewayOrderDto> & Pick<GatewayOrderDto, 'id' | 'orderId' | 'status'>,
): GatewayOrderDto => ({
  provider: 'razorpay',
  kind: 'PaymentLink',
  amount: 350,
  currency: 'INR',
  linkUrl: 'https://rzp.io/i/nh8Kq2',
  checkoutKey: null,
  billId: RECORD_ID,
  ipAccountId: null,
  receiptId: null,
  channel: 'Staff',
  createdAt: minutesAgo(30),
  paidAt: null,
  lastError: null,
  paymentId: null,
  ...overrides,
});

const LINKS: GatewayOrderDto[] = [
  order({ id: RECORD_ID, orderId: 'plink_Nh2026Kq8', status: 'Created' }),
  order({ id: id('42'), orderId: 'plink_Nh2026Fa1', status: 'Failed', lastError: 'Payment declined by the bank' }),
  order({
    id: id('43'),
    orderId: 'plink_Nh2026Pd4',
    status: 'Paid',
    amount: 15_000,
    billId: null,
    ipAccountId: RECORD_ID,
    receiptId: id('02'),
    paidAt: minutesAgo(60 * 20),
    paymentId: 'pay_Nh20264412',
  }),
];

// ------------------------------------------------------------------ in-patient accounts

const ACCOUNTS: IpAccountListItem[] = [
  {
    id: RECORD_ID,
    admissionId: SUNIL.admissionId,
    admissionNumber: SUNIL.admissionNumber,
    patientId: SUNIL.patientId,
    uhid: SUNIL.uhid,
    patientName: SUNIL.patientName,
    payerType: 'Self',
    payerName: null,
    status: 'Open',
    admittedAt: minutesAgo(60 * 24 * 3),
    dischargedAt: null,
    estimatedAmount: 45_000,
    depositBalance: 20_000,
    hasPackage: false,
  },
  {
    id: id('52'),
    admissionId: IN_PATIENTS[2]!.admissionId,
    admissionNumber: IN_PATIENTS[2]!.admissionNumber,
    patientId: IN_PATIENTS[2]!.patientId,
    uhid: IN_PATIENTS[2]!.uhid,
    patientName: IN_PATIENTS[2]!.patientName,
    payerType: 'Insurance',
    payerName: 'Star Health',
    status: 'DischargeInitiated',
    admittedAt: minutesAgo(60 * 24 * 8),
    dischargedAt: null,
    estimatedAmount: 120_000,
    depositBalance: 10_000,
    hasPackage: true,
  },
];

const ACCOUNT: IpAccountDetail = {
  id: RECORD_ID,
  admissionId: SUNIL.admissionId,
  admissionNumber: SUNIL.admissionNumber,
  patientId: SUNIL.patientId,
  uhid: SUNIL.uhid,
  patientName: SUNIL.patientName,
  visitId: id('53'),
  payerType: 'Self',
  payerId: null,
  payerName: null,
  tariffPlanId: id('61'),
  tariffPlanName: 'General',
  policyId: null,
  attendingPractitionerId: STAFF_USER.practitionerId,
  attendingPractitionerName: 'Dr. Meera Iyer',
  bedServiceItemId: id('54'),
  bedServiceCode: 'BED-GEN',
  admittedAt: minutesAgo(60 * 24 * 3),
  dischargeInitiatedAt: null,
  dischargedAt: null,
  status: 'Open',
  estimatedAmount: 45_000,
  depositReceived: 20_000,
  depositAdjusted: 0,
  depositRefunded: 0,
  depositBalance: 20_000,
  packageId: null,
  packageName: null,
  dischargeWithDuesApproved: false,
  dischargeApprovalReason: null,
  notes: null,
  version: 3,
};

const RUNNING_BILL: RunningBill = {
  ipAccountId: RECORD_ID,
  admissionNumber: SUNIL.admissionNumber,
  status: 'Open',
  byCategory: [
    { category: 'BedCharge', items: 3, gross: 7_500, discount: 0, tax: 0, net: 7_500 },
    { category: 'Laboratory', items: 4, gross: 2_950, discount: 0, tax: 0, net: 2_950 },
    { category: 'DoctorVisit', items: 3, gross: 3_000, discount: 0, tax: 0, net: 3_000 },
  ],
  // No days: the "By day" view draws them on a canvas chart (ECharts), which the test DOM cannot draw.
  byDate: [],
  lines: [
    {
      chargeId: id('71'),
      serviceDate: minutesAgo(60 * 24 * 2),
      serviceCode: 'BED-GEN',
      description: 'General ward bed, per day',
      category: 'BedCharge',
      quantity: 1,
      unitPrice: 2_500,
      netAmount: 2_500,
      status: 'Unbilled',
      billId: null,
      practitionerName: null,
      isReturn: false,
    },
    {
      chargeId: id('72'),
      serviceDate: minutesAgo(60 * 24),
      serviceCode: 'LAB-CRP',
      description: 'C-reactive protein',
      category: 'Laboratory',
      quantity: 1,
      unitPrice: 650,
      netAmount: 650,
      status: 'Unbilled',
      billId: null,
      practitionerName: 'Dr. Meera Iyer',
      isReturn: false,
    },
  ],
  grossTotal: 13_450,
  discountTotal: 0,
  taxTotal: 0,
  netTotal: 13_450,
  unbilledNet: 13_450,
  billedNet: 0,
  estimatedPatientPayable: 13_450,
  paidOnBills: 0,
  depositBalance: 20_000,
  estimatedBalance: 6_550,
  estimatedAmount: 45_000,
  package: null,
  insurance: null,
  returnsNet: 0,
};

const IP_CLEARANCE: ClearanceDto = {
  cleared: false,
  outstanding: 0,
  reason: 'The final bill has not been made.',
  unbilledCount: 10,
  unbilledAmount: 13_450,
  hasFinalBill: false,
  patientDue: 0,
  depositBalance: 20_000,
  dischargeWithDuesApproved: false,
  billedOnInterimBills: false,
};

const NO_PACKAGE: PackageStatus | null = null;

const SUNIL_DUES: PatientDues = {
  patientId: SUNIL.patientId,
  totalDue: 0,
  advanceBalance: 20_000,
  bills: [],
  advances: [],
};

// ------------------------------------------------------------------ tariffs and packages

const PLANS: TariffPlanListItem[] = [
  {
    id: RECORD_ID,
    code: 'GEN',
    name: 'General',
    type: 'General',
    payerId: null,
    payerName: null,
    facilityId: null,
    defaultDiscountPercent: 0,
    isDefault: true,
    isActive: true,
    priceCount: 214,
  },
  {
    id: id('62'),
    code: 'STAR',
    name: 'Star Health TPA',
    type: 'Insurance',
    payerId: id('63'),
    payerName: 'Star Health and Allied Insurance',
    facilityId: null,
    defaultDiscountPercent: 10,
    isDefault: false,
    isActive: true,
    priceCount: 88,
  },
];

const PLAN: TariffPlanDetail = {
  id: RECORD_ID,
  code: 'GEN',
  name: 'General',
  type: 'General',
  payerId: null,
  facilityId: null,
  defaultDiscountPercent: 0,
  isDefault: true,
  isActive: true,
  description: 'Walk-in patients paying themselves.',
  bedMultipliers: [
    { bedServiceItemId: id('54'), bedServiceCode: 'BED-GEN', bedServiceName: 'General ward bed', multiplier: 1 },
  ],
  version: 6,
};

const PRICES: TariffPriceDto[] = [
  {
    id: id('64'),
    serviceItemId: id('65'),
    serviceCode: 'CONS-NEW',
    serviceName: 'Consultation, new patient',
    category: 'Consultation',
    basePrice: 500,
    price: 500,
    effectiveFrom: dateIn(-200),
    effectiveTo: null,
    version: 1,
  },
  {
    id: id('66'),
    serviceItemId: id('67'),
    serviceCode: 'LAB-CBC',
    serviceName: 'Complete blood count',
    category: 'Laboratory',
    basePrice: 350,
    price: 320,
    effectiveFrom: dateIn(-60),
    effectiveTo: null,
    version: 2,
  },
];

/** An AB PM-JAY HBP procedure the package is mapped to. */
const HBP_PROCEDURE: HbpProcedure = {
  code: 'SG039A',
  packageCode: 'SG039',
  specialtyCode: 'SG',
  specialty: 'General Surgery',
  packageName: 'Appendicectomy',
  procedureName: 'Appendicectomy (laparoscopic)',
  rate: 21000,
  lengthOfStayDays: 3,
  reservedForPublic: false,
  stratification: null,
  masterVersion: 'HBP 2022',
  isActive: true,
};

const PACKAGE_ID = '0192a000-0000-7000-8000-000000000b41';
const PACKAGE: PackageDetail = {
  id: PACKAGE_ID,
  code: 'PKG-CATARACT',
  name: 'Cataract surgery, day care',
  serviceItemId: id('68'),
  serviceCode: 'PKG-CATARACT',
  price: 18_000,
  lengthOfStayDays: null,
  tariffPlanId: null,
  usedFor: 'Both',
  description: 'Phacoemulsification with foldable IOL, one eye.',
  isActive: true,
  hbpCode: 'SG039A',
  hbpProcedureName: 'Appendicectomy (laparoscopic)',
  inclusions: [
    {
      id: id('69'),
      serviceItemId: null,
      serviceCode: null,
      serviceName: null,
      category: 'Surgery',
      maxQuantity: 1,
      maxAmount: null,
      notes: null,
    },
  ],
  version: 2,
};

// ------------------------------------------------------------------ gateway reconciliation

const GATEWAY_SUMMARY: ReconciliationSummaryDto = {
  enabled: true,
  settlementDays: 30,
  open: [
    { kind: 'UnrecordedPayment', count: 1, amount: 350 },
    { kind: 'UnsettledReceipt', count: 1, amount: 15_000 },
  ],
  lastSettledOn: dateIn(-1),
  lastFetchedAt: minutesAgo(60 * 3),
};

const discrepancy = (
  overrides: Partial<GatewayDiscrepancyDto> & Pick<GatewayDiscrepancyDto, 'id' | 'kind' | 'key' | 'summary'>,
): GatewayDiscrepancyDto => ({
  rank: 1,
  status: 'Open',
  orderId: 'plink_Nh2026Kq8',
  recordedAmount: null,
  gatewayAmount: 350,
  gatewayAt: minutesAgo(25),
  billId: RECORD_ID,
  billNumber: 'NHOB260000198',
  ipAccountId: null,
  receiptId: null,
  receiptNumber: null,
  refundId: null,
  refundNumber: null,
  appointmentId: null,
  appointmentNumber: null,
  gatewayOrderRecordId: RECORD_ID,
  canRecordReceipt: true,
  detectedAt: minutesAgo(20),
  lastSeenAt: minutesAgo(5),
  resolvedAt: null,
  resolvedByName: null,
  resolution: null,
  resolvedAutomatically: false,
  version: 1,
  ...overrides,
});

const EXCEPTIONS: GatewayDiscrepancyDto[] = [
  discrepancy({
    id: RECORD_ID,
    kind: 'UnrecordedPayment',
    key: 'pay_Nh20265501',
    summary: '₹350 paid online for bill NHOB260000198, no receipt in HC360',
  }),
  discrepancy({
    id: id('82'),
    kind: 'RefundMismatch',
    rank: 2,
    key: 'rfnd_Nh2026099',
    summary: 'Refund NHRF260000064 failed at the gateway',
    orderId: null,
    recordedAmount: 500,
    gatewayAmount: 0,
    billId: null,
    billNumber: null,
    refundId: id('13'),
    refundNumber: 'NHRF260000064',
    gatewayOrderRecordId: null,
    canRecordReceipt: false,
  }),
];

const GATEWAY_SETTLEMENTS: SettlementDayDto[] = [
  {
    date: dateIn(-1),
    count: 1,
    paymentsAmount: 48_600,
    refundsAmount: 500,
    adjustmentsAmount: 0,
    charges: 972,
    tax: 175,
    amount: 46_953,
    unmatched: 0,
    settlements: [
      {
        id: id('83'),
        provider: 'razorpay',
        settlementId: 'setl_Nh2026A1',
        utr: 'HDFCR52026102300418',
        status: 'Processed',
        settledAt: minutesAgo(60 * 20),
        settledOn: dateIn(-1),
        paymentsAmount: 48_600,
        refundsAmount: 500,
        adjustmentsAmount: 0,
        charges: 972,
        tax: 175,
        amount: 46_953,
        difference: 0,
        lines: 23,
        unmatched: 0,
        isShare: false,
      },
    ],
  },
];

// ------------------------------------------------------------------ card terminal settlements

const POS_IMPORT: PosSettlementImportDto = {
  id: id('91'),
  acquirer: 'HDFC Bank',
  fileName: 'MPR_23102026.csv',
  fileSizeBytes: 18_204,
  rows: 41,
  added: 41,
  alreadyImported: 0,
  ignored: 0,
  firstSettledOn: dateIn(-1),
  lastSettledOn: dateIn(-1),
  importedAt: minutesAgo(60 * 5),
  importedByName: 'Accounts Manager',
};

const POS_SUMMARY: PosSettlementSummaryDto = {
  settlementDays: 30,
  toLookAt: [{ match: 'NoReceipt', count: 1, amount: 1_200 }],
  unsettled: 1,
  unsettledAmount: 750,
  lastSettledOn: dateIn(-1),
  lastImport: POS_IMPORT,
  otherTerminals: ['PH-T02'],
};

const posLine = (
  overrides: Partial<PosSettlementLineDto> & Pick<PosSettlementLineDto, 'id' | 'match' | 'amount'>,
): PosSettlementLineDto => ({
  acquirer: 'HDFC Bank',
  type: 'Sale',
  mode: 'Card',
  terminalId: 'OP-T01',
  transactionAt: minutesAgo(60 * 28),
  transactionOn: dateIn(-2),
  charges: 0,
  tax: 0,
  net: overrides.amount,
  rrn: '429900184420',
  approvalCode: '611904',
  cardLast4: '4417',
  settledOn: dateIn(-1),
  utr: 'HDFCR52026102300512',
  matchBasis: null,
  recordedAmount: null,
  receiptId: null,
  receiptNumber: null,
  billId: null,
  refundId: null,
  refundNumber: null,
  needsAttention: false,
  reviewedAt: null,
  reviewedByName: null,
  reviewNote: null,
  version: 1,
  ...overrides,
});

const POS_LINES: PosSettlementLineDto[] = [
  posLine({ id: RECORD_ID, match: 'NoReceipt', amount: 1_200, needsAttention: true }),
  posLine({
    id: id('92'),
    match: 'Matched',
    amount: 850,
    matchBasis: 'Rrn',
    recordedAmount: 850,
    receiptId: id('01'),
    receiptNumber: 'NHOR260001342',
    billId: RECORD_ID,
  }),
];

const POS_DAYS: PosSettlementDayDto[] = [
  {
    date: dateIn(-1),
    lines: 41,
    sales: 62_400,
    refunds: 0,
    charges: 1_123,
    tax: 202,
    net: 61_075,
    toLookAt: 1,
    payouts: [
      {
        settledOn: dateIn(-1),
        utr: 'HDFCR52026102300512',
        acquirers: 'HDFC Bank',
        lines: 41,
        sales: 62_400,
        refunds: 0,
        charges: 1_123,
        tax: 202,
        net: 61_075,
        toLookAt: 1,
        voucherNumber: 'POS/2026/0412',
      },
    ],
  },
];

const UNSETTLED: UnsettledPosReceiptDto[] = [
  {
    receiptId: id('93'),
    receiptNumber: 'NHOR260001277',
    paymentId: id('94'),
    billId: null,
    receivedAt: minutesAgo(60 * 24 * 4),
    mode: 'Upi',
    amount: 750,
    reference: '428812004417',
    cardLast4: null,
    terminalId: 'OP-T01',
    daysWaiting: 4,
  },
];

// ------------------------------------------------------------------ Tally

const tallyExport = (
  overrides: Partial<TallyExportDto> & Pick<TallyExportDto, 'id' | 'date' | 'status'>,
): TallyExportDto => ({
  voucherCount: 46,
  attempts: 1,
  createdCount: 46,
  alteredCount: 0,
  errorCount: 0,
  createdAt: minutesAgo(60 * 12),
  lastAttemptAt: minutesAgo(60 * 12),
  postedAt: minutesAgo(60 * 12),
  message: null,
  confirmedCount: 46,
  rejectedCount: 0,
  fileDownloadedAt: null,
  importedAt: null,
  importedByName: null,
  recordsCheck: 'Matched',
  recordsCheckedAt: minutesAgo(60 * 11),
  recordsDifferences: 0,
  tallyCheck: 'NotChecked',
  tallyCheckedAt: null,
  tallyDifferences: 0,
  tallyCheckMessage: null,
  ...overrides,
});

const TALLY_EXPORTS: TallyExportDto[] = [
  tallyExport({ id: RECORD_ID, date: dateIn(-1), status: 'Posted' }),
  tallyExport({
    id: id('a2'),
    date: dateIn(-2),
    status: 'Failed',
    createdCount: 40,
    errorCount: 3,
    confirmedCount: 40,
    rejectedCount: 3,
    message: 'Ledger "UPI Collections" does not exist in Tally',
    recordsCheck: 'NotChecked',
    recordsCheckedAt: null,
  }),
];

const DAY_BOOK: AccountingVoucher[] = [
  {
    voucherType: 'Sales',
    date: dateIn(0),
    number: 'NHOB260000198',
    narration: 'OP bill, Asha Devi (NH26000123)',
    entries: [
      { ledgerName: 'Asha Devi', amount: 850, isDebit: true, costCentre: null },
      { ledgerName: 'OP Consultation Income', amount: 500, isDebit: false, costCentre: 'General medicine' },
      { ledgerName: 'Laboratory Income', amount: 350, isDebit: false, costCentre: 'Laboratory' },
    ],
    reference: RECORD_ID,
    remoteId: null,
    key: `bill:${RECORD_ID}`,
    amount: 850,
  },
];

const SENDING = {
  vouchers: 46,
  inTally: 46,
  notSent: 0,
  refused: 0,
  changedSinceSent: 0,
  differsInTally: 0,
  noLongerInHc360: 0,
  onlyInTally: 0,
  complete: true,
};

const tallyDay = (request: FakeRequest): TallyDayVouchers | TallyDayCheck | undefined => {
  const [date, part] = request.path.split('/').slice(-2);
  if (part === 'vouchers') {
    return { date: date!, status: 'Posted', sending: SENDING, vouchers: [] };
  }
  if (part === 'check') {
    return {
      date: date!,
      status: 'Posted',
      records: 'Matched',
      lines: [],
      notes: [],
      sending: SENDING,
      tally: 'NotChecked',
      tallyCheckedAt: null,
      tallyDifferences: 0,
      tallyMessage: null,
      canCheckTally: true,
    };
  }
  return undefined;
};

export const ANSWERS: FakeAnswers = {
  [`GET /api/v1/billing/bills/${RECORD_ID}`]: BILL,
  'GET /api/v1/billing/receipts': paged(RECEIPTS),
  [`GET /api/v1/billing/receipts/${id('01')}`]: RECEIPT,
  // In the status asked for (the billing home page lists those awaiting approval and those to pay separately).
  'GET /api/v1/billing/refunds': (request: FakeRequest) => {
    const status = request.url.searchParams.get('status');
    return paged(status ? REFUNDS.filter((r) => r.status === status) : REFUNDS);
  },
  ...Object.fromEntries(REFUNDS.map((r) => [`GET /api/v1/billing/refunds/${r.id}`, r])),
  'GET /api/v1/billing/discount-approvals': paged(APPROVALS),
  'GET /api/v1/billing/discount-limits': LIMITS,
  'GET /api/v1/billing/shifts': paged(SHIFTS),
  ...Object.fromEntries(SHIFTS.map((s) => [`GET /api/v1/billing/shifts/${s.id}`, s])),
  'GET /api/v1/billing/payment-links': paged(LINKS),
  'GET /api/v1/billing/ip-accounts': paged(ACCOUNTS),
  [`GET /api/v1/billing/ip-accounts/${RECORD_ID}`]: ACCOUNT,
  [`GET /api/v1/billing/ip-accounts/${RECORD_ID}/running-bill`]: RUNNING_BILL,
  [`GET /api/v1/billing/ip-accounts/${RECORD_ID}/package-status`]: NO_PACKAGE,
  [`GET /api/v1/billing/ip-accounts/${RECORD_ID}/clearance`]: IP_CLEARANCE,
  [`GET /api/v1/billing/patients/${SUNIL.patientId}/dues`]: SUNIL_DUES,
  'GET /api/v1/billing/tariffs': PLANS,
  [`GET /api/v1/billing/tariffs/${RECORD_ID}`]: PLAN,
  [`GET /api/v1/billing/tariffs/${RECORD_ID}/prices`]: paged(PRICES),
  [`GET /api/v1/billing/packages/${PACKAGE_ID}`]: PACKAGE,
  'GET /api/v1/billing/hbp/procedures': [HBP_PROCEDURE],
  'GET /api/v1/billing/hbp/procedures/SG039A': HBP_PROCEDURE,
  'GET /api/v1/billing/gateway/summary': GATEWAY_SUMMARY,
  'GET /api/v1/billing/gateway/exceptions': paged(EXCEPTIONS),
  'GET /api/v1/billing/gateway/settlements': GATEWAY_SETTLEMENTS,
  'GET /api/v1/billing/pos/settlements/summary': POS_SUMMARY,
  'GET /api/v1/billing/pos/settlements/lines': paged(POS_LINES),
  'GET /api/v1/billing/pos/settlements/days': POS_DAYS,
  'GET /api/v1/billing/pos/settlements/unsettled': UNSETTLED,
  'GET /api/v1/billing/pos/settlements/imports': paged([POS_IMPORT]),
  'GET /api/v1/billing/tally/exports': TALLY_EXPORTS,
  'GET /api/v1/billing/tally/day-book': DAY_BOOK,
  'GET /api/v1/billing/tally/days/*': tallyDay,
};
