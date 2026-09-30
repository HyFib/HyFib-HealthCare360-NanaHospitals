import type { StatusTone } from '@hc360/ui';

const quantityFormat = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 3 });
const percentFormat = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 2 });

/** "1,250" or "2.5" (Indian grouping, up to 3 decimals), with the unit when given: "20 TAB". */
export function formatQty(value: number | null | undefined, unit?: string | null): string {
  if (value === null || value === undefined || Number.isNaN(value)) {
    return '—';
  }
  const text = quantityFormat.format(value);
  return unit ? `${text} ${unit}` : text;
}

/** "12%" / "0.25%". */
export function formatPercent(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : `${percentFormat.format(value)}%`;
}

/** Colour of an expiry: red once expired, amber within `warnDays` (default 90), otherwise neutral. */
export function expiryTone(daysToExpiry: number | null | undefined, warnDays = 90): StatusTone {
  if (daysToExpiry === null || daysToExpiry === undefined) {
    return 'informative';
  }
  if (daysToExpiry < 0) {
    return 'danger';
  }
  return daysToExpiry <= warnDays ? 'warning' : 'success';
}

/** "Expired 3 days ago" · "Expires today" · "Expires in 12 days" · "Expires in 5 months" · "No expiry". */
export function expiryLabel(daysToExpiry: number | null | undefined): string {
  if (daysToExpiry === null || daysToExpiry === undefined) {
    return 'No expiry';
  }
  if (daysToExpiry < 0) {
    const ago = -daysToExpiry;
    return `Expired ${ago === 1 ? 'yesterday' : `${ago} days ago`}`;
  }
  if (daysToExpiry === 0) {
    return 'Expires today';
  }
  if (daysToExpiry === 1) {
    return 'Expires tomorrow';
  }
  if (daysToExpiry < 62) {
    return `Expires in ${daysToExpiry} days`;
  }
  return `Expires in ${Math.round(daysToExpiry / 30.4)} months`;
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** Pack-style expiry "Mar 2027" from "2027-03-31". */
export function formatExpiry(dateOnly: string | null | undefined): string {
  if (!dateOnly) {
    return '—';
  }
  const [year, month] = dateOnly.split('-').map(Number);
  return year && month && month >= 1 && month <= 12 ? `${MONTHS[month - 1]} ${year}` : dateOnly;
}

/** Days from `today` to `dateOnly` (both "yyyy-MM-dd", calendar days, no time zone). */
export function daysBetween(today: string, dateOnly: string): number {
  const toUtc = (value: string) => {
    const [y, m, d] = value.split('-').map(Number);
    return Date.UTC(y ?? 1970, (m ?? 1) - 1, d ?? 1);
  };
  return Math.round((toUtc(dateOnly) - toUtc(today)) / 86_400_000);
}

/** "yyyy-MM-dd" plus `days` (calendar arithmetic, no time zone). */
export function addDays(dateOnly: string, days: number): string {
  const [y, m, d] = dateOnly.split('-').map(Number);
  const value = new Date(Date.UTC(y ?? 1970, (m ?? 1) - 1, (d ?? 1) + days));
  return value.toISOString().slice(0, 10);
}

/** Masks all but the last four characters ("XXXXXX1234") for lists; full values stay on the detail view. */
export function maskTail(value: string | null | undefined, visible = 4): string {
  if (!value) {
    return '—';
  }
  return value.length <= visible ? value : `${'X'.repeat(Math.min(value.length - visible, 6))}${value.slice(-visible)}`;
}

/** Number input text → number or null (blank → null, "1,250.5" → 1250.5). */
export function parseNumber(text: string): number | null {
  const cleaned = text.replace(/,/g, '').trim();
  if (cleaned === '') {
    return null;
  }
  const value = Number(cleaned);
  return Number.isFinite(value) ? value : null;
}

/** Search result to take on Enter: the exact code or barcode first, otherwise the first result. */
export function bestItemMatch<T extends { code: string }>(items: readonly T[], typed: string): T | undefined {
  const upper = typed.trim().toUpperCase();
  return items.find((i) => i.code.toUpperCase() === upper) ?? items[0];
}

const istDateFormat = new Intl.DateTimeFormat('en-CA', {
  timeZone: 'Asia/Kolkata',
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
});

/** Calendar date in IST ("yyyy-MM-dd") of an instant, e.g. to tell whether an invoice was raised today. */
export function istDateOf(iso: string): string {
  return istDateFormat.format(new Date(iso));
}
