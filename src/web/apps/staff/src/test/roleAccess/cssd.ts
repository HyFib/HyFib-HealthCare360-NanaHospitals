import type {
  CssdRequestDto,
  CssdSummary,
  InstrumentSetDto,
  LoadDto,
  LoadListItem,
  RecallReport,
  SetIssueDto,
  SoiledReceiptDto,
  SterilePackDto,
  WashCycleDto,
  WashCycleListItem,
} from '@/modules/cssd/api';
import { paged, type FakeAnswers } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// CSSD: instrument sets, department requests (open and partly issued), soiled receipts, wash cycles, sterilizer loads
// (one per status), packs on the shelf and issues (one to record use on, one recalled waiting for acknowledgement).
// The load and wash cycle opened as records are the ones whose actions are most numerous: a load whose cycle is
// complete with the biological indicator pending (Release, Biological indicator, Abort), a wash still running.

const now = Date.now();
const hoursAgo = (hours: number) => new Date(now - hours * 3_600_000).toISOString();
const dateIn = (days: number) => new Date(now + days * 86_400_000).toISOString().slice(0, 10);

const id = (suffix: string) => `0192a000-0000-7000-8000-00000000c${suffix}`;

const OT = { id: '0192a000-0000-7000-8000-00000000de01', name: 'Main operation theatre' };
const LABOUR = { id: '0192a000-0000-7000-8000-00000000de02', name: 'Labour room' };

const LAPAROTOMY = id('101');
const DELIVERY = id('102');
const ORTHO = id('103');

const SETS: InstrumentSetDto[] = [
  {
    id: LAPAROTOMY,
    code: 'LAP-01',
    name: 'Major laparotomy set',
    category: 'General surgery',
    ownerDepartmentId: OT.id,
    method: 'Steam',
    shelfLifeDays: 30,
    isActive: true,
    notes: null,
    items: [
      { name: 'Mayo scissors', quantity: 2 },
      { name: 'Artery forceps', quantity: 12 },
      { name: 'Needle holder', quantity: 2 },
      { name: 'BP handle no. 4', quantity: 1 },
    ],
    instrumentCount: 17,
    packsAvailable: 4,
    version: 3,
  },
  {
    id: DELIVERY,
    code: 'DEL-01',
    name: 'Normal delivery set',
    category: 'Obstetrics',
    ownerDepartmentId: LABOUR.id,
    method: 'Steam',
    shelfLifeDays: 30,
    isActive: true,
    notes: null,
    items: [
      { name: 'Episiotomy scissors', quantity: 1 },
      { name: 'Cord clamp', quantity: 2 },
      { name: 'Sponge holder', quantity: 1 },
    ],
    instrumentCount: 4,
    packsAvailable: 6,
    version: 1,
  },
  {
    id: ORTHO,
    code: 'ORT-IMP',
    name: 'DHS implant set',
    category: 'Orthopaedics',
    ownerDepartmentId: OT.id,
    method: 'Plasma',
    shelfLifeDays: 90,
    isActive: true,
    notes: 'Implant set: release only after a passing biological indicator',
    items: [
      { name: 'DHS plate 4-hole', quantity: 1 },
      { name: 'Lag screw guide', quantity: 1 },
    ],
    instrumentCount: 2,
    packsAvailable: 1,
    version: 2,
  },
];

const REQUESTS: CssdRequestDto[] = [
  {
    id: RECORD_ID,
    requestNumber: 'CSR2600118',
    departmentId: OT.id,
    departmentName: OT.name,
    requestedByName: 'Sr. Lata Pawar',
    requestedAt: hoursAgo(2),
    requiredBy: hoursAgo(-3),
    priority: 'Urgent',
    status: 'Open',
    notes: 'For the 2 pm emergency laparotomy',
    cancelReason: null,
    lines: [{ instrumentSetId: LAPAROTOMY, setName: 'Major laparotomy set', quantity: 2, issuedQuantity: 0 }],
  },
  {
    id: id('202'),
    requestNumber: 'CSR2600115',
    departmentId: LABOUR.id,
    departmentName: LABOUR.name,
    requestedByName: 'Sr. Anjali More',
    requestedAt: hoursAgo(6),
    requiredBy: null,
    priority: 'Routine',
    status: 'PartiallyIssued',
    notes: null,
    cancelReason: null,
    lines: [{ instrumentSetId: DELIVERY, setName: 'Normal delivery set', quantity: 4, issuedQuantity: 2 }],
  },
];

const RECEIPTS: SoiledReceiptDto[] = [
  {
    id: RECORD_ID,
    departmentId: OT.id,
    departmentName: OT.name,
    receivedAt: hoursAgo(1),
    receivedByName: 'Ganesh Shinde',
    handedOverByName: 'Sr. Lata Pawar',
    hasDiscrepancy: true,
    notes: null,
    lines: [
      {
        instrumentSetId: LAPAROTOMY,
        setName: 'Major laparotomy set',
        setIssueId: id('501'),
        expectedCount: 17,
        receivedCount: 16,
        missingCount: 1,
        extraCount: 0,
        damagedCount: 0,
        discrepancies: 'Artery forceps: 1 missing',
      },
    ],
  },
  {
    id: id('302'),
    departmentId: LABOUR.id,
    departmentName: LABOUR.name,
    receivedAt: hoursAgo(5),
    receivedByName: 'Ganesh Shinde',
    handedOverByName: null,
    hasDiscrepancy: false,
    notes: null,
    lines: [
      {
        instrumentSetId: DELIVERY,
        setName: 'Normal delivery set',
        setIssueId: null,
        expectedCount: 4,
        receivedCount: 4,
        missingCount: 0,
        extraCount: 0,
        damagedCount: 0,
        discrepancies: null,
      },
    ],
  },
];

const WASHES: WashCycleListItem[] = [
  {
    id: RECORD_ID,
    cycleNumber: 'WD-2600214',
    washerName: 'Getinge WD 46',
    program: 'Instruments 93 °C',
    status: 'Running',
    startedAt: hoursAgo(0.5),
    completedAt: null,
    a0: null,
    trays: 3,
    failureReason: null,
  },
  {
    id: id('402'),
    cycleNumber: 'WD-2600213',
    washerName: 'Getinge WD 46',
    program: 'Instruments 93 °C',
    status: 'Passed',
    startedAt: hoursAgo(3),
    completedAt: hoursAgo(2.2),
    a0: 3600,
    trays: 4,
    failureReason: null,
  },
  {
    id: id('403'),
    cycleNumber: 'WD-2600210',
    washerName: 'Miele PG 8536',
    program: 'Anaesthesia 90 °C',
    status: 'Failed',
    startedAt: hoursAgo(26),
    completedAt: hoursAgo(25),
    a0: 1200,
    trays: 2,
    failureReason: 'A0 below 3000: rewash',
  },
];

const WASH: WashCycleDto = {
  id: RECORD_ID,
  cycleNumber: 'WD-2600214',
  washerName: 'Getinge WD 46',
  program: 'Instruments 93 °C',
  status: 'Running',
  startedAt: hoursAgo(0.5),
  completedAt: null,
  disinfectionTemperatureC: null,
  holdMinutes: null,
  a0: null,
  requiredA0: 3000,
  inspectedClean: null,
  failureReason: null,
  operatorName: 'Ganesh Shinde',
  notes: null,
  trays: [
    { instrumentSetId: LAPAROTOMY, setName: 'Major laparotomy set', quantity: 2 },
    { instrumentSetId: DELIVERY, setName: 'Normal delivery set', quantity: 1 },
  ],
  packs: [],
  version: 1,
};

const loadItem = (
  overrides: Partial<LoadListItem> & Pick<LoadListItem, 'id' | 'cycleNumber' | 'status'>,
): LoadListItem => ({
  sterilizerName: 'Steam autoclave 1 (Tuttnauer)',
  method: 'Steam',
  loadedAt: hoursAgo(3),
  completedAt: null,
  chemicalIndicator: 'Pending',
  biologicalIndicator: 'Pending',
  releasedAt: null,
  failureReason: null,
  containsImplants: false,
  packs: 6,
  ...overrides,
});

const LOADS: LoadListItem[] = [
  loadItem({
    id: RECORD_ID,
    cycleNumber: 'ST1-2600341',
    status: 'Completed',
    completedAt: hoursAgo(1),
    chemicalIndicator: 'Pass',
  }),
  loadItem({ id: id('602'), cycleNumber: 'ST1-2600342', status: 'Running', loadedAt: hoursAgo(0.7) }),
  loadItem({ id: id('603'), cycleNumber: 'ST2-2600120', status: 'Loaded', loadedAt: hoursAgo(0.2), packs: 3 }),
  loadItem({
    id: id('604'),
    cycleNumber: 'ST1-2600339',
    status: 'Released',
    loadedAt: hoursAgo(28),
    completedAt: hoursAgo(27),
    chemicalIndicator: 'Pass',
    biologicalIndicator: 'Pass',
    releasedAt: hoursAgo(26),
  }),
  loadItem({
    id: id('605'),
    cycleNumber: 'PL1-2600044',
    sterilizerName: 'Plasma sterilizer (Sterrad 100NX)',
    method: 'Plasma',
    status: 'Failed',
    loadedAt: hoursAgo(50),
    completedAt: hoursAgo(49),
    chemicalIndicator: 'Pass',
    biologicalIndicator: 'Fail',
    releasedAt: hoursAgo(48),
    failureReason: 'Biological indicator grew at 24 hours',
    containsImplants: true,
    packs: 2,
  }),
];

const pack = (
  overrides: Partial<SterilePackDto> & Pick<SterilePackDto, 'id' | 'packNumber' | 'instrumentSetId' | 'setName'>,
): SterilePackDto => ({
  loadId: id('604'),
  cycleNumber: 'ST1-2600339',
  status: 'Available',
  sterileUntil: dateIn(29),
  isExpired: false,
  issuedToDepartmentId: null,
  washCycleId: id('402'),
  washCycleNumber: 'WD-2600213',
  ...overrides,
});

const PACKS: SterilePackDto[] = [
  pack({ id: id('701'), packNumber: 'PK-LAP01-0412', instrumentSetId: LAPAROTOMY, setName: 'Major laparotomy set' }),
  pack({ id: id('702'), packNumber: 'PK-LAP01-0413', instrumentSetId: LAPAROTOMY, setName: 'Major laparotomy set' }),
  pack({ id: id('703'), packNumber: 'PK-DEL01-0208', instrumentSetId: DELIVERY, setName: 'Normal delivery set' }),
  pack({
    id: id('704'),
    packNumber: 'PK-DEL01-0171',
    instrumentSetId: DELIVERY,
    setName: 'Normal delivery set',
    sterileUntil: dateIn(-2),
    isExpired: true,
  }),
];

const LOAD: LoadDto = {
  id: RECORD_ID,
  cycleNumber: 'ST1-2600341',
  sterilizerName: 'Steam autoclave 1 (Tuttnauer)',
  method: 'Steam',
  program: '134 °C prion cycle',
  containsImplants: false,
  status: 'Completed',
  loadedAt: hoursAgo(3),
  startedAt: hoursAgo(2.5),
  completedAt: hoursAgo(1),
  temperatureC: 134,
  pressureKpa: 304,
  exposureMinutes: 18,
  chemicalIndicator: 'Pass',
  biologicalIndicator: 'Pending',
  biologicalIndicatorLot: null,
  biologicalReadAt: null,
  operatorName: 'Ganesh Shinde',
  releasedAt: null,
  releasedByName: null,
  failureReason: null,
  notes: null,
  packs: [
    pack({
      id: id('711'),
      packNumber: 'PK-LAP01-0414',
      loadId: RECORD_ID,
      cycleNumber: 'ST1-2600341',
      instrumentSetId: LAPAROTOMY,
      setName: 'Major laparotomy set',
      status: 'InProcess',
      sterileUntil: null,
    }),
    pack({
      id: id('712'),
      packNumber: 'PK-DEL01-0209',
      loadId: RECORD_ID,
      cycleNumber: 'ST1-2600341',
      instrumentSetId: DELIVERY,
      setName: 'Normal delivery set',
      status: 'InProcess',
      sterileUntil: null,
    }),
  ],
  version: 4,
};

const issue = (
  overrides: Partial<SetIssueDto> & Pick<SetIssueDto, 'id' | 'packId' | 'packNumber' | 'status'>,
): SetIssueDto => ({
  loadId: id('604'),
  cycleNumber: 'ST1-2600339',
  instrumentSetId: LAPAROTOMY,
  setName: 'Major laparotomy set',
  departmentId: OT.id,
  departmentName: OT.name,
  issuedAt: hoursAgo(20),
  issuedToName: 'Sr. Lata Pawar',
  sterileUntil: dateIn(28),
  patientId: null,
  usedAt: null,
  recallFlaggedAt: null,
  recallReason: null,
  recallAcknowledgedAt: null,
  recallAction: null,
  ...overrides,
});

const ISSUES: SetIssueDto[] = [
  issue({ id: RECORD_ID, packId: id('721'), packNumber: 'PK-LAP01-0409', status: 'Issued' }),
  issue({
    id: id('802'),
    packId: id('722'),
    packNumber: 'PK-ORT-0031',
    status: 'Recalled',
    loadId: id('605'),
    cycleNumber: 'PL1-2600044',
    instrumentSetId: ORTHO,
    setName: 'DHS implant set',
    issuedAt: hoursAgo(47),
    recallFlaggedAt: hoursAgo(26),
    recallReason: 'Load PL1-2600044 failed its biological indicator after release',
  }),
  issue({
    id: id('803'),
    packId: id('723'),
    packNumber: 'PK-LAP01-0398',
    status: 'Used',
    patientId: '0192a000-0000-7000-8000-000000000002',
    usedAt: hoursAgo(18),
  }),
];

const RECALL: RecallReport = {
  loadId: RECORD_ID,
  cycleNumber: 'ST1-2600341',
  recallRequired: false,
  packsOnShelfRecalled: 0,
  issuesFlagged: 0,
  issuesAcknowledged: 0,
  patientsExposed: 0,
  issues: [],
};

const SUMMARY: CssdSummary = {
  from: dateIn(-29),
  to: dateIn(0),
  loads: 186,
  loadsReleased: 179,
  loadsFailed: 2,
  failureRatePercent: 1.1,
  biologicalPending: 1,
  byMethod: [
    { method: 'Steam', loads: 171, failed: 1 },
    { method: 'Plasma', loads: 15, failed: 1 },
  ],
  packsIssued: 942,
  openRequests: 2,
  receiptsWithDiscrepancy: 1,
  openRecalls: 1,
  packsExpiringSoon: 3,
  availableBySet: [
    { instrumentSetId: LAPAROTOMY, code: 'LAP-01', name: 'Major laparotomy set', available: 4, expiringSoon: 1 },
    { instrumentSetId: DELIVERY, code: 'DEL-01', name: 'Normal delivery set', available: 6, expiringSoon: 2 },
    { instrumentSetId: ORTHO, code: 'ORT-IMP', name: 'DHS implant set', available: 1, expiringSoon: 0 },
  ],
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/cssd/sets': SETS,
  'GET /api/v1/cssd/requests': REQUESTS,
  'GET /api/v1/cssd/receipts': RECEIPTS,
  'GET /api/v1/cssd/packs': PACKS,
  'GET /api/v1/cssd/issues': paged(ISSUES),
  'GET /api/v1/cssd/reports/summary': SUMMARY,
  'GET /api/v1/cssd/loads': paged(LOADS),
  [`GET /api/v1/cssd/loads/${RECORD_ID}`]: LOAD,
  [`GET /api/v1/cssd/loads/${RECORD_ID}/recall`]: RECALL,
  'GET /api/v1/cssd/wash-cycles': paged(WASHES),
  [`GET /api/v1/cssd/wash-cycles/${RECORD_ID}`]: WASH,
};
