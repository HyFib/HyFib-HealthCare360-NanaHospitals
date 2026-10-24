import type {
  GoodsReceiptDetail,
  GoodsReceiptListItem,
  PurchaseOrderDetail,
  PurchaseOrderListItem,
  RequisitionDetail,
  RequisitionListItem,
  VendorDetail,
  VendorListItem,
} from '@/modules/purchase/api';
import { IP_PHARMACY, ITEMS, OP_PHARMACY } from '../accessibility/fixtures/pharmacy';
import { paged, type FakeAnswers, type FakeRequest } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// Purchase: vendors, requisitions (awaiting approval, approved, ordered), purchase orders in each open state and
// goods receipts (a draft and a posted one). The records opened by id wait for the next step: the requisition and the
// order await approval, the goods receipt is a draft to post.

const now = Date.now();
const daysAgo = (days: number) => new Date(now - days * 86_400_000).toISOString();
const dateIn = (days: number) => new Date(now + days * 86_400_000).toISOString().slice(0, 10);
const dateAgo = (days: number) => dateIn(-days);

const id = (suffix: string) => `0192a000-0000-7000-8000-0000000f0${suffix}`;
const [PARACETAMOL, PANTOPRAZOLE, TRAMADOL] = ITEMS.map((item) => item.id) as [string, string, string];

const MICRO = id('101');
const ALKEM = id('102');

const VENDORS: VendorListItem[] = [
  {
    id: MICRO,
    code: 'V-MLD',
    name: 'Shree Ganesh Pharma Distributors',
    gstin: '27AAKFS1234L1Z5',
    drugLicenseNumber: 'MH-PZ3-20B-112233',
    drugLicenseValidUntil: dateIn(400),
    drugLicenseExpired: false,
    contactPerson: 'Mahesh Agarwal',
    phone: '9822098220',
    city: 'Pune',
    paymentTermsDays: 30,
    rating: 4,
    isActive: true,
  },
  {
    id: ALKEM,
    code: 'V-KMD',
    name: 'Kothari Medical Agencies',
    gstin: '27AABCK5678M1Z2',
    drugLicenseNumber: 'MH-PZ3-21B-445566',
    drugLicenseValidUntil: dateAgo(12),
    drugLicenseExpired: true,
    contactPerson: 'Nilesh Kothari',
    phone: '9890198901',
    city: 'Pimpri-Chinchwad',
    paymentTermsDays: 45,
    rating: 3,
    isActive: true,
  },
];

const VENDOR: VendorDetail = {
  id: MICRO,
  code: 'V-MLD',
  name: 'Shree Ganesh Pharma Distributors',
  gstin: '27AAKFS1234L1Z5',
  pan: 'AAKFS1234L',
  drugLicenseNumber: 'MH-PZ3-20B-112233',
  drugLicenseValidUntil: dateIn(400),
  contactPerson: 'Mahesh Agarwal',
  phone: '9822098220',
  email: 'orders@shreeganeshpharma.in',
  address: { line1: '14, Market Yard Road', city: 'Pune', state: 'Maharashtra', pinCode: '411037', country: 'India' },
  paymentTermsDays: 30,
  rating: 4,
  notes: null,
  isActive: true,
  version: 2,
};

const VENDOR_DETAILS: VendorDetail[] = [
  VENDOR,
  {
    ...VENDOR,
    id: ALKEM,
    code: 'V-KMD',
    name: 'Kothari Medical Agencies',
    gstin: '27AABCK5678M1Z2',
    pan: 'AABCK5678M',
    drugLicenseNumber: 'MH-PZ3-21B-445566',
    drugLicenseValidUntil: dateAgo(12),
    contactPerson: 'Nilesh Kothari',
    phone: '9890198901',
    email: null,
    address: { line1: 'Shop 3, Pimpri Camp', city: 'Pimpri-Chinchwad', state: 'Maharashtra', pinCode: '411017' },
    paymentTermsDays: 45,
    rating: 3,
    version: 1,
  },
];

const requisition = (
  overrides: Partial<RequisitionListItem> & Pick<RequisitionListItem, 'id' | 'number' | 'status'>,
): RequisitionListItem => ({
  storeId: OP_PHARMACY.id,
  storeName: OP_PHARMACY.name,
  requiredBy: dateIn(5),
  lineCount: 3,
  createdAt: daysAgo(1),
  requestedBy: 'Suresh Patil',
  ...overrides,
});

const REQUISITIONS: RequisitionListItem[] = [
  requisition({ id: RECORD_ID, number: 'PR2600087', status: 'Submitted' }),
  requisition({
    id: id('202'),
    number: 'PR2600085',
    status: 'Approved',
    storeId: IP_PHARMACY.id,
    storeName: IP_PHARMACY.name,
    createdAt: daysAgo(3),
  }),
  requisition({ id: id('203'), number: 'PR2600079', status: 'Ordered', createdAt: daysAgo(9), lineCount: 5 }),
];

const REQUISITION: RequisitionDetail = {
  id: RECORD_ID,
  number: 'PR2600087',
  storeId: OP_PHARMACY.id,
  storeName: OP_PHARMACY.name,
  status: 'Submitted',
  requiredBy: dateIn(5),
  remarks: 'Below reorder level after the dengue season',
  rejectReason: null,
  purchaseOrderId: null,
  createdAt: daysAgo(1),
  decidedAt: null,
  lines: [
    { id: id('211'), itemId: PARACETAMOL, itemName: 'Paracetamol 500 mg tablet', quantity: 50, remarks: null },
    { id: id('212'), itemId: PANTOPRAZOLE, itemName: 'Pantoprazole 40 mg tablet', quantity: 30, remarks: null },
    { id: id('213'), itemId: TRAMADOL, itemName: 'Tramadol 50 mg capsule', quantity: 10, remarks: 'Schedule H1' },
  ],
  version: 1,
};

const order = (
  overrides: Partial<PurchaseOrderListItem> & Pick<PurchaseOrderListItem, 'id' | 'number' | 'status' | 'netAmount'>,
): PurchaseOrderListItem => ({
  vendorId: MICRO,
  vendorName: 'Shree Ganesh Pharma Distributors',
  storeId: OP_PHARMACY.id,
  storeName: OP_PHARMACY.name,
  orderDate: dateAgo(1),
  expectedDeliveryDate: dateIn(4),
  lineCount: 3,
  ...overrides,
});

const ORDERS: PurchaseOrderListItem[] = [
  order({ id: RECORD_ID, number: 'PO2600142', status: 'Submitted', netAmount: 8_918 }),
  order({ id: id('302'), number: 'PO2600140', status: 'Draft', netAmount: 4_250, lineCount: 1 }),
  order({
    id: id('303'),
    number: 'PO2600137',
    status: 'Approved',
    netAmount: 23_480,
    vendorId: ALKEM,
    vendorName: 'Kothari Medical Agencies',
    orderDate: dateAgo(3),
  }),
  order({ id: id('304'), number: 'PO2600131', status: 'Sent', netAmount: 12_096, orderDate: dateAgo(6) }),
  order({
    id: id('305'),
    number: 'PO2600124',
    status: 'PartiallyReceived',
    netAmount: 31_640,
    orderDate: dateAgo(10),
    storeId: IP_PHARMACY.id,
    storeName: IP_PHARMACY.name,
  }),
];

const ORDER: PurchaseOrderDetail = {
  id: RECORD_ID,
  number: 'PO2600142',
  vendorId: MICRO,
  vendorName: 'Shree Ganesh Pharma Distributors',
  vendorGstin: '27AAKFS1234L1Z5',
  storeId: OP_PHARMACY.id,
  storeName: OP_PHARMACY.name,
  requisitionId: id('203'),
  orderDate: dateAgo(1),
  expectedDeliveryDate: dateIn(4),
  isInterState: false,
  paymentTermsDays: 30,
  remarks: null,
  status: 'Submitted',
  reviewNote: null,
  approvedAt: null,
  sentAt: null,
  closedAt: null,
  closeReason: null,
  grossAmount: 8_200,
  discountAmount: 237.5,
  taxableAmount: 7_962.5,
  cgstAmount: 477.75,
  sgstAmount: 477.75,
  igstAmount: 0,
  roundOff: 0,
  netAmount: 8_918,
  lines: [
    {
      id: id('311'),
      itemId: PARACETAMOL,
      itemName: 'Paracetamol 500 mg tablet',
      purchaseUnit: 'Strip',
      quantity: 50,
      freeQuantity: 5,
      rate: 12,
      discountPercent: 0,
      gstRate: 12,
      taxableAmount: 600,
      cgstAmount: 36,
      sgstAmount: 36,
      igstAmount: 0,
      lineTotal: 672,
      receivedQuantity: 0,
      pendingQuantity: 50,
    },
    {
      id: id('312'),
      itemId: PANTOPRAZOLE,
      itemName: 'Pantoprazole 40 mg tablet',
      purchaseUnit: 'Strip',
      quantity: 30,
      freeQuantity: 0,
      rate: 52,
      discountPercent: 5,
      gstRate: 12,
      taxableAmount: 1_482,
      cgstAmount: 88.92,
      sgstAmount: 88.92,
      igstAmount: 0,
      lineTotal: 1_659.84,
      receivedQuantity: 0,
      pendingQuantity: 30,
    },
    {
      id: id('313'),
      itemId: TRAMADOL,
      itemName: 'Tramadol 50 mg capsule',
      purchaseUnit: 'Strip',
      quantity: 100,
      freeQuantity: 0,
      rate: 61,
      discountPercent: 2.5,
      gstRate: 12,
      taxableAmount: 5_947.5,
      cgstAmount: 356.85,
      sgstAmount: 356.85,
      igstAmount: 0,
      lineTotal: 6_661.2,
      receivedQuantity: 0,
      pendingQuantity: 100,
    },
  ],
  version: 2,
};

const GRNS: GoodsReceiptListItem[] = [
  {
    id: RECORD_ID,
    number: 'GRN2600098',
    vendorId: MICRO,
    vendorName: 'Shree Ganesh Pharma Distributors',
    storeId: OP_PHARMACY.id,
    storeName: OP_PHARMACY.name,
    vendorInvoiceNumber: 'SGP/26-27/1873',
    vendorInvoiceDate: dateAgo(0),
    receivedOn: dateAgo(0),
    status: 'Draft',
    netAmount: 2_688,
    purchaseOrderId: id('304'),
  },
  {
    id: id('402'),
    number: 'GRN2600095',
    vendorId: MICRO,
    vendorName: 'Shree Ganesh Pharma Distributors',
    storeId: IP_PHARMACY.id,
    storeName: IP_PHARMACY.name,
    vendorInvoiceNumber: 'SGP/26-27/1811',
    vendorInvoiceDate: dateAgo(4),
    receivedOn: dateAgo(4),
    status: 'Posted',
    netAmount: 15_820,
    purchaseOrderId: id('305'),
  },
];

const GRN: GoodsReceiptDetail = {
  id: RECORD_ID,
  number: 'GRN2600098',
  vendorId: MICRO,
  vendorName: 'Shree Ganesh Pharma Distributors',
  vendorGstin: '27AAKFS1234L1Z5',
  storeId: OP_PHARMACY.id,
  storeName: OP_PHARMACY.name,
  purchaseOrderId: id('304'),
  vendorInvoiceNumber: 'SGP/26-27/1873',
  vendorInvoiceDate: dateAgo(0),
  receivedOn: dateAgo(0),
  isInterState: false,
  status: 'Draft',
  remarks: null,
  postedAt: null,
  grossAmount: 2_400,
  discountAmount: 0,
  taxableAmount: 2_400,
  cgstAmount: 144,
  sgstAmount: 144,
  igstAmount: 0,
  roundOff: 0,
  netAmount: 2_688,
  lines: [
    {
      id: id('411'),
      itemId: PARACETAMOL,
      itemName: 'Paracetamol 500 mg tablet',
      purchaseOrderLineId: id('391'),
      batchNumber: 'PCM2609B',
      manufactureDate: dateAgo(40),
      expiryDate: dateIn(690),
      quantity: 200,
      freeQuantity: 20,
      rate: 12,
      mrp: 22,
      discountPercent: 0,
      gstRate: 12,
      taxableAmount: 2_400,
      cgstAmount: 144,
      sgstAmount: 144,
      igstAmount: 0,
      lineTotal: 2_688,
      baseQuantity: 2_200,
      unitCost: 1.09,
      unitMrp: 2.2,
      stockBatchId: null,
      itemCode: 'PCM500',
      baseUnit: 'Tablet',
    },
  ],
  version: 1,
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/purchase/vendors': paged(VENDORS),
  'GET /api/v1/purchase/vendors/*': (request: FakeRequest) => VENDOR_DETAILS.find((v) => request.path.endsWith(v.id)),
  'GET /api/v1/purchase/requisitions': paged(REQUISITIONS),
  [`GET /api/v1/purchase/requisitions/${RECORD_ID}`]: REQUISITION,
  'GET /api/v1/purchase/orders': paged(ORDERS),
  [`GET /api/v1/purchase/orders/${RECORD_ID}`]: ORDER,
  'GET /api/v1/purchase/grns': paged(GRNS),
  [`GET /api/v1/purchase/grns/${RECORD_ID}`]: GRN,
};
