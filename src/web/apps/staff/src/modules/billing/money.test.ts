import { describe, expect, it } from 'vitest';
import {
  amountInputText,
  amountInWords,
  formatPercent,
  hasAtMostTwoDecimals,
  parsePercent,
  parseRupees,
  percentOfPaise,
  roundDiv,
  roundToRupee,
  scaleDecimal,
  splitGstPaise,
  subtractRupees,
  sumRupees,
  toPaise,
} from './money';

describe('exact decimal scaling (Money.Round)', () => {
  it.each([
    [10.005, 1001n],
    [1.005, 101n],
    [-1.005, -101n],
    [2.675, 268n],
    [0.1 + 0.2, 30n],
    [100.4, 10040n],
    [1e-7, 0n],
    [-0.004, 0n],
  ])('rounds %s to %s paise half away from zero', (value, expected) => {
    expect(toPaise(value)).toBe(expected);
  });

  it('keeps extra precision when asked', () => {
    expect(scaleDecimal(12.3456, 4)).toBe(123456n);
    expect(scaleDecimal(1.23e21, 0)).toBe(1_230_000_000_000_000_000_000n);
  });

  it('divides with half away from zero rounding', () => {
    expect(roundDiv(5n, 2n)).toBe(3n);
    expect(roundDiv(-5n, 2n)).toBe(-3n);
    expect(roundDiv(4n, 3n)).toBe(1n);
    expect(roundDiv(-4n, 3n)).toBe(-1n);
  });

  it('matches Money.PercentOf and Money.SplitGst', () => {
    expect(percentOfPaise(100_000n, 1_000n)).toBe(10_000n);
    expect(percentOfPaise(10_010n, 500n)).toBe(501n);
    expect(splitGstPaise(501n)).toEqual({ cgst: 251n, sgst: 250n });
    expect(splitGstPaise(-501n)).toEqual({ cgst: -251n, sgst: -250n });
  });

  it('rounds to the rupee half away from zero', () => {
    expect(roundToRupee(15_050n)).toBe(15_100n);
    expect(roundToRupee(-15_050n)).toBe(-15_100n);
    expect(roundToRupee(15_049n)).toBe(15_000n);
  });

  it('adds and subtracts rupees without floating point drift', () => {
    expect(sumRupees([0.1, 0.2])).toBe(0.3);
    expect(sumRupees([100.4, 50.35, -0.75])).toBe(150);
    expect(subtractRupees(2000, 1249.99)).toBe(750.01);
  });
});

describe('typed amounts', () => {
  it('reads rupees with grouping, the rupee sign and paise', () => {
    expect(parseRupees('1,250')).toMatchObject({ ok: true, paise: 125_000n, rupees: 1250 });
    expect(parseRupees('₹ 99.5')).toMatchObject({ ok: true, paise: 9_950n });
    expect(parseRupees('100.')).toMatchObject({ ok: true, paise: 10_000n });
  });

  it('refuses what the server refuses', () => {
    expect(parseRupees('')).toMatchObject({ ok: false });
    expect(parseRupees('0')).toEqual({ ok: false, error: 'Enter an amount greater than zero.' });
    expect(parseRupees('0', { allowZero: true })).toMatchObject({ ok: true, paise: 0n });
    expect(parseRupees('10.005')).toEqual({ ok: false, error: 'Use at most 2 decimal places.' });
    expect(parseRupees('-5')).toEqual({ ok: false, error: 'The amount cannot be negative.' });
    expect(parseRupees('100000001')).toEqual({ ok: false, error: 'The amount is too large.' });
    expect(parseRupees('12a')).toMatchObject({ ok: false });
  });

  it('reads percentages between 0 and 100', () => {
    expect(parsePercent('12.5')).toMatchObject({ ok: true, centi: 1_250n, percent: 12.5 });
    expect(parsePercent('10 %')).toMatchObject({ ok: true, percent: 10 });
    expect(parsePercent('100.01')).toMatchObject({ ok: false });
    expect(parsePercent('0')).toMatchObject({ ok: false });
    expect(parsePercent('0', { allowZero: true })).toMatchObject({ ok: true, percent: 0 });
  });

  it('checks decimals of numbers sent to the server', () => {
    expect(hasAtMostTwoDecimals(100.1)).toBe(true);
    expect(hasAtMostTwoDecimals(100.10000000000001)).toBe(false);
    expect(hasAtMostTwoDecimals(10.005)).toBe(false);
  });

  it('writes amounts back for inputs and percentages for labels', () => {
    expect(amountInputText(1250.5)).toBe('1250.50');
    expect(amountInputText(1250)).toBe('1250');
    expect(amountInputText(null)).toBe('');
    expect(formatPercent(12.5)).toBe('12.5%');
    expect(formatPercent(10)).toBe('10%');
  });
});

describe('amount in words (Indian system)', () => {
  it.each([
    [0, 'Rupees Zero Only'],
    [1, 'Rupees One Only'],
    [15, 'Rupees Fifteen Only'],
    [1250, 'Rupees One Thousand Two Hundred Fifty Only'],
    [123_456.78, 'Rupees One Lakh Twenty Three Thousand Four Hundred Fifty Six and Seventy Eight Paise Only'],
    [10_000_000, 'Rupees One Crore Only'],
    [12_34_56_789, 'Rupees Twelve Crore Thirty Four Lakh Fifty Six Thousand Seven Hundred Eighty Nine Only'],
    [-150.4, 'Minus Rupees One Hundred Fifty and Forty Paise Only'],
  ])('%s → %s', (value, words) => {
    expect(amountInWords(value)).toBe(words);
  });
});
