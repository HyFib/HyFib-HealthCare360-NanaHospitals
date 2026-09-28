import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
import { clampRange, matchPreset, MAX_CENSUS_DAYS, presetRange, shortDate, sumTransfers } from './census';
import { daysBetween } from './time';

// A fixed "now" (26 Sep 2026, noon IST). The functions under test take `today` as an argument and never read the clock;
// pinning the clock as well keeps the suite deterministic if that ever changes.
const today = '2026-09-26';

beforeAll(() => {
  vi.useFakeTimers({ now: new Date('2026-09-26T06:30:00Z'), toFake: ['Date'] });
});

afterAll(() => {
  vi.useRealTimers();
});

describe('census date ranges', () => {
  it('builds preset ranges ending today', () => {
    expect(presetRange('today', today)).toEqual({ from: today, to: today });
    expect(presetRange('7d', today)).toEqual({ from: '2026-09-20', to: today });
    expect(presetRange('30d', today)).toEqual({ from: '2026-08-28', to: today });
    expect(presetRange('month', today)).toEqual({ from: '2026-09-01', to: today });
  });

  it('recognises a preset from its dates', () => {
    expect(matchPreset('2026-09-20', today, today)).toBe('7d');
    expect(matchPreset('2026-09-19', today, today)).toBeNull();
  });

  it('keeps ranges valid for the API', () => {
    expect(clampRange('2026-09-30', '2026-10-05', today)).toEqual({ from: today, to: today });
    expect(clampRange('2026-09-30', '2026-09-10', today)).toEqual({ from: '2026-09-10', to: today });
    expect(clampRange('2026-09-25', '2026-09-20', today)).toEqual({ from: '2026-09-20', to: '2026-09-25' });
    const long = clampRange('2026-01-01', today, today);
    expect(long.to).toBe(today);
    expect(daysBetween(long.from, long.to) + 1).toBe(MAX_CENSUS_DAYS);
  });
});

describe('census helpers', () => {
  it('sums transfers across days', () => {
    const day = { date: today, admissions: 0, discharges: 0, deaths: 0, midnightCensus: 0 };
    expect(sumTransfers([{ ...day, transfersIn: 2, transfersOut: 1 }, { ...day, transfersIn: 1, transfersOut: 3 }])).toEqual({ transfersIn: 3, transfersOut: 4 });
  });

  it('formats short axis dates without shifting the day', () => {
    expect(shortDate('2026-09-26')).toMatch(/^26 Sep/);
    expect(shortDate('2026-01-01')).toMatch(/^1 Jan/);
    expect(shortDate('bad')).toBe('bad');
  });
});
