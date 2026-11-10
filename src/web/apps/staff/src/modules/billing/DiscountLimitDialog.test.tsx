import type * as Ui from '@hc360/ui';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, paged } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import { DiscountLimitDialog } from './DiscountDecision';

vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', async (importOriginal) => ({ ...(await importOriginal<typeof Ui>()), useNotify: () => notify }));

const MEERA = { id: 'u1', displayName: 'Meera Iyer', userName: 'meera.iyer' };

describe('DiscountLimitDialog', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a user chosen as the search for what was typed runs is still the one chosen', async () => {
    const server = fakeServer({
      'GET /api/v1/admin/users': paged([MEERA]),
      'PUT /api/v1/billing/discount-limits': null,
    });
    const user = userEvent.setup();
    renderPicker(<DiscountLimitDialog limit={null} onClose={vi.fn()} />);
    const combobox = await screen.findByRole('combobox', { name: /User/ });

    await user.type(combobox, 'Me');
    await screen.findByRole('option', { name: /Meera Iyer/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'e', /Meera Iyer/);

    await waitFor(() => expect(combobox).toHaveValue('Meera Iyer'));
    await user.type(screen.getByRole('textbox', { name: /Maximum discount/ }), '5');
    await user.click(screen.getByRole('button', { name: 'Save limit' }));
    await waitFor(() =>
      expect(server.requests.find((r) => r.method === 'PUT')?.body).toMatchObject({ userId: 'u1', maxPercent: 5 }),
    );
  });

  it('offers none of the last search once the text is shorter than a search', async () => {
    fakeServer({ 'GET /api/v1/admin/users': paged([MEERA]) });
    const user = userEvent.setup();
    renderPicker(<DiscountLimitDialog limit={null} onClose={vi.fn()} />);
    const combobox = await screen.findByRole('combobox', { name: /User/ });

    await user.type(combobox, 'Me');
    await screen.findByRole('option', { name: /Meera Iyer/ });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: /Meera Iyer/ })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
  });
});
