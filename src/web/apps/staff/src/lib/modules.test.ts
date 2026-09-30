import type { AuthUser } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import { matchLink, visibleModules, type ModuleManifest } from './modules';
import { safeRedirect } from './redirect';

const Icon = (() => null) as unknown as ModuleManifest['icon'];

const manifests: ModuleManifest[] = [
  {
    id: 'patients',
    title: 'Patients',
    section: 'patients',
    icon: Icon,
    links: [
      { label: 'Find patient', to: '/patients', permission: 'patients.read' },
      { label: 'Register patient', to: '/patients/register', permission: 'patients.register' },
    ],
  },
  {
    id: 'admin',
    title: 'Administration',
    section: 'admin',
    icon: Icon,
    links: [{ label: 'Users', to: '/admin/users', permission: 'admin.users.read' }],
    quickActions: [
      { label: 'Add user', to: '/admin/users', permission: 'admin.users.manage', icon: Icon },
      { label: 'Test an integration', to: '/admin/integrations', permission: 'admin.integrations.manage', icon: Icon },
    ],
  },
];

function user(permissions: string[]): AuthUser {
  return {
    id: 'u1',
    userName: 'test',
    displayName: 'Test User',
    type: 'staff',
    facilityId: null,
    facilityName: null,
    practitionerId: null,
    patientId: null,
    roles: [],
    permissions,
    facilities: [],
    mfaEnabled: false,
    hasPin: false,
    mustChangePassword: false,
    restriction: null,
  };
}

describe('visibleModules', () => {
  it('hides links the user may not open and drops empty modules', () => {
    const visible = visibleModules(user(['patients.read']), manifests);
    expect(visible.map((m) => m.id)).toEqual(['patients']);
    expect(visible[0]!.links.map((l) => l.to)).toEqual(['/patients']);
  });

  it('shows everything for the super administrator wildcard', () => {
    const visible = visibleModules(user(['*']), manifests);
    expect(visible.flatMap((m) => m.links)).toHaveLength(3);
  });
});

describe('matchLink', () => {
  it('prefers the most specific link', () => {
    expect(matchLink('/patients/register', manifests)?.link.label).toBe('Register patient');
    expect(matchLink('/patients/1234', manifests)?.link.label).toBe('Find patient');
    expect(matchLink('/unknown', manifests)).toBeNull();
  });
});

describe('safeRedirect', () => {
  it.each([
    ['/patients?q=ramesh', '/patients?q=ramesh'],
    [undefined, '/'],
    ['https://evil.example', '/'],
    ['//evil.example', '/'],
    ['/\\evil.example', '/'],
    ['/login?redirect=/x', '/'],
    ['/loginhelp', '/loginhelp'],
  ])('%s → %s', (input, expected) => {
    expect(safeRedirect(input)).toBe(expected);
  });
});

describe('visibleModules quick actions', () => {
  it('hides quick actions the user may not open, like links', () => {
    const [admin] = visibleModules(user(['admin.users.read', 'admin.users.manage']), manifests).filter(
      (m) => m.id === 'admin',
    );
    expect(admin?.quickActions?.map((a) => a.label)).toEqual(['Add user']);
  });
});
