import { describe, expect, it } from 'vitest';
import { inBatches, parsePriceSheet, revisePrice } from './tariffBulk';

describe('revisePrice', () => {
  it('raises and lowers prices by a percentage, rounded half away from zero', () => {
    expect(revisePrice(350, 10, 'paise')).toBe(385);
    expect(revisePrice(333.33, 7.5, 'paise')).toBe(358.33);
    expect(revisePrice(1000, -12.5, 'paise')).toBe(875);
  });

  it('rounds to the chosen step', () => {
    expect(revisePrice(333.33, 7.5, 'rupee')).toBe(358);
    expect(revisePrice(1234, 5, 'five')).toBe(1295);
    expect(revisePrice(1234, 5, 'ten')).toBe(1300);
    expect(revisePrice(102.5, 0, 'five')).toBe(105);
  });

  it('never goes below zero', () => {
    expect(revisePrice(100, -100, 'paise')).toBe(0);
  });
});

describe('parsePriceSheet', () => {
  it('reads tab, comma and space separated rows and skips a header', () => {
    const sheet = 'Code\tPrice\ncbc\t350\nLFT, 1,200.50\n\nTSH 499';
    expect(parsePriceSheet(sheet)).toEqual({
      rows: [
        { line: 2, code: 'CBC', price: 350 },
        { line: 3, code: 'LFT', price: 1200.5 },
        { line: 5, code: 'TSH', price: 499 },
      ],
      problems: [],
    });
  });

  it('names the line of every problem', () => {
    const { rows, problems } = parsePriceSheet('CBC\t350\nCBC\t360\nLFT\nESR\t12.345\nX-RAY CHEST\t400');
    expect(rows).toEqual([{ line: 1, code: 'CBC', price: 350 }]);
    expect(problems).toEqual([
      { line: 2, message: 'CBC is listed twice (also on line 1).' },
      { line: 3, message: 'LFT: the price is missing.' },
      { line: 4, message: 'ESR: Use at most 2 decimal places.' },
      { line: 5, message: '"X-RAY CHEST" is not a service code.' },
    ]);
  });

  it('allows a zero price (free service)', () => {
    expect(parsePriceSheet('REG-FU\t0').rows).toEqual([{ line: 1, code: 'REG-FU', price: 0 }]);
  });
});

describe('inBatches', () => {
  it('splits into server-sized batches', () => {
    expect(inBatches([1, 2, 3, 4, 5], 2)).toEqual([[1, 2], [3, 4], [5]]);
    expect(inBatches([], 2)).toEqual([]);
  });
});
