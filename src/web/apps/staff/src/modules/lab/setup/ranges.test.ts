import { describe, expect, it } from 'vitest';
import type { ReferenceRangeDto } from '../api';
import { ageBandLabel, emptyRange, fromDays, parseList, rangeProblem, rangeSummary, sortRanges, toDays } from './ranges';

const range = (partial: Partial<ReferenceRangeDto>): ReferenceRangeDto => ({ ...emptyRange(), ...partial });

describe('age band conversion', () => {
  it('converts like the seeded catalogue', () => {
    expect(toDays(18, 'years')).toBe(6574);
    expect(toDays(12, 'years')).toBe(4383);
    expect(toDays(6, 'years')).toBe(2192);
    expect(toDays(1, 'months')).toBe(30);
    expect(toDays(7, 'days')).toBe(7);
  });

  it('finds the friendliest unit back', () => {
    expect(fromDays(6574)).toEqual({ value: 18, unit: 'years' });
    expect(fromDays(30)).toEqual({ value: 1, unit: 'months' });
    expect(fromDays(7)).toEqual({ value: 7, unit: 'days' });
    expect(fromDays(0)).toEqual({ value: 0, unit: 'years' });
  });

  it('labels bands in words', () => {
    expect(ageBandLabel(0, null)).toBe('All ages');
    expect(ageBandLabel(6574, null)).toBe('18 years and above');
    expect(ageBandLabel(0, 30)).toBe('Below 1 month');
    expect(ageBandLabel(toDays(6, 'years'), toDays(12, 'years'))).toBe('6 years to 12 years');
    expect(ageBandLabel(1, 7)).toBe('1 day to 7 days');
  });
});

describe('interval checks and summaries', () => {
  it('mirrors the server rules', () => {
    expect(rangeProblem(range({ low: 13, high: 17, criticalLow: 7, criticalHigh: 20 }))).toBeNull();
    expect(rangeProblem(range({ ageFromDays: 100, ageToDays: 50 }))).toMatch(/age band/);
    expect(rangeProblem(range({ low: 20, high: 10 }))).toMatch(/lower limit/);
    expect(rangeProblem(range({ low: 13, high: 17, criticalLow: 13 }))).toMatch(/critical low/);
    expect(rangeProblem(range({ low: 13, high: 17, criticalHigh: 17 }))).toMatch(/critical high/);
    expect(rangeProblem(range({ normalValues: ['Negative'], criticalValues: ['negative'] }))).toMatch(/both normal and critical/);
  });

  it('summarises an interval on one line', () => {
    expect(rangeSummary(range({ gender: 'Male', ageFromDays: 6574, low: 13, high: 17, criticalLow: 7, criticalHigh: 20 }))).toBe(
      'Male · 18 years and above · 13 – 17 · critical below 7, above 20',
    );
    expect(rangeSummary(range({ normalValues: ['Non-reactive'], criticalValues: ['Reactive'] }))).toBe('Any sex · All ages · Non-reactive · critical Reactive');
    expect(rangeSummary(range({ high: 200 }))).toBe('Any sex · All ages · up to 200');
  });

  it('parses answer lists and sorts intervals', () => {
    expect(parseList('Negative, Non-reactive\nnegative, ')).toEqual(['Negative', 'Non-reactive']);
    const sorted = sortRanges([range({ gender: 'Female', ageFromDays: 10 }), range({ gender: 'Male' }), range({ ageFromDays: 5 }), range({})]);
    expect(sorted.map((r) => `${r.gender ?? 'Any'}:${r.ageFromDays}`)).toEqual(['Any:0', 'Any:5', 'Male:0', 'Female:10']);
  });
});
