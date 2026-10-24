import type {
  CanteenSaleDto,
  CanteenSaleListItem,
  DailySalesSummary,
  MenuItemDto,
  StaffBalanceDto,
  StaffStatement,
} from '@/modules/canteen/api';
import { paged, type FakeAnswers, type FakeRequest } from '../fakeApi';
import { RECORD_ID } from '../roleAccessIds';

// The staff canteen: today's menu (food made in the kitchen, and packaged goods whose stock is tracked), today's bills
// (completed and cancelled), the day report and staff credit balances.

const now = Date.now();
const minutesAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString();
const today = new Date(now).toISOString().slice(0, 10);
const month = today.slice(0, 7);

const id = (suffix: string) => `0192a000-0000-7000-8000-0000000ca${suffix}`;

const IDLI = id('101');
const THALI = id('102');
const CHAI = id('103');
const WATER = id('104');

const item = (
  overrides: Partial<MenuItemDto> & Pick<MenuItemDto, 'id' | 'code' | 'name' | 'category' | 'price'>,
): MenuItemDto => ({
  gstRate: 5,
  hsnSac: null,
  priceIncludesGst: true,
  isVeg: true,
  isAvailable: true,
  trackStock: false,
  stockOnHand: 0,
  displayOrder: 0,
  description: null,
  version: 1,
  ...overrides,
});

const MENU: MenuItemDto[] = [
  item({ id: IDLI, code: 'IDL', name: 'Idli sambar (2 pcs)', category: 'Breakfast', price: 40, displayOrder: 1 }),
  item({
    id: THALI,
    code: 'THL',
    name: 'Veg thali',
    category: 'Meals',
    price: 90,
    displayOrder: 2,
    description: 'Chapati, rice, dal, sabzi, salad',
  }),
  item({ id: CHAI, code: 'CHA', name: 'Masala chai', category: 'Beverages', price: 15, displayOrder: 3 }),
  item({
    id: WATER,
    code: 'BIS1L',
    name: 'Packaged drinking water 1 L',
    category: 'Packaged',
    price: 20,
    gstRate: 18,
    hsnSac: '220190',
    trackStock: true,
    stockOnHand: 46,
    displayOrder: 4,
  }),
];

const SALES: CanteenSaleListItem[] = [
  {
    id: RECORD_ID,
    billNumber: 'CN2600412',
    soldAt: minutesAgo(20),
    customerType: 'Staff',
    customerName: null,
    employeeCode: 'EMP0217',
    netAmount: 145,
    paymentMode: 'StaffCredit',
    status: 'Completed',
    cashierName: 'Sanjay Gaikwad',
  },
  {
    id: id('202'),
    billNumber: 'CN2600411',
    soldAt: minutesAgo(35),
    customerType: 'Attendant',
    customerName: 'Rekha Pawar',
    employeeCode: null,
    netAmount: 110,
    paymentMode: 'Upi',
    status: 'Completed',
    cashierName: 'Sanjay Gaikwad',
  },
  {
    id: id('203'),
    billNumber: 'CN2600409',
    soldAt: minutesAgo(70),
    customerType: 'Visitor',
    customerName: null,
    employeeCode: null,
    netAmount: 40,
    paymentMode: 'Cash',
    status: 'Cancelled',
    cashierName: 'Sanjay Gaikwad',
  },
];

const SALE: CanteenSaleDto = {
  id: RECORD_ID,
  billNumber: 'CN2600412',
  soldAt: minutesAgo(20),
  businessDate: today,
  customerType: 'Staff',
  customerName: null,
  employeeCode: 'EMP0217',
  employeeName: 'Sr. Lata Pawar',
  taxableAmount: 138.1,
  cgstAmount: 3.45,
  sgstAmount: 3.45,
  grossAmount: 145,
  roundOff: 0,
  netAmount: 145,
  paymentMode: 'StaffCredit',
  paymentReference: null,
  status: 'Completed',
  cashierName: 'Sanjay Gaikwad',
  cancelledAt: null,
  cancelReason: null,
  lines: [
    {
      menuItemId: THALI,
      itemName: 'Veg thali',
      hsnSac: '996333',
      quantity: 1,
      unitPrice: 90,
      gstRate: 5,
      taxableAmount: 85.71,
      cgstAmount: 2.14,
      sgstAmount: 2.15,
      lineTotal: 90,
    },
    {
      menuItemId: IDLI,
      itemName: 'Idli sambar (2 pcs)',
      hsnSac: '996333',
      quantity: 1,
      unitPrice: 40,
      gstRate: 5,
      taxableAmount: 38.1,
      cgstAmount: 0.95,
      sgstAmount: 0.95,
      lineTotal: 40,
    },
    {
      menuItemId: CHAI,
      itemName: 'Masala chai',
      hsnSac: '996333',
      quantity: 1,
      unitPrice: 15,
      gstRate: 5,
      taxableAmount: 14.29,
      cgstAmount: 0.36,
      sgstAmount: 0.35,
      lineTotal: 15,
    },
  ],
};

const DAILY: DailySalesSummary = {
  date: today,
  bills: 214,
  cancelledBills: 1,
  taxableAmount: 18_342.5,
  cgst: 486.25,
  sgst: 486.25,
  roundOff: 0,
  netSales: 19_315,
  byPaymentMode: [
    { key: 'Cash', bills: 96, amount: 7_820 },
    { key: 'Upi', bills: 81, amount: 7_465 },
    { key: 'StaffCredit', bills: 37, amount: 4_030 },
  ],
  byCustomerType: [
    { key: 'Staff', bills: 92, amount: 8_640 },
    { key: 'Attendant', bills: 88, amount: 7_910 },
    { key: 'Visitor', bills: 34, amount: 2_765 },
  ],
  byCategory: [
    { key: 'Meals', bills: 110, amount: 9_900 },
    { key: 'Breakfast', bills: 72, amount: 3_960 },
    { key: 'Beverages', bills: 160, amount: 3_615 },
    { key: 'Packaged', bills: 92, amount: 1_840 },
  ],
  items: [
    {
      menuItemId: THALI,
      itemName: 'Veg thali',
      category: 'Meals',
      quantitySold: 110,
      amount: 9_900,
      closingStock: null,
    },
    {
      menuItemId: WATER,
      itemName: 'Packaged drinking water 1 L',
      category: 'Packaged',
      quantitySold: 92,
      amount: 1_840,
      closingStock: 46,
    },
  ],
  byHsnSac: [
    { hsnSac: '996333', gstRate: 5, quantity: 512, taxableAmount: 16_783.4, cgst: 419.6, sgst: 419.6, total: 17_622.6 },
    { hsnSac: '220190', gstRate: 18, quantity: 92, taxableAmount: 1_559.3, cgst: 140.35, sgst: 140.35, total: 1_840 },
  ],
};

const BALANCES: StaffBalanceDto[] = [
  { employeeCode: 'EMP0217', employeeName: 'Sr. Lata Pawar', balance: 1_265, lastEntryDate: today },
  { employeeCode: 'EMP0144', employeeName: 'Dr. Farah Khan', balance: 2_480, lastEntryDate: today },
  { employeeCode: 'EMP0390', employeeName: 'Ganesh Shinde', balance: 340, lastEntryDate: today },
];

const STATEMENT: StaffStatement = {
  employeeCode: 'EMP0217',
  employeeName: 'Sr. Lata Pawar',
  period: month,
  openingBalance: 980,
  purchases: 1_265,
  credits: 980,
  closingBalance: 1_265,
  lines: [
    { date: `${month}-01`, kind: 'PayrollDeduction', amount: -980, reference: 'Salary deduction', balance: 0 },
    { date: today, kind: 'Sale', amount: 145, reference: 'CN2600412', balance: 1_265 },
  ],
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/canteen/menu': MENU,
  'GET /api/v1/canteen/sales': paged(SALES),
  'GET /api/v1/canteen/sales/*': (request: FakeRequest) => (request.path.endsWith(RECORD_ID) ? SALE : undefined),
  'GET /api/v1/canteen/reports/daily': DAILY,
  'GET /api/v1/canteen/staff-credit/balances': BALANCES,
  'GET /api/v1/canteen/staff-credit/statement': STATEMENT,
};
