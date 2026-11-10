import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import type { OtStoreItem, SurgeryDetail } from './api';
import { MaterialsPanel } from './MaterialsPanel';

vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));

const BOOKING = {
  id: 'b1',
  bookingNumber: 'NHOT2600042',
  status: 'InProgress',
  startedAt: '2026-11-08T04:00:00Z',
  procedureName: 'Inguinal hernia repair',
  version: 1,
} as SurgeryDetail;

const MESH: OtStoreItem = {
  itemId: 'i1',
  code: 'MESH-15',
  name: 'Polypropylene mesh',
  genericName: null,
  manufacturer: 'Ethicon',
  type: 'Implant',
  baseUnit: 'pc',
  available: 5,
  nearestExpiry: null,
  mrp: 5200,
  batches: [],
};

describe('MaterialsPanel', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a store item chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/ot/bookings/b1/materials': [], 'GET /api/v1/ot/store-items': [MESH] });
    const user = userEvent.setup();
    renderPicker(<MaterialsPanel booking={BOOKING} />);
    await user.click(await screen.findByRole('button', { name: 'Record item' }));
    const combobox = await screen.findByRole('combobox', { name: /Item from the OT store/ });

    await user.type(combobox, 'Mes');
    await screen.findByRole('option', { name: /Polypropylene mesh/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'h', /Polypropylene mesh/);

    await waitFor(() => expect(combobox).toHaveValue('Polypropylene mesh'));
    // The item's details are filled from the whole store item, not only its name.
    expect(screen.getByRole('textbox', { name: /^Item/ })).toHaveValue('Polypropylene mesh');
  });
});
