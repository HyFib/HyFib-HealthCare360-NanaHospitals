import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeServer, openScreen, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
import type { PermissionGroup, RoleDetail, RoleListItem } from './api';

// Editing a role on the roles screen, whole with the shell, against a fake server (#155): permissions added to a role wait
// for a second administrator. Those waiting show as given, with a notice, so saving again asks for nothing twice; a save
// that adds one says it was asked for, not that it was saved.

vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  RealtimeProvider: ({ children }: { children: ReactNode }) => children,
}));

const roleId = '0192a000-0000-7000-8000-00000000c001';

const LISTED: RoleListItem[] = [
  {
    id: roleId,
    name: 'Theatre desk',
    description: 'Books theatres',
    isSystem: false,
    permissionCount: 1,
    userCount: 3,
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
