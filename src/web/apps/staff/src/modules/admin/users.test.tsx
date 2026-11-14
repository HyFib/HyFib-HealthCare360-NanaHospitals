import type * as Core from '@hc360/core';
import { expectNoAxeViolations } from '@hc360/ui/testing';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FACILITY_ID, fakeServer, openScreen, paged, SCREEN_TESTS, SLOW, stubBrowserApis } from '@/test/appScreen';
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

const ANNEXE_ID = id('502');

const FACILITIES = [
  { id: FACILITY_ID, code: 'NHP', name: 'Nana Hospitals, Pune', isActive: true },
  { id: ANNEXE_ID, code: 'NHA', name: 'Nana Hospitals, Annexe', isActive: true },
];

let server: ReturnType<typeof fakeServer>;

beforeEach(() => {
  stubBrowserApis();
  server = fakeServer({
    'GET /api/v1/admin/users': paged([]),
    'GET /api/v1/admin/roles': ROLES,
    'GET /api/v1/masters/lookups/practitioners': [],
    'GET /api/v1/masters/facilities': FACILITIES,
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

  it('starts a new user in this facility, and gives all facilities only when asked, explicitly', async () => {
    const user = userEvent.setup();
    openScreen('/admin/users');
    await user.click(await screen.findByRole('button', { name: 'Add user' }, SLOW));
    const drawer = await screen.findByRole('dialog', { name: /Add user/ });
    const all = within(drawer).getByRole('switch', { name: 'All facilities, those added later included' });
    expect(all).not.toBeChecked();
    const worksIn = await within(drawer).findByRole('group', { name: 'Works in' }, SLOW);
    expect(await within(worksIn).findByRole('checkbox', { name: 'Nana Hospitals, Pune' })).toBeChecked();
    expect(within(worksIn).getByRole('checkbox', { name: 'Nana Hospitals, Annexe' })).not.toBeChecked();

    // All facilities is a choice of its own: the list goes, and nothing is sent for it.
    await user.click(all);
    expect(within(drawer).queryByRole('group', { name: 'Works in' })).not.toBeInTheDocument();
    await user.type(within(drawer).getByRole('textbox', { name: /User name/ }), 'priya.s');
    await user.type(within(drawer).getByRole('textbox', { name: /Full name/ }), 'Priya Sharma');
    await user.click(within(drawer).getByRole('checkbox', { name: 'Nurse' }));
    await user.click(within(drawer).getByRole('button', { name: 'Create user' }));

    await waitFor(() => expect(created()).toHaveLength(1));
    expect(created()[0]?.body).toMatchObject({ allFacilities: true, facilityIds: [] });
  });
});

// Access is given by two people: what an edit adds waits for another administrator who may approve grants.
describe('access that waits for a second administrator', SCREEN_TESTS, () => {
  const userId = id('200');
  const waiting = {
    approvalId: id('901'),
    roleIds: [id('004')],
    allFacilities: false,
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
      'GET /api/v1/masters/facilities': FACILITIES,
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
        allFacilities: false,
        facilityIds: [FACILITY_ID],
        version: 3,
        pendingGrant: waiting,
      },
      'GET /api/v1/admin/roles': ROLES,
      'GET /api/v1/masters/lookups/practitioners': [],
      'GET /api/v1/masters/facilities': FACILITIES,
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
    expect(put?.body).toMatchObject({
      roleIds: [id('004'), id('002')],
      allFacilities: false,
      facilityIds: [FACILITY_ID],
      version: 3,
    });
  });

  it('shows all facilities explicitly, waiting or given, and an empty list as no facility', async () => {
    const account = {
      id: userId,
      userName: 'priya.s',
      displayName: 'Priya Sharma',
      email: null,
      phone: null,
      isActive: true,
      mfaEnabled: false,
      mustChangePassword: false,
      lastLoginAt: null,
      lockoutEndsAt: null,
      practitionerId: null,
      defaultFacilityId: null,
      roleIds: [id('004')],
      allFacilities: false,
      facilityIds: [],
      version: 4,
      pendingGrant: { ...waiting, roleIds: [], allFacilities: true },
    };
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
          roles: ['Nurse'],
        },
      ]),
      [`GET /api/v1/admin/users/${userId}`]: account,
      'GET /api/v1/admin/roles': ROLES,
      'GET /api/v1/masters/lookups/practitioners': [],
      'GET /api/v1/masters/facilities': FACILITIES,
      [`PUT /api/v1/admin/users/${userId}`]: null,
    });
    const user = userEvent.setup();
    openScreen('/admin/users');
    await user.click(await screen.findByText('Priya Sharma', {}, SLOW));
    const drawer = await screen.findByRole('dialog', { name: /Priya Sharma/ }, SLOW);

    // All facilities waits for approval: the notice names it, and the switch shows it as given, so it is not asked twice.
    expect(await within(drawer).findByText(/All facilities, asked for by Hospital Admin/)).toBeInTheDocument();
    const all = within(drawer).getByRole('switch', { name: 'All facilities, those added later included' });
    expect(all).toBeChecked();

    // Turned off, the list is explicit: with none ticked, the user works in no facility.
    await user.click(all);
    const worksIn = await within(drawer).findByRole('group', { name: 'Works in' });
    expect(worksIn).toHaveAccessibleDescription('No facility: they cannot sign in until given one.');
    await user.click(await within(worksIn).findByRole('checkbox', { name: 'Nana Hospitals, Annexe' }, SLOW));
    await user.click(within(drawer).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(server.requests.filter((r) => r.method === 'PUT')).toHaveLength(1));
    expect(server.requests.find((r) => r.method === 'PUT')?.body).toMatchObject({
      allFacilities: false,
      facilityIds: [ANNEXE_ID],
    });
  });
});

// Nobody takes over a colleague's account alone: a reset's temporary password goes to the account's own mobile number
// and e-mail address, and the screen says where, never what; a two-step verification reset waits for another
// administrator, and the screen says it was asked for.
describe("resetting a colleague's sign-in", SCREEN_TESTS, () => {
  const nurseId = id('300');
  const NURSE = {
    id: nurseId,
    userName: 'kavitha.r',
    displayName: 'Kavitha Raman',
    email: null,
    phone: '+919840012345',
    isActive: true,
    isLocked: false,
    mfaEnabled: true,
    lastLoginAt: null,
    roles: ['Nurse'],
  };

  const serve = (action: 'reset-password' | 'reset-mfa', answer: unknown) => {
    server = fakeServer({
      'GET /api/v1/admin/users': paged([NURSE]),
      'GET /api/v1/admin/roles': ROLES,
      [`POST /api/v1/admin/users/${nurseId}/${action}`]: answer,
    });
  };

  const choose = async (user: ReturnType<typeof userEvent.setup>, item: string) => {
    openScreen('/admin/users');
    await user.click(await screen.findByRole('button', { name: 'Actions for Kavitha Raman' }, SLOW));
    await user.click(await screen.findByRole('menuitem', { name: item }));
    const confirm = await screen.findByRole('alertdialog');
    await user.click(within(confirm).getByRole('button', { name: 'Continue' }));
  };

  it('says where the temporary password was sent, and never shows it to the administrator', async () => {
    serve('reset-password', {
      userId: nurseId,
      sentTo: ['mobile +91******2345'],
      expiresAt: '2026-11-11T06:30:00Z',
      temporaryPassword: null,
    });
    const user = userEvent.setup();
    await choose(user, 'Reset password');

    const sent = await screen.findByRole('dialog', { name: 'Password reset for Kavitha Raman' }, SLOW);
    expect(within(sent).getByText(/A temporary password was sent to their mobile \+91\*{6}2345/)).toBeInTheDocument();
    expect(within(sent).getByText('Not shown to you')).toBeInTheDocument();
    expect(screen.queryByRole('dialog', { name: /Temporary password for/ })).not.toBeInTheDocument();
    expect(within(sent).queryByRole('button', { name: 'Copy' })).not.toBeInTheDocument();
    await expectNoAxeViolations();
    expect(server.requests.filter((r) => r.method === 'POST')).toHaveLength(1);
  });

  it("asks for the holder's own mobile number when the account has none, and says the reset waits for another administrator", async () => {
    const porter = { ...NURSE, id: id('301'), userName: 'murugan.p', displayName: 'Murugan P', phone: null };
    server = fakeServer({
      'GET /api/v1/admin/users': paged([porter]),
      'GET /api/v1/admin/roles': ROLES,
      [`POST /api/v1/admin/users/${porter.id}/reset-password`]: {
        id: id('904'),
        kind: 'identity.password_reset',
        summary: 'Temporary password for Murugan P (murugan.p), sent to the mobile number given for them',
      },
    });
    const user = userEvent.setup();
    openScreen('/admin/users');
    await user.click(await screen.findByRole('button', { name: 'Actions for Murugan P' }, SLOW));
    await user.click(await screen.findByRole('menuitem', { name: 'Reset password' }));

    const dialog = await screen.findByRole('alertdialog', { name: 'Reset password?' });
    expect(within(dialog).getByText(/has no mobile number or e-mail address/)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Continue' })).toBeDisabled();
    await user.type(within(dialog).getByRole('textbox', { name: 'Their mobile number' }), '12');
    expect(within(dialog).getByText('Enter a valid phone number.')).toBeInTheDocument();
    await user.clear(within(dialog).getByRole('textbox', { name: 'Their mobile number' }));
    await user.type(within(dialog).getByRole('textbox', { name: 'Their mobile number' }), '98400 54321');
    await user.click(within(dialog).getByRole('button', { name: 'Continue' }));

    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    expect(screen.getAllByText(/nobody is shown it/).length).toBeGreaterThan(0);
    const posts = server.requests.filter((r) => r.method === 'POST');
    expect(posts).toHaveLength(1);
    expect(posts[0]?.body).toEqual({ phone: '98400 54321', email: null });
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
  });

  it('says a two-step verification reset was asked for, and that nothing changes until it is approved', async () => {
    serve('reset-mfa', {
      id: id('903'),
      kind: 'identity.mfa_reset',
      summary: 'Two-step verification reset for Kavitha Raman (kavitha.r)',
    });
    const user = userEvent.setup();
    await choose(user, 'Reset two-step verification');

    expect(await screen.findByText('Asked for approval', {}, SLOW)).toBeInTheDocument();
    expect(screen.getAllByText(/nothing changes until then/).length).toBeGreaterThan(0);
    expect(screen.queryByText('Two-step verification reset')).not.toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument());
    // The closed confirmation's portal can keep the rest of the page aria-hidden for a moment after the dialog is
    // gone; axe run then would see a hidden page that is being torn down (aria-hidden-focus, "axe encountered an error").
    await waitFor(() => expect(document.querySelector('body > div[aria-hidden="true"]')).toBeNull());
    await expectNoAxeViolations();
  });
});
