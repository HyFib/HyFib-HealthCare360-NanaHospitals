import { describe, expect, it } from 'vitest';
import { doctorWaitBreached, formatMinutes } from './api';

describe('formatMinutes', () => {
  it('shows minutes, then hours and minutes', () => {
    expect(formatMinutes(0)).toBe('0 min');
    expect(formatMinutes(35)).toBe('35 min');
    expect(formatMinutes(125)).toBe('2 h 05 min');
  });
});

describe('doctorWaitBreached', () => {
  it('flags level 1 at once and level 2 after ten minutes without a doctor', () => {
    expect(doctorWaitBreached(1, 0, false)).toBe(true);
    expect(doctorWaitBreached(2, 10, false)).toBe(false);
    expect(doctorWaitBreached(2, 11, false)).toBe(true);
    expect(doctorWaitBreached(2, 40, true)).toBe(false);
    expect(doctorWaitBreached(3, 90, false)).toBe(false);
    expect(doctorWaitBreached(null, 90, false)).toBe(false);
  });
});
