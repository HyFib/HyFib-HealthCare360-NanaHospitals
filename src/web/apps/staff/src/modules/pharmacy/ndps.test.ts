import { describe, expect, it } from 'vitest';
import { countProblem, signed } from './ndps';

describe('the shift count', () => {
  const items = [{ itemId: 'm' }, { itemId: 'f' }];

  it('counts every drug on the sheet, as a number not below zero', () => {
    expect(countProblem(items, { m: '12', f: '0' })).toBeNull();
    expect(countProblem(items, { m: '12' })).toBe('Count every drug on the sheet.');
    expect(countProblem(items, { m: '12', f: '-1' })).toBe('Enter each count as a number not below zero.');
    expect(countProblem(items, { m: 'twelve', f: '1' })).toBe('Enter each count as a number not below zero.');
  });

  it('shows the difference from the register with its sign', () => {
    expect(signed(2)).toBe('+2');
    expect(signed(-1)).toBe('-1');
    expect(signed(0)).toBe('0');
  });
});
