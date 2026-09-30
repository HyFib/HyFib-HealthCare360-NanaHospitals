import { describe, expect, it } from 'vitest';
import { assignLanes, hourMarks, utilisation, visibleRange } from './calendarLayout';

describe('assignLanes', () => {
  it('gives non-overlapping items the full width', () => {
    const lanes = assignLanes([
      { id: 'a', start: 540, end: 555 },
      { id: 'b', start: 555, end: 570 },
    ]);
    expect(lanes.get('a')).toEqual({ lane: 0, lanes: 1 });
    expect(lanes.get('b')).toEqual({ lane: 0, lanes: 1 });
  });

  it('places overlapping items side by side and reuses freed columns', () => {
    const lanes = assignLanes([
      { id: 'a', start: 540, end: 570 },
      { id: 'b', start: 540, end: 555 },
      { id: 'c', start: 555, end: 570 },
      { id: 'd', start: 600, end: 615 },
    ]);
    expect(lanes.get('a')).toEqual({ lane: 0, lanes: 2 });
    expect(lanes.get('b')).toEqual({ lane: 1, lanes: 2 });
    expect(lanes.get('c')).toEqual({ lane: 1, lanes: 2 });
    expect(lanes.get('d')).toEqual({ lane: 0, lanes: 1 });
  });

  it('handles three bookings in one group slot', () => {
    const lanes = assignLanes([
      { id: 'x', start: 600, end: 615 },
      { id: 'y', start: 600, end: 615 },
      { id: 'z', start: 600, end: 615 },
    ]);
    expect([...lanes.values()].map((p) => p.lanes)).toEqual([3, 3, 3]);
    expect(new Set([...lanes.values()].map((p) => p.lane)).size).toBe(3);
  });
});

describe('visibleRange', () => {
  it('rounds out to whole hours', () => {
    expect(
      visibleRange([
        { start: 555, end: 780 },
        { start: 1020, end: 1140 },
      ]),
    ).toEqual({ start: 540, end: 1140 });
    expect(visibleRange([{ start: 545, end: 560 }])).toEqual({ start: 540, end: 600 });
  });

  it('falls back to a working day when there is nothing to show', () => {
    expect(visibleRange([])).toEqual({ start: 480, end: 1200 });
  });

  it('lists the hour marks inside the range', () => {
    expect(hourMarks({ start: 540, end: 780 })).toEqual([540, 600, 660, 720]);
  });
});

describe('utilisation', () => {
  it('sums seats and rounds the percentage', () => {
    expect(
      utilisation([
        { capacity: 1, booked: 1 },
        { capacity: 1, booked: 0 },
        { capacity: 1, booked: 0 },
      ]),
    ).toEqual({ capacity: 3, booked: 1, percent: 33 });
    expect(utilisation([])).toEqual({ capacity: 0, booked: 0, percent: 0 });
    expect(utilisation([{ capacity: 1, booked: 2 }]).percent).toBe(200);
  });
});
