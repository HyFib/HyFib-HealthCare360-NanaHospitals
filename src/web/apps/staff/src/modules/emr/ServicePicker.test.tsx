import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import type { ServiceItemDto } from './ordersApi';
import { ServicePicker } from './ServicePicker';

const CBC: ServiceItemDto = {
  id: 'svc1',
  code: 'LAB-CBC',
  name: 'Complete Blood Count',
  shortName: 'CBC',
  category: 'Laboratory',
  departmentId: null,
  basePrice: 350,
  taxRate: 0,
  sacCode: null,
  isDiscountAllowed: true,
  isPriceEditable: false,
  description: null,
  isActive: true,
  version: 1,
};

describe('ServicePicker', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a service chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/masters/lookups/services': [CBC] });
    const onPick = vi.fn();
    const user = userEvent.setup();
    renderPicker(<ServicePicker label="Service" categories={null} onPick={onPick} />);
    const combobox = screen.getByRole('combobox', { name: 'Service' });

    await user.type(combobox, 'Co');
    await screen.findByRole('option', { name: /Complete Blood Count/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'm', /Complete Blood Count/);

    await waitFor(() => expect(onPick).toHaveBeenCalledWith(CBC));
  });

  it('offers none of the last search once the text is shorter than a search', async () => {
    fakeServer({ 'GET /api/v1/masters/lookups/services': [CBC] });
    const user = userEvent.setup();
    renderPicker(<ServicePicker label="Service" categories={null} onPick={vi.fn()} />);
    const combobox = screen.getByRole('combobox', { name: 'Service' });

    await user.type(combobox, 'Co');
    await screen.findByRole('option', { name: /Complete Blood Count/ });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: /Complete Blood Count/ })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
  });
});
