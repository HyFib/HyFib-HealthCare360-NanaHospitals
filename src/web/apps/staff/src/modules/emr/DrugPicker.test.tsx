import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import { DrugPicker } from './DrugPicker';
import type { PrescribableDrug } from './prescriptionsApi';
import type { MedicineDefaults } from './prescriptionForm';

vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));

const PARACETAMOL: PrescribableDrug = {
  id: 'd1',
  code: 'PCM500',
  name: 'Paracetamol 500 mg tablet',
  genericName: 'Paracetamol',
  brandName: null,
  strength: '500 mg',
  dosageForm: 'Tablet',
  schedule: 'None',
  isControlled: false,
  controlledSchedule: null,
  requiresPrescription: false,
  isColdChain: false,
  baseUnit: 'Tablet',
  available: 120,
  nearestExpiry: null,
};

function Picker({ onPick }: { onPick: (defaults: MedicineDefaults) => void }) {
  const [value, setValue] = useState('');
  return <DrugPicker ariaLabel="Medicine" value={value} onChange={setValue} onPick={onPick} />;
}

describe('DrugPicker', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a drug chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/emr/favourites': [], 'GET /api/v1/emr/drugs': [PARACETAMOL] });
    const onPick = vi.fn();
    const user = userEvent.setup();
    renderPicker(<Picker onPick={onPick} />);
    const combobox = screen.getByRole('combobox', { name: 'Medicine' });

    await user.type(combobox, 'Par');
    await screen.findByRole('option', { name: /Paracetamol 500 mg tablet/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'a', /Paracetamol 500 mg tablet/);

    await waitFor(() =>
      expect(onPick).toHaveBeenLastCalledWith(
        expect.objectContaining({ drugItemId: 'd1', drugName: 'Paracetamol 500 mg tablet' }),
      ),
    );
  });

  it('offers none of the last search once the text is shorter than a search', async () => {
    fakeServer({ 'GET /api/v1/emr/favourites': [], 'GET /api/v1/emr/drugs': [PARACETAMOL] });
    const user = userEvent.setup();
    renderPicker(<Picker onPick={vi.fn()} />);
    const combobox = screen.getByRole('combobox', { name: 'Medicine' });

    await user.type(combobox, 'Pa');
    await screen.findByRole('option', { name: /Paracetamol/ });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: /Paracetamol/ })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
  });
});
