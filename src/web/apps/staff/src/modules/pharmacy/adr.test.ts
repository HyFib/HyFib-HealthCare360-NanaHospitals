import { describe, expect, it } from 'vitest';
import { adrProblems, blankDrug, seriousnessNames, seriousnessValue } from './adr';

describe('a suspected adverse drug reaction report', () => {
  const today = '2026-10-25';
  const suspect = { ...blankDrug(true), name: 'Ceftriaxone 1 g Injection' };
  const base = {
    patientId: 'p1',
    reaction: 'Urticaria',
    startedOn: '2026-10-24',
    stoppedOn: '',
    learnedOn: '2026-10-24',
    outcome: 'Recovering' as const,
    seriousness: [] as string[],
    drugs: [suspect],
    today,
  };

  it('needs a suspected drug and its dates in order', () => {
    expect(Object.values(adrProblems(base)).every((p) => p === null)).toBe(true);
    expect(adrProblems({ ...base, drugs: [{ ...suspect, suspected: false }] }).drugs).toBe(
      'Name at least one suspected drug.',
    );
    expect(adrProblems({ ...base, stoppedOn: '2026-10-20' }).stoppedOn).not.toBeNull();
    expect(adrProblems({ ...base, learnedOn: '2026-10-20' }).learnedOn).not.toBeNull();
  });

  it('keeps a fatal outcome and death as the seriousness together', () => {
    expect(adrProblems({ ...base, outcome: 'Fatal' }).outcome).not.toBeNull();
    expect(adrProblems({ ...base, outcome: 'Fatal', seriousness: ['Death'] }).outcome).toBeNull();
  });

  it('sends the seriousness as the server names it', () => {
    expect(seriousnessValue([])).toBe('None');
    expect(seriousnessValue(['Hospitalisation', 'RequiredIntervention'])).toBe('Hospitalisation, RequiredIntervention');
    expect(seriousnessNames('Hospitalisation, RequiredIntervention')).toEqual([
      'Hospitalisation',
      'RequiredIntervention',
    ]);
    expect(seriousnessNames('None')).toEqual([]);
  });
});
