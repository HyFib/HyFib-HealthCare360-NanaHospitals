import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import { RuleDrawer } from './RuleDrawer';

vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));

const CONSULT = { id: 's1', code: 'CON-GEN', name: 'General consultation', basePrice: 500 };
const OPTION = 'General consultation (CON-GEN)';

async function openServiceBox() {
  fakeServer({ 'GET /api/v1/masters/lookups/services': [CONSULT] });
  renderPicker(<RuleDrawer rule={null} onClose={vi.fn()} />);
  await userEvent.setup().click(await screen.findByRole('radio', { name: 'One service' }));
  return screen.findByRole('combobox', { name: /Service/ });
}

describe('RuleDrawer', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a service chosen as the search for what was typed runs is still the one chosen', async () => {
    const combobox = await openServiceBox();
    const user = userEvent.setup();

    await user.type(combobox, 'Gen');
    await screen.findByRole('option', { name: OPTION });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'e', OPTION);

    await waitFor(() => expect(combobox).toHaveValue(OPTION));
  });

  it('offers none of the last search once the text is shorter than a search', async () => {
    const combobox = await openServiceBox();
    const user = userEvent.setup();

    await user.type(combobox, 'Ge');
    await screen.findByRole('option', { name: OPTION });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: OPTION })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
  });
});
