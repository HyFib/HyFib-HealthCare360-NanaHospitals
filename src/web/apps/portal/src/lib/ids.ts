const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** True for a GUID (the only kind of id the portal accepts from the address bar). */
export function isGuid(value: unknown): value is string {
  return typeof value === 'string' && GUID.test(value);
}

/** A search parameter that must be a GUID, else dropped. */
export function guidParam(value: unknown): string | undefined {
  return isGuid(value) ? value : undefined;
}

/** A short free-text search parameter (trimmed, capped), else dropped. */
export function textParam(value: unknown, max = 80): string | undefined {
  return typeof value === 'string' && value.trim() ? value.trim().slice(0, max) : undefined;
}

/** A yes/no search parameter ("true" or true), else dropped. */
export function flagParam(value: unknown): true | undefined {
  return value === true || value === 'true' ? true : undefined;
}

/** A calendar date search parameter ("yyyy-MM-dd"), else dropped. */
export function dateParam(value: unknown): string | undefined {
  return typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value) ? value : undefined;
}
