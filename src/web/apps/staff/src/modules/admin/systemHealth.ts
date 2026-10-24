import type { IntegrationHealthSummary, ServerNodeHealth, SystemHealthReport, ClockCheck } from './api';

export type HealthTone = 'ok' | 'warn' | 'bad';

/** System health starts to warn this long before a certificate expires, as the server does. */
const CERTIFICATE_WARNING_DAYS = 30;

/** How worrying a certificate's expiry is: red once expired, amber within the warning window, nothing before. */
export function expiryTone(expiresAt: string | null, now: Date = new Date()): HealthTone | undefined {
  if (!expiresAt) {
    return undefined;
  }
  const left = new Date(expiresAt).getTime() - now.getTime();
  if (left <= 0) {
    return 'bad';
  }
  return left < CERTIFICATE_WARNING_DAYS * 24 * 60 * 60 * 1000 ? 'warn' : undefined;
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

/** Whether a server runs the background jobs (backups, messages, the analysers' and modalities' listeners). */
export function runsJobs(role: string): boolean {
  return role.toLowerCase() !== 'api';
}

/** What one server of several is for, as System health names it. */
export function serverRole(role: string): string {
  switch (role.toLowerCase()) {
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
 * runs the background jobs, that nothing does; and when more than one that reports runs them, which, as only one should.
 */
export function serversSummary(servers: ServerNodeHealth[]): { text: string; tone: HealthTone } {
  const silent = servers.filter((node) => !node.reporting).map((node) => node.server.machineName);
  const jobs = servers.some((node) => runsJobs(node.server.role));
  const running = servers
    .filter((node) => node.reporting && runsJobs(node.server.role))
    .map((node) => node.server.machineName);
  const parts = [silent.length === 0 ? `All ${servers.length} reporting` : `${silent.join(', ')} not reporting`];
  if (!jobs) {
    parts.push('none runs the background jobs');
  } else if (running.length > 1) {
    parts.push(`${running.join(', ')} each run the background jobs`);
  }
  return { text: parts.join('; '), tone: silent.length > 0 || !jobs ? 'bad' : running.length > 1 ? 'warn' : 'ok' };
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
