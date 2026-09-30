import { describe, expect, it } from 'vitest';
import {
  balanceTone,
  chartDay,
  formatBalance,
  routeMatches,
  routesFor,
  runningBalance,
  summarizeFluids,
  type FluidEntryInput,
} from './fluidBalance';
import {
  addDays,
  atIst,
  dutyDate,
  formatDuration,
  formatTimeOfDay,
  fromIstInput,
  previousMonth,
  shiftAt,
  toIstInput,
} from './time';

/** IST wall-clock → ISO instant, for readable fixtures. */
const ist = (date: string, hh: number, mm = 0) => atIst(date, hh, mm);

describe('IST time helpers', () => {
  it('reads and writes datetime-local values as IST whatever the workstation zone', () => {
    expect(fromIstInput('2026-09-26T10:30')).toBe('2026-09-26T05:00:00.000Z');
    expect(toIstInput('2026-09-26T05:00:00.000Z')).toBe('2026-09-26T10:30');
    expect(toIstInput('2026-09-25T20:00:00Z')).toBe('2026-09-26T01:30');
    expect(fromIstInput('26/09/2026 10:30')).toBeNull();
    expect(toIstInput(null)).toBe('');
  });

  it('knows the nursing shift of an instant', () => {
    expect(shiftAt(ist('2026-09-26', 7, 59))).toBe('Night');
    expect(shiftAt(ist('2026-09-26', 8))).toBe('Morning');
    expect(shiftAt(ist('2026-09-26', 13, 59))).toBe('Morning');
    expect(shiftAt(ist('2026-09-26', 14))).toBe('Evening');
    expect(shiftAt(ist('2026-09-26', 20))).toBe('Night');
  });

  it('puts the small hours on the previous night duty sheet', () => {
    expect(dutyDate(ist('2026-09-26', 2))).toBe('2026-09-25');
    expect(dutyDate(ist('2026-09-26', 8))).toBe('2026-09-26');
    expect(dutyDate(ist('2026-09-26', 23, 30))).toBe('2026-09-26');
  });

  it('does calendar arithmetic across month ends', () => {
    expect(addDays('2026-09-30', 1)).toBe('2026-10-01');
    expect(addDays('2026-03-01', -1)).toBe('2026-02-28');
    expect(previousMonth('2026-01')).toBe('2025-12');
  });

  it('formats durations and times of day', () => {
    expect(formatDuration(45)).toBe('45 min');
    expect(formatDuration(125)).toBe('2 h 5 min');
    expect(formatDuration(60 * 24 * 3 + 60)).toBe('3 d 1 h');
    expect(formatTimeOfDay('08:00:00')).toBe('8:00 am');
    expect(formatTimeOfDay('16:30:00')).toBe('4:30 pm');
    expect(formatTimeOfDay('00:15')).toBe('12:15 am');
  });
});

describe('fluid routes', () => {
  it('matches routes to their direction like the server', () => {
    expect(routeMatches('Intake', 'Oral')).toBe(true);
    expect(routeMatches('Output', 'Urine')).toBe(true);
    expect(routeMatches('Intake', 'Urine')).toBe(false);
    expect(routeMatches('Output', 'IntravenousFluid')).toBe(false);
    expect(routeMatches('Intake', 'Other')).toBe(true);
    expect(routeMatches('Output', 'Other')).toBe(true);
  });

  it('only offers routes that fit the direction', () => {
    expect(routesFor('Intake')).not.toContain('Urine');
    expect(routesFor('Output')).toContain('Drain');
  });
});

describe('fluid balance', () => {
  const day = '2026-09-26';
  const entries: FluidEntryInput[] = [
    { recordedAt: ist(day, 9), direction: 'Intake', route: 'Oral', volumeMl: 200 },
    { recordedAt: ist(day, 10), direction: 'Intake', route: 'IntravenousFluid', volumeMl: 500 },
    { recordedAt: ist(day, 12), direction: 'Output', route: 'Urine', volumeMl: 300 },
    { recordedAt: ist(day, 16), direction: 'Output', route: 'Urine', volumeMl: 250 },
    { recordedAt: ist(addDays(day, 1), 2), direction: 'Intake', route: 'Oral', volumeMl: 100 },
    // Outside the 08:00–08:00 chart day:
    { recordedAt: ist(day, 7, 30), direction: 'Intake', route: 'Oral', volumeMl: 999 },
    { recordedAt: ist(addDays(day, 1), 8), direction: 'Output', route: 'Urine', volumeMl: 999 },
  ];

  it('runs the chart day from 08:00 to 08:00 IST', () => {
    expect(chartDay(day)).toEqual({ from: '2026-09-26T02:30:00.000Z', to: '2026-09-27T02:30:00.000Z' });
  });

  it('totals intake, output and balance for the chart day', () => {
    const summary = summarizeFluids(entries, day);
    expect(summary.intakeMl).toBe(800);
    expect(summary.outputMl).toBe(550);
    expect(summary.balanceMl).toBe(250);
  });

  it('gives each shift its own subtotal', () => {
    const { byShift } = summarizeFluids(entries, day);
    expect(byShift).toEqual([
      { shift: 'Morning', intakeMl: 700, outputMl: 300, balanceMl: 400 },
      { shift: 'Evening', intakeMl: 0, outputMl: 250, balanceMl: -250 },
      { shift: 'Night', intakeMl: 100, outputMl: 0, balanceMl: 100 },
    ]);
  });

  it('groups by route with intake first, in server order', () => {
    const { byRoute } = summarizeFluids(entries, day);
    expect(byRoute).toEqual([
      { route: 'Oral', direction: 'Intake', volumeMl: 300 },
      { route: 'IntravenousFluid', direction: 'Intake', volumeMl: 500 },
      { route: 'Urine', direction: 'Output', volumeMl: 550 },
    ]);
  });

  it('counts everything when no chart day is given', () => {
    expect(summarizeFluids(entries).intakeMl).toBe(1799);
  });

  it('keeps a running balance in time order', () => {
    const rows = runningBalance(entries.slice(0, 4).reverse());
    expect(rows.map((r) => r.balanceMl)).toEqual([200, 700, 400, 150]);
  });

  it('formats the balance with a sign and a word-free tone', () => {
    expect(formatBalance(1250)).toBe('+1,250 ml');
    expect(formatBalance(-200)).toBe('−200 ml');
    expect(formatBalance(0)).toBe('0 ml');
    expect(balanceTone(-1)).toBe('negative');
    expect(balanceTone(0)).toBe('even');
  });
});
