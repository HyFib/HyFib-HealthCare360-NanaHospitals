import { describe, expect, it } from 'vitest';
import type {
  IntegrationHealthSummary,
  LostCable,
  NetworkLink,
  ServerNodeHealth,
  SystemHealthReport,
  WifiConnectionInfo,
} from './api';
import {
  clockText,
  expiryTone,
  HOSPITAL_CA,
  SERVER_CERTIFICATE,
  integrationsSummary,
  integrationTone,
  linkSummary,
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
      cables: null,
      cableLost: false,
      lastCableDropAt: null,
      lostCables: null,
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

  it('with the job lease, is green for one holder and says when another is taking the jobs over', () => {
    const lease = (name: string, active: boolean, reporting = true): ServerNodeHealth => {
      const node = server(name, 'all', reporting);
      return { ...node, server: { ...node.server, jobs: { active, since: active ? '2026-11-09T10:00:00Z' : null } } };
    };
    expect(serversSummary([lease('A', true), lease('B', false)])).toEqual({ text: 'All 2 reporting', tone: 'ok' });
    expect(serversSummary([lease('A', false), lease('B', false)])).toEqual({
      text: 'All 2 reporting; another server is taking the background jobs over',
      tone: 'warn',
    });
  });
});

describe('servers', () => {
  it('names what each server is for', () => {
    expect(serverRole({ role: 'api' })).toBe('API node');
    expect(serverRole({ role: 'worker' })).toBe('Runs the background jobs');
    expect(serverRole({ role: 'all' })).toBe('API and background jobs');
    expect([runsJobs({ role: 'api' }), runsJobs({ role: 'worker' }), runsJobs({ role: 'All' })]).toEqual([
      false,
      true,
      true,
    ]);
  });

  it('says which server holds the job lease, and which stand by', () => {
    const holds = { role: 'all', jobs: { active: true, since: '2026-11-09T10:00:00Z' } };
    const standsBy = { role: 'worker', jobs: { active: false, since: null } };
    expect(serverRole(holds)).toBe('API and background jobs (holds the job lease)');
    expect(serverRole(standsBy)).toBe('Stands by for the background jobs');
    expect(serverRole({ role: 'all', jobs: { active: false, since: null } })).toBe(
      'API node, standing by for the background jobs',
    );
    expect([runsJobs(holds), runsJobs(standsBy)]).toEqual([true, false]);
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

// SRS DED-06 on a cabled LAN, as at Nana Hospitals: the server's link is its network cable, and a WiFi card beside it is
// not in use. A cabled server's idle WiFi card was a red "WiFi: Disconnected" on the home screen for as long as it ran.
describe('linkSummary', () => {
  const cable = (speedMbps: number | null = 1000): NetworkLink => ({
    name: 'Ethernet',
    description: 'Intel(R) Ethernet Connection (17) I219-V',
    medium: 'Cable',
    isUp: true,
    speedMbps,
    addresses: ['192.168.1.10'],
    carriesNetwork: true,
  });
  const wifi = (isConnected: boolean): WifiConnectionInfo => ({
    interfaceDescription: 'Intel(R) Wi-Fi 6 AX201 160MHz',
    isConnected,
    ssid: isConnected ? 'Staff' : null,
    bssid: null,
    signalQuality: isConnected ? 82 : 0,
    rssiDbm: null,
    receiveRateMbps: null,
    transmitRateMbps: null,
    phyType: null,
    authAlgorithm: null,
    cipherAlgorithm: null,
    security: isConnected ? 'Enterprise' : 'Unknown',
    detailsAvailable: isConnected,
    note: null,
  });
  const warning = (code: string, server: string | null = null) => ({
    code,
    severity: 'critical',
    message: code,
    server,
  });
  const link = (network: Partial<SystemHealthReport['network']>, warnings: ReturnType<typeof warning>[] = []) => {
    const node = server('HC360-SRV', 'all');
    return linkSummary({ server: node.server, warnings, network: { ...node.network, ...network } });
  };

  it('is the cable while one is up, whatever the WiFi card beside it is doing', () => {
    expect(link({ cables: [cable()], wifiSupported: true, wifi: wifi(false) })).toEqual({
      label: 'Network',
      text: 'Cable · 1 Gbps',
      tone: 'ok',
    });
    expect(link({ cables: [cable()], wifiSupported: true, wifi: wifi(true) }).text).toBe('Cable · 1 Gbps');
    expect(link({ cables: [cable(100)] }).text).toBe('Cable · 100 Mbps');
    expect(link({ cables: [cable(null)] }).text, 'a speed the card does not report is left out').toBe('Cable');
  });

  it('says the cable is lost: bad with no WiFi to fall back on, a warning while the server is on WiFi', () => {
    expect(link({ cables: [], cableLost: true, wifiSupported: true, wifi: wifi(false) })).toEqual({
      label: 'Network',
      text: 'Cable disconnected',
      tone: 'bad',
    });
    expect(link({ cables: [], cableLost: true, wifiSupported: true, wifi: wifi(true) })).toEqual({
      label: 'Network',
      text: 'Cable disconnected · on WiFi Staff',
      tone: 'warn',
    });
  });

  it('says a server with no cable and no WiFi is not connected, not that it is wired', () => {
    // As when it started with its cable out: the server warns network.down.
    expect(link({ cables: [] }, [warning('network.down')])).toEqual({
      label: 'Network',
      text: 'Not connected',
      tone: 'bad',
    });
    expect(link({ cables: [], wifiSupported: true, wifi: wifi(false) }, [warning('network.down')]).text).toBe(
      'Not connected',
    );
    // The cables read and none up is no link, even without the warning (another server's is not this one's).
    expect(link({ cables: [] }, [warning('network.down', 'HC360-JOBS')]).text).toBe('Not connected');
  });

  it('is the WiFi on a server that has no cable up, as before', () => {
    expect(link({ cables: [], wifiSupported: true, wifi: wifi(true) })).toEqual({
      label: 'WiFi',
      text: 'Staff · 82%',
      tone: 'ok',
    });
    expect(link({ cables: [], wifiSupported: true, wifi: wifi(false) })).toEqual({
      label: 'WiFi',
      text: 'Disconnected',
      tone: 'bad',
    });
    expect(link({ cables: [] }, [warning('wifi.adapter_missing')])).toEqual({
      label: 'WiFi',
      text: 'Adapter missing',
      tone: 'bad',
    });
  });

  // A server with two cards, the hospital LAN and an imaging network: each lost cable is named, beside the one still up.
  it('names a cable lost beside one still up: bad if the workstations used it, a warning otherwise', () => {
    const imaging: NetworkLink = { ...cable(), name: 'Ethernet 2', addresses: ['10.20.0.5'] };
    const lost = (name: string, servedWorkstations: boolean): LostCable => ({
      name,
      addresses: [],
      lostAt: '2026-11-13T06:00:00Z',
      servedWorkstations,
    });

    expect(link({ cables: [imaging], cableLost: true, lostCables: [lost('Ethernet', true)] })).toEqual({
      label: 'Network',
      text: "Cable 'Ethernet' disconnected",
      tone: 'bad',
    });
    expect(link({ cables: [cable()], cableLost: true, lostCables: [lost('Ethernet 2', false)] })).toEqual({
      label: 'Network',
      text: "Cable · 1 Gbps · 'Ethernet 2' disconnected",
      tone: 'warn',
    });
  });

  it('takes a server with no WiFi adapter to be wired while the link monitor is off and no cable was read', () => {
    expect(link({ cables: null })).toEqual({ label: 'Network', text: 'Wired network', tone: 'ok' });
  });
});
