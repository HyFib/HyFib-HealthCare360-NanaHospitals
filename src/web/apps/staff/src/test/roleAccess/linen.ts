import type { LinenItem, LinenMovement, LinenStock } from '@/modules/linen/api';
import type { FakeAnswers } from '../fakeApi';

// Linen: bed sheets on a ward below par, infected linen waiting at the laundry, and a thermal disinfection batch.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;

const ITEMS: LinenItem[] = [
  {
    id: id('l1'),
    code: 'BEDSHEET',
    name: 'Bed sheet',
    lifeWashes: 150,
    averageWashes: 41.5,
    isActive: true,
    version: 1,
  },
  { id: id('l2'), code: 'GOWN', name: 'Patient gown', lifeWashes: 100, averageWashes: 93, isActive: true, version: 1 },
];

const STOCK: LinenStock = {
  items: [
    {
      linenItemId: id('l1'),
      code: 'BEDSHEET',
      name: 'Bed sheet',
      store: 120,
      wards: 34,
      soiled: 20,
      infected: 4,
      inCirculation: 178,
    },
    {
      linenItemId: id('l2'),
      code: 'GOWN',
      name: 'Patient gown',
      store: 60,
      wards: 25,
      soiled: 10,
      infected: 0,
      inCirculation: 95,
    },
  ],
  wards: [
    {
      wardId: id('w1'),
      wardName: 'General ward (male)',
      lines: [
        { linenItemId: id('l1'), itemName: 'Bed sheet', held: 34, par: 60, short: 26 },
        { linenItemId: id('l2'), itemName: 'Patient gown', held: 25, par: 20, short: 0 },
      ],
    },
  ],
};

const MOVEMENTS: LinenMovement[] = [
  {
    id: id('m1'),
    number: 'NHLN26000012',
    kind: 'Washed',
    wardName: null,
    infected: true,
    process: 'ThermalDisinfection',
    temperatureC: 72,
    holdMinutes: 4,
    condemnReason: null,
    notes: null,
    at: new Date().toISOString(),
    byName: 'Laundry supervisor',
    lines: [{ linenItemId: id('l1'), itemName: 'Bed sheet', quantity: 6 }],
    pieces: 6,
  },
];

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/linen/items': ITEMS,
  'GET /api/v1/linen/wards': [{ id: id('w1'), name: 'General ward (male)' }],
  'GET /api/v1/linen/stock': STOCK,
  'GET /api/v1/linen/movements': MOVEMENTS,
};
