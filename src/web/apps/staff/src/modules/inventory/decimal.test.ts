import { describe, expect, it } from 'vitest';
import { dec, divRound, mul, round2, roundTo, subtractNumbers, sum, sumNumbers, toNumber, toText } from './decimal';

describe('decimal', () => {
  it('adds without binary floating-point drift', () => {
    expect(toNumber(dec(0.1) + dec(0.2))).toBe(0.3);
    expect(sumNumbers([0.1, 0.2, 0.3])).toBe(0.6);
    expect(subtractNumbers(1, 0.9)).toBe(0.1);
  });

  it('rounds half away from zero like C# MidpointRounding.AwayFromZero', () => {
    expect(toText(round2(dec('2.345')))).toBe('2.35');
    expect(toText(round2(dec('-2.345')))).toBe('-2.35');
    expect(toText(round2(dec('2.3449')))).toBe('2.34');
    expect(toText(roundTo(dec('10.5'), 0))).toBe('11');
    expect(toText(roundTo(dec('-10.5'), 0))).toBe('-11');
    // 1.005 * 100 is 100.49999… in floating point; decimal arithmetic keeps the half.
    expect(toText(round2(dec(1.005)))).toBe('1.01');
  });

  it('multiplies exactly', () => {
    expect(toText(mul(dec('0.467'), dec(5)))).toBe('2.335');
    expect(toText(round2(mul(dec('0.467'), dec(5))))).toBe('2.34');
    expect(toText(mul(dec('12.345'), dec('3.1234')))).toBe('38.558373');
  });

  it('divides as an exact rational before rounding', () => {
    expect(toText(divRound(dec(1), dec(3), 2))).toBe('0.33');
    expect(toText(divRound(dec(2), dec(3), 2))).toBe('0.67');
    expect(toText(divRound(dec(-1), dec(8), 2))).toBe('-0.13');
    expect(() => divRound(dec(1), dec(0), 2)).toThrow(RangeError);
  });

  it('parses numbers and strings, including exponent notation', () => {
    expect(toText(dec('12.50'))).toBe('12.5');
    expect(toText(dec(1e-7))).toBe('0.0000001');
    expect(toText(dec('-.5'))).toBe('-0.5');
    expect(toText(dec('0.000000015'))).toBe('0.00000002');
    expect(dec(null)).toBe(0n);
    expect(dec('')).toBe(0n);
    expect(() => dec('abc')).toThrow(RangeError);
    expect(() => dec('.')).toThrow(RangeError);
  });

  it('formats with fixed decimals and never prints negative zero', () => {
    expect(toText(dec('12.5'), 2)).toBe('12.50');
    expect(toText(dec('-0.001'), 2)).toBe('0.00');
    expect(toText(sum([dec(1), dec('2.25')]), 3)).toBe('3.250');
  });
});
