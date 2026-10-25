import type { PendingWaste, WasteHandover, WasteMonth, WasteYear } from '@/modules/bmw/api';
import type { FakeAnswers } from '../fakeApi';

// Bio-medical waste: two bags waiting (one kept beyond 48 hours, so voiding is offered), a handover, the month and the year.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const hoursAgo = (hours: number) => new Date(Date.now() - hours * 3_600_000).toISOString();
const totals = (yellow: number, red: number, white: number, blue: number) => [
  { category: 'Yellow' as const, bags: yellow ? 1 : 0, kg: yellow },
  { category: 'Red' as const, bags: red ? 1 : 0, kg: red },
  { category: 'White' as const, bags: white ? 1 : 0, kg: white },
  { category: 'Blue' as const, bags: blue ? 1 : 0, kg: blue },
];

const PENDING: PendingWaste = {
  bags: [
    {
      id: id('b1'),
      barcode: 'NHBW260000412',
      category: 'White',
      departmentId: null,
      source: 'Ward 3 dressing room',
      weightKg: 0.8,
      collectedAt: hoursAgo(50),
      collectedByName: 'Sr. Kavitha Nair',
      status: 'Collected',
      overdue: true,
      handoverId: null,
      voidReason: null,
      version: 1,
    },
    {
      id: id('b2'),
      barcode: 'NHBW260000413',
      category: 'Yellow',
      departmentId: null,
      source: 'Main theatre',
      weightKg: 4.25,
      collectedAt: hoursAgo(3),
      collectedByName: 'Ravi (OT)',
      status: 'Collected',
      overdue: false,
      handoverId: null,
      voidReason: null,
      version: 1,
    },
  ],
  byCategory: totals(4.25, 0, 0.8, 0),
  overdue: 1,
};

const HANDOVER: WasteHandover = {
  id: id('h1'),
  number: 'NHBH2600031',
  handedOverAt: hoursAgo(26),
  cbwtf: 'Ramky CBWTF, Kumbakonam',
  vehicleNumber: 'TN 49 AX 2071',
  receivedBy: 'S. Raman',
  receiptNumber: 'RC-88412',
  handedOverByName: 'Quality office',
  bagCount: 14,
  totalKg: 38.6,
  lateBags: 0,
  byCategory: totals(21.4, 9.8, 4.2, 3.2),
};

const now = new Date();
const MONTH: WasteMonth = {
  year: now.getFullYear(),
  month: now.getMonth() + 1,
  days: [{ date: hoursAgo(26).slice(0, 10), byCategory: totals(21.4, 9.8, 4.2, 3.2), totalKg: 38.6 }],
  totals: totals(21.4, 9.8, 4.2, 3.2),
  handovers: 1,
  lateBags: 0,
};

const YEAR: WasteYear = {
  year: now.getFullYear() - 1,
  authorisationNumber: 'TNPCB/BMW/2024/0412',
  cbwtf: 'Ramky CBWTF, Kumbakonam',
  totals: totals(7120, 3260, 1410, 980),
  totalKg: 12770,
  averageKgPerDay: 34.986,
  handovers: 361,
  lateBags: 4,
  voidedBags: 12,
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/bmw/bags/pending': PENDING,
  'GET /api/v1/bmw/handovers': [HANDOVER],
  'GET /api/v1/bmw/register': MONTH,
  'GET /api/v1/bmw/annual': YEAR,
};
