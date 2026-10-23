import type { BatchStock, ItemDetail, StoreDto } from '@/modules/inventory/api';
import type { PharmacyDashboard, QueueEntryDetail, QueueLineDto, QueueListItem } from '@/modules/pharmacy/api';
import { paged, STAFF_USER, type FakeAnswers, type FakeRequest } from '../../fakeApi';

// The pharmacy's dispensing queue and one prescription being dispensed, with canned data: three medicines (one of them
// controlled), their batches in the OP pharmacy and the drug master.

const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
/** A calendar date `days` from today, as the server sends expiry dates (yyyy-MM-dd). */
const dateIn = (days: number) => new Date(now + days * 86_400_000).toISOString().slice(0, 10);
/** The id the API gives a record ends with. */
const lastSegment = (request: FakeRequest) => request.path.split('/').at(-1) ?? '';

export const ASHA = '0192a000-0000-7000-8000-000000000001';
export const RAVI = '0192a000-0000-7000-8000-000000000002';

export const OP_PHARMACY: StoreDto = {
  id: '0192a000-0000-7000-8000-000000000e61',
  code: 'OPPH',
  name: 'OP pharmacy',
  type: 'Pharmacy',
  departmentId: null,
  departmentName: null,
  inChargeUserId: null,
  inChargeName: 'Suresh Patil',
  location: 'Ground floor, OPD block',
  isActive: true,
  version: 1,
};
export const IP_PHARMACY: StoreDto = {
  ...OP_PHARMACY,
  id: '0192a000-0000-7000-8000-000000000e62',
  code: 'IPPH',
  name: 'IP pharmacy',
  location: 'First floor, ward block',
};

const queued = (
  overrides: Partial<QueueListItem> & Pick<QueueListItem, 'id' | 'prescriptionNumber' | 'patientId' | 'patientName'>,
): QueueListItem => ({
  uhid: 'NH26000123',
  gender: 'Female',
  age: '39Y',
  prescriberName: 'Dr. Meera Iyer',
  isInpatient: false,
  status: 'Pending',
  lineCount: 2,
  openLines: 2,
  hasControlledDrugs: false,
  isRefill: false,
  signedAt: minutesAgo(13),
  receivedAt: minutesAgo(12),
  waitingMinutes: 12,
  ...overrides,
});

export const ENTRY_ID = '0192a000-0000-7000-8000-000000000e01';

export const QUEUE: QueueListItem[] = [
  queued({
    id: ENTRY_ID,
    prescriptionNumber: 'RX2600311',
    patientId: RAVI,
    patientName: 'Ravi Shankar',
    uhid: 'NH26000124',
    gender: 'Male',
    age: '62Y',
    lineCount: 3,
    openLines: 3,
    hasControlledDrugs: true,
    signedAt: minutesAgo(25),
    receivedAt: minutesAgo(24),
    waitingMinutes: 24,
  }),
  queued({
    id: '0192a000-0000-7000-8000-000000000e02',
    prescriptionNumber: 'RX2600314',
    patientId: ASHA,
    patientName: 'Asha Devi',
    prescriberName: 'Dr. Anil Rao',
  }),
  queued({
    id: '0192a000-0000-7000-8000-000000000e03',
    prescriptionNumber: 'RX2600309',
    patientId: '0192a000-0000-7000-8000-000000000005',
    patientName: 'Fatima Shaikh',
    uhid: 'NH26000098',
    age: '54Y',
    status: 'PartiallyDispensed',
    lineCount: 4,
    openLines: 1,
    signedAt: minutesAgo(48),
    receivedAt: minutesAgo(47),
    waitingMinutes: 41,
  }),
  queued({
    id: '0192a000-0000-7000-8000-000000000e04',
    prescriptionNumber: 'RX2600317',
    patientId: '0192a000-0000-7000-8000-000000000006',
    patientName: 'Suresh Kumar',
    uhid: 'NH26000077',
    gender: 'Male',
    age: '71Y',
    prescriberName: 'Dr. Anil Rao',
    isRefill: true,
    signedAt: minutesAgo(4),
    receivedAt: minutesAgo(3),
    waitingMinutes: 3,
  }),
];

export const DASHBOARD: PharmacyDashboard = {
  pendingOutpatient: 4,
  pendingInpatient: 2,
  partiallyDispensed: 1,
  pendingWardIndents: 3,
  pendingRefills: 1,
  salesToday: 38,
  salesAmountToday: 24650.5,
  refundsToday: 0,
  byMode: [
    { mode: 'Cash', amount: 11240, count: 21 },
    { mode: 'Upi', amount: 10890.5, count: 14 },
    { mode: 'Card', amount: 2520, count: 3 },
  ],
  nearExpiryBatches: 1,
  nearExpiryValueAtMrp: 84,
  nearExpiry: [
    {
      batchId: '0192a000-0000-7000-8000-000000000e55',
      storeId: OP_PHARMACY.id,
      storeName: OP_PHARMACY.name,
      itemId: '0192a000-0000-7000-8000-000000000e42',
      itemCode: 'PAN40',
      itemName: 'Pantoprazole 40 mg tablet',
      batchNumber: 'PTZ2309',
      expiryDate: dateIn(20),
      daysToExpiry: 20,
      quantity: 10,
      valueAtCost: 52,
      valueAtMrp: 84,
    },
  ],
  averageWaitMinutes: 9,
};

const PARACETAMOL = '0192a000-0000-7000-8000-000000000e41';
const PANTOPRAZOLE = '0192a000-0000-7000-8000-000000000e42';
const TRAMADOL = '0192a000-0000-7000-8000-000000000e43';

const drugLine = (
  overrides: Partial<QueueLineDto> &
    Pick<
      QueueLineDto,
      'id' | 'prescriptionLineId' | 'drugItemId' | 'drugName' | 'genericName' | 'strength' | 'requestedQuantity'
    >,
): QueueLineDto => ({
  dosageForm: 'Tablet',
  route: 'Oral',
  dose: '1 tablet',
  frequency: 'Once a day',
  durationDays: 5,
  instructions: null,
  isControlledDrug: false,
  dispensedQuantity: 0,
  remainingQuantity: overrides.requestedQuantity,
  status: 'Pending',
  available: null,
  mrp: null,
  nearestExpiry: null,
  ...overrides,
});

export const ENTRY: QueueEntryDetail = {
  id: ENTRY_ID,
  prescriptionId: '0192a000-0000-7000-8000-000000000e11',
  prescriptionNumber: 'RX2600311',
  patient: {
    id: RAVI,
    uhid: 'NH26000124',
    fullName: 'Ravi Shankar',
    gender: 'Male',
    dateOfBirth: null,
    age: '62Y',
    phone: '9822012345',
    bloodGroup: 'BPositive',
    category: 'Staff',
    isVip: false,
    isDeceased: false,
    photoFileId: null,
  },
  visitId: '0192a000-0000-7000-8000-000000000e15',
  admissionId: null,
  practitionerId: STAFF_USER.practitionerId!,
  prescriberName: 'Dr. Meera Iyer',
  prescriberRegistrationNumber: 'MMC 2009/04/1234',
  isInpatient: false,
  status: 'Pending',
  signedAt: minutesAgo(25),
  receivedAt: minutesAgo(24),
  closeReason: null,
  refillsAuthorized: 0,
  refillsUsed: 0,
  refillValidUntil: null,
  refillOfEntryId: null,
  lines: [
    drugLine({
      id: '0192a000-0000-7000-8000-000000000e21',
      prescriptionLineId: '0192a000-0000-7000-8000-000000000e31',
      drugItemId: PARACETAMOL,
      drugName: 'Paracetamol 500 mg tablet',
      genericName: 'Paracetamol',
      strength: '500 mg',
      frequency: 'Three times a day',
      instructions: 'After food',
      requestedQuantity: 15,
      available: 240,
      mrp: 2.1,
      nearestExpiry: dateIn(82),
    }),
    drugLine({
      id: '0192a000-0000-7000-8000-000000000e22',
      prescriptionLineId: '0192a000-0000-7000-8000-000000000e32',
      drugItemId: PANTOPRAZOLE,
      drugName: 'Pantoprazole 40 mg tablet',
      genericName: 'Pantoprazole',
      strength: '40 mg',
      frequency: 'Once a day before breakfast',
      durationDays: 14,
      requestedQuantity: 14,
      available: 60,
      mrp: 8.4,
      nearestExpiry: dateIn(173),
    }),
    drugLine({
      id: '0192a000-0000-7000-8000-000000000e23',
      prescriptionLineId: '0192a000-0000-7000-8000-000000000e33',
      drugItemId: TRAMADOL,
      drugName: 'Tramadol 50 mg capsule',
      genericName: 'Tramadol',
      strength: '50 mg',
      dosageForm: 'Capsule',
      dose: '1 capsule',
      frequency: 'Twice a day',
      durationDays: 3,
      instructions: 'Only when the knee pain is severe',
      isControlledDrug: true,
      requestedQuantity: 6,
      available: 20,
      mrp: 9.5,
      nearestExpiry: dateIn(233),
    }),
  ],
  version: 3,
};

const drug = (
  overrides: Partial<ItemDetail> & Pick<ItemDetail, 'id' | 'code' | 'name' | 'genericName' | 'strength'>,
): ItemDetail => ({
  brandName: null,
  type: 'Drug',
  categoryId: null,
  dosageForm: 'Tablet',
  packSize: '10 tablets per strip',
  baseUnit: 'Tablet',
  purchaseUnit: 'Strip',
  purchaseUnitFactor: 10,
  hsnCode: '30049099',
  gstRate: 12,
  schedule: 'H',
  isControlled: false,
  isColdChain: false,
  vedClass: 'Essential',
  reorderLevel: 100,
  reorderQuantity: 500,
  minimumStock: 50,
  maximumStock: 1000,
  manufacturer: 'Micro Labs Ltd',
  barcode: null,
  isActive: true,
  requiresPrescription: true,
  requiresRegister: false,
  version: 1,
  ...overrides,
});

export const ITEMS: ItemDetail[] = [
  drug({
    id: PARACETAMOL,
    code: 'PCM500',
    name: 'Paracetamol 500 mg tablet',
    genericName: 'Paracetamol',
    strength: '500 mg',
    schedule: 'Otc',
    requiresPrescription: false,
    barcode: '8901234500017',
  }),
  drug({
    id: PANTOPRAZOLE,
    code: 'PAN40',
    name: 'Pantoprazole 40 mg tablet',
    genericName: 'Pantoprazole',
    strength: '40 mg',
    manufacturer: 'Alkem Laboratories',
  }),
  drug({
    id: TRAMADOL,
    code: 'TRM50',
    name: 'Tramadol 50 mg capsule',
    genericName: 'Tramadol',
    strength: '50 mg',
    dosageForm: 'Capsule',
    packSize: '10 capsules per strip',
    baseUnit: 'Capsule',
    schedule: 'H1',
    isControlled: true,
    vedClass: 'Vital',
    reorderLevel: 20,
    reorderQuantity: 100,
    minimumStock: 10,
    maximumStock: 200,
    manufacturer: 'Sun Pharma',
    requiresRegister: true,
  }),
];

const batch = (
  overrides: Partial<BatchStock> & Pick<BatchStock, 'batchId' | 'itemId' | 'batchNumber' | 'daysToExpiry' | 'mrp'>,
): BatchStock => ({
  storeId: OP_PHARMACY.id,
  expiryDate: dateIn(overrides.daysToExpiry ?? 0),
  unitCost: 0,
  gstRate: 12,
  onHand: 0,
  reserved: 0,
  available: 0,
  isExpired: false,
  isBlocked: false,
  blockReason: null,
  ...overrides,
});

/** Batches of each medicine in the OP pharmacy, as the server lists them (earliest expiry first). */
export const BATCHES: Record<string, BatchStock[]> = {
  [PARACETAMOL]: [
    batch({
      batchId: '0192a000-0000-7000-8000-000000000e51',
      itemId: PARACETAMOL,
      batchNumber: 'PCM2411A',
      daysToExpiry: 82,
      mrp: 2.1,
      unitCost: 1.2,
      onHand: 40,
      available: 40,
    }),
    batch({
      batchId: '0192a000-0000-7000-8000-000000000e52',
      itemId: PARACETAMOL,
      batchNumber: 'PCM2503C',
      daysToExpiry: 355,
      mrp: 2.2,
      unitCost: 1.25,
      onHand: 200,
      available: 200,
    }),
  ],
  [PANTOPRAZOLE]: [
    batch({
      batchId: '0192a000-0000-7000-8000-000000000e55',
      itemId: PANTOPRAZOLE,
      batchNumber: 'PTZ2309',
      daysToExpiry: 20,
      mrp: 8.4,
      unitCost: 5.2,
      onHand: 10,
      available: 10,
    }),
    batch({
      batchId: '0192a000-0000-7000-8000-000000000e53',
      itemId: PANTOPRAZOLE,
      batchNumber: 'PTZ2502',
      daysToExpiry: 173,
      mrp: 8.4,
      unitCost: 5.2,
      onHand: 60,
      available: 60,
    }),
  ],
  [TRAMADOL]: [
    batch({
      batchId: '0192a000-0000-7000-8000-000000000e54',
      itemId: TRAMADOL,
      batchNumber: 'TRM2406',
      daysToExpiry: 233,
      mrp: 9.5,
      unitCost: 6.1,
      gstRate: 12,
      onHand: 22,
      reserved: 2,
      available: 20,
    }),
  ],
};

/** What the dispensing screens ask the server for, beyond the patient's banner. */
export const PHARMACY_ANSWERS: FakeAnswers = {
  'GET /api/v1/pharmacy/queue': paged(QUEUE),
  'GET /api/v1/pharmacy/dashboard': DASHBOARD,
  [`GET /api/v1/pharmacy/queue/${ENTRY_ID}`]: ENTRY,
  'GET /api/v1/inventory/stores': [OP_PHARMACY, IP_PHARMACY],
  'GET /api/v1/inventory/items/*': (request: FakeRequest) => ITEMS.find((item) => item.id === lastSegment(request)),
  'GET /api/v1/pharmacy/stock/batches': (request: FakeRequest) =>
    request.url.searchParams.get('storeId') === OP_PHARMACY.id
      ? BATCHES[request.url.searchParams.get('itemId') ?? '']
      : undefined,
};
