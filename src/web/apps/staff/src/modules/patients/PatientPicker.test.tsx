import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, paged } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import type { PatientListItem } from './api';
import { PatientPicker, type PickedPatient } from './PatientPicker';

const ASHA: PatientListItem = {
  id: 'p1',
  uhid: 'NH000123',
  fullName: 'Asha Raman',
  gender: 'Female',
  dateOfBirth: '1990-04-01',
  age: '36 y',
  phoneMasked: null,
  category: 'General',
  isVip: false,
  isDeceased: false,
  registeredAt: '2026-01-05T09:00:00Z',
  lastVisitAt: null,
};

function Picker({ onChange }: { onChange: (patient: PickedPatient | null) => void }) {
  const [value, setValue] = useState<PickedPatient | null>(null);
  return (
    <PatientPicker
      value={value}
      onChange={(patient) => {
        setValue(patient);
        onChange(patient);
      }}
    />
  );
}

describe('PatientPicker', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a patient chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/patients': paged([ASHA]) });
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderPicker(<Picker onChange={onChange} />);
    const combobox = screen.getByRole('combobox', { name: 'Patient' });

    await user.type(combobox, 'Ram');
    await screen.findByRole('option', { name: /Asha Raman · NH000123/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'a', /Asha Raman · NH000123/);

    await waitFor(() =>
      expect(onChange).toHaveBeenLastCalledWith({ id: 'p1', uhid: 'NH000123', fullName: 'Asha Raman' }),
    );
    expect(combobox).toHaveValue('Asha Raman (NH000123)');
  });

  it('offers none of the last search once the text is shorter than a search', async () => {
    fakeServer({ 'GET /api/v1/patients': paged([ASHA]) });
    const user = userEvent.setup();
    renderPicker(<Picker onChange={vi.fn()} />);
    const combobox = screen.getByRole('combobox', { name: 'Patient' });

    await user.type(combobox, 'Ram');
    await screen.findByRole('option', { name: /Asha Raman/ });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: /Asha Raman/ })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
  });
});
