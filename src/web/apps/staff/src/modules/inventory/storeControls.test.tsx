import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { RecallTrace } from '@/modules/pharmacy/recallTrace';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import { ANSWERS } from '@/test/roleAccess/inventory';
import { RECORD_ID } from '@/test/roleAccessIds';
import type { ItemLookup } from './api';

// The store's second-person controls on screen: a transfer counted short is sent for approval instead of booked in, and
// the recall trace lists every patient who had a batch, run only when asked.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const ITEM: ItemLookup = {
  id: '0192a000-0000-7000-8000-00000000dd01',
  code: 'PCM500',
  name: 'Paracetamol 500 mg Tablet',
  genericName: 'Paracetamol',
  strength: '500 mg',
  type: 'Drug',
  schedule: 'Otc',
  isControlled: false,
  isColdChain: false,
  baseUnit: 'TAB',
  purchaseUnit: 'TAB',
  purchaseUnitFactor: 1,
  gstRate: 5,
  available: null,
  mrp: null,
  nearestExpiry: null,
  isHighAlert: false,
};

const TRACE: RecallTrace = {
  itemId: ITEM.id,
  itemCode: ITEM.code,
  itemName: ITEM.name,
  batchNumber: 'PC2611',
  generatedAt: '2026-11-08T05:00:00Z',
  generatedBy: 'Asha Pharmacist',
  patientCount: 2,
  quantityWithPatients: 7,
  patients: [
    {
      source: 'CounterSale',
      at: '2026-11-02T05:00:00Z',
      document: 'PH2600112',
      storeName: 'OP Pharmacy',
      patientId: null,
      uhid: null,
      patientName: 'Walk-in Ravi',
      phone: '9000000001',
      quantity: 3,
      returned: 0,
      kept: 3,
      procedure: null,
      serialNumber: null,
      clinician: null,
    },
    {
      source: 'WardIssue',
      at: '2026-11-03T05:00:00Z',
      document: 'IPI2600031',
      storeName: 'IP Pharmacy',
      patientId: '0192a000-0000-7000-8000-00000000dd11',
      uhid: 'NH2600091',
      patientName: 'Meena Raj',
      phone: '9000000002',
      quantity: 5,
      returned: 1,
      kept: 4,
      procedure: null,
      serialNumber: null,
      clinician: null,
    },
    {
      source: 'Implant',
      at: '2026-11-04T05:00:00Z',
      document: 'SUR2600007',
      storeName: 'OT 1',
      patientId: '0192a000-0000-7000-8000-00000000dd12',
      uhid: 'NH2600092',
      patientName: 'Kumar Velu',
      phone: '9000000003',
      quantity: 1,
      returned: 0,
      kept: 1,
      procedure: 'Total knee replacement, right',
      serialNumber: 'SN-1001',
      clinician: 'Dr. Arun',
    },
  ],
  stock: [{ storeName: 'Central Store', onHand: 40, isBlocked: false, isRecalled: false, blockReason: null }],
};

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('a transfer counted in at the receiving store', SCREEN_TESTS, () => {
  it('sends a line counted short for approval with the counted quantity and the reason', async () => {
    stubBrowserApis();
    const server = fakeServer({
      ...ANSWERS,
      [`POST /api/v1/inventory/transfers/${RECORD_ID}/receive`]: { id: '0192a000-0000-7000-8000-00000000dd21' },
    });
    openScreen(`/inventory/transfers?id=${RECORD_ID}`);
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Receive' }, SLOW));
    const dialog = await screen.findByRole('dialog', { name: /Receive transfer/ }, SLOW);
    const counted = within(dialog).getByRole('spinbutton');
    await user.clear(counted);
    await user.type(counted, '8');
    expect(within(dialog).getByText(/counted short/)).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox', { name: 'Why it arrived short' }), 'Two strips crushed');
    await user.click(within(dialog).getByRole('button', { name: 'Send shortfall for approval' }));
    await waitFor(
      () =>
        expect(server.requests.find((r) => r.method === 'POST' && r.path.endsWith('/receive'))?.body).toEqual({
          lines: [{ lineId: expect.any(String), receivedQuantity: 8 }],
          note: 'Two strips crushed',
        }),
      SLOW,
    );
  });
});

describe('the recall trace', SCREEN_TESTS, () => {
  it('runs only when asked and lists every patient who had the batch', async () => {
    stubBrowserApis();
    const server = fakeServer({
      ...ANSWERS,
      'GET /api/v1/inventory/items/lookup': [ITEM],
      'GET /api/v1/pharmacy/recalls/trace': TRACE,
    });
    openScreen('/pharmacy/recall-trace');
    const user = userEvent.setup();
    await user.type(await screen.findByRole('combobox', { name: 'Item' }, SLOW), 'Parac');
    await user.click(await screen.findByRole('option', { name: /Paracetamol 500 mg Tablet/ }, SLOW));
    await user.type(screen.getByRole('textbox', { name: 'Batch number on the recall notice' }), 'PC2611');
    expect(server.requests.some((r) => r.path.endsWith('/recalls/trace'))).toBe(false);
    await user.click(screen.getByRole('button', { name: 'Trace' }));
    expect(await screen.findByText('Walk-in Ravi', {}, SLOW)).toBeInTheDocument();
    expect(screen.getByText('Meena Raj')).toBeInTheDocument();
    expect(screen.getByText('Implanted in surgery')).toBeInTheDocument();
    expect(screen.getByText('Total knee replacement, right; serial SN-1001; surgeon Dr. Arun')).toBeInTheDocument();
    expect(screen.getByText('Not held: hold it in Stock')).toBeInTheDocument();
    const asked = server.requests.find((r) => r.path.endsWith('/recalls/trace'))!.url.searchParams;
    expect([asked.get('itemId'), asked.get('batchNumber')]).toEqual([ITEM.id, 'PC2611']);
    await expectNoAxeViolations();
  });
});
