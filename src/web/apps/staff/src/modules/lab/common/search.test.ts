import { describe, expect, it } from 'vitest';
import { searchDate, searchFlag, searchId, searchInt, searchOneOf, searchPage, searchText } from './search';

describe('search param validators', () => {
  it('accepts only real calendar dates', () => {
    expect(searchDate('2026-09-26')).toBe('2026-09-26');
    expect(searchDate('2026-02-30')).toBeUndefined();
    expect(searchDate('26-09-2026')).toBeUndefined();
    expect(searchDate(20260926)).toBeUndefined();
  });

  it('keeps page numbers above one only', () => {
    expect(searchPage(3)).toBe(3);
    expect(searchPage('4')).toBe(4);
    expect(searchPage(1)).toBeUndefined();
    expect(searchPage(2.5)).toBeUndefined();
    expect(searchPage('x')).toBeUndefined();
  });

  it('accepts enum values from the allowed list', () => {
    expect(searchOneOf('Stat', ['Routine', 'Urgent', 'Stat'] as const)).toBe('Stat');
    expect(searchOneOf('stat', ['Routine', 'Urgent', 'Stat'] as const)).toBeUndefined();
  });

  it('validates ids, text, flags and integers', () => {
    expect(searchId('0b7c5a36-8a4e-4f7e-9d7a-2f8c0e1d9a11')).toBe('0b7c5a36-8a4e-4f7e-9d7a-2f8c0e1d9a11');
    expect(searchId('not-a-guid')).toBeUndefined();
    expect(searchText('  cbc ')).toBe('cbc');
    expect(searchText('')).toBeUndefined();
    expect(searchText('x'.repeat(101))).toBeUndefined();
    expect(searchText(123456789)).toBe('123456789');
    expect(searchFlag('true')).toBe(true);
    expect(searchFlag(false)).toBeUndefined();
    expect(searchInt('30', 7, 365)).toBe(30);
    expect(searchInt(5, 7, 365)).toBeUndefined();
  });
});
