import type * as Core from '@hc360/core';
import { screen, waitFor, within } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ItemDetail } from '@/modules/inventory/api';
import type { QueueEntryDetail, QueueLineDto } from '@/modules/pharmacy/api';
import { ENTRY, ENTRY_ID, ITEMS, PHARMACY_ANSWERS } from './accessibility/fixtures/pharmacy';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis, type FakeRequest } from './appScreen';

// The dispensing screen marks each medicine with its schedule from the drug master. In the production-style run it
// marked ceftriaxone, a Schedule H1 antibiotic, with a red "NDPS" badge: it took the prescription line's controlled
// flag (Schedule H1, X or NDPS) for the item's NDPS flag.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const CEFTRIAXONE = '0192a000-0000-7000-8000-000000000e44';
const TRAMADOL_LINE = ENTRY.lines.find((l) => l.drugName.startsWith('Tramadol'))!;
const TRAMADOL_ITEM = ITEMS.find((i) => i.id === TRAMADOL_LINE.drugItemId)!;

const ceftriaxoneLine: QueueLineDto = {
  ...TRAMADOL_LINE,
  id: '0192a000-0000-7000-8000-000000000e24',
  prescriptionLineId: '0192a000-0000-7000-8000-000000000e34',
  drugItemId: CEFTRIAXONE,
  drugName: 'Ceftriaxone 1 g Injection',
  genericName: 'Ceftriaxone',
  strength: '1 g',
  dosageForm: 'Powder for injection',
  route: 'IV',
  dose: '1 g',
  instructions: null,
  // The line is controlled (Schedule H1: a register entry and the pharmacist's confirmation), not NDPS.
  isControlledDrug: true,
};

const ceftriaxone: ItemDetail = {
  ...TRAMADOL_ITEM,
  id: CEFTRIAXONE,
  code: 'CEF1G',
  name: 'Ceftriaxone 1 g Injection',
  genericName: 'Ceftriaxone',
  strength: '1 g',
  dosageForm: 'Powder for injection',
  baseUnit: 'Vial',
  schedule: 'H1',
  isControlled: false,
};

const entry: QueueEntryDetail = { ...ENTRY, lines: [...ENTRY.lines, ceftriaxoneLine] };
const items = [...ITEMS, ceftriaxone];

beforeEach(() => {
  stubBrowserApis();
  localStorage.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** The row of the prescription lines table that holds this medicine. */
async function lineRow(drugName: string) {
  const table = await screen.findByRole('table', { name: 'Prescription lines' }, SLOW);
  const row = within(table)
    .getAllByRole('row')
    .find((r) => within(r).queryByText(drugName));
  expect(row, drugName).toBeDefined();
  return row!;
}

describe('the dispensing screen marks each medicine with its own schedule', SCREEN_TESTS, () => {
  it('shows ceftriaxone as Schedule H1, not NDPS, and tramadol as H1 and NDPS', async () => {
    fakeServer({
      ...PHARMACY_ANSWERS,
      'GET /api/v1/print/letterhead': { name: 'Nana Hospitals, Pune' },
      'GET /api/v1/emr/patients/*': { allergyStatus: 'NoKnownAllergies', allergies: [], alerts: [] },
      'GET /api/v1/patients/*': (request: FakeRequest) =>
        request.path.endsWith('/visits') ? paged([]) : request.path.endsWith('/payer') ? { current: null } : undefined,
      [`GET /api/v1/pharmacy/queue/${ENTRY_ID}`]: entry,
      'GET /api/v1/inventory/items/*': (request: FakeRequest) =>
        items.find((item) => item.id === request.path.split('/').at(-1)),
    });
    openScreen(`/pharmacy/queue/${ENTRY_ID}`);

    const cef = await lineRow('Ceftriaxone 1 g Injection');
    // The line's own mark: it needs the controlled-medicine confirmation.
    expect(within(cef).getByText('Controlled')).toBeInTheDocument();
    await waitFor(() => expect(within(cef).getByText('H1')).toBeInTheDocument(), SLOW);
    expect(within(cef).queryByText(/NDPS/)).not.toBeInTheDocument();

    const tramadol = await lineRow(TRAMADOL_LINE.drugName);
    expect(within(tramadol).getByText('H1 · NDPS')).toBeInTheDocument();
    // A prescription-only medicine that is not scheduled H1 or X shows that, and no register mark.
    const pantoprazole = await lineRow('Pantoprazole 40 mg tablet');
    expect(within(pantoprazole).getByText('Rx · H')).toBeInTheDocument();
  });
});
