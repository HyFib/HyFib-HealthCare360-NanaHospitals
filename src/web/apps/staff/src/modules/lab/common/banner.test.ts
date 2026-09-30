import { describe, expect, it } from 'vitest';
import { bannerFor, parseAge, parseAgeSex, parseAgeYears } from './banner';

describe('compact age parsing', () => {
  it('reads years, months and days', () => {
    expect(parseAge('34Y')).toBe(Math.round(34 * 365.25));
    expect(parseAge('34Y 2M')).toBe(Math.round(34 * 365.25 + 2 * 30.44));
    expect(parseAge('5M 12D')).toBe(Math.round(5 * 30.44 + 12));
    expect(parseAge('9D')).toBe(9);
    expect(parseAge('0D')).toBe(0);
  });

  it('returns null for unknown ages', () => {
    expect(parseAge(null)).toBeNull();
    expect(parseAge('-')).toBeNull();
    expect(parseAge('about forty')).toBeNull();
  });

  it('gives whole years', () => {
    expect(parseAgeYears('45Y 11M')).toBe(45);
    expect(parseAgeYears('11M 3D')).toBe(0);
    expect(parseAgeYears(undefined)).toBeNull();
  });

  it('splits the label age/sex text', () => {
    expect(parseAgeSex('34Y 2M / M')).toEqual({
      age: '34Y 2M',
      ageDays: Math.round(34 * 365.25 + 2 * 30.44),
      gender: 'Male',
    });
    expect(parseAgeSex('- / F')).toEqual({ age: null, ageDays: null, gender: 'Female' });
    expect(parseAgeSex('')).toEqual({ age: null, ageDays: null, gender: 'Unknown' });
  });

  it('builds banner data from a diagnostics record', () => {
    expect(
      bannerFor({ patientId: 'p1', uhid: 'NH26000001', patientName: 'Asha Rao', gender: 'Female', age: '31Y' }),
    ).toEqual({
      id: 'p1',
      uhid: 'NH26000001',
      fullName: 'Asha Rao',
      gender: 'Female',
      age: '31Y',
    });
  });
});
