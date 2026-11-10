import { useSession } from '@hc360/core';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, paged, STAFF_USER } from '@/test/appScreen';
import { renderPicker, typeAndChooseAsTheSearchRuns } from '@/test/searchPickers';
import { UserPicker } from './UserPicker';
import type { DirectoryUser, PickedUser } from './users';

vi.mock('@/lib/permissions', () => ({ usePermission: () => true }));

const KAVYA: DirectoryUser = {
  id: '0192a000-0000-7000-8000-0000000000b2',
  userName: 'kavya.nair',
  displayName: 'Kavya Nair',
  roles: ['Nurse'],
  isActive: true,
};

function Picker({ onChange }: { onChange: (user: PickedUser | null) => void }) {
  const [value, setValue] = useState<PickedUser | null>(null);
  return (
    <UserPicker
      label="Assign to"
      value={value}
      onChange={(user) => {
        setValue(user);
        onChange(user);
      }}
    />
  );
}

describe('UserPicker', () => {
  beforeEach(() => {
    useSession.setState({
      status: 'authenticated',
      accessToken: 'test-token',
      expiresAt: Date.now() + 8 * 3600_000,
      user: STAFF_USER,
      locked: false,
    });
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('a staff member chosen as the search for what was typed runs is still the one chosen', async () => {
    fakeServer({ 'GET /api/v1/admin/users': paged([KAVYA]) });
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderPicker(<Picker onChange={onChange} />);
    const combobox = screen.getByRole('combobox', { name: 'Assign to' });

    await user.type(combobox, 'Ka');
    await screen.findByRole('option', { name: /Kavya Nair · kavya.nair/ });
    await typeAndChooseAsTheSearchRuns(user, combobox, 'v', /Kavya Nair · kavya.nair/);

    await waitFor(() => expect(onChange).toHaveBeenLastCalledWith({ id: KAVYA.id, name: 'Kavya Nair' }));
    expect(combobox).toHaveValue('Kavya Nair');
  });

  it('offers none of the last search once the text is shorter than a search', async () => {
    fakeServer({ 'GET /api/v1/admin/users': paged([KAVYA]) });
    const user = userEvent.setup();
    renderPicker(<Picker onChange={vi.fn()} />);
    const combobox = screen.getByRole('combobox', { name: 'Assign to' });

    await user.type(combobox, 'Ka');
    await screen.findByRole('option', { name: /Kavya Nair/ });
    await user.type(combobox, '{Backspace}', { skipClick: true });

    await waitFor(() => expect(screen.queryByRole('option', { name: /Kavya Nair/ })).not.toBeInTheDocument());
    expect(combobox).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('option', { name: /\(you\)/ })).toBeInTheDocument();
  });
});
