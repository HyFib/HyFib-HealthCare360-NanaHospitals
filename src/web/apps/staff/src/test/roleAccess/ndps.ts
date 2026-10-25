import type {
  NarcoticCount,
  NarcoticCountSheet,
  NdpsAnnualReturn,
  NdpsDailyAccount,
  NdpsStore,
} from '@/modules/pharmacy/ndps';
import type { FakeAnswers } from '../fakeApi';

// The NDPS accounts: one store holding morphine, its day with a receipt and a dispense, last year's return, and a
// count waiting for its witness (so that witnessing it is offered).

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const daysAgo = (days: number) => new Date(Date.now() - days * 86_400_000).toISOString();
const STORE: NdpsStore = { id: id('s1'), name: 'Main pharmacy' };
const MORPHINE = { itemId: id('i1'), itemName: 'Morphine 10 mg/ml Injection' };

const DAILY: NdpsDailyAccount = {
  date: daysAgo(0).slice(0, 10),
  storeId: STORE.id,
  storeName: STORE.name,
  drugs: [
    {
      ...MORPHINE,
      opening: 20,
      received: 10,
      dispensed: 2,
      otherIn: 0,
      otherOut: 0,
      closing: 28,
      balanced: true,
      movements: [
        {
          at: daysAgo(0),
          type: 'Dispense',
          in: 0,
          out: 2,
          balanceAfter: 18,
          reference: 'DSP-26-00412',
          patientUhid: 'NH26000611',
          patientName: 'Lakshmi Narayanan',
          prescriberName: 'Dr. Anil Rao',
          prescriptionNumber: 'RX-26-01920',
        },
      ],
    },
  ],
};

const ANNUAL: NdpsAnnualReturn = {
  year: new Date().getFullYear() - 1,
  drugs: [
    {
      ...MORPHINE,
      opening: 40,
      received: 300,
      consumed: 296,
      writtenOff: 2,
      closing: 42,
      balanced: true,
      estimateNextYear: 326,
    },
  ],
};

const COUNT: NarcoticCount = {
  id: id('c1'),
  storeId: STORE.id,
  storeName: STORE.name,
  countedAt: daysAgo(0),
  countedByName: 'Sr. Kavitha Nair',
  status: 'Counted',
  witnessedAt: null,
  witnessedByName: null,
  explanation: null,
  hasDifference: false,
  lines: [{ ...MORPHINE, registerBalance: 28, counted: 28, difference: 0 }],
  version: 1,
};

const SHEET: NarcoticCountSheet = { storeId: STORE.id, storeName: STORE.name, items: [MORPHINE] };

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/pharmacy/ndps/stores': [STORE],
  'GET /api/v1/pharmacy/ndps/daily': DAILY,
  'GET /api/v1/pharmacy/ndps/annual': ANNUAL,
  'GET /api/v1/pharmacy/ndps/counts': [COUNT],
  'GET /api/v1/pharmacy/ndps/counts/sheet': SHEET,
};
