import { describe, expect, it } from 'vitest';
import { dammCheckDigit, dammVerifies } from './damm';

describe('dammCheckDigit', () => {
  it("works out Damm's digit, as the server does", () => {
    expect(dammCheckDigit('572')).toBe('4');
    expect(dammCheckDigit('11294')).toBe('6');
    expect(dammCheckDigit('100000000')).toBe('5');
    expect(dammCheckDigit('100001234')).toBe('2');
    expect(dammCheckDigit('NH/26-000124')).toBe(dammCheckDigit('26000124'));
  });
});

describe('dammVerifies', () => {
  it('knows a number that ends in its check digit', () => {
    expect(dammVerifies('5724')).toBe(true);
    expect(dammVerifies('5727')).toBe(false);
    expect(dammVerifies('112946')).toBe(true);
    expect(dammVerifies('112949')).toBe(false);
    expect(dammVerifies('1000012342')).toBe(true);
    expect(dammVerifies('0')).toBe(false);
    expect(dammVerifies('')).toBe(false);
  });

  it('catches every wrong digit and every swap of two neighbouring digits', () => {
    for (const running of ['100001234', '271828182', '999999998', '123456789']) {
      const number = running + dammCheckDigit(running);
      expect(dammVerifies(number)).toBe(true);
      for (let place = 0; place < number.length; place++) {
        for (const digit of '0123456789') {
          if (digit !== number[place]) {
            expect(dammVerifies(number.slice(0, place) + digit + number.slice(place + 1))).toBe(false);
          }
        }
      }
      for (let place = 0; place + 1 < number.length; place++) {
        if (number[place] !== number[place + 1]) {
          const swapped = number.slice(0, place) + number[place + 1] + number[place] + number.slice(place + 2);
          expect(dammVerifies(swapped)).toBe(false);
        }
      }
    }
  });
});
