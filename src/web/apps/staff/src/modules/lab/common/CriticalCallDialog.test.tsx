import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import { CriticalCallDialog } from './CriticalCallDialog';

const DR_RAO = { id: 'pr1', name: 'Dr. Rao', code: 'RAO' };

function renderDialog() {
  renderPicker(
    <CriticalCallDialog
      open
      findings={[{ id: 'f1', label: 'Potassium 6.9 mmol/L (critically high)' }]}
      onSubmit={vi.fn()}
      onCancel={vi.fn()}
    />,
  );
  return screen.findByRole('combobox', { name: /Doctor in the system/ });
}

describe('CriticalCallDialog', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a doctor chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/masters/lookups/practitioners': [DR_RAO] });
    const user = userEvent.setup();
    const combobox = await renderDialog();

    await user.type(combobox, 'Ra');
    await screen.findByRole('option', { name: 'Dr. Rao' });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'o', 'Dr. Rao');

    await waitFor(() => expect(combobox).toHaveValue('Dr. Rao'));
    expect(screen.getByRole('textbox', { name: /Person informed/ })).toHaveValue('Dr. Rao');
  });

  it('offers none of the last search once the text is shorter than a search', async () => {
    fakeServer({ 'GET /api/v1/masters/lookups/practitioners': [DR_RAO] });
    const user = userEvent.setup();
    const combobox = await renderDialog();

    await user.type(combobox, 'Ra');
    await screen.findByRole('option', { name: 'Dr. Rao' });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: 'Dr. Rao' })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
  });
});
