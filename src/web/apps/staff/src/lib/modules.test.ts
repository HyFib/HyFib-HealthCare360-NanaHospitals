import type { AuthUser } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import { featureSwitches, isFeatureOn } from './featureSwitches';
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

describe('visibleModules feature switches', () => {
  const Widget = () => null;
  const virtualCare: ModuleManifest = {
    id: 'virtualcare',
    title: 'Virtual care',
    section: 'virtual',
    icon: Icon,
    links: [
      {
        label: 'Video consultations',
        to: '/telehealth',
        permission: 'telehealth.sessions.read',
        feature: 'telehealth',
      },
      { label: 'Remote monitoring', to: '/rpm', permission: 'rpm.read' },
    ],
    quickActions: [{ label: 'Video consultations', to: '/telehealth', icon: Icon, feature: 'telehealth' }],
    widgets: [
      { id: 'telehealth-today', permission: 'telehealth.sessions.read', feature: 'telehealth', component: Widget },
      { id: 'rpm-alerts', permission: 'rpm.read', component: Widget },
    ],
  };
  const videoOnly: ModuleManifest = {
    id: 'video',
    title: 'Video',
    section: 'virtual',
    icon: Icon,
    links: [{ label: 'Video consultations', to: '/telehealth', feature: 'telehealth' }],
  };

  it('hides the links, quick actions and widgets of a feature that is switched off', () => {
    const [care] = visibleModules(user(['*']), [virtualCare], { telehealth: false });
    expect(care!.links.map((l) => l.to)).toEqual(['/rpm']);
    expect(care!.quickActions).toEqual([]);
    expect(care!.widgets?.map((w) => w.id)).toEqual(['rpm-alerts']);
  });

  it('shows them while the feature is on, or before the switches are known', () => {
    for (const features of [{ telehealth: true }, {}, undefined]) {
      const [care] = visibleModules(user(['*']), [virtualCare], features);
      expect(care!.links).toHaveLength(2);
      expect(care!.quickActions).toHaveLength(1);
      expect(care!.widgets).toHaveLength(2);
    }
  });

  it('drops a module whose every link is switched off', () => {
    expect(visibleModules(user(['*']), [videoOnly], { telehealth: false })).toEqual([]);
    expect(visibleModules(user(['*']), [videoOnly], { patientPortal: false })).toHaveLength(1);
  });

  it('still needs the permission when the feature is on', () => {
    const [care] = visibleModules(user(['rpm.read']), [virtualCare], { telehealth: true });
    expect(care!.links.map((l) => l.to)).toEqual(['/rpm']);
    expect(care!.widgets?.map((w) => w.id)).toEqual(['rpm-alerts']);
  });
});

describe('featureSwitches', () => {
  it('reads true and false from the public settings and nothing else', () => {
    expect(
      featureSwitches({ 'features.telehealth': false, 'features.patientPortal': true, 'ui.idleLockMinutes': 10 }),
    ).toEqual({ telehealth: false, patientPortal: true });
    expect(featureSwitches({ 'features.telehealth': 'false', 'features.patientPortal': null })).toEqual({});
    expect(featureSwitches(undefined)).toEqual({});
  });

  it('counts a feature as on unless it is known to be off', () => {
    expect(isFeatureOn({ telehealth: false }, 'telehealth')).toBe(false);
    expect(isFeatureOn({ telehealth: true }, 'telehealth')).toBe(true);
    expect(isFeatureOn({}, 'telehealth')).toBe(true);
    expect(isFeatureOn(undefined, 'patientPortal')).toBe(true);
    expect(isFeatureOn({ telehealth: false }, undefined)).toBe(true);
  });
});
