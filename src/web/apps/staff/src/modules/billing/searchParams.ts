/**
 * Readers for URL search params (`validateSearch`): anything unexpected in a bookmarked or hand-edited URL is
 * dropped instead of reaching the API. Only ids and filter values ever go into billing URLs, never patient details.
 */

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;

export function idParam(value: unknown): string | undefined {
  return typeof value === 'string' && GUID.test(value) ? value : undefined;
}

export function dateParam(value: unknown): string | undefined {
  return typeof value === 'string' && ISO_DATE.test(value) ? value : undefined;
}

export function pageParam(value: unknown): number | undefined {
  const page = typeof value === 'number' ? value : typeof value === 'string' ? Number(value) : Number.NaN;
  return Number.isInteger(page) && page > 1 && page < 100_000 ? page : undefined;
}

export function textParam(value: unknown, maxLength = 100): string | undefined {
  return typeof value === 'string' && value.trim() && value.length <= maxLength ? value : undefined;
}

export function enumParam<T extends string>(value: unknown, allowed: readonly T[]): T | undefined {
  return typeof value === 'string' && (allowed as readonly string[]).includes(value) ? (value as T) : undefined;
}

export function flagParam(value: unknown): true | undefined {
  return value === true || value === 'true' || value === 1 || value === '1' ? true : undefined;
}
