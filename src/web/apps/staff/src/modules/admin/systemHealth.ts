import type { IntegrationHealthSummary } from './api';

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
