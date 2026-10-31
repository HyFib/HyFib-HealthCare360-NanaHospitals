import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { RoleListItem } from './api';

// Adding a user on the users screen, whole with the shell, against a fake server: the roles are a group of checkboxes,
// each named by its own label, and "at least one role" is asked of the group, not of each box.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const id = (suffix: string) => `0192a000-0000-7000-8000-00000000b${suffix}`;

const role = (suffix: string, name: string): RoleListItem => ({
  id: id(suffix),
  name,
  description: null,
  isSystem: true,
  permissionCount: 10,
  userCount: 1,
});

const ROLES = [role('001', 'Accountant'), role('002', 'Billing'), role('003', 'FrontOffice'), role('004', 'Nurse')];

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  server = fakeServer({
    'GET /api/v1/admin/users': paged([]),
    'GET /api/v1/admin/roles': ROLES,
    'GET /api/v1/masters/lookups/practitioners': [],
    'POST /api/v1/admin/users': {
      userId: id('100'),
      temporaryPassword: 'Kite-Lamp-42',
      expiresAt: '2026-11-01T06:30:00Z',
    },
  });
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const created = () => server.requests.filter((request) => request.method === 'POST');

describe('adding a user', SCREEN_TESTS, () => {
  it('names each role by its own label and asks for at least one on the group', async () => {
    const user = userEvent.setup();
    openScreen('/admin/users');
    await user.click(await screen.findByRole('button', { name: 'Add user' }, SLOW));
    const drawer = await screen.findByRole('dialog', { name: /Add user/ });
    const roles = await within(drawer).findByRole('group', { name: 'Roles (required)' }, SLOW);

    // Each box has an id of its own and is named by its own label; none is required, and only the group's label
    // shows the asterisk.
    const boxes = ROLES.map((r) => within(roles).getByRole('checkbox', { name: r.name }));
    expect(new Set(boxes.map((box) => box.id)).size).toBe(ROLES.length);
    for (const box of boxes) {
      expect(box).not.toBeRequired();
    }
    expect(within(roles).getAllByText('*')).toHaveLength(1);
    await expectNoAxeViolations();

    // A role's name ticks its own box.
    await user.click(within(roles).getByText('FrontOffice'));
    expect(within(roles).getByRole('checkbox', { name: 'FrontOffice' })).toBeChecked();
    expect(within(roles).getByRole('checkbox', { name: 'Accountant' })).not.toBeChecked();
    await user.click(within(roles).getByText('FrontOffice'));
    expect(within(roles).getByRole('checkbox', { name: 'FrontOffice' })).not.toBeChecked();

    // Without a role nothing is sent, and the group says why.
    await user.type(within(drawer).getByRole('textbox', { name: /User name/ }), 'priya.s');
    await user.type(within(drawer).getByRole('textbox', { name: /Full name/ }), 'Priya Sharma');
    await user.click(within(drawer).getByRole('button', { name: 'Create user' }));
    await waitFor(() => expect(roles).toHaveAccessibleDescription('Give the user at least one role.'));
    expect(created()).toEqual([]);

    await user.click(within(roles).getByRole('checkbox', { name: 'Nurse' }));
    await user.click(within(roles).getByRole('checkbox', { name: 'Billing' }));
    await user.click(within(drawer).getByRole('button', { name: 'Create user' }));
    await waitFor(() => expect(created()).toHaveLength(1));
    expect(created()[0]).toMatchObject({
      path: '/api/v1/admin/users',
      body: { userName: 'priya.s', displayName: 'Priya Sharma', roleIds: [id('004'), id('002')] },
    });
  });
});
