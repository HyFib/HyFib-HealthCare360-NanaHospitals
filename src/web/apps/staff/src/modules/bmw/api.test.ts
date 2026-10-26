import { describe, expect, it } from 'vitest';
import { bagProblems, parseBarcodes } from './api';

describe('a waste bag', () => {
  it('needs its colour, where it came from, and a weight above 0 and at most 50 kg', () => {
    expect(bagProblems({ category: 'Yellow', source: 'Ward 3', weight: '2.45' })).toEqual({
      category: null,
      source: null,
      weight: null,
    });
    expect(bagProblems({ category: '', source: ' ', weight: '0' })).toEqual({
      category: 'Choose the colour of the bag.',
      source: 'Say where the waste came from.',
      weight: 'Weigh the bag: more than 0 and at most 50 kg.',
    });
    expect(bagProblems({ category: 'Red', source: 'OT', weight: '51' }).weight).not.toBeNull();
  });
});

describe('the handover', () => {
  it('reads scanned barcodes one per line or separated by commas, each once and in capitals', () => {
    expect(parseBarcodes('nhbw260000001\nNHBW260000002, NHBW260000001  ')).toEqual(['NHBW260000001', 'NHBW260000002']);
    expect(parseBarcodes('  ')).toEqual([]);
  });
});
