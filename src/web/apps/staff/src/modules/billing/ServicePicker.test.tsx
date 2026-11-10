import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import type { ServiceItemDto } from './api';
import { ServiceSearchField } from './ServicePicker';

const XRAY: ServiceItemDto = {
  id: 'svc1',
  code: 'RAD-CXR',
  name: 'Chest X-ray',
  shortName: null,
  category: 'Radiology',
  departmentId: null,
  basePrice: 450,
  taxRate: 0,
  sacCode: null,
  isDiscountAllowed: true,
  isPriceEditable: false,
  description: null,
  isActive: true,
  version: 1,
};

function Field({ onChange }: { onChange: (item: ServiceItemDto | null) => void }) {
  const [value, setValue] = useState<ServiceItemDto | null>(null);
  return (
    <ServiceSearchField
      label="Service"
      value={value}
      onChange={(item) => {
        setValue(item);
        onChange(item);
      }}
    />
  );
}

describe('ServiceSearchField', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a service chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/masters/lookups/services': [XRAY] });
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderPicker(<Field onChange={onChange} />);
    const combobox = screen.getByRole('combobox', { name: 'Service' });

    await user.type(combobox, 'Ch');
    await screen.findByRole('option', { name: /Chest X-ray/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'e', /Chest X-ray/);

    await waitFor(() => expect(onChange).toHaveBeenLastCalledWith(XRAY));
    expect(combobox).toHaveValue('Chest X-ray (RAD-CXR)');
  });

  it('offers none of the last search once the text is shorter than a search', async () => {
    fakeServer({ 'GET /api/v1/masters/lookups/services': [XRAY] });
    const user = userEvent.setup();
    renderPicker(<Field onChange={vi.fn()} />);
    const combobox = screen.getByRole('combobox', { name: 'Service' });

    await user.type(combobox, 'Ch');
    await screen.findByRole('option', { name: /Chest X-ray/ });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: /Chest X-ray/ })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
  });
});
