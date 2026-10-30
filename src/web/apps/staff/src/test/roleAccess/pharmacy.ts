import type {
  ControlledRegisterRow,
  IpIssueDetail,
  IpIssueListItem,
  PharmacySaleDetail,
  PharmacySaleListItem,
  PharmacyStockRow,
  QueueEntryDetail,
  RefillRequestDto,
  SaleReturnDto,
  ScheduleHRegisterRow,
  WardIndentDetail,
  WardIndentListItem,
} from '@/modules/pharmacy/api';
import { BATCHES, ENTRY, IP_PHARMACY, ITEMS, OP_PHARMACY } from '../accessibility/fixtures/pharmacy';
import { paged, type FakeAnswers, type FakeRequest } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// What the pharmacy fixtures (accessibility/fixtures/pharmacy.ts: the queue, the dashboard, one prescription, the
// drug master and the OP pharmacy's batches) leave out: the prescription opened as a record, invoices (paid, with dues,
// cancelled), refill requests waiting, ward indents (pending, partly issued), issues to wards and the drug registers.

const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const hoursAgo = (hours: number) => minutesAgo(hours * 60);
const dateIn = (days: number) => new Date(now + days * 86_400_000).toISOString().slice(0, 10);

const id = (suffix: string) => `0192a000-0000-7000-8000-0000000fa${suffix}`;
const [PARACETAMOL, PANTOPRAZOLE, TRAMADOL] = ITEMS as [
  (typeof ITEMS)[number],
  (typeof ITEMS)[number],
  (typeof ITEMS)[number],
];
const RAVI = ENTRY.patient;
const SURESH = {
  ...RAVI,
  id: '0192a000-0000-7000-8000-000000000006',
  uhid: 'NH26000077',
  fullName: 'Suresh Kumar',
  age: '71Y',
  phone: '9850012345',
  category: 'General' as const,
};
const ADMISSION = '0192a000-0000-7000-8000-00000000d202';

/**
 * The prescription the queue's record screen opens: the fixtures' one under the id record screens open with, without
 * its controlled drug, whose register confirmation (a checkbox) would keep Dispense disabled.
 */
const QUEUE_ENTRY: QueueEntryDetail = {
  ...ENTRY,
  id: RECORD_ID,
  lines: ENTRY.lines.filter((line) => !line.isControlledDrug),
};

const STOCK: PharmacyStockRow[] = ITEMS.map((item) => {
  const batches = BATCHES[item.id] ?? [];
  return {
    itemId: item.id,
    code: item.code,
    name: item.name,
    genericName: item.genericName,
    strength: item.strength,
    schedule: item.schedule,
    isControlled: item.isControlled,
    requiresPrescription: item.requiresPrescription,
    isColdChain: item.isColdChain,
    baseUnit: item.baseUnit,
    available: batches.reduce((sum, b) => sum + b.available, 0),
    mrp: batches[0]?.mrp ?? null,
    nearestExpiry: batches[0]?.expiryDate ?? null,
    barcode: item.barcode,
    scanned: null,
  };
});

const sale = (
  overrides: Partial<PharmacySaleListItem> & Pick<PharmacySaleListItem, 'id' | 'invoiceNumber' | 'netAmount'>,
): PharmacySaleListItem => ({
  type: 'Counter',
  soldAt: minutesAgo(30),
  storeId: OP_PHARMACY.id,
  patientId: null,
  customerName: 'Walk-in customer',
  prescriptionNumber: null,
  paidAmount: overrides.netAmount,
  dueAmount: 0,
  returnedAmount: 0,
  status: 'Paid',
  ...overrides,
});

const SALES: PharmacySaleListItem[] = [
  sale({
    id: RECORD_ID,
    invoiceNumber: 'PS2600873',
    type: 'Prescription',
    netAmount: 186.5,
    paidAmount: 100,
    dueAmount: 86.5,
    status: 'PartiallyPaid',
    patientId: RAVI.id,
    customerName: RAVI.fullName,
    prescriptionNumber: 'RX2600311',
    soldAt: minutesAgo(15),
  }),
  sale({ id: id('202'), invoiceNumber: 'PS2600871', netAmount: 42 }),
  sale({
    id: id('203'),
    invoiceNumber: 'PS2600866',
    netAmount: 117.6,
    status: 'Cancelled',
    paidAmount: 0,
    soldAt: hoursAgo(3),
  }),
];

const SALE: PharmacySaleDetail = {
  id: RECORD_ID,
  invoiceNumber: 'PS2600873',
  type: 'Prescription',
  storeId: OP_PHARMACY.id,
  storeName: OP_PHARMACY.name,
  patientId: RAVI.id,
  patientUhid: RAVI.uhid,
  customerName: RAVI.fullName,
  customerPhone: RAVI.phone,
  prescriptionNumber: 'RX2600311',
  prescriberName: 'Dr. Meera Iyer',
  prescriberRegistrationNumber: 'MMC 2009/04/1234',
  soldAt: minutesAgo(15),
  soldBy: 'Suresh Patil',
  counsellingNotes: 'Take pantoprazole half an hour before breakfast',
  discountPercent: 0,
  grossAmount: 186.5,
  discountAmount: 0,
  taxableAmount: 166.52,
  cgstAmount: 9.99,
  sgstAmount: 9.99,
  igstAmount: 0,
  buyerGstin: null,
  buyerLegalName: null,
  placeOfSupply: null,
  roundOff: 0,
  netAmount: 186.5,
  paidAmount: 100,
  dueAmount: 86.5,
  returnedAmount: 0,
  refundedAmount: 0,
  status: 'PartiallyPaid',
  cancelReason: null,
  items: [
    {
      id: id('211'),
      itemId: PARACETAMOL.id,
      itemName: PARACETAMOL.name,
      genericName: PARACETAMOL.genericName,
      hsnCode: PARACETAMOL.hsnCode,
      schedule: PARACETAMOL.schedule,
      batchNumber: 'PCM2411A',
      expiryDate: dateIn(82),
      quantity: 15,
      unitMrp: 2.1,
      gstRate: 12,
      grossAmount: 31.5,
      discountAmount: 0,
      taxableAmount: 28.13,
      cgstAmount: 1.69,
      sgstAmount: 1.68,
      igstAmount: 0,
      netAmount: 31.5,
      returnedQuantity: 0,
      isSubstitution: false,
      prescribedDrugName: null,
      substitutionReason: null,
    },
    {
      id: id('212'),
      itemId: PANTOPRAZOLE.id,
      itemName: PANTOPRAZOLE.name,
      genericName: PANTOPRAZOLE.genericName,
      hsnCode: PANTOPRAZOLE.hsnCode,
      schedule: PANTOPRAZOLE.schedule,
      batchNumber: 'PTZ2502',
      expiryDate: dateIn(173),
      quantity: 14,
      unitMrp: 8.4,
      gstRate: 12,
      grossAmount: 117.6,
      discountAmount: 0,
      taxableAmount: 105,
      cgstAmount: 6.3,
      sgstAmount: 6.3,
      igstAmount: 0,
      netAmount: 117.6,
      returnedQuantity: 0,
      isSubstitution: false,
      prescribedDrugName: null,
      substitutionReason: null,
    },
    {
      id: id('213'),
      itemId: TRAMADOL.id,
      itemName: TRAMADOL.name,
      genericName: TRAMADOL.genericName,
      hsnCode: TRAMADOL.hsnCode,
      schedule: TRAMADOL.schedule,
      batchNumber: 'TRM2406',
      expiryDate: dateIn(233),
      quantity: 4,
      unitMrp: 9.35,
      gstRate: 12,
      grossAmount: 37.4,
      discountAmount: 0,
      taxableAmount: 33.39,
      cgstAmount: 2,
      sgstAmount: 2.01,
      igstAmount: 0,
      netAmount: 37.4,
      returnedQuantity: 0,
      isSubstitution: false,
      prescribedDrugName: null,
      substitutionReason: null,
    },
  ],
  payments: [
    {
      id: id('221'),
      mode: 'Upi',
      amount: 100,
      reference: 'UPI 431298765432',
      receivedAt: minutesAgo(15),
      isRefund: false,
    },
  ],
  version: 2,
};

const SALE_RETURNS: SaleReturnDto[] = [];

const REFILLS: RefillRequestDto[] = [
  {
    id: RECORD_ID,
    number: 'RF2600019',
    queueEntryId: '0192a000-0000-7000-8000-000000000e04',
    prescriptionNumber: 'RX2600317',
    patientId: SURESH.id,
    patientName: SURESH.fullName,
    uhid: SURESH.uhid,
    channel: 'Portal',
    status: 'Requested',
    requestedAt: hoursAgo(2),
    preferredPickupDate: dateIn(1),
    notes: 'Will collect after 5 pm',
    rejectReason: null,
    refillEntryId: null,
  },
  {
    id: id('302'),
    number: 'RF2600017',
    queueEntryId: id('391'),
    prescriptionNumber: 'RX2600288',
    patientId: '0192a000-0000-7000-8000-000000000001',
    patientName: 'Asha Devi',
    uhid: 'NH26000123',
    channel: 'Staff',
    status: 'Approved',
    requestedAt: hoursAgo(28),
    preferredPickupDate: null,
    notes: null,
    rejectReason: null,
    refillEntryId: id('392'),
  },
];

const wardIndent = (
  overrides: Partial<WardIndentListItem> & Pick<WardIndentListItem, 'id' | 'number' | 'status'>,
): WardIndentListItem => ({
  patientId: SURESH.id,
  patientName: SURESH.fullName,
  uhid: SURESH.uhid,
  admissionId: ADMISSION,
  wardName: 'General Ward — Male',
  bedLabel: 'GW-07',
  urgency: 'Routine',
  raisedAt: minutesAgo(40),
  raisedBy: 'Sr. Lata Pawar',
  lineCount: 2,
  ...overrides,
});

const WARD_INDENTS: WardIndentListItem[] = [
  wardIndent({ id: RECORD_ID, number: 'WI2600412', status: 'Pending', urgency: 'Stat' }),
  wardIndent({
    id: id('402'),
    number: 'WI2600405',
    status: 'PartiallyIssued',
    patientId: RAVI.id,
    patientName: RAVI.fullName,
    uhid: RAVI.uhid,
    admissionId: id('491'),
    bedLabel: 'GW-04',
    raisedAt: hoursAgo(5),
  }),
];

const WARD_INDENT: WardIndentDetail = {
  id: RECORD_ID,
  number: 'WI2600412',
  patient: SURESH,
  admissionId: ADMISSION,
  visitId: null,
  wardName: 'General Ward — Male',
  bedLabel: 'GW-07',
  urgency: 'Stat',
  status: 'Pending',
  raisedAt: minutesAgo(40),
  raisedBy: 'Sr. Lata Pawar',
  prescriptionId: null,
  remarks: 'Post-operative pain',
  cancelReason: null,
  lines: [
    {
      id: id('411'),
      itemId: PARACETAMOL.id,
      itemName: PARACETAMOL.name,
      requestedQuantity: 9,
      issuedQuantity: 0,
      remainingQuantity: 9,
      remarks: null,
      available: 240,
    },
    {
      id: id('412'),
      itemId: PANTOPRAZOLE.id,
      itemName: PANTOPRAZOLE.name,
      requestedQuantity: 3,
      issuedQuantity: 0,
      remainingQuantity: 3,
      remarks: null,
      available: 70,
    },
  ],
  version: 1,
};

const IP_ISSUES: IpIssueListItem[] = [
  {
    id: RECORD_ID,
    number: 'IPI2600733',
    patientId: RAVI.id,
    patientName: RAVI.fullName,
    uhid: RAVI.uhid,
    admissionId: id('491'),
    wardIndentId: id('402'),
    issuedAt: hoursAgo(4),
    totalAmount: 50.4,
    lineCount: 1,
    issuedBy: 'Suresh Patil',
  },
];

const IP_ISSUE: IpIssueDetail = {
  id: RECORD_ID,
  number: 'IPI2600733',
  storeId: IP_PHARMACY.id,
  patient: RAVI,
  admissionId: id('491'),
  wardIndentId: id('402'),
  queueEntryId: null,
  issuedAt: hoursAgo(4),
  issuedBy: 'Suresh Patil',
  totalAmount: 50.4,
  lines: [
    {
      id: id('511'),
      itemId: PANTOPRAZOLE.id,
      itemName: PANTOPRAZOLE.name,
      batchNumber: 'PTZ2502',
      expiryDate: dateIn(173),
      quantity: 6,
      unitMrp: 8.4,
      amount: 50.4,
      returnedQuantity: 0,
      chargeId: id('591'),
      unitPrice: 8.4,
    },
  ],
  returns: [],
};

const CONTROLLED: ControlledRegisterRow[] = [
  {
    id: id('601'),
    occurredAt: hoursAgo(26),
    storeId: OP_PHARMACY.id,
    storeName: OP_PHARMACY.name,
    itemId: TRAMADOL.id,
    itemName: TRAMADOL.name,
    schedule: 'H1',
    isControlled: true,
    batchNumber: 'TRM2406',
    expiryDate: dateIn(233),
    movementType: 'Sale',
    quantityIn: 0,
    quantityOut: 4,
    balanceAfter: 22,
    patientName: 'Mohan Deshpande',
    patientUhid: 'NH26000141',
    patientAddress: 'Flat 12, Shivaji Nagar, Pune 411005',
    prescriberName: 'Dr. Anil Rao',
    prescriberRegistrationNumber: 'MMC 2012/07/5678',
    prescriptionNumber: 'RX2600297',
    referenceNumber: 'PS2600812',
    recordedBy: 'Suresh Patil',
  },
  {
    id: id('602'),
    occurredAt: hoursAgo(50),
    storeId: OP_PHARMACY.id,
    storeName: OP_PHARMACY.name,
    itemId: TRAMADOL.id,
    itemName: TRAMADOL.name,
    schedule: 'H1',
    isControlled: true,
    batchNumber: 'TRM2406',
    expiryDate: dateIn(233),
    movementType: 'Grn',
    quantityIn: 20,
    quantityOut: 0,
    balanceAfter: 26,
    patientName: null,
    patientUhid: null,
    patientAddress: null,
    prescriberName: null,
    prescriberRegistrationNumber: null,
    prescriptionNumber: null,
    referenceNumber: 'GRN2600091',
    recordedBy: 'Suresh Patil',
  },
];

const SCHEDULE_H: ScheduleHRegisterRow[] = [
  {
    issuedAt: minutesAgo(15),
    documentNumber: 'PS2600873',
    source: 'Prescription sale',
    patientName: RAVI.fullName,
    prescriberName: 'Dr. Meera Iyer',
    prescriptionNumber: 'RX2600311',
    itemName: PANTOPRAZOLE.name,
    batchNumber: 'PTZ2502',
    quantity: 14,
  },
];

export const ANSWERS: FakeAnswers = {
  // The ward indent issues from the IP pharmacy; the screen tests' answer has batches only in the OP pharmacy.
  'GET /api/v1/pharmacy/stock/batches': (request: FakeRequest) =>
    [OP_PHARMACY.id, IP_PHARMACY.id].includes(request.url.searchParams.get('storeId') ?? '')
      ? BATCHES[request.url.searchParams.get('itemId') ?? '']
      : undefined,
  [`GET /api/v1/pharmacy/queue/${RECORD_ID}`]: QUEUE_ENTRY,
  'GET /api/v1/pharmacy/stock': STOCK,
  'GET /api/v1/pharmacy/sales': paged(SALES),
  [`GET /api/v1/pharmacy/sales/${RECORD_ID}`]: SALE,
  [`GET /api/v1/pharmacy/sales/${RECORD_ID}/returns`]: SALE_RETURNS,
  'GET /api/v1/pharmacy/refills': paged(REFILLS),
  'GET /api/v1/pharmacy/ward-indents': paged(WARD_INDENTS),
  [`GET /api/v1/pharmacy/ward-indents/${RECORD_ID}`]: WARD_INDENT,
  'GET /api/v1/pharmacy/ip-issues': (request: FakeRequest) => {
    const admissionId = request.url.searchParams.get('admissionId');
    return paged(admissionId ? IP_ISSUES.filter((i) => i.admissionId === admissionId) : IP_ISSUES);
  },
  [`GET /api/v1/pharmacy/ip-issues/${RECORD_ID}`]: IP_ISSUE,
  'GET /api/v1/pharmacy/registers/controlled': paged(CONTROLLED),
  'GET /api/v1/pharmacy/registers/schedule-h': paged(SCHEDULE_H),
};
