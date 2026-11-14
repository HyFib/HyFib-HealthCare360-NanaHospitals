import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, json, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { PermissionGroup, RoleDetail, RoleListItem } from './api';

// Editing a role on the roles screen, whole with the shell, against a fake server: permissions added to a role wait
// for a second administrator. Those waiting show as given, with a notice, so saving again asks for nothing twice; a save
// that adds one says it was asked for, not that it was saved. A whole role is turned off from the list, after saying who
// is left with nothing, and turning it back on is asked for; the Super Administrator's role has no switch to use.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const roleId = '0192a000-0000-7000-8000-00000000c001';
const superAdminId = '0192a000-0000-7000-8000-00000000c002';

const LISTED: RoleListItem[] = [
  {
    id: roleId,
    name: 'Theatre desk',
    description: 'Books theatres',
    isSystem: false,
    permissionCount: 1,
    userCount: 3,
    isActive: true,
    soleRoleHolders: 2,
    canTurnOff: true,
    version: 7,
  },
  {
    id: superAdminId,
    name: 'SuperAdmin',
    description: 'Full access to everything, including security settings',
    isSystem: true,
    permissionCount: 1,
    userCount: 1,
    isActive: true,
    soleRoleHolders: 1,
    canTurnOff: false,
    version: 2,
  },
];

const GROUPS: PermissionGroup[] = [
  {
    group: 'theatre',
    permissions: [
      { name: 'ot.read', description: 'See theatre bookings' },
      { name: 'ot.book', description: 'Book theatres' },
      { name: 'ot.cancel', description: 'Cancel theatre bookings' },
    ],
  },
];

const ROLE: RoleDetail = {
  id: roleId,
  name: 'Theatre desk',
  description: 'Books theatres',
  isSystem: false,
  permissions: ['ot.read'],
  version: 7,
  isActive: true,
  pendingGrant: {
    approvalId: '0192a000-0000-7000-8000-00000000c901',
    permissions: ['ot.book'],
    requestedBy: 'Hospital Admin',
    requestedAt: '2026-11-01T05:00:00Z',
  },
};

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  server = fakeServer({
    'GET /api/v1/admin/roles': LISTED,
    [`GET /api/v1/admin/roles/${roleId}`]: ROLE,
    'GET /api/v1/admin/permissions': GROUPS,
    [`PUT /api/v1/admin/roles/${roleId}`]: {
      id: '0192a000-0000-7000-8000-00000000c902',
      kind: 'identity.role_grant',
      summary: 'Permissions for the role Theatre desk: ot.cancel',
    },
  });
});

afterEach(() => {
  expect(server.unanswered).toEqual([]);
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('editing a role', SCREEN_TESTS, () => {
  it('shows the permissions waiting on a role as given, and says a save that adds one was asked for', async () => {
    // The role is read after its drawer opens, as on a slow network: the drawer opens on its spinner first.
    let release = () => {};
    const held = new Promise<void>((resolve) => (release = resolve));
    const fake = globalThis.fetch;
    vi.stubGlobal('fetch', async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
      if (new URL(url, 'https://hc360.test').pathname === `/api/v1/admin/roles/${roleId}`) {
        await held;
      }
      return fake(input, init);
    });

    const user = userEvent.setup();
    openScreen('/admin/roles');
    await user.click(await screen.findByText('Theatre desk', {}, SLOW));
    await screen.findByRole('dialog', { name: 'Role' }, SLOW);
    release();

    // Read, the role is a dialog named after it, which assistive technology can reach.
    const drawer = await screen.findByRole('dialog', { name: /Theatre desk/ }, SLOW);

    // Booking theatres waits: the notice says so, with who asked, and its box is ticked so nothing is asked twice.
    expect(await within(drawer).findByText('Waiting for approval')).toBeInTheDocument();
    expect(within(drawer).getByText(/Permissions ot\.book, asked for by Hospital Admin/)).toBeInTheDocument();
    await user.click(within(drawer).getByRole('button', { name: /Theatre/ }));
    expect(within(drawer).getByRole('checkbox', { name: /ot\.read/ })).toBeChecked();
    expect(within(drawer).getByRole('checkbox', { name: /ot\.book/ })).toBeChecked();
    await expectNoAxeViolations();

    await user.click(within(drawer).getByRole('checkbox', { name: /ot\.cancel/ }));
    await user.type(
      within(drawer).getByRole('textbox', { name: /Reason, for the approver/ }),
      'Cancels for the surgeons.',
    );
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    const put = server.requests.find((request) => request.method === 'PUT');
    expect(put?.body).toMatchObject({
      permissions: ['ot.read', 'ot.book', 'ot.cancel'],
      version: 7,
      reason: 'Cancels for the surgeons.',
    });
  });
});

describe('turning a whole role off and on', SCREEN_TESTS, () => {
  it('turns a role off after saying who is left with nothing, against the version it was shown at', async () => {
    server = fakeServer({
      'GET /api/v1/admin/roles': LISTED,
      [`PUT /api/v1/admin/roles/${roleId}/active`]: null,
    });
    const user = userEvent.setup();
    openScreen('/admin/roles');

    const theatre = await screen.findByRole('switch', { name: 'Theatre desk in use' }, SLOW);
    expect(theatre).toBeChecked();
    expect(screen.getByRole('switch', { name: 'SuperAdmin in use' })).toBeDisabled();

    await user.click(theatre);
    const dialog = await screen.findByRole('alertdialog', { name: 'Turn off the role Theatre desk?' });
    expect(
      within(dialog).getByText(
        /3 people hold it and will lose what it gives at once\. For 2 of them it is the only role in use/,
      ),
    ).toBeInTheDocument();
    expect(within(dialog).getByText(/two-step verification, still applies/)).toBeInTheDocument();
    await expectNoAxeViolations();

    await user.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'Theatre on paper for the audit week.');
    await user.click(within(dialog).getByRole('button', { name: 'Turn off' }));
    expect(await screen.findByText('Role turned off', {}, SLOW)).toBeInTheDocument();
    const put = server.requests.find((request) => request.method === 'PUT');
    expect(put?.path).toBe(`/api/v1/admin/roles/${roleId}/active`);
    expect(put?.body).toEqual({ active: false, version: 7, reason: 'Theatre on paper for the audit week.' });
    // The switch is in the role's row, but using it does not open the role.
    expect(screen.queryByRole('dialog', { name: /Theatre desk/ })).not.toBeInTheDocument();
  });

  it('asks for a role to be turned back on, and says it was asked for rather than done', async () => {
    server = fakeServer({
      'GET /api/v1/admin/roles': [{ ...LISTED[0], isActive: false }, LISTED[1]],
      [`PUT /api/v1/admin/roles/${roleId}/active`]: json(
        {
          id: '0192a000-0000-7000-8000-00000000c903',
          kind: 'identity.role_activation',
          summary: 'Turn the role Theatre desk back on',
        },
        202,
      ),
    });
    const user = userEvent.setup();
    openScreen('/admin/roles');

    const theatre = await screen.findByRole('switch', { name: 'Theatre desk in use' }, SLOW);
    expect(theatre).not.toBeChecked();
    await user.click(theatre);
    const dialog = await screen.findByRole('alertdialog', { name: 'Turn the role Theatre desk back on?' });
    expect(within(dialog).getByText(/Another administrator who may approve grants decides it/)).toBeInTheDocument();

    await user.type(
      within(dialog).getByRole('textbox', { name: /Reason, for the approver/ }),
      'Theatre back on HC360.',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Ask to turn on' }));
    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    expect(server.requests.find((request) => request.method === 'PUT')?.body).toEqual({
      active: true,
      version: 7,
      reason: 'Theatre back on HC360.',
    });
  });
});
