/** Turnaround-time helpers for the lab dashboard, widget and TAT report (pure; unit-tested). */
import type { LabSection, LabTatRow } from './api';

/** "45 min", "2 h 5 min", "1 d 3 h" — how people talk about turnaround. */
export function formatMinutes(minutes: number | null | undefined): string {
  if (minutes === null || minutes === undefined || !Number.isFinite(minutes) || minutes < 0) {
    return '—';
  }
  const total = Math.round(minutes);
  if (total < 60) {
    return `${total} min`;
  }
  if (total < 24 * 60) {
    const hours = Math.floor(total / 60);
    const rest = total % 60;
    return rest ? `${hours} h ${rest} min` : `${hours} h`;
  }
  const days = Math.floor(total / (24 * 60));
  const hours = Math.round((total % (24 * 60)) / 60);
  return hours ? `${days} d ${hours} h` : `${days} d`;
}

/** Minutes as hours with one decimal (chart axis), or null. */
export function toHours(minutes: number | null | undefined): number | null {
  return minutes === null || minutes === undefined ? null : Math.round((minutes / 60) * 10) / 10;
}

/** Share of released tests that met their target turnaround, across tests (weighted by volume). */
export function withinTargetPercent(rows: readonly Pick<LabTatRow, 'released' | 'withinTarget'>[]): number | null {
  const released = rows.reduce((sum, row) => sum + row.released, 0);
  if (released === 0) {
    return null;
  }
  const within = rows.reduce((sum, row) => sum + row.withinTarget, 0);
  return Math.round((within / released) * 1000) / 10;
}

/** Rejected samples as a share of samples collected (a NABH pre-analytical quality indicator). */
export function rejectionRate(samples: number, rejections: number): number | null {
  return samples > 0 ? Math.round((rejections / samples) * 1000) / 10 : null;
}

/** The busiest tests first (ties by name), for charts that can show only a few bars. */
export function busiestTests<T extends Pick<LabTatRow, 'released' | 'testName'>>(rows: readonly T[], count: number): T[] {
  return [...rows]
    .filter((row) => row.released > 0)
    .sort((a, b) => b.released - a.released || a.testName.localeCompare(b.testName))
    .slice(0, count);
}

export interface SectionTat {
  section: LabSection;
  released: number;
  withinTarget: number;
  percent: number | null;
}

/** Released volume and target compliance per bench. */
export function bySection(rows: readonly Pick<LabTatRow, 'section' | 'released' | 'withinTarget'>[]): SectionTat[] {
  const map = new Map<LabSection, SectionTat>();
  for (const row of rows) {
    const entry = map.get(row.section) ?? { section: row.section, released: 0, withinTarget: 0, percent: null };
    entry.released += row.released;
    entry.withinTarget += row.withinTarget;
    map.set(row.section, entry);
  }
  return [...map.values()].map((entry) => ({
    ...entry,
    percent: entry.released > 0 ? Math.round((entry.withinTarget / entry.released) * 1000) / 10 : null,
  }));
}

/** Tone for a target-compliance percentage: 90 % and above is good, below 75 % needs action. */
export function complianceTone(percent: number | null): 'good' | 'warning' | 'bad' | 'none' {
  if (percent === null) {
    return 'none';
  }
  return percent >= 90 ? 'good' : percent >= 75 ? 'warning' : 'bad';
}
