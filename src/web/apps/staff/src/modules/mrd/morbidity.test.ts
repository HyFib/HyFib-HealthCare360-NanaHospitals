import { describe, expect, it } from 'vitest';
import { chartLabel, morbidityCsv, periodProblem, topRows, totals, type MorbidityRowLike } from './morbidity';

function row(partial: Partial<MorbidityRowLike> & Pick<MorbidityRowLike, 'key'>): MorbidityRowLike {
  return {
    title: partial.key,
    chapter: null,
    episodes: 1,
    patients: 1,
    male: 1,
    female: 0,
    otherOrUnknownGender: 0,
    age0To14: 0,
    age15To44: 1,
    age45To64: 0,
    age65Plus: 0,
    ageUnknown: 0,
    ...partial,
  };
}

describe('periodProblem', () => {
  it('accepts up to one year', () => {
    expect(periodProblem('2026-01-01', '2026-12-31')).toBeNull();
    expect(periodProblem('2024-01-01', '2024-12-31')).toBeNull();
  });

  it('rejects reversed, missing and too long periods', () => {
    expect(periodProblem('2026-02-01', '2026-01-01')).toContain('on or before');
    expect(periodProblem('', '2026-01-01')).toBe('Choose both dates.');
    expect(periodProblem('2025-01-01', '2026-01-02')).toContain('one year');
  });
});

describe('chart data', () => {
  it('keeps the most frequent groups', () => {
    const rows = [
      row({ key: 'A', episodes: 2 }),
      row({ key: 'B', episodes: 9 }),
      row({ key: 'C', episodes: 9, patients: 3 }),
    ];
    expect(topRows(rows, 2).map((r) => r.key)).toEqual(['C', 'B']);
  });

  it('shortens long labels', () => {
    expect(chartLabel({ key: 'E11.9', title: 'Type 2 diabetes mellitus without complications' }, 20)).toBe(
      'E11.9 Type 2 diabet…',
    );
    expect(chartLabel({ key: 'IV', title: 'IV' })).toBe('IV');
  });

  it('adds up totals', () => {
    expect(totals([row({ key: 'A', episodes: 2, female: 2, male: 0, age65Plus: 1 }), row({ key: 'B' })])).toEqual({
      episodes: 3,
      patients: 2,
      male: 1,
      female: 2,
      children: 0,
      elderly: 1,
    });
  });
});

describe('morbidityCsv', () => {
  it('writes a header and escapes commas and quotes', () => {
    const csv = morbidityCsv(
      [
        row({ key: 'J06.9', title: 'Acute upper respiratory infection, unspecified' }),
        row({ key: 'X', title: 'Say "hi"' }),
      ],
      'ICD-10 code',
    );
    const lines = csv.split('\r\n');
    expect(lines[0]).toBe(
      'ICD-10 code,Title,Chapter,Episodes,Patients,Male,Female,Other or unknown,Age 0-14,Age 15-44,Age 45-64,Age 65+,Age unknown',
    );
    expect(lines[1]).toBe('J06.9,"Acute upper respiratory infection, unspecified",,1,1,1,0,0,0,1,0,0,0');
    expect(lines[2]).toContain('"Say ""hi"""');
  });
});
