import { describe, expect, it } from 'vitest';
import { bradenScore, morseScore, painScore, painScoreOptions, riskTone, type BradenInput, type MorseInput } from './riskScales';

const noRisk: MorseInput = {
  historyOfFalling: false,
  secondaryDiagnosis: false,
  ambulatoryAid: 'None',
  intravenousAccess: false,
  gait: 'Normal',
  overestimatesAbility: false,
};

describe('Morse fall scale', () => {
  it('scores 0 for a patient with no risk factors', () => {
    expect(morseScore(noRisk)).toMatchObject({ score: 0, risk: 'Low' });
  });

  it('adds the item points like the server (maximum 125)', () => {
    const all: MorseInput = {
      historyOfFalling: true,
      secondaryDiagnosis: true,
      ambulatoryAid: 'Furniture',
      intravenousAccess: true,
      gait: 'Impaired',
      overestimatesAbility: true,
    };
    expect(morseScore(all)).toMatchObject({ score: 125, risk: 'High' });
  });

  it('uses the 25 and 45 cut-offs', () => {
    expect(morseScore({ ...noRisk, historyOfFalling: true }).risk).toBe('Moderate');
    expect(morseScore({ ...noRisk, historyOfFalling: true, intravenousAccess: true }).risk).toBe('High');
    expect(morseScore({ ...noRisk, secondaryDiagnosis: true, gait: 'Weak' }).risk).toBe('Moderate');
    expect(morseScore({ ...noRisk, ambulatoryAid: 'CrutchesCaneWalker' })).toMatchObject({ score: 15, risk: 'Low' });
  });
});

describe('Braden scale', () => {
  const best: BradenInput = { sensoryPerception: 4, moisture: 4, activity: 4, mobility: 4, nutrition: 4, frictionShear: 3 };

  it('scores 23 and no risk for a fully independent patient', () => {
    expect(bradenScore(best)).toMatchObject({ score: 23, risk: 'None' });
  });

  it.each([
    [19, 'None'],
    [18, 'Low'],
    [15, 'Low'],
    [14, 'Moderate'],
    [13, 'Moderate'],
    [12, 'High'],
    [10, 'High'],
    [9, 'VeryHigh'],
    [6, 'VeryHigh'],
  ])('a total of %i is %s risk', (total, risk) => {
    // Lower subscales one point at a time from the best score until the total is reached.
    const input = { ...best };
    const keys = Object.keys(input) as (keyof BradenInput)[];
    let current = 23;
    while (current > total) {
      const key = keys.find((k) => input[k] > 1)!;
      input[key] -= 1;
      current -= 1;
    }
    expect(bradenScore(input)).toMatchObject({ score: total, risk });
  });

  it('rejects out-of-range subscale scores', () => {
    expect(() => bradenScore({ ...best, frictionShear: 4 })).toThrow(RangeError);
    expect(() => bradenScore({ ...best, moisture: 0 })).toThrow(RangeError);
  });
});

describe('pain score', () => {
  it.each([
    [0, 'None'],
    [3, 'Low'],
    [4, 'Moderate'],
    [6, 'Moderate'],
    [7, 'High'],
    [10, 'High'],
  ])('%i is %s', (score, risk) => {
    expect(painScore(score).risk).toBe(risk);
  });

  it('rejects scores outside 0–10', () => {
    expect(() => painScore(11)).toThrow(RangeError);
    expect(() => painScore(-1)).toThrow(RangeError);
  });

  it('offers only even scores on the Wong-Baker faces scale', () => {
    expect(painScoreOptions('WongBakerFaces')).toEqual([0, 2, 4, 6, 8, 10]);
    expect(painScoreOptions('Numeric')).toHaveLength(11);
  });

  it('maps risk to badge tones', () => {
    expect(riskTone('None')).toBe('success');
    expect(riskTone('VeryHigh')).toBe('danger');
  });
});
