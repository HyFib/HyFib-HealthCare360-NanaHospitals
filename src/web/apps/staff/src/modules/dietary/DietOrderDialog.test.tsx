import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import type { DietTypeDto } from './api';
import { DietOrderDialog } from './DietOrderDialog';

vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));

const NORMAL: DietTypeDto = {
  id: 'd1',
  code: 'NRM',
  name: 'Normal',
  description: null,
  category: 'Regular',
  chargeServiceItemId: null,
  chargeServiceCode: null,
  defaultMealSlots: 'Lunch',
  defaultMeals: ['Lunch'],
  displayOrder: 1,
  isActive: true,
  version: 1,
};
const DR_RAO = { id: 'pr1', name: 'Dr. Rao', code: 'RAO' };
const ADMISSION = { admissionId: 'a1', patientId: 'p1', patientName: 'Asha Raman' };

describe('DietOrderDialog', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a doctor chosen as the search for what was typed runs is still the one chosen', async () => {
    const server = fakeServer({
      'GET /api/v1/dietary/diet-types': [NORMAL],
      'GET /api/v1/masters/lookups/practitioners': [DR_RAO],
      'POST /api/v1/dietary/orders': { id: 'o1', dietName: 'Normal' },
    });
    const user = userEvent.setup();
    renderPicker(<DietOrderDialog admission={ADMISSION} onClose={vi.fn()} />);
    const combobox = await screen.findByRole('combobox', { name: /Ordered by/ });

    await user.type(combobox, 'Ra');
    await screen.findByRole('option', { name: 'Dr. Rao' });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'o', 'Dr. Rao');

    await waitFor(() => expect(combobox).toHaveValue('Dr. Rao'));
    await screen.findByRole('option', { name: 'Normal (NRM)' });
    await user.selectOptions(screen.getByRole('combobox', { name: /^Diet/ }), 'Normal (NRM)');
    await user.click(screen.getByRole('button', { name: 'Order diet' }));
    await waitFor(() =>
      expect(server.requests.find((r) => r.method === 'POST')?.body).toMatchObject({ orderedByPractitionerId: 'pr1' }),
    );
  });

  it('offers none of the last search once the text is shorter than a search', async () => {
    fakeServer({
      'GET /api/v1/dietary/diet-types': [NORMAL],
      'GET /api/v1/masters/lookups/practitioners': [DR_RAO],
    });
    const user = userEvent.setup();
    renderPicker(<DietOrderDialog admission={ADMISSION} onClose={vi.fn()} />);
    const combobox = await screen.findByRole('combobox', { name: /Ordered by/ });

    await user.type(combobox, 'Ra');
    await screen.findByRole('option', { name: 'Dr. Rao' });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: 'Dr. Rao' })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
  });
});
