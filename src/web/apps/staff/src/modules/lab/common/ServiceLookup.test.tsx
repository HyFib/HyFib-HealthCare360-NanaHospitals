import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import { ServiceLookup, type ServiceItem } from './ServiceLookup';

const CBC: ServiceItem = {
  id: 'svc1',
  code: 'LAB-CBC',
  name: 'Complete Blood Count',
  shortName: 'CBC',
  category: 'Laboratory',
  basePrice: 350,
  taxRate: 0,
  isActive: true,
};

function Lookup({ onChange }: { onChange: (id: string | null) => void }) {
  const [value, setValue] = useState<string | null>(null);
  return (
    <ServiceLookup
      category="Laboratory"
      value={value}
      onChange={(id) => {
        setValue(id);
        onChange(id);
      }}
    />
  );
}

describe('ServiceLookup', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a service chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/masters/lookups/services': [CBC] });
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderPicker(<Lookup onChange={onChange} />);
    const combobox = screen.getByRole('combobox', { name: 'Billable service' });

    await user.type(combobox, 'Co');
    await screen.findByRole('option', { name: /Complete Blood Count · LAB-CBC/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'm', /Complete Blood Count · LAB-CBC/);

    await waitFor(() => expect(onChange).toHaveBeenLastCalledWith('svc1'));
    expect(combobox).toHaveValue('Complete Blood Count (LAB-CBC) · ₹350');
  });

  it('offers none of the last search once the text is shorter than a search', async () => {
    fakeServer({ 'GET /api/v1/masters/lookups/services': [CBC] });
    const user = userEvent.setup();
    renderPicker(<Lookup onChange={vi.fn()} />);
    const combobox = screen.getByRole('combobox', { name: 'Billable service' });

    await user.type(combobox, 'Co');
    await screen.findByRole('option', { name: /Complete Blood Count/ });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: /Complete Blood Count/ })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
  });
});
