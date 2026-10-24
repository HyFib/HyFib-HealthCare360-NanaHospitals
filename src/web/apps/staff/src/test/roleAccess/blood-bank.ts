import { todayIso } from '@hc360/core';
import type {
  BloodInventoryDashboard,
  BloodRequestDetail,
  BloodRequestListItem,
  BloodUnitDto,
  CompatibleUnitDto,
  DonationDetail,
  DonationListItem,
  DonorDetail,
  DonorListItem,
  DonorQuestionDto,
  TransfusionReactionDto,
  TtiPanel,
} from '@/modules/blood-bank/api';
import { paged, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// The blood bank with stock, donors, donations, requests and reactions: rows in the states that offer their actions
// (a quarantined and an available unit to discard, a reported and an investigated reaction), and one of each record.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const inDays = (days: number) => new Date(now + days * 86_400_000).toISOString();
const HOUR = 60;
const DAY = 24 * HOUR;

const NON_REACTIVE: TtiPanel = {
  hiv: 'NonReactive',
  hbsAg: 'NonReactive',
  hcv: 'NonReactive',
  syphilis: 'NonReactive',
  malaria: 'NonReactive',
};
const PENDING: TtiPanel = { hiv: 'Pending', hbsAg: 'Pending', hcv: 'Pending', syphilis: 'Pending', malaria: 'Pending' };

const ASHA = { patientId: '0192a000-0000-7000-8000-000000000001', uhid: 'NH26000123', patientName: 'Asha Devi' };
const RAVI = { patientId: '0192a000-0000-7000-8000-000000000002', uhid: 'NH26000124', patientName: 'Ravi Shankar' };

const unit = (
  n: string,
  unitNumber: string,
  status: BloodUnitDto['status'],
  expiresInDays: number,
  extra: Partial<BloodUnitDto> = {},
): BloodUnitDto => ({
  id: id(n),
  unitNumber,
  donationId: id('bd01'),
  componentType: 'PRBC',
  bloodGroup: 'BPositive',
  volumeMl: 280,
  collectedAt: minutesAgo(5 * DAY),
  expiresAt: inDays(expiresInDays),
  status,
  tti: status === 'Quarantine' ? PENDING : NON_REACTIVE,
  storageLocation: 'Refrigerator 2, shelf B',
  reservedForRequestId: null,
  issuedAt: null,
  discardReason: null,
  isExpired: false,
  ...extra,
});

const UNITS: BloodUnitDto[] = [
  unit('b101', 'NHBB26-00412-PR', 'Available', 2),
  unit('b102', 'NHBB26-00418-FF', 'Quarantine', 360, { componentType: 'FFP', volumeMl: 220, bloodGroup: 'OPositive' }),
  unit('b103', 'NHBB26-00377-PR', 'Reserved', 9, { reservedForRequestId: RECORD_ID }),
  unit('b104', 'NHBB26-00351-PC', 'Issued', 1, {
    componentType: 'PlateletConcentrate',
    volumeMl: 60,
    issuedAt: minutesAgo(3 * HOUR),
  }),
];

const INVENTORY: BloodInventoryDashboard = {
  asOf: minutesAgo(1),
  stock: [
    { component: 'PRBC', bloodGroup: 'BPositive', available: 14, reserved: 2, quarantine: 3, expiringSoon: 1 },
    { component: 'PRBC', bloodGroup: 'OPositive', available: 21, reserved: 1, quarantine: 4, expiringSoon: 0 },
    { component: 'PRBC', bloodGroup: 'ONegative', available: 2, reserved: 0, quarantine: 1, expiringSoon: 0 },
    { component: 'FFP', bloodGroup: 'OPositive', available: 30, reserved: 0, quarantine: 6, expiringSoon: 0 },
    {
      component: 'PlateletConcentrate',
      bloodGroup: 'APositive',
      available: 3,
      reserved: 1,
      quarantine: 0,
      expiringSoon: 2,
    },
  ],
  expiringSoon: [
    {
      id: id('b101'),
      unitNumber: 'NHBB26-00412-PR',
      component: 'PRBC',
      bloodGroup: 'BPositive',
      expiresAt: inDays(2),
      status: 'Available',
    },
  ],
  openRequests: 4,
  emergencyRequests: 1,
  unitsInQuarantine: 14,
};

const donor = (
  n: string,
  donorNumber: string,
  fullName: string,
  gender: DonorListItem['gender'],
  age: string,
  bloodGroup: DonorListItem['bloodGroup'],
  extra: Partial<DonorListItem> = {},
): DonorListItem => ({
  id: n === 'record' ? RECORD_ID : id(n),
  donorNumber,
  fullName,
  gender,
  age,
  bloodGroup,
  bloodGroupConfirmed: true,
  phoneMasked: '98******44',
  donationCount: 6,
  lastDonationAt: minutesAgo(140 * DAY),
  isPermanentlyDeferred: false,
  deferredUntil: null,
  nextEligibleOn: null,
  ...extra,
});

const DONORS: DonorListItem[] = [
  donor('record', 'BD26-000311', 'Rahul Deshmukh', 'Male', '34Y', 'BPositive'),
  donor('bb02', 'BD26-000287', 'Sneha Kulkarni', 'Female', '27Y', 'OPositive', {
    donationCount: 2,
    lastDonationAt: minutesAgo(40 * DAY),
    nextEligibleOn: todayIso(80),
  }),
  donor('bb03', 'BD26-000199', 'Imran Qureshi', 'Male', '45Y', 'ABNegative', {
    bloodGroupConfirmed: false,
    deferredUntil: todayIso(30),
  }),
];

const DONATIONS: DonationListItem[] = [
  {
    id: RECORD_ID,
    bagNumber: 'BAG26-01145',
    donorId: RECORD_ID,
    donorNumber: 'BD26-000311',
    donorName: 'Rahul Deshmukh',
    bagType: 'Triple',
    volumeMl: 450,
    type: 'Voluntary',
    collectedAt: minutesAgo(2 * HOUR),
    status: 'Collected',
    bloodGroup: 'BPositive',
    ttiComplete: false,
    ttiReactive: false,
  },
  {
    id: id('bd01'),
    bagNumber: 'BAG26-01098',
    donorId: id('bb02'),
    donorNumber: 'BD26-000287',
    donorName: 'Sneha Kulkarni',
    bagType: 'Double',
    volumeMl: 350,
    type: 'Replacement',
    collectedAt: minutesAgo(40 * DAY),
    status: 'Separated',
    bloodGroup: 'OPositive',
    ttiComplete: true,
    ttiReactive: false,
  },
];

const DONOR: DonorDetail = {
  id: RECORD_ID,
  donorNumber: 'BD26-000311',
  firstName: 'Rahul',
  lastName: 'Deshmukh',
  fullName: 'Rahul Deshmukh',
  gender: 'Male',
  dateOfBirth: '1992-06-18',
  age: '34Y',
  bloodGroup: 'BPositive',
  bloodGroupConfirmed: true,
  phone: '9822014455',
  email: 'rahul.deshmukh@example.in',
  address: { line1: '14, Shivaji Nagar', city: 'Pune', state: 'Maharashtra', pinCode: '411005', country: 'India' },
  occupation: 'Software engineer',
  consentToNotify: true,
  donationCount: 6,
  lastDonationAt: minutesAgo(140 * DAY),
  nextEligibleOn: null,
  deferredUntil: null,
  isPermanentlyDeferred: false,
  deferralReason: null,
  notes: null,
  isActive: true,
  screenings: [
    {
      id: id('b5a1'),
      screenedAt: minutesAgo(30),
      screenedByName: 'Sr. Anita Pawar',
      haemoglobinGdl: 14.2,
      weightKg: 72,
      systolic: 122,
      diastolic: 78,
      pulse: 76,
      temperatureC: 36.8,
      answers: [{ key: 'recent-illness', yes: false }],
      outcome: 'Eligible',
      reasons: [],
      deferredUntil: null,
      maxBagVolumeMl: 450,
      donationId: null,
    },
  ],
  donations: DONATIONS.slice(1).map((d) => ({ ...d, donorId: RECORD_ID })),
  version: 3,
};

const QUESTIONS: DonorQuestionDto[] = [
  {
    key: 'recent-illness',
    text: 'Have you had fever or any illness in the last 2 weeks?',
    onlyFor: null,
    deferralDays: 14,
    permanentDeferral: false,
  },
  {
    key: 'tattoo',
    text: 'Have you had a tattoo or piercing in the last 12 months?',
    onlyFor: null,
    deferralDays: 365,
    permanentDeferral: false,
  },
  {
    key: 'pregnant',
    text: 'Are you pregnant or breastfeeding?',
    onlyFor: 'Female',
    deferralDays: 365,
    permanentDeferral: false,
  },
];

const DONATION: DonationDetail = {
  id: RECORD_ID,
  bagNumber: 'BAG26-01145',
  donorId: RECORD_ID,
  donorNumber: 'BD26-000311',
  donorName: 'Rahul Deshmukh',
  bagType: 'Triple',
  volumeMl: 450,
  type: 'Voluntary',
  replacementForPatientId: null,
  collectedAt: minutesAgo(2 * HOUR),
  collectedByName: 'Sr. Anita Pawar',
  status: 'Collected',
  bloodGroup: 'BPositive',
  tti: PENDING,
  labResultsAt: null,
  separatedAt: null,
  donorReaction: null,
  notes: null,
  components: [],
  version: 1,
};

const request = (
  n: string,
  requestNumber: string,
  who: typeof ASHA,
  status: BloodRequestListItem['status'],
  extra: Partial<BloodRequestListItem> = {},
): BloodRequestListItem => ({
  id: n === 'record' ? RECORD_ID : id(n),
  requestNumber,
  ...who,
  componentType: 'PRBC',
  unitsRequested: 2,
  unitsReserved: 0,
  unitsIssued: 0,
  urgency: 'Routine',
  status,
  patientBloodGroup: 'BPositive',
  createdAt: minutesAgo(5 * HOUR),
  requiredBy: inDays(1),
  ...extra,
});

const REQUESTS: BloodRequestListItem[] = [
  request('record', 'BR26-00231', ASHA, 'PartiallyIssued', {
    urgency: 'Emergency',
    unitsReserved: 1,
    unitsIssued: 1,
    requiredBy: minutesAgo(-HOUR),
  }),
  request('bf02', 'BR26-00229', RAVI, 'Pending', {
    componentType: 'FFP',
    unitsRequested: 4,
    patientBloodGroup: 'Unknown',
  }),
];

const REQUEST: BloodRequestDetail = {
  id: RECORD_ID,
  requestNumber: 'BR26-00231',
  ...ASHA,
  admissionId: id('ad01'),
  visitId: null,
  orderItemId: null,
  componentType: 'PRBC',
  unitsRequested: 2,
  unitsReserved: 1,
  unitsIssued: 1,
  urgency: 'Emergency',
  indication: 'Post-partum haemorrhage, Hb 6.1 g/dL',
  requiredBy: minutesAgo(-HOUR),
  requestedByName: 'Dr. Meera Iyer',
  haemoglobinGdl: 6.1,
  plateletCount: 182_000,
  patientBloodGroup: 'BPositive',
  groupedAt: minutesAgo(4 * HOUR),
  groupedByName: 'Mr. Santosh Gaikwad',
  emergencyReleaseAuthorisedBy: null,
  emergencyReleaseReason: null,
  status: 'PartiallyIssued',
  cancelReason: null,
  crossMatches: [
    {
      id: id('bc01'),
      componentId: id('b103'),
      unitNumber: 'NHBB26-00377-PR',
      method: 'GelCard',
      result: 'Compatible',
      performedAt: minutesAgo(3 * HOUR),
      performedByName: 'Mr. Santosh Gaikwad',
      remarks: null,
      isValid: true,
      chargeStatus: 'Posted',
    },
  ],
  issues: [
    {
      id: id('b1e1'),
      requestId: RECORD_ID,
      componentId: id('b105'),
      unitNumber: 'NHBB26-00366-PR',
      componentType: 'PRBC',
      unitBloodGroup: 'BPositive',
      isEmergencyRelease: false,
      issuedAt: minutesAgo(15),
      issuedByName: 'Mr. Santosh Gaikwad',
      issuedToName: 'Sr. Kavita More',
      returnedAt: null,
      restocked: null,
      returnNotes: null,
      chargeStatus: 'Posted',
      chargeError: null,
    },
  ],
  version: 5,
};

const COMPATIBLE: CompatibleUnitDto[] = [
  {
    id: id('b103'),
    unitNumber: 'NHBB26-00377-PR',
    bloodGroup: 'BPositive',
    volumeMl: 280,
    expiresAt: inDays(9),
    reservedForThisRequest: true,
  },
  {
    id: id('b101'),
    unitNumber: 'NHBB26-00412-PR',
    bloodGroup: 'BPositive',
    volumeMl: 280,
    expiresAt: inDays(2),
    reservedForThisRequest: false,
  },
];

const reaction = (
  n: string,
  status: TransfusionReactionDto['status'],
  extra: Partial<TransfusionReactionDto> = {},
): TransfusionReactionDto => ({
  id: id(n),
  issueId: id('b1e1'),
  requestId: RECORD_ID,
  ...ASHA,
  unitNumber: 'NHBB26-00366-PR',
  reportedAt: minutesAgo(2 * HOUR),
  reportedByName: 'Sr. Kavita More',
  onsetAt: minutesAgo(2 * HOUR + 10),
  symptoms: ['Fever', 'ChillsRigors'],
  vitals: 'Temp 38.9 °C, pulse 112/min, BP 108/70',
  transfusionStopped: true,
  volumeTransfusedMl: 90,
  actionsTaken: 'Transfusion stopped, IV paracetamol given, line kept open with normal saline.',
  severity: 'Moderate',
  type: 'Unclassified',
  status,
  investigationFindings: null,
  reportedToHaemovigilance: false,
  closedAt: null,
  closedByName: null,
  ...extra,
});

const REACTIONS: TransfusionReactionDto[] = [
  reaction('bea1', 'Reported'),
  reaction('bea2', 'UnderInvestigation', {
    ...RAVI,
    unitNumber: 'NHBB26-00302-FF',
    symptoms: ['Urticaria', 'Itching'],
    severity: 'Mild',
    reportedAt: minutesAgo(2 * DAY),
  }),
  reaction('bea3', 'Closed', {
    symptoms: ['Dyspnoea'],
    severity: 'Severe',
    type: 'TransfusionAssociatedCirculatoryOverload',
    investigationFindings: 'Fluid overload; managed with furosemide.',
    reportedToHaemovigilance: true,
    reportedAt: minutesAgo(9 * DAY),
    closedAt: minutesAgo(7 * DAY),
    closedByName: 'Dr. Sunita Rane',
  }),
];

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/blood-bank/inventory': INVENTORY,
  'GET /api/v1/blood-bank/units': paged(UNITS),
  'GET /api/v1/blood-bank/donor-questions': QUESTIONS,
  'GET /api/v1/blood-bank/donors': paged(DONORS),
  [`GET /api/v1/blood-bank/donors/${RECORD_ID}`]: DONOR,
  'GET /api/v1/blood-bank/donations': paged(DONATIONS),
  [`GET /api/v1/blood-bank/donations/${RECORD_ID}`]: DONATION,
  'GET /api/v1/blood-bank/requests': paged(REQUESTS),
  [`GET /api/v1/blood-bank/requests/${RECORD_ID}`]: REQUEST,
  [`GET /api/v1/blood-bank/requests/${RECORD_ID}/compatible-units`]: COMPATIBLE,
  'GET /api/v1/blood-bank/reactions': paged(REACTIONS),
};
