import { describe, expect, it } from 'vitest';
import type { DeviceDto } from './api';
import { compareVersions, desktopUpdateSummary, desktopVersionStatus } from './desktopVersions';

function device(patch: Partial<DeviceDto>): DeviceDto {
  return {
    id: crypto.randomUUID(),
    name: 'OPD Reception 2',
    machineName: 'OPD-PC-2',
    kind: 'Workstation',
    osVersion: 'Windows 11',
    appVersion: '1.2.0',
    enrolledAt: '2026-10-01T05:00:00Z',
    lastSeenAt: '2026-10-12T05:00:00Z',
    lastIpAddress: '192.168.1.40',
    lastWifiSsid: 'NH-Clinical',
    lastWifiSignal: 80,
    isRevoked: false,
    facilityId: null,
    departmentId: null,
    location: null,
    version: 1,
    ...patch,
  };
}

describe('compareVersions', () => {
  it('compares release numbers as numbers, as the server does', () => {
    expect(compareVersions('1.2.0', '1.10.0')).toBe(-1);
    expect(compareVersions('1.10.0', '1.9.9')).toBe(1);
    expect(compareVersions('1.2', '1.2.0')).toBe(0);
    expect(compareVersions('1.2.0+abc123', '1.2.0')).toBe(0);
    expect(compareVersions('1.2.0.1', '1.2.0')).toBe(1);
  });

  it('puts a pre-release before its release', () => {
    expect(compareVersions('1.3.0-rc.1', '1.3.0')).toBe(-1);
    expect(compareVersions('1.3.0-rc.10', '1.3.0-rc.9')).toBe(1);
    expect(compareVersions('1.3.0-beta', '1.3.0-rc.1')).toBe(-1);
  });

  it('cannot compare what is not a version', () => {
    expect(compareVersions(null, '1.2.0')).toBeNull();
    expect(compareVersions('dev', '1.2.0')).toBeNull();
    expect(compareVersions('1', '1.2.0')).toBeNull();
  });
});

describe('desktopVersionStatus', () => {
  it('marks a workstation behind the offered version as out of date', () => {
    expect(desktopVersionStatus(device({ appVersion: '1.1.0' }), '1.2.0')).toBe('outdated');
    expect(desktopVersionStatus(device({ kind: 'Kiosk', appVersion: '1.1.9' }), '1.2.0')).toBe('outdated');
    expect(desktopVersionStatus(device({ appVersion: '1.2.0' }), '1.2.0')).toBe('current');
    expect(desktopVersionStatus(device({ appVersion: '1.3.0' }), '1.2.0')).toBe('current');
    expect(desktopVersionStatus(device({ appVersion: null }), '1.2.0')).toBe('unknown');
  });

  it('compares nothing without an offered version, for lab agents, or for revoked devices', () => {
    expect(desktopVersionStatus(device({ appVersion: '1.1.0' }), null)).toBe('none');
    expect(desktopVersionStatus(device({ kind: 'LabAgent', appVersion: '1.0.0' }), '1.2.0')).toBe('none');
    expect(desktopVersionStatus(device({ appVersion: '1.0.0', isRevoked: true }), '1.2.0')).toBe('none');
  });
});

describe('desktopUpdateSummary', () => {
  const offered = { offeredVersion: '1.2.0', releasedAt: '2026-10-12T05:30:00Z', problem: null };

  it('counts the devices still behind', () => {
    expect(
      desktopUpdateSummary(
        [device({ appVersion: '1.1.0' }), device({ appVersion: '1.0.0' }), device({ appVersion: '1.2.0' })],
        offered,
      ),
    ).toEqual({
      intent: 'warning',
      text: 'Workstations are offered HC360 Desktop 1.2.0. 2 devices run an older version: each installs the update the next time HC360 Desktop is closed or the computer starts.',
    });
    expect(desktopUpdateSummary([device({ appVersion: '1.1.0' })], offered)?.text).toContain(
      '1 device runs an older version',
    );
    expect(
      desktopUpdateSummary(
        [device({ appVersion: '1.2.0' }), device({ kind: 'LabAgent', appVersion: '1.0.0' })],
        offered,
      ),
    ).toEqual({
      intent: 'info',
      text: 'Workstations are offered HC360 Desktop 1.2.0, and every one runs it.',
    });
  });

  it('says why no update is offered, and nothing when none was put on the server', () => {
    expect(
      desktopUpdateSummary([device({})], {
        offeredVersion: null,
        releasedAt: null,
        problem: 'The package is not signed: HC360 installs only packages signed by HyFib.',
      }),
    ).toEqual({
      intent: 'warning',
      text: 'Workstations are offered no update of HC360 Desktop: The package is not signed: HC360 installs only packages signed by HyFib.',
    });
    expect(desktopUpdateSummary([device({})], { offeredVersion: null, releasedAt: null, problem: null })).toBeNull();
    expect(desktopUpdateSummary(undefined, undefined)).toBeNull();
  });
});
