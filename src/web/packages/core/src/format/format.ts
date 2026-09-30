/** Display formatting for Indian hospitals: INR with lakh grouping, IST dates, compact ages. */

export const HOSPITAL_TIME_ZONE = 'Asia/Kolkata';

const inr = new Intl.NumberFormat('en-IN', {
  style: 'currency',
  currency: 'INR',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});
const inrWhole = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', maximumFractionDigits: 0 });
const number = new Intl.NumberFormat('en-IN');
const dateFmt = new Intl.DateTimeFormat('en-IN', {
  timeZone: HOSPITAL_TIME_ZONE,
  day: '2-digit',
  month: 'short',
  year: 'numeric',
});
const timeFmt = new Intl.DateTimeFormat('en-IN', {
  timeZone: HOSPITAL_TIME_ZONE,
  hour: 'numeric',
  minute: '2-digit',
  hour12: true,
});
const dateTimeFmt = new Intl.DateTimeFormat('en-IN', {
  timeZone: HOSPITAL_TIME_ZONE,
  day: '2-digit',
  month: 'short',
  year: 'numeric',
  hour: 'numeric',
  minute: '2-digit',
  hour12: true,
});
const isoDateFmt = new Intl.DateTimeFormat('en-CA', {
  timeZone: HOSPITAL_TIME_ZONE,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
});

export function formatInr(value: number | null | undefined, options?: { whole?: boolean }): string {
  if (value === null || value === undefined || Number.isNaN(value)) {
    return '—';
  }
  return (options?.whole ? inrWhole : inr).format(value);
}

export function formatNumber(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : number.format(value);
}

/** Instant (ISO with offset) → "25 Sept 2026" in IST. */
export function formatDate(iso: string | null | undefined): string {
  return iso ? dateFmt.format(new Date(iso)) : '—';
}

export function formatTime(iso: string | null | undefined): string {
  return iso ? timeFmt.format(new Date(iso)) : '—';
}

export function formatDateTime(iso: string | null | undefined): string {
  return iso ? dateTimeFmt.format(new Date(iso)) : '—';
}

/** Calendar date "yyyy-MM-dd" (no time zone) → "14 Mar 1987". */
export function formatDateOnly(value: string | null | undefined): string {
  if (!value) {
    return '—';
  }
  const [y, m, d] = value.split('-').map(Number);
  if (!y || !m || !d) {
    return value;
  }
  return new Intl.DateTimeFormat('en-IN', { day: '2-digit', month: 'short', year: 'numeric', timeZone: 'UTC' }).format(
    Date.UTC(y, m - 1, d),
  );
}

/** Today's date in IST as "yyyy-MM-dd" (what the server expects for date filters). */
export function todayIso(offsetDays = 0): string {
  return isoDateFmt.format(new Date(Date.now() + offsetDays * 86_400_000));
}

/** "just now", "5 min ago", "3 h ago", else the date. */
export function formatRelative(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) {
    return '—';
  }
  const minutes = Math.round((now - Date.parse(iso)) / 60_000);
  if (minutes < 1) {
    return 'just now';
  }
  if (minutes < 60) {
    return `${minutes} min ago`;
  }
  if (minutes < 24 * 60) {
    return `${Math.round(minutes / 60)} h ago`;
  }
  return formatDate(iso);
}

/** "34Y 2M" / "5M 12D" / "9D" — the compact age used on banners and prints. */
export function formatAge(dateOfBirth: string | null | undefined, asOf: Date = new Date()): string {
  if (!dateOfBirth) {
    return '—';
  }
  const [y, m, d] = dateOfBirth.split('-').map(Number);
  if (!y || !m || !d) {
    return '—';
  }
  let years = asOf.getFullYear() - y;
  let months = asOf.getMonth() + 1 - m;
  let days = asOf.getDate() - d;
  if (days < 0) {
    months--;
    days += new Date(asOf.getFullYear(), asOf.getMonth(), 0).getDate();
  }
  if (months < 0) {
    years--;
    months += 12;
  }
  if (years >= 1) {
    return months > 0 ? `${years}Y ${months}M` : `${years}Y`;
  }
  return months >= 1 ? `${months}M ${days}D` : `${Math.max(days, 0)}D`;
}

/** "OutPatient" → "Out patient", "HalfDay" → "Half day". */
export function humanize(value: string | null | undefined): string {
  if (!value) {
    return '';
  }
  const spaced = value.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/_/g, ' ');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}

/** "+919876543210" → "+91 98765 43210". */
export function formatPhone(e164: string | null | undefined): string {
  if (!e164) {
    return '—';
  }
  const match = /^\+91(\d{5})(\d{5})$/.exec(e164);
  return match ? `+91 ${match[1]} ${match[2]}` : e164;
}

export function initials(name: string | null | undefined): string {
  if (!name) {
    return '?';
  }
  const parts = name
    .replace(/^(dr|mr|mrs|ms|master|baby)\.?\s+/i, '')
    .split(/\s+/)
    .filter(Boolean);
  return ((parts[0]?.[0] ?? '') + (parts.length > 1 ? (parts.at(-1)?.[0] ?? '') : '')).toUpperCase() || '?';
}

/**
 * Normalises a phone number to E.164 exactly like the server (SharedKernel Phone.Normalize): 10 digits get +91,
 * a leading 0 or 91 is accepted, "+"/"00" prefixes keep their country code. Returns null when invalid.
 */
export function normalizePhone(input: string | null | undefined): string | null {
  const trimmed = input?.trim();
  if (!trimmed) {
    return null;
  }
  let digits = trimmed.replace(/\D/g, '');
  if (!digits) {
    return null;
  }
  if (trimmed.startsWith('+')) {
    return digits.length >= 8 && digits.length <= 15 ? `+${digits}` : null;
  }
  if (digits.startsWith('00')) {
    digits = digits.slice(2);
    return digits.length >= 8 && digits.length <= 15 ? `+${digits}` : null;
  }
  if (digits.length === 10) {
    return `+91${digits}`;
  }
  if (digits.length === 11 && digits.startsWith('0')) {
    return `+91${digits.slice(1)}`;
  }
  if (digits.length === 12 && digits.startsWith('91')) {
    return `+${digits}`;
  }
  return null;
}

/** True for a valid Indian mobile number (10 digits starting 6–9, with or without +91/0). */
export function isIndianMobile(input: string | null | undefined): boolean {
  return /^\+91[6-9]\d{9}$/.test(normalizePhone(input) ?? '');
}
