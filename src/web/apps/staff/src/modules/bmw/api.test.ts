import { describe, expect, it } from 'vitest';
import { bagProblems, handoverWeightProblem, parseBarcodes, weightMismatch, weightTolerance } from './api';

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

describe('the weighed handover', () => {
  it('flags a bag whose weight at handover differs beyond 5% (at least 100 g) from where it was filled', () => {
    expect(weightTolerance(1)).toBe(0.1);
    expect(weightTolerance(10)).toBe(0.5);
    expect(weightMismatch(2.45, 2.95)).toBe(true);
    expect(weightMismatch(2.45, 2.5)).toBe(false);
  });

  it('asks for every scanned bag to be weighed', () => {
    expect(handoverWeightProblem(['A', 'B'], { A: '1.2', B: '' })).toBe('Weigh every bag at the handover: B.');
    expect(handoverWeightProblem(['A'], { A: '60' })).toBe('Weigh every bag at the handover: A.');
    expect(handoverWeightProblem(['A'], { A: '0.8' })).toBeNull();
  });
});
