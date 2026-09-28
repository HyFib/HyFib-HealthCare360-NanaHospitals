import { describe, expect, it } from 'vitest';
import { busiestTests, bySection, complianceTone, formatMinutes, rejectionRate, toHours, withinTargetPercent } from './tat';

describe('turnaround formatting', () => {
  it('uses minutes, hours and days', () => {
    expect(formatMinutes(0)).toBe('0 min');
    expect(formatMinutes(44.6)).toBe('45 min');
    expect(formatMinutes(120)).toBe('2 h');
    expect(formatMinutes(125)).toBe('2 h 5 min');
    expect(formatMinutes(24 * 60 + 185)).toBe('1 d 3 h');
    expect(formatMinutes(48 * 60)).toBe('2 d');
    expect(formatMinutes(null)).toBe('—');
    expect(formatMinutes(-5)).toBe('—');
  });

  it('converts to hours with one decimal', () => {
    expect(toHours(90)).toBe(1.5);
    expect(toHours(100)).toBe(1.7);
    expect(toHours(null)).toBeNull();
  });
});

describe('turnaround aggregates', () => {
  const rows = [
    { testName: 'CBC', section: 'Hematology' as const, released: 40, withinTarget: 38 },
    { testName: 'LFT', section: 'Biochemistry' as const, released: 20, withinTarget: 14 },
    { testName: 'KFT', section: 'Biochemistry' as const, released: 20, withinTarget: 18 },
    { testName: 'Culture', section: 'Microbiology' as const, released: 0, withinTarget: 0 },
  ];

  it('weights target compliance by volume', () => {
    expect(withinTargetPercent(rows)).toBe(87.5);
    expect(withinTargetPercent([])).toBeNull();
  });

  it('groups by bench', () => {
    expect(bySection(rows)).toEqual([
      { section: 'Hematology', released: 40, withinTarget: 38, percent: 95 },
      { section: 'Biochemistry', released: 40, withinTarget: 32, percent: 80 },
      { section: 'Microbiology', released: 0, withinTarget: 0, percent: null },
    ]);
  });

  it('picks the busiest tests with released results', () => {
    expect(busiestTests(rows, 2).map((r) => r.testName)).toEqual(['CBC', 'KFT']);
    expect(busiestTests(rows, 10)).toHaveLength(3);
  });

  it('computes the rejection rate and compliance tone', () => {
    expect(rejectionRate(400, 6)).toBe(1.5);
    expect(rejectionRate(0, 0)).toBeNull();
    expect(complianceTone(95)).toBe('good');
    expect(complianceTone(80)).toBe('warning');
    expect(complianceTone(60)).toBe('bad');
    expect(complianceTone(null)).toBe('none');
  });
});
