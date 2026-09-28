import { describe, expect, it } from 'vitest';
import {
  directionsText,
  dosesPerDay,
  durationText,
  frequencyOptionLabel,
  parseCustomFrequency,
  parseDose,
  parseStrengthMg,
  quantityToDispense,
  quantityUnit,
} from './dosing';

describe('parseDose', () => {
  it.each([
    ['1 tab', { value: 1, unit: 'count' }],
    ['2 caps', { value: 2, unit: 'count' }],
    ['½', { value: 0.5, unit: 'count' }],
    ['1½ tab', { value: 1.5, unit: 'count' }],
    ['500 mg', { value: 500, unit: 'mg' }],
    ['1 g', { value: 1000, unit: 'mg' }],
    ['250mcg', { value: 0.25, unit: 'mg' }],
    ['5 ml', { value: 5, unit: 'ml' }],
    ['2 puffs', { value: 2, unit: 'other' }],
    ['1/2 tab', { value: 0.5, unit: 'count' }],
  ])('%s', (input, expected) => {
    expect(parseDose(input)).toEqual(expected);
  });

  it('returns null for empty or unreadable text', () => {
    expect(parseDose('')).toBeNull();
    expect(parseDose('as directed')).toBeNull();
    expect(parseDose('1/0 tab')).toBeNull();
  });
});

describe('frequency', () => {
  it.each([
    ['1-0-1', 2],
    ['2-0-2', 4],
    ['1-1-1-1', 4],
    ['8 hourly', 3],
    ['q6h', 4],
    ['every 12 hours', 2],
    ['alternate day', 0.5],
    ['twice a week', 2 / 7],
    ['once a week', 1 / 7],
    ['daily', 1],
  ])('custom %s → %d a day', (text, expected) => {
    expect(parseCustomFrequency(text)).toBeCloseTo(expected, 6);
  });

  it('knows the standard frequencies and leaves SOS open', () => {
    expect(dosesPerDay('OD')).toBe(1);
    expect(dosesPerDay('BD')).toBe(2);
    expect(dosesPerDay('TDS')).toBe(3);
    expect(dosesPerDay('QID')).toBe(4);
    expect(dosesPerDay('HS')).toBe(1);
    expect(dosesPerDay('SOS')).toBeNull();
    expect(dosesPerDay('SOS', null, 3)).toBe(3);
    expect(dosesPerDay('Custom', 'whenever')).toBeNull();
  });

  it('labels options with their meaning', () => {
    expect(frequencyOptionLabel('BD')).toBe('BD – twice a day');
    expect(frequencyOptionLabel('Custom')).toContain('1-0-1');
  });
});

describe('parseStrengthMg', () => {
  it('reads single and combination strengths', () => {
    expect(parseStrengthMg('500 mg')).toEqual([500]);
    expect(parseStrengthMg('400 mg + 325 mg')).toEqual([400, 325]);
    expect(parseStrengthMg('400/325 mg')).toEqual([400, 325]);
    expect(parseStrengthMg('1 g')).toEqual([1000]);
  });

  it('ignores liquids, IU and percentages', () => {
    expect(parseStrengthMg('250 mg/5 ml')).toEqual([]);
    expect(parseStrengthMg('60000 IU')).toEqual([]);
    expect(parseStrengthMg('1%')).toEqual([]);
    expect(parseStrengthMg(null)).toEqual([]);
  });
});

describe('quantityToDispense', () => {
  it('multiplies units, doses a day and days', () => {
    expect(quantityToDispense('1 tab', '500 mg', 'BD', null, null, 5)).toBe(10);
    expect(quantityToDispense('2 tabs', null, 'TDS', null, null, 3)).toBe(18);
    expect(quantityToDispense('1 tab', null, 'Custom', '1-0-1', null, 7)).toBe(14);
  });

  it('converts a mg dose into tablets of the given strength, including halves', () => {
    expect(quantityToDispense('500 mg', '500 mg', 'TDS', null, null, 5)).toBe(15);
    expect(quantityToDispense('250 mg', '500 mg', 'BD', null, null, 5)).toBe(5);
    expect(quantityToDispense('300 mg', '500 mg', 'BD', null, null, 5)).toBeNull();
  });

  it('rounds up and avoids floating point surprises on weekly schedules', () => {
    expect(quantityToDispense('½ tab', null, 'OD', null, null, 5)).toBe(3);
    expect(quantityToDispense('1 tab', null, 'Weekly', null, null, 28)).toBe(4);
    expect(quantityToDispense('1 tab', null, 'Weekly', null, null, 30)).toBe(5);
    expect(quantityToDispense('1 tab', null, 'Custom', 'once a week', null, 28)).toBe(4);
  });

  it('dispenses a single dose for STAT', () => {
    expect(quantityToDispense('2 tabs', null, 'STAT', null, null, 1)).toBe(2);
  });

  it('leaves liquids, SOS and unreadable doses to the doctor', () => {
    expect(quantityToDispense('5 ml', null, 'TDS', null, null, 5)).toBeNull();
    expect(quantityToDispense('1 tab', null, 'SOS', null, null, 3)).toBeNull();
    expect(quantityToDispense('as directed', null, 'OD', null, null, 3)).toBeNull();
    expect(quantityToDispense('1 tab', null, 'OD', null, null, 0)).toBeNull();
  });
});

describe('directions', () => {
  it('builds readable one-line directions', () => {
    expect(directionsText({ dose: '1 tab', frequency: 'BD', durationDays: 5, instructions: 'After food' })).toBe('1 tab · twice a day (BD) · 5 days · after food');
    expect(directionsText({ dose: '1 tab', frequency: 'Custom', customFrequency: '1-0-1', durationDays: 28, route: 'Oral' })).toBe('1 tab · 1-0-1 · 4 weeks');
    expect(directionsText({ dose: '1 g', frequency: 'STAT', durationDays: 1, route: 'IV' })).toBe('1 g · immediately, once (STAT) · IV');
  });

  it('formats durations', () => {
    expect(durationText(1)).toBe('1 day');
    expect(durationText(10)).toBe('10 days');
    expect(durationText(21)).toBe('3 weeks');
    expect(durationText(0)).toBe('');
  });

  it('suggests a unit for the quantity', () => {
    expect(quantityUnit('Tablet', '1 tab')).toBe('tabs');
    expect(quantityUnit('Syrup', '5 ml')).toBe('bottles');
    expect(quantityUnit(null, '1 tab')).toBe('units');
  });
});
