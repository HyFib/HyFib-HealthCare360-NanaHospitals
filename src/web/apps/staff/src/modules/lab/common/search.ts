/**
 * URL search-param validators for the diagnostics screens. Filters live in the URL (Back/Forward and bookmarks work),
 * but only well-formed values are accepted so a hand-edited link can never send garbage to the server.
 */

const DATE = /^\d{4}-\d{2}-\d{2}$/;
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** A non-empty trimmed string of at most `max` characters. */
export function searchText(value: unknown, max = 100): string | undefined {
  if (typeof value !== 'string' && typeof value !== 'number') {
    return undefined;
  }
  const text = String(value).trim();
  return text && text.length <= max ? text : undefined;
}

/** A calendar date "yyyy-MM-dd" that actually exists. */
export function searchDate(value: unknown): string | undefined {
  if (typeof value !== 'string' || !DATE.test(value)) {
    return undefined;
  }
  const [y, m, d] = value.split('-').map(Number);
  const date = new Date(Date.UTC(y!, m! - 1, d!));
  return date.getUTCFullYear() === y && date.getUTCMonth() === m! - 1 && date.getUTCDate() === d ? value : undefined;
}

/** A page number above 1 (page 1 is the default and stays out of the URL). */
export function searchPage(value: unknown): number | undefined {
  const page = typeof value === 'number' ? value : typeof value === 'string' ? Number(value) : Number.NaN;
  return Number.isInteger(page) && page > 1 && page < 100_000 ? page : undefined;
}

/** One of the allowed values (enums arrive as strings). */
export function searchOneOf<T extends string>(value: unknown, allowed: readonly T[]): T | undefined {
  return typeof value === 'string' && (allowed as readonly string[]).includes(value) ? (value as T) : undefined;
}

/** A GUID (record ids in links). */
export function searchId(value: unknown): string | undefined {
  return typeof value === 'string' && GUID.test(value) ? value : undefined;
}

/** A boolean flag; only `true` is kept so the default (false) stays out of the URL. */
export function searchFlag(value: unknown): true | undefined {
  return value === true || value === 'true' ? true : undefined;
}

/** A small positive integer within [min, max]. */
export function searchInt(value: unknown, min: number, max: number): number | undefined {
  const n = typeof value === 'number' ? value : typeof value === 'string' ? Number(value) : Number.NaN;
  return Number.isInteger(n) && n >= min && n <= max ? n : undefined;
}
