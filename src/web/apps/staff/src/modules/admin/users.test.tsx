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

// Access is given by two people (#155): what an edit adds waits for another administrator who may approve grants.
describe('access that waits for a second administrator', SCREEN_TESTS, () => {
  const userId = id('200');
  const waiting = {
    approvalId: id('901'),
    roleIds: [id('004')],
    facilityIds: [],
    activate: false,
    requestedBy: 'Hospital Admin',
    requestedAt: '2026-11-02T05:00:00Z',
  };

  it("says when a new user's roles wait for approval", async () => {
    server = fakeServer({
      'GET /api/v1/admin/users': paged([]),
      'GET /api/v1/admin/roles': ROLES,
      'GET /api/v1/masters/lookups/practitioners': [],
      'POST /api/v1/admin/users': {
        userId: id('100'),
        temporaryPassword: 'Kite-Lamp-42',
        expiresAt: '2026-11-01T06:30:00Z',
        approvalId: id('900'),
      },
    });
    const user = userEvent.setup();
    openScreen('/admin/users');
    await user.click(await screen.findByRole('button', { name: 'Add user' }, SLOW));
    const drawer = await screen.findByRole('dialog', { name: /Add user/ });
    await user.type(within(drawer).getByRole('textbox', { name: /User name/ }), 'priya.s');
    await user.type(within(drawer).getByRole('textbox', { name: /Full name/ }), 'Priya Sharma');
    await user.click(await within(drawer).findByRole('checkbox', { name: 'Nurse' }, SLOW));
    await user.type(within(drawer).getByRole('textbox', { name: /Reason, for the approver/ }), 'Joins ward 3.');
    await user.click(within(drawer).getByRole('button', { name: 'Create user' }));

    const dialog = await screen.findByRole('dialog', { name: 'Temporary password for Priya Sharma' }, SLOW);
    expect(
      within(dialog).getByText(/Their roles wait for another administrator who may approve grants/),
    ).toBeInTheDocument();
    expect(created()[0]).toMatchObject({ body: { roleIds: [id('004')], reason: 'Joins ward 3.' } });
  });

  it('shows the roles waiting on an account as given, and says an edit that adds one was asked for', async () => {
    server = fakeServer({
      'GET /api/v1/admin/users': paged([
        {
          id: userId,
          userName: 'priya.s',
          displayName: 'Priya Sharma',
          email: null,
          phone: null,
          isActive: true,
          isLocked: false,
          mfaEnabled: false,
          lastLoginAt: null,
          roles: [],
        },
      ]),
      [`GET /api/v1/admin/users/${userId}`]: {
        id: userId,
        userName: 'priya.s',
        displayName: 'Priya Sharma',
        email: null,
        phone: null,
        isActive: true,
        mfaEnabled: false,
        mustChangePassword: true,
        lastLoginAt: null,
        lockoutEndsAt: null,
        practitionerId: null,
        defaultFacilityId: null,
        roleIds: [],
        facilityIds: [],
        version: 3,
        pendingGrant: waiting,
      },
      'GET /api/v1/admin/roles': ROLES,
      'GET /api/v1/masters/lookups/practitioners': [],
      [`PUT /api/v1/admin/users/${userId}`]: {
        id: id('902'),
        kind: 'identity.user_grant',
        summary: 'Access for Priya Sharma: roles Billing',
      },
    });
    const user = userEvent.setup();
    openScreen('/admin/users');
    await user.click(await screen.findByText('Priya Sharma', {}, SLOW));
    const drawer = await screen.findByRole('dialog', { name: /Priya Sharma/ }, SLOW);

    // The nurse role waits: the notice says so, with the request, and its box stays ticked so nothing is asked twice.
    expect(await within(drawer).findByText('Waiting for approval')).toBeInTheDocument();
    expect(within(drawer).getByText(/Roles Nurse, asked for by Hospital Admin/)).toBeInTheDocument();
    expect(within(drawer).getByRole('checkbox', { name: 'Nurse' })).toBeChecked();
    await expectNoAxeViolations();

    await user.click(within(drawer).getByRole('checkbox', { name: 'Billing' }));
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    const put = server.requests.find((request) => request.method === 'PUT');
    expect(put?.body).toMatchObject({ roleIds: [id('004'), id('002')], version: 3 });
  });
});
