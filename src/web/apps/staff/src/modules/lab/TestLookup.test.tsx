import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, paged } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import type { LabTestListItem } from './api';
import { TestLookup, type PickedTestRef } from './TestLookup';

const HAEMOGLOBIN = {
  id: 't1',
  code: 'HB',
  name: 'Haemoglobin',
  section: 'Hematology',
  unit: 'g/dL',
} as LabTestListItem;

function Lookup({ onChange }: { onChange: (test: LabTestListItem | null) => void }) {
  const [value, setValue] = useState<PickedTestRef | null>(null);
  return (
    <TestLookup
      label="Test"
      value={value}
      onChange={(test) => {
        setValue(test);
        onChange(test);
      }}
    />
  );
}

describe('TestLookup', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a test chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/lab/tests': paged([HAEMOGLOBIN]) });
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderPicker(<Lookup onChange={onChange} />);
    const combobox = screen.getByRole('combobox', { name: 'Test' });

    await user.type(combobox, 'Ha');
    await screen.findByRole('option', { name: /Haemoglobin/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'e', /Haemoglobin/);

    await waitFor(() => expect(onChange).toHaveBeenLastCalledWith(HAEMOGLOBIN));
  });

  it('offers none of the last search once the text is shorter than a search', async () => {
    fakeServer({ 'GET /api/v1/lab/tests': paged([HAEMOGLOBIN]) });
    const user = userEvent.setup();
    renderPicker(<Lookup onChange={vi.fn()} />);
    const combobox = screen.getByRole('combobox', { name: 'Test' });

    await user.type(combobox, 'Ha');
    await screen.findByRole('option', { name: /Haemoglobin/ });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: /Haemoglobin/ })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
  });
});
