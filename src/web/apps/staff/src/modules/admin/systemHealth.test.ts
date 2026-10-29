import { describe, expect, it } from 'vitest';
import type { IntegrationHealthSummary, ServerNodeHealth, SystemHealthReport } from './api';
import {
  clockText,
  expiryTone,
  HOSPITAL_CA,
  SERVER_CERTIFICATE,
  integrationsSummary,
  integrationTone,
  lowDiskSpace,
  runningFor,
  runsJobs,
  serverRole,
  serversSummary,
  wifiAdapterMissing,
} from './systemHealth';

function row(name: string, health: string, enabled = true, configured = true): IntegrationHealthSummary {
  return {
    name,
    displayName: name.toUpperCase(),
    enabled,
    configured,
    health,
    lastSuccessAt: null,
    lastFailureAt: null,
    failuresLast24Hours: 0,
  };
}

describe('clockText', () => {
  const check = (offsetSeconds: number | null) => ({
    checkedAt: '2026-10-24T10:00:00Z',
    server: offsetSeconds === null ? null : 'time.nplindia.org',
    offsetSeconds,
    problem: offsetSeconds === null ? 'time.nplindia.org: timed out.' : null,
  });

  it('says how far off the clock is, and from which time server, and flags more than a second', () => {
    expect(clockText(undefined)).toBeNull();
    expect(clockText(check(0.0004))).toEqual({ text: 'Exact · time.nplindia.org', bad: false });
    expect(clockText(check(0.25))).toEqual({ text: '0.250 s behind · time.nplindia.org', bad: false });
    expect(clockText(check(-3.5))).toEqual({ text: '3.500 s ahead · time.nplindia.org', bad: true });
    expect(clockText(check(null))).toEqual({ text: 'Not checked: no national time server answered', bad: true });
  });
});

describe('expiryTone', () => {
  const now = new Date('2026-10-16T09:00:00Z');
  const inDays = (days: number) => new Date(now.getTime() + days * 24 * 60 * 60 * 1000).toISOString();

  it('marks the server certificate amber within 30 days and red within 14, as the server warns', () => {
    expect(expiryTone(inDays(31), SERVER_CERTIFICATE, now)).toBeUndefined();
    expect(expiryTone(inDays(29), SERVER_CERTIFICATE, now)).toBe('warn');
    expect(expiryTone(inDays(13), SERVER_CERTIFICATE, now)).toBe('bad');
    expect(expiryTone(inDays(-1), SERVER_CERTIFICATE, now)).toBe('bad');
  });

  it('marks the hospital CA amber a year ahead and red in its last 90 days', () => {
    expect(expiryTone(inDays(366), HOSPITAL_CA, now)).toBeUndefined();
    expect(expiryTone(inDays(364), HOSPITAL_CA, now)).toBe('warn');
    expect(expiryTone(inDays(91), HOSPITAL_CA, now)).toBe('warn');
    expect(expiryTone(inDays(89), HOSPITAL_CA, now)).toBe('bad');
  });

  it('says nothing without a date', () => {
    expect(expiryTone(null, SERVER_CERTIFICATE, now)).toBeUndefined();
  });
});

describe('integrationsSummary', () => {
  it('counts failing, troubled, working and switched-off integrations, leaving out those not set up', () => {
    expect(
      integrationsSummary([
        row('sms', 'failing'),
        row('tally', 'degraded'),
        row('email', 'healthy'),
        row('icd', 'idle'),
        row('pacs', 'disabled', false),
        row('pos', 'not_configured', false, false),
      ]),
    ).toEqual({ text: '1 failing, 1 with failures in 24 h, 2 working, 1 turned off', tone: 'bad' });
  });

  it('is amber with failures today but nothing failing now, and green when all work', () => {
    expect(integrationsSummary([row('sms', 'degraded'), row('email', 'healthy')]).tone).toBe('warn');
    expect(integrationsSummary([row('email', 'healthy'), row('labels', 'idle')])).toEqual({
      text: '2 working',
      tone: 'ok',
    });
  });

  it('has no tone when nothing is on', () => {
    expect(integrationsSummary([row('pos', 'not_configured', false, false)])).toEqual({
      text: 'None set up',
      tone: undefined,
    });
    expect(integrationsSummary([row('pacs', 'disabled', false)])).toEqual({ text: '1 turned off', tone: undefined });
  });
});

describe('integrationTone', () => {
  it('colours only integrations that are switched on', () => {
    expect(integrationTone(row('sms', 'failing'))).toBe('bad');
    expect(integrationTone(row('sms', 'degraded'))).toBe('warn');
    expect(integrationTone(row('sms', 'healthy'))).toBe('ok');
    expect(integrationTone(row('sms', 'idle'))).toBeUndefined();
    expect(integrationTone(row('sms', 'failing', false))).toBeUndefined();
  });
});

function server(machineName: string, role: string, reporting = true): ServerNodeHealth {
  return {
    server: {
      machineName,
      osDescription: 'Microsoft Windows 10.0.20348',
      version: '1.4.0',
      environment: 'Production',
      role,
      startedAt: '2026-10-20T04:00:00Z',
      uptimeHours: 26,
      processorCount: 8,
      workingSetBytes: 1,
      framework: '.NET 10.0.12',
    },
    network: {
      wifiSupported: false,
      wifi: null,
      connectedSince: null,
      lastDisconnectAt: null,
      recentEvents: [],
      ipAddresses: [],
      httpsPort: 7360,
      portalHttpsPort: 7361,
      mdnsEnabled: false,
    },
    storage: { dataDirectory: 'C:\\data', freeBytes: 1, totalBytes: 2, encryption: null },
    certificate: { caFingerprint: null, serverExpiresAt: null, subjectAlternativeNames: [], caExpiresAt: null },
    power: { available: false, onBattery: null, chargePercent: null, source: null },
    warnings: [],
    reportedAt: '2026-10-21T05:59:00Z',
    reporting,
  };
}

describe('serversSummary', () => {
  it('is green while every server reports, the one that runs the jobs among them', () => {
    expect(serversSummary([server('JOBS', 'worker'), server('API1', 'api'), server('API2', 'api')])).toEqual({
      text: 'All 3 reporting',
      tone: 'ok',
    });
  });

  it('names the servers that no longer report', () => {
    expect(
      serversSummary([server('JOBS', 'worker', false), server('API1', 'api'), server('API2', 'api', false)]),
    ).toEqual({ text: 'JOBS, API2 not reporting', tone: 'bad' });
  });

  it('says so when none of them runs the background jobs', () => {
    expect(serversSummary([server('API1', 'api'), server('API2', 'api')])).toEqual({
      text: 'All 2 reporting; none runs the background jobs',
      tone: 'bad',
    });
  });

  it('names the servers that run the background jobs when more than one does, as only one should', () => {
    // An API node whose settings miss "Role": "api" runs them as "all".
    expect(serversSummary([server('JOBS', 'worker'), server('API1', 'all'), server('API2', 'api')])).toEqual({
      text: 'All 3 reporting; JOBS, API1 each run the background jobs',
      tone: 'warn',
    });
    // One that no longer reports runs nothing.
    const stopped = [server('JOBS', 'worker', false), server('JOBS2', 'worker'), server('API1', 'api')];
    expect(serversSummary(stopped)).toEqual({ text: 'JOBS not reporting', tone: 'bad' });
  });
});

describe('servers', () => {
  it('names what each server is for', () => {
    expect(serverRole('api')).toBe('API node');
    expect(serverRole('worker')).toBe('Runs the background jobs');
    expect(serverRole('all')).toBe('API and background jobs');
    expect([runsJobs('api'), runsJobs('worker'), runsJobs('All')]).toEqual([false, true, true]);
  });

  it('says how long a server has run, and when its data drive is nearly full', () => {
    expect(runningFor(47.6)).toBe('48 hours');
    expect(runningFor(72)).toBe('3 days');
    expect(lowDiskSpace({ dataDirectory: 'D:', freeBytes: 9, totalBytes: 100, encryption: null })).toBe(true);
    expect(lowDiskSpace({ dataDirectory: 'D:', freeBytes: 10, totalBytes: 100, encryption: null })).toBe(false);
    expect(lowDiskSpace({ dataDirectory: 'D:', freeBytes: null, totalBytes: 100, encryption: null })).toBe(false);
  });
});

describe('wifiAdapterMissing', () => {
  const warning = (server: string | null) => ({
    code: 'wifi.adapter_missing',
    severity: 'critical',
    message: 'Server Node lost its WiFi adapter.',
    server,
  });
  const report = (warnings: ReturnType<typeof warning>[]) => ({
    server: { machineName: 'HC360-SRV' } as SystemHealthReport['server'],
    warnings,
  });

  it("is this server's WiFi adapter that went missing, not another server's", () => {
    // A single Server Node names no server in its warnings; with several, each warning names its own.
    expect(wifiAdapterMissing(report([warning(null)]))).toBe(true);
    expect(wifiAdapterMissing(report([warning('HC360-SRV')]))).toBe(true);
    expect(wifiAdapterMissing(report([warning('HC360-JOBS')]))).toBe(false);
    expect(wifiAdapterMissing(report([]))).toBe(false);
  });
});
