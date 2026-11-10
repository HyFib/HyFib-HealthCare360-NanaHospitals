import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import type { ServiceItemDto } from './api';
import { PackageDrawer } from './PackageDrawer';

vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));

const BED: ServiceItemDto = {
  id: 's1',
  code: 'BED-GEN',
  name: 'General ward bed',
  shortName: null,
  category: 'BedCharge',
  departmentId: null,
  basePrice: 1500,
  taxRate: 0,
  sacCode: null,
  isDiscountAllowed: true,
  isPriceEditable: false,
  description: null,
  isActive: true,
  version: 1,
};
const OPTION = /General ward bed \(BED-GEN\)/;

async function openDrawer() {
  fakeServer({ 'GET /api/v1/masters/lookups/services': [BED], 'GET /api/v1/billing/tariffs': [] });
  renderPicker(<PackageDrawer packageDetail={null} onClose={vi.fn()} />);
  return screen.findByRole('combobox', { name: 'Add a service' });
}

describe('PackageDrawer', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a service chosen as the search for what was typed runs is still the one chosen', async () => {
    const user = userEvent.setup();
    const combobox = await openDrawer();

    await user.type(combobox, 'Gen');
    await screen.findByRole('option', { name: OPTION });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'e', OPTION);

    // The service becomes an inclusion of the package, named by its name and code.
    await waitFor(() => expect(screen.getByText('General ward bed (BED-GEN)')).toBeInTheDocument());
  });

  it('offers none of the last search once the text is shorter than a search', async () => {
    const user = userEvent.setup();
    const combobox = await openDrawer();

    await user.type(combobox, 'Ge');
    await screen.findByRole('option', { name: OPTION });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: OPTION })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
  });
});
