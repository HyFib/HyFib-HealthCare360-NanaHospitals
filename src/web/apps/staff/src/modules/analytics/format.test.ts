import { describe, expect, it } from 'vitest';
import {
  formatAxisValue,
  formatBytes,
  formatCategory,
  formatCount,
  formatInrCompact,
  formatKpi,
  formatMinutes,
  formatMonth,
  formatShortDate,
  formatValue,
  kpiChange,
  toNumber,
} from './format';

describe('report values', () => {
  it('formats each column type the way hospital staff read it', () => {
    expect(formatValue(1234567, 'Integer')).toBe('12,34,567');
    expect(formatValue(12.345, 'Decimal')).toBe('12.35');
    expect(formatValue(123456.5, 'Money')).toMatch(/1,23,456\.50/);
    expect(formatValue(45.25, 'Percent')).toBe('45.3%');
    expect(formatValue(3.25, 'Days')).toBe('3.3');
    expect(formatValue(3.25, 'Days', 'figure')).toBe('3.3 days');
    expect(formatValue(42, 'Minutes')).toBe('42 min');
    expect(formatValue('Cardiology', 'Text')).toBe('Cardiology');
    expect(formatValue(true, 'Text')).toBe('Yes');
  });

  it('shows blanks as a dash and never throws on odd input', () => {
    expect(formatValue(null, 'Money')).toBe('—');
    expect(formatValue(undefined, 'Integer')).toBe('—');
    expect(formatValue('', 'Text')).toBe('—');
    expect(formatValue('n/a', 'Integer')).toBe('n/a');
  });

  it('reads calendar dates and months without shifting the day', () => {
    expect(formatValue('2026-09-01', 'Date')).toMatch(/^01 Sept? 2026$/);
    expect(formatMonth('2026-09-01')).toMatch(/^Sept? 2026$/);
    expect(formatShortDate('2026-01-31')).toMatch(/^31 Jan$/);
    expect(formatCategory('2026-03-05', 'Date')).toMatch(/^5 Mar$/);
    expect(formatCategory('Cardiology', 'Text')).toBe('Cardiology');
    expect(formatMonth('not a date')).toBe('not a date');
  });

  it('converts numeric cells only', () => {
    expect(toNumber(12)).toBe(12);
    expect(toNumber('12.5')).toBe(12.5);
    expect(toNumber('Cardiology')).toBeNull();
    expect(toNumber(null)).toBeNull();
    expect(toNumber(Number.NaN)).toBeNull();
  });
});

describe('turnaround minutes', () => {
  it('uses minutes, then hours, then days', () => {
    expect(formatMinutes(42.5)).toBe('42.5 min');
    expect(formatMinutes(125)).toBe('2 h 05 min');
    expect(formatMinutes(3 * 1440 + 240)).toBe('3 d 4 h');
    expect(formatMinutes(-30)).toBe('−30 min');
  });
});

describe('rupees', () => {
  it('abbreviates in thousands, lakhs and crores', () => {
    expect(formatInrCompact(950)).toBe('₹950');
    expect(formatInrCompact(8_500)).toBe('₹8.5 K');
    expect(formatInrCompact(1_234_567)).toBe('₹12.35 L');
    expect(formatInrCompact(123_456_789)).toBe('₹12.35 Cr');
    expect(formatInrCompact(-250_000)).toBe('−₹2.5 L');
    expect(formatInrCompact(null)).toBe('—');
  });

  it('keeps axis labels short', () => {
    expect(formatAxisValue(2_500_000, 'Money')).toBe('₹25 L');
    expect(formatAxisValue(45, 'Percent')).toBe('45%');
    expect(formatAxisValue(30, 'Minutes')).toBe('30 min');
    expect(formatAxisValue(1200, 'Integer')).toBe('1,200');
  });
});

describe('KPIs', () => {
  it('formats a KPI in its unit', () => {
    expect(formatKpi(1234, 'count')).toBe('1,234');
    expect(formatKpi(78.44, 'percent')).toBe('78.4%');
    expect(formatKpi(4.2, 'days')).toBe('4.2 days');
    expect(formatKpi(3_456_789, 'inr')).toBe('₹34.57 L');
    expect(formatKpi(42, 'score')).toBe('+42');
    expect(formatKpi(-5, 'score')).toBe('−5');
    expect(formatKpi(null, 'count')).toBe('—');
  });

  it('judges a rise as good or bad by the KPI direction', () => {
    const visits = kpiChange({ value: 1100, previous: 1000, changePercent: 10, higherIsBetter: true, unit: 'count' });
    expect(visits).toMatchObject({ text: '+10%', direction: 'up', tone: 'good' });
    expect(visits?.description).toContain('better');

    const alos = kpiChange({ value: 5, previous: 4, changePercent: 25, higherIsBetter: false, unit: 'days' });
    expect(alos).toMatchObject({ text: '+25%', direction: 'up', tone: 'bad' });
  });

  it('shows rates and scores as a change in points', () => {
    const occupancy = kpiChange({ value: 82.5, previous: 80, changePercent: 3.1, higherIsBetter: true, unit: 'percent' });
    expect(occupancy).toMatchObject({ text: '+2.5 pts', tone: 'good' });
    const nps = kpiChange({ value: 30, previous: 40, changePercent: -25, higherIsBetter: true, unit: 'score' });
    expect(nps).toMatchObject({ text: '−10 pts', direction: 'down', tone: 'bad' });
  });

  it('has no change without a previous value, and a flat change is neutral', () => {
    expect(kpiChange({ value: 10, previous: null, changePercent: null, higherIsBetter: true, unit: 'count' })).toBeNull();
    expect(kpiChange({ value: 10, previous: 10, changePercent: 0, higherIsBetter: true, unit: 'count' })).toMatchObject({ tone: 'neutral', direction: 'flat' });
  });
});

describe('small helpers', () => {
  it('formats sizes and counts', () => {
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(2048)).toBe('2 KB');
    expect(formatBytes(1_572_864)).toBe('1.5 MB');
    expect(formatCount(1, 'row')).toBe('1 row');
    expect(formatCount(1200, 'row')).toBe('1,200 rows');
  });
});
