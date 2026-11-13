import { formatLinkSpeed } from '@hc360/core';
import type { IntegrationHealthSummary, ServerInfo, ServerNodeHealth, SystemHealthReport, ClockCheck } from './api';

export type HealthTone = 'ok' | 'warn' | 'bad';

/** When the server's warning about a certificate starts (amber) and turns critical (red), in days, as the server decides. */
export interface ExpiryPeriods {
  warnDays: number;
  urgentDays: number;
}

/** The server certificate: issued again by itself 30 days ahead, so one left with under 14 days was not. */
export const SERVER_CERTIFICATE: ExpiryPeriods = { warnDays: 30, urgentDays: 14 };

/** The hospital CA: every device trusts it, so replacing it is planned a year ahead, and urgent in its last 90 days. */
export const HOSPITAL_CA: ExpiryPeriods = { warnDays: 365, urgentDays: 90 };

/** How worrying a certificate's expiry is: red once expired or urgent, amber within the warning period, nothing before. */
export function expiryTone(
  expiresAt: string | null,
  periods: ExpiryPeriods,
  now: Date = new Date(),
): HealthTone | undefined {
  if (!expiresAt) {
    return undefined;
  }
  const daysLeft = (new Date(expiresAt).getTime() - now.getTime()) / (24 * 60 * 60 * 1000);
  if (daysLeft < periods.urgentDays) {
    return 'bad';
  }
  return daysLeft < periods.warnDays ? 'warn' : undefined;
}

/** The tone of one integration's row: only those switched on can be in trouble. */
export function integrationTone(row: IntegrationHealthSummary): HealthTone | undefined {
  if (!row.enabled) {
    return undefined;
  }
  switch (row.health) {
    case 'failing':
      return 'bad';
    case 'degraded':
      return 'warn';
    case 'healthy':
      return 'ok';
    default:
      return undefined;
  }
}

/**
 * The System health line for the integrations (SRS DED-17): how many are failing or had failures today, how many work,
 * and the worst tone among them. Integrations that are not set up are left out; they need no attention.
 */
export function integrationsSummary(rows: IntegrationHealthSummary[]): { text: string; tone: HealthTone | undefined } {
  const setUp = rows.filter((row) => row.configured);
  if (setUp.length === 0) {
    return { text: 'None set up', tone: undefined };
  }
  const on = setUp.filter((row) => row.enabled);
  const failing = on.filter((row) => row.health === 'failing').length;
  const degraded = on.filter((row) => row.health === 'degraded').length;
  const working = on.length - failing - degraded;
  const off = setUp.length - on.length;
  const parts = [
    failing > 0 && `${failing} failing`,
    degraded > 0 && `${degraded} with failures in 24 h`,
    working > 0 && `${working} working`,
    off > 0 && `${off} turned off`,
  ].filter((part): part is string => typeof part === 'string');
  return {
    text: parts.join(', '),
    tone: failing > 0 ? 'bad' : degraded > 0 ? 'warn' : on.length > 0 ? 'ok' : undefined,
  };
}

/**
 * Whether a server runs the background jobs (backups, messages, the analysers' and modalities' listeners): the one
 * holding the job lease, where the servers take turns at them; else any but an API node.
 */
export function runsJobs(server: Pick<ServerInfo, 'role' | 'jobs'>): boolean {
  return server.jobs ? server.jobs.active : server.role.toLowerCase() !== 'api';
}

/** Whether a server stands by to take the background jobs over (the job lease). */
export function standsBy(server: Pick<ServerInfo, 'jobs'>): boolean {
  return server.jobs?.active === false;
}

/** What one server of several is for, as System health names it. */
export function serverRole(server: Pick<ServerInfo, 'role' | 'jobs'>): string {
  const role = server.role.toLowerCase();
  if (server.jobs) {
    if (server.jobs.active) {
      return role === 'worker'
        ? 'Runs the background jobs (holds the job lease)'
        : 'API and background jobs (holds the job lease)';
    }
    return role === 'worker' ? 'Stands by for the background jobs' : 'API node, standing by for the background jobs';
  }
  switch (role) {
    case 'api':
      return 'API node';
    case 'worker':
      return 'Runs the background jobs';
    default:
      return 'API and background jobs';
  }
}

/**
 * The home screen's line for several servers (SCAL-01): all of them reporting, or which have stopped; when none of them
 * runs the background jobs, that nothing does, or, while others stand by for the job lease, that one is about to; and when
 * more than one that reports runs them, which, as only one should.
 */
export function serversSummary(servers: ServerNodeHealth[]): { text: string; tone: HealthTone } {
  const silent = servers.filter((node) => !node.reporting).map((node) => node.server.machineName);
  const running = servers
    .filter((node) => node.reporting && runsJobs(node.server))
    .map((node) => node.server.machineName);
  const jobs = servers.some((node) => runsJobs(node.server));
  const standingBy = servers.some((node) => node.reporting && standsBy(node.server));
  const parts = [silent.length === 0 ? `All ${servers.length} reporting` : `${silent.join(', ')} not reporting`];
  if (running.length === 0 && standingBy) {
    parts.push('another server is taking the background jobs over');
  } else if (!jobs) {
    parts.push('none runs the background jobs');
  } else if (running.length > 1) {
    parts.push(`${running.join(', ')} each run the background jobs`);
  }
  const tone: HealthTone = silent.length > 0 || (!jobs && !standingBy) ? 'bad' : running.length !== 1 ? 'warn' : 'ok';
  return { text: parts.join('; '), tone };
}

/**
 * The WiFi adapter of this server's link is missing (DED-06: disabled, removed, or the WLAN AutoConfig service stopped),
 * as its System health warning says. Windows then shows no adapter at all, as on a server that is wired only.
 */
export function wifiAdapterMissing(report: Pick<SystemHealthReport, 'server' | 'warnings'>): boolean {
  return report.warnings.some(
    (w) => w.code === 'wifi.adapter_missing' && (w.server == null || w.server === report.server.machineName),
  );
}

/**
 * How a server reaches the network, in one line (DED-06): its network cable while one is up, else its WiFi. A cabled
 * server's idle WiFi card is not its link, so it is not "Disconnected". `tone` is bad when the server has no link: its
 * cable lost, its WiFi down or its adapter missing, or no connection at all, as its System health warning says.
 */
export function linkSummary(report: Pick<SystemHealthReport, 'server' | 'warnings' | 'network'>): {
  label: string;
  text: string;
  tone: HealthTone;
} {
  const { network } = report;
  const wifi = network.wifi;
  const mine = (code: string) =>
    report.warnings.some((w) => w.code === code && (w.server == null || w.server === report.server.machineName));
  const cable = network.cables?.[0];
  if (cable) {
    return {
      label: 'Network',
      text: `Cable${cable.speedMbps !== null ? ` · ${formatLinkSpeed(cable.speedMbps)}` : ''}`,
      tone: 'ok',
    };
  }
  if (network.cableLost) {
    return {
      label: 'Network',
      text: wifi?.isConnected ? `Cable disconnected · on WiFi ${wifi.ssid ?? ''}`.trim() : 'Cable disconnected',
      tone: wifi?.isConnected ? 'warn' : 'bad',
    };
  }
  if (wifi?.isConnected) {
    return {
      label: 'WiFi',
      text: `${wifi.ssid ?? 'Connected'}${wifi.detailsAvailable ? ` · ${wifi.signalQuality}%` : ''}`,
      tone: 'ok',
    };
  }
  if (wifiAdapterMissing(report)) {
    return { label: 'WiFi', text: 'Adapter missing', tone: 'bad' };
  }
  if (mine('network.down')) {
    return { label: 'Network', text: 'Not connected', tone: 'bad' };
  }
  if (network.wifiSupported) {
    return { label: 'WiFi', text: 'Disconnected', tone: 'bad' };
  }
  if (network.cables) {
    // The cables were read and none is up, and there is no WiFi on this machine: it has no link at all.
    return { label: 'Network', text: 'Not connected', tone: 'bad' };
  }
  // The link monitor is off, so no cable was read: as before, a server with no WiFi adapter is taken to be wired.
  return { label: 'Network', text: 'Wired network', tone: 'ok' };
}

/** Less than a tenth of the data drive is free, as the server warns. */
export function lowDiskSpace(storage: SystemHealthReport['storage']): boolean {
  return (
    storage.freeBytes !== null &&
    storage.totalBytes !== null &&
    storage.totalBytes > 0 &&
    storage.freeBytes / storage.totalBytes < 0.1
  );
}

/** How long a server has been running, in hours for two days and in days after. */
/** The clock row: how far off, and from which time server; null before the first check. */
export function clockText(clock: ClockCheck | null | undefined): { text: string; bad: boolean } | null {
  if (!clock) {
    return null;
  }
  if (clock.offsetSeconds === null) {
    return { text: 'Not checked: no national time server answered', bad: true };
  }
  const off = Math.abs(clock.offsetSeconds);
  return {
    text: `${off < 0.001 ? 'Exact' : `${off.toFixed(3)} s ${clock.offsetSeconds > 0 ? 'behind' : 'ahead'}`} · ${clock.server}`,
    bad: off > 1,
  };
}

export function runningFor(uptimeHours: number): string {
  return uptimeHours < 48 ? `${Math.round(uptimeHours)} hours` : `${Math.round(uptimeHours / 24)} days`;
}
