import { describe, expect, it } from 'vitest';
import type { IntegrationHealthSummary, ServerNodeHealth } from './api';
import {
  expiryTone,
  integrationsSummary,
  integrationTone,
  lowDiskSpace,
  runningFor,
  runsJobs,
  serverRole,
  serversSummary,
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

describe('expiryTone', () => {
  const now = new Date('2026-10-16T09:00:00Z');

  it('is red once expired and amber within 30 days', () => {
    expect(expiryTone('2026-10-16T08:59:59Z', now)).toBe('bad');
    expect(expiryTone('2026-11-14T09:00:00Z', now)).toBe('warn');
  });

  it('says nothing further out, or without a date', () => {
    expect(expiryTone('2026-11-16T09:00:00Z', now)).toBeUndefined();
    expect(expiryTone(null, now)).toBeUndefined();
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
