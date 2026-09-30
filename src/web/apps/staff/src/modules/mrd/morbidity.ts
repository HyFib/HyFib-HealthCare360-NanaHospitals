/**
 * Morbidity report helpers (pure, unit-tested): period validation, the top-N chart series and CSV export.
 */

export interface MorbidityRowLike {
  key: string;
  title: string;
  chapter: string | null;
  episodes: number;
  patients: number;
  male: number;
  female: number;
  otherOrUnknownGender: number;
  age0To14: number;
  age15To44: number;
  age45To64: number;
  age65Plus: number;
  ageUnknown: number;
}

/** The server accepts periods of up to one year (366 days inclusive). */
export const MAX_REPORT_DAYS = 366;

function dayNumber(iso: string): number {
  const [y, m, d] = iso.split('-').map(Number);
  return Date.UTC(y ?? 1970, (m ?? 1) - 1, d ?? 1) / 86_400_000;
}

/** Null when the period is fine, else what to change. */
export function periodProblem(from: string, to: string): string | null {
  if (!from || !to) {
    return 'Choose both dates.';
  }
  if (from > to) {
    return 'The start date must be on or before the end date.';
  }
  return dayNumber(to) - dayNumber(from) >= MAX_REPORT_DAYS ? 'Choose a period of up to one year.' : null;
}

/** The most frequent rows (episodes, then patients, then code), for the chart. */
export function topRows<T extends MorbidityRowLike>(rows: readonly T[], count = 15): T[] {
  return rows
    .toSorted((a, b) => b.episodes - a.episodes || b.patients - a.patients || a.key.localeCompare(b.key))
    .slice(0, count);
}

/** Labels for a horizontal bar chart: "E11.9 Type 2 diabetes…" shortened so the axis stays readable. */
export function chartLabel(row: Pick<MorbidityRowLike, 'key' | 'title'>, max = 42): string {
  const label = row.title && row.title !== row.key ? `${row.key} ${row.title}` : row.key;
  return label.length > max ? `${label.slice(0, max - 1)}…` : label;
}

function csvCell(value: string | number | null): string {
  const text = value === null ? '' : String(value);
  return /[",\n\r]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
}

/** CSV for spreadsheets and the state health returns (UTF-8, header row, one line per group). */
export function morbidityCsv(rows: readonly MorbidityRowLike[], groupLabel: string): string {
  const header = [
    groupLabel,
    'Title',
    'Chapter',
    'Episodes',
    'Patients',
    'Male',
    'Female',
    'Other or unknown',
    'Age 0-14',
    'Age 15-44',
    'Age 45-64',
    'Age 65+',
    'Age unknown',
  ];
  const lines = rows.map((r) =>
    [
      r.key,
      r.title,
      r.chapter,
      r.episodes,
      r.patients,
      r.male,
      r.female,
      r.otherOrUnknownGender,
      r.age0To14,
      r.age15To44,
      r.age45To64,
      r.age65Plus,
      r.ageUnknown,
    ]
      .map(csvCell)
      .join(','),
  );
  return [header.map(csvCell).join(','), ...lines].join('\r\n');
}

export interface Totals {
  episodes: number;
  patients: number;
  male: number;
  female: number;
  children: number;
  elderly: number;
}

export function totals(rows: readonly MorbidityRowLike[]): Totals {
  return rows.reduce<Totals>(
    (sum, r) => ({
      episodes: sum.episodes + r.episodes,
      patients: sum.patients + r.patients,
      male: sum.male + r.male,
      female: sum.female + r.female,
      children: sum.children + r.age0To14,
      elderly: sum.elderly + r.age65Plus,
    }),
    { episodes: 0, patients: 0, male: 0, female: 0, children: 0, elderly: 0 },
  );
}
