import type {
  AssetContractDto,
  AssetDashboard,
  AssetDepreciation,
  AssetDetail,
  AssetListItem,
  CalibrationDto,
  CondemnationDto,
  MaintenanceDueItem,
  PmPlanDto,
  PmVisitDto,
  TicketDetail,
  TicketListItem,
} from '@/modules/assets/api';
import { FACILITY_ID, paged, type FakeAnswers, type FakeRequest } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Biomedical and general assets: the register (a ventilator in use, a monitor under maintenance, an idle ECG machine,
// a condemned autoclave), breakdown tickets in each open state, PM plans and what falls due, condemnation requests
// (pending and approved) and one asset's record with its contracts, depreciation, PM visits and calibrations.

const now = Date.now();
const daysAgo = (days: number) => new Date(now - days * 86_400_000).toISOString();
const dateIn = (days: number) => new Date(now + days * 86_400_000).toISOString().slice(0, 10);
const dateAgo = (days: number) => dateIn(-days);

const id = (suffix: string) => `0192a000-0000-7000-8000-0000000a5${suffix}`;
const ICU = { id: '0192a000-0000-7000-8000-00000000de04', name: 'Medical ICU' };
const CARDIO = { id: '0192a000-0000-7000-8000-00000000de02', name: 'Cardiology' };
const MONITOR = id('102');
const ECG = id('103');
const AUTOCLAVE = id('104');

const asset = (
  overrides: Partial<AssetListItem> & Pick<AssetListItem, 'id' | 'assetTag' | 'name' | 'status'>,
): AssetListItem => ({
  category: 'MedicalEquipment',
  make: null,
  model: null,
  serialNumber: null,
  departmentId: ICU.id,
  departmentName: ICU.name,
  location: 'MICU, 3rd floor',
  criticality: 'High',
  warrantyExpiry: null,
  nextPmDue: null,
  nextCalibrationDue: null,
  openTickets: 0,
  ...overrides,
});

const ASSETS: AssetListItem[] = [
  asset({
    id: RECORD_ID,
    assetTag: 'NHP-BME-0042',
    name: 'Ventilator',
    status: 'InUse',
    make: 'Dräger',
    model: 'Savina 300',
    serialNumber: 'ASCK-0193',
    criticality: 'Critical',
    warrantyExpiry: dateIn(120),
    nextPmDue: dateIn(5),
    nextCalibrationDue: dateIn(40),
    openTickets: 1,
  }),
  asset({
    id: MONITOR,
    assetTag: 'NHP-BME-0057',
    name: 'Multipara patient monitor',
    status: 'UnderMaintenance',
    make: 'Philips',
    model: 'IntelliVue MX450',
    serialNumber: 'DE7301928',
    nextPmDue: dateAgo(3),
    openTickets: 1,
  }),
  asset({
    id: ECG,
    assetTag: 'NHP-BME-0110',
    name: '12-lead ECG machine',
    status: 'Idle',
    make: 'BPL',
    model: 'Cardiart 9108',
    departmentId: CARDIO.id,
    departmentName: CARDIO.name,
    location: 'Cardiology OPD',
    criticality: 'Medium',
  }),
  asset({
    id: AUTOCLAVE,
    assetTag: 'NHP-CSSD-0003',
    name: 'Horizontal steam autoclave',
    status: 'Condemned',
    make: 'Nat Steel',
    model: 'HS-300',
    departmentId: null,
    departmentName: 'CSSD',
    location: 'CSSD, basement',
  }),
];

const DETAIL: AssetDetail = {
  id: RECORD_ID,
  assetTag: 'NHP-BME-0042',
  barcode: 'NHPBME0042',
  name: 'Ventilator',
  category: 'MedicalEquipment',
  make: 'Dräger',
  model: 'Savina 300',
  serialNumber: 'ASCK-0193',
  departmentId: ICU.id,
  departmentName: ICU.name,
  location: 'MICU, 3rd floor, bed 4',
  custodianUserId: null,
  custodianName: 'Sr. Lata Pawar',
  purchaseDate: dateAgo(610),
  purchaseCost: 14_50_000,
  vendorId: '0192a000-0000-7000-8000-00000000e701',
  vendorName: 'Medisys Biomedical Pvt Ltd',
  purchaseOrderNumber: 'PO2400087',
  invoiceNumber: 'MBPL/24-25/0193',
  installationDate: dateAgo(600),
  warrantyExpiry: dateIn(120),
  underWarranty: true,
  status: 'InUse',
  criticality: 'Critical',
  depreciationMethod: 'WrittenDownValue',
  usefulLifeYears: 10,
  salvageValue: 72_500,
  depreciationRatePercent: 15,
  bookValue: 10_52_000,
  requiresCalibration: true,
  nextCalibrationDue: dateIn(40),
  condemnedOn: null,
  disposedOn: null,
  disposalValue: null,
  disposalMethod: null,
  notes: null,
  version: 4,
};

const CONTRACTS: AssetContractDto[] = [
  {
    id: id('201'),
    assetId: RECORD_ID,
    type: 'Cmc',
    vendorId: '0192a000-0000-7000-8000-00000000e701',
    vendorName: 'Medisys Biomedical Pvt Ltd',
    contractNumber: 'CMC/2026/042',
    startDate: dateIn(120),
    endDate: dateIn(485),
    cost: 1_20_000,
    coverageNotes: 'Parts and labour, two PM visits a year',
    isCurrent: false,
    version: 1,
  },
];

const DEPRECIATION: AssetDepreciation = {
  assetId: RECORD_ID,
  method: 'WrittenDownValue',
  cost: 14_50_000,
  salvageValue: 72_500,
  ratePercent: 15,
  startDate: dateAgo(600),
  asOf: dateIn(0),
  bookValue: 10_52_000,
  accumulatedDepreciation: 3_98_000,
  schedule: [
    { year: 1, from: dateAgo(600), to: dateAgo(236), opening: 14_50_000, depreciation: 2_17_500, closing: 12_32_500 },
    { year: 2, from: dateAgo(235), to: dateIn(130), opening: 12_32_500, depreciation: 1_84_875, closing: 10_47_625 },
  ],
};

const PLANS: PmPlanDto[] = [
  {
    id: id('301'),
    assetId: RECORD_ID,
    assetTag: 'NHP-BME-0042',
    assetName: 'Ventilator',
    title: 'Quarterly PM (flow sensor, O₂ cell, filters)',
    frequencyDays: 90,
    nextDueDate: dateIn(5),
    lastDoneOn: dateAgo(85),
    checklist: 'Replace HEPA filter\nCalibrate flow sensor\nCheck O₂ cell\nAlarm test',
    assignedTo: 'Medisys Biomedical Pvt Ltd',
    vendorId: '0192a000-0000-7000-8000-00000000e701',
    isActive: true,
    daysToDue: 5,
    version: 3,
  },
  {
    id: id('302'),
    assetId: MONITOR,
    assetTag: 'NHP-BME-0057',
    assetName: 'Multipara patient monitor',
    title: 'Half-yearly PM',
    frequencyDays: 180,
    nextDueDate: dateAgo(3),
    lastDoneOn: dateAgo(183),
    checklist: null,
    assignedTo: 'In-house biomedical (Rahul Kale)',
    vendorId: null,
    isActive: true,
    daysToDue: -3,
    version: 2,
  },
];

const VISITS: PmVisitDto[] = [
  {
    id: id('311'),
    planId: id('301'),
    assetId: RECORD_ID,
    visitDate: dateAgo(85),
    performedBy: 'Medisys — Prakash N.',
    outcome: 'Completed',
    findings: 'Flow sensor recalibrated; filters replaced',
    cost: 0,
    nextDueDate: dateIn(5),
    serviceReportFileId: null,
  },
];

const CALIBRATIONS: CalibrationDto[] = [
  {
    id: id('321'),
    assetId: RECORD_ID,
    calibratedOn: dateAgo(325),
    agency: 'NABL-accredited — Sigma Calibration Labs, Pune',
    certificateNumber: 'SCL/26/11873',
    certificateFileId: null,
    nextDueDate: dateIn(40),
    outcome: 'Pass',
    remarks: null,
  },
];

const DUE: MaintenanceDueItem[] = [
  {
    kind: 'PM',
    facilityId: FACILITY_ID,
    assetId: MONITOR,
    assetTag: 'NHP-BME-0057',
    assetName: 'Multipara patient monitor',
    title: 'Half-yearly PM',
    dueDate: dateAgo(3),
    daysToDue: -3,
  },
  {
    kind: 'PM',
    facilityId: FACILITY_ID,
    assetId: RECORD_ID,
    assetTag: 'NHP-BME-0042',
    assetName: 'Ventilator',
    title: 'Quarterly PM (flow sensor, O₂ cell, filters)',
    dueDate: dateIn(5),
    daysToDue: 5,
  },
  {
    kind: 'Calibration',
    facilityId: FACILITY_ID,
    assetId: RECORD_ID,
    assetTag: 'NHP-BME-0042',
    assetName: 'Ventilator',
    title: 'Annual calibration',
    dueDate: dateIn(40),
    daysToDue: 40,
  },
  {
    kind: 'Warranty',
    facilityId: FACILITY_ID,
    assetId: RECORD_ID,
    assetTag: 'NHP-BME-0042',
    assetName: 'Ventilator',
    title: 'Warranty ends',
    dueDate: dateIn(120),
    daysToDue: 120,
  },
];

const ticket = (
  overrides: Partial<TicketListItem> & Pick<TicketListItem, 'id' | 'number' | 'status' | 'issue'>,
): TicketListItem => ({
  assetId: RECORD_ID,
  assetTag: 'NHP-BME-0042',
  assetName: 'Ventilator',
  assetCriticality: 'Critical',
  priority: 'High',
  reportedAt: daysAgo(0.2),
  reportedBy: 'Sr. Lata Pawar',
  assignedTo: null,
  resolvedAt: null,
  downtimeMinutes: null,
  ...overrides,
});

const TICKETS: TicketListItem[] = [
  ticket({ id: RECORD_ID, number: 'BRK2600071', status: 'Open', issue: 'High-pressure alarm keeps sounding' }),
  ticket({
    id: id('402'),
    number: 'BRK2600069',
    status: 'InProgress',
    issue: 'SpO₂ module shows no reading',
    assetId: MONITOR,
    assetTag: 'NHP-BME-0057',
    assetName: 'Multipara patient monitor',
    assetCriticality: 'High',
    priority: 'Medium',
    reportedAt: daysAgo(1),
    assignedTo: 'Rahul Kale (biomedical)',
  }),
  ticket({
    id: id('403'),
    number: 'BRK2600066',
    status: 'Resolved',
    issue: 'Paper not feeding',
    assetId: ECG,
    assetTag: 'NHP-BME-0110',
    assetName: '12-lead ECG machine',
    assetCriticality: 'Medium',
    priority: 'Low',
    reportedAt: daysAgo(3),
    assignedTo: 'Rahul Kale (biomedical)',
    resolvedAt: daysAgo(2),
    downtimeMinutes: 240,
  }),
];

const TICKET: TicketDetail = {
  id: RECORD_ID,
  number: 'BRK2600071',
  assetId: RECORD_ID,
  assetTag: 'NHP-BME-0042',
  assetName: 'Ventilator',
  priority: 'High',
  status: 'Open',
  issue: 'High-pressure alarm keeps sounding',
  reportedAt: daysAgo(0.2),
  reportedBy: 'Sr. Lata Pawar',
  assignedTo: null,
  assignedToUserId: null,
  vendorId: null,
  assignedAt: null,
  startedAt: null,
  resolvedAt: null,
  closedAt: null,
  resolution: null,
  rootCause: null,
  cost: 0,
  downtimeMinutes: null,
  version: 1,
};

const CONDEMNATIONS: CondemnationDto[] = [
  {
    id: RECORD_ID,
    assetId: ECG,
    assetTag: 'NHP-BME-0110',
    assetName: '12-lead ECG machine',
    reason: 'Main board failed; spares no longer made',
    bookValueAtRequest: 18_400,
    status: 'Pending',
    requestedAt: daysAgo(2),
    requestedBy: 'Rahul Kale (biomedical)',
    decidedAt: null,
    decisionRemarks: null,
  },
  {
    id: id('502'),
    assetId: AUTOCLAVE,
    assetTag: 'NHP-CSSD-0003',
    assetName: 'Horizontal steam autoclave',
    reason: 'Chamber corrosion; failed the pressure vessel test',
    bookValueAtRequest: 42_000,
    status: 'Approved',
    requestedAt: daysAgo(30),
    requestedBy: 'Rahul Kale (biomedical)',
    decidedAt: daysAgo(21),
    decisionRemarks: 'Approved by the condemnation committee',
  },
];

const DASHBOARD: AssetDashboard = {
  totalAssets: 1_284,
  byStatus: [
    { status: 'InUse', count: 1_198 },
    { status: 'UnderMaintenance', count: 23 },
    { status: 'Idle', count: 41 },
    { status: 'Condemned', count: 22 },
  ],
  openTickets: 2,
  criticalOpenTickets: 1,
  pmDueThisWeek: 1,
  pmOverdue: 1,
  calibrationDue: 1,
  warrantyExpiring: 1,
  contractsExpiring: 0,
  downtimeHoursThisMonth: 37.5,
  bookValue: 18_42_60_000,
};

const DETAILS: Record<string, AssetDetail> = {
  [RECORD_ID]: DETAIL,
  [ECG]: {
    ...DETAIL,
    id: ECG,
    assetTag: 'NHP-BME-0110',
    barcode: null,
    name: '12-lead ECG machine',
    make: 'BPL',
    model: 'Cardiart 9108',
    serialNumber: 'C9108-22-1187',
    status: 'Idle',
    criticality: 'Medium',
    purchaseCost: 92_000,
    bookValue: 18_400,
    underWarranty: false,
    warrantyExpiry: null,
    requiresCalibration: false,
    nextCalibrationDue: null,
  },
  [AUTOCLAVE]: {
    ...DETAIL,
    id: AUTOCLAVE,
    assetTag: 'NHP-CSSD-0003',
    barcode: null,
    name: 'Horizontal steam autoclave',
    make: 'Nat Steel',
    model: 'HS-300',
    status: 'Condemned',
    condemnedOn: dateAgo(21),
    criticality: 'High',
    purchaseCost: 6_80_000,
    bookValue: 42_000,
    underWarranty: false,
    warrantyExpiry: null,
  },
};

const assetPath = `/api/v1/assets/${RECORD_ID}`;

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/assets': paged(ASSETS),
  'GET /api/v1/assets/dashboard': DASHBOARD,
  'GET /api/v1/assets/maintenance/due': DUE,
  'GET /api/v1/assets/pm-plans': (request: FakeRequest) => {
    const assetId = request.url.searchParams.get('assetId');
    return assetId ? PLANS.filter((p) => p.assetId === assetId) : PLANS;
  },
  'GET /api/v1/assets/tickets': paged(TICKETS),
  [`GET /api/v1/assets/tickets/${RECORD_ID}`]: TICKET,
  'GET /api/v1/assets/condemnations': CONDEMNATIONS,
  [`GET ${assetPath}`]: DETAIL,
  [`GET /api/v1/assets/${ECG}`]: DETAILS[ECG],
  [`GET /api/v1/assets/${AUTOCLAVE}`]: DETAILS[AUTOCLAVE],
  [`GET ${assetPath}/contracts`]: CONTRACTS,
  [`GET ${assetPath}/depreciation`]: DEPRECIATION,
  [`GET ${assetPath}/pm-visits`]: VISITS,
  [`GET ${assetPath}/calibrations`]: CALIBRATIONS,
};
