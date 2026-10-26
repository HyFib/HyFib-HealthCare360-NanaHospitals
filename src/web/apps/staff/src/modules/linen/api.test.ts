import { describe, expect, it } from 'vitest';
import { countedLines, disinfects } from './api';

describe('a wash batch', () => {
  it('disinfects at 71 °C held 3 minutes or 65 °C held 10', () => {
    expect(disinfects(71, 3)).toBe(true);
    expect(disinfects(65, 10)).toBe(true);
    expect(disinfects(70, 9)).toBe(false);
    expect(disinfects(90, 2)).toBe(false);
    expect(disinfects(null, 10)).toBe(false);
  });
});

describe('a count', () => {
  it('keeps the items counted, in whole pieces', () => {
    expect(countedLines({ a: '12', b: '', c: '0', d: '2.5' })).toEqual([{ linenItemId: 'a', quantity: 12 }]);
  });
});
