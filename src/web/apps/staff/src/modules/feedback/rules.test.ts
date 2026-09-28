import { describe, expect, it } from 'vitest';
import {
  arcPoint,
  complaintMoveLabel,
  complaintMoves,
  complaintNoteRequired,
  deadlineText,
  isLowScore,
  lowScorePriority,
  npsAngle,
  npsBand,
  npsGroup,
  npsScore,
  npsText,
  presetRange,
  ratingKey,
  resolvePeriod,
  toRatingsInput,
  turnaroundText,
} from './rules';

describe('NPS', () => {
  it('groups answers like the server', () => {
    expect(npsGroup(10)).toBe('Promoter');
    expect(npsGroup(9)).toBe('Promoter');
    expect(npsGroup(8)).toBe('Passive');
    expect(npsGroup(7)).toBe('Passive');
    expect(npsGroup(6)).toBe('Detractor');
    expect(npsGroup(0)).toBe('Detractor');
  });

  it('computes % promoters minus % detractors to one decimal', () => {
    expect(npsScore([])).toBeNull();
    expect(npsScore([10, 10, 9])).toBe(100);
    expect(npsScore([0, 3, 6])).toBe(-100);
    expect(npsScore([10, 8, 5])).toBe(0);
    // 2 promoters, 1 detractor of 3 → 33.33… → 33.3
    expect(npsScore([10, 9, 2])).toBe(33.3);
    // Out-of-range answers are ignored.
    expect(npsScore([10, 11, -1])).toBe(100);
  });

  it('rounds half away from zero, also for negative scores', () => {
    // 1 promoter, 3 detractors, 4 passives of 8 → −25
    expect(npsScore([10, 1, 2, 3, 7, 7, 8, 8])).toBe(-25);
    // 1 detractor of 16 → −6.25 → −6.3 (Math.round alone would give −6.2)
    const passives = Array.from({ length: 15 }, () => 7);
    expect(npsScore([1, ...passives])).toBe(-6.3);
    expect(npsScore([10, ...passives])).toBe(6.3);
    // never −0
    expect(Object.is(npsScore([7, 8]), 0)).toBe(true);
  });

  it('formats and bands the score', () => {
    expect(npsText(42.5)).toBe('+42.5');
    expect(npsText(-10)).toBe('−10');
    expect(npsText(0)).toBe('0');
    expect(npsText(null)).toBe('—');
    expect(npsBand(null).tone).toBe('informative');
    expect(npsBand(72).label).toBe('Excellent');
    expect(npsBand(30).label).toBe('Good');
    expect(npsBand(0).label).toBe('Needs improvement');
    expect(npsBand(-1).tone).toBe('danger');
  });

  it('maps the score onto a half-circle gauge', () => {
    expect(npsAngle(-100)).toBe(180);
    expect(npsAngle(0)).toBe(90);
    expect(npsAngle(100)).toBe(0);
    expect(npsAngle(250)).toBe(0);
    expect(arcPoint(100, 100, 50, 0)).toEqual({ x: 150, y: 100 });
    expect(arcPoint(100, 100, 50, 90)).toEqual({ x: 100, y: 50 });
    expect(arcPoint(100, 100, 50, 180)).toEqual({ x: 50, y: 100 });
  });
});

describe('ratings', () => {
  it('uses the camelCase keys of the server RatingsInput', () => {
    expect(ratingKey('FrontDesk')).toBe('frontDesk');
    expect(ratingKey('Overall')).toBe('overall');
  });

  it('keeps only answered aspects that are on the form', () => {
    expect(toRatingsInput(['Doctor', 'Billing'], { Doctor: 4, Billing: null, Food: 2 })).toEqual({ doctor: 4 });
    expect(toRatingsInput(['Doctor'], {})).toBeNull();
  });

  it('flags low scores and their priority like the server', () => {
    expect(isLowScore([5, 4], 9)).toBe(false);
    expect(isLowScore([5, 2], 9)).toBe(true);
    expect(isLowScore([], 6)).toBe(true);
    expect(isLowScore([null, undefined], null)).toBe(false);
    expect(lowScorePriority([2], 5)).toBe('Normal');
    expect(lowScorePriority([1], null)).toBe('High');
    expect(lowScorePriority([], 3)).toBe('High');
  });
});

describe('complaints', () => {
  it('allows only the server workflow', () => {
    expect(complaintMoves('Open')).toEqual(['InProgress', 'Resolved']);
    expect(complaintMoves('InProgress')).toEqual(['Resolved']);
    expect(complaintMoves('Resolved')).toEqual(['Closed', 'InProgress']);
    expect(complaintMoves('Closed')).toEqual([]);
  });

  it('asks for a note when resolving or reopening', () => {
    expect(complaintNoteRequired('Open', 'Resolved')).toBe(true);
    expect(complaintNoteRequired('Resolved', 'InProgress')).toBe(true);
    expect(complaintNoteRequired('Open', 'InProgress')).toBe(false);
    expect(complaintNoteRequired('Resolved', 'Closed')).toBe(false);
    expect(complaintMoveLabel('Resolved', 'InProgress')).toBe('Reopen');
    expect(complaintMoveLabel('Open', 'InProgress')).toBe('Start handling');
  });

  it('describes the deadline', () => {
    const now = Date.parse('2026-09-28T10:00:00Z');
    expect(deadlineText('2026-09-28T14:00:00Z', now)).toEqual({ text: '4 h left', overdue: false });
    expect(deadlineText('2026-09-28T09:30:00Z', now)).toEqual({ text: 'Overdue by 30 min', overdue: true });
    expect(deadlineText('2026-10-01T10:00:00Z', now)).toEqual({ text: '3 days left', overdue: false });
    expect(turnaroundText(5)).toBe('5 h');
    expect(turnaroundText(60)).toBe('2.5 days');
    expect(turnaroundText(null)).toBe('—');
  });
});

describe('periods', () => {
  it('builds calendar-month ranges', () => {
    expect(presetRange('thisMonth', '2026-09-28')).toEqual({ from: '2026-09-01', to: '2026-09-28' });
    expect(presetRange('lastMonth', '2026-03-15')).toEqual({ from: '2026-02-01', to: '2026-02-28' });
    expect(presetRange('lastMonth', '2026-01-10')).toEqual({ from: '2025-12-01', to: '2025-12-31' });
    expect(presetRange('last3Months', '2026-02-10')).toEqual({ from: '2025-12-01', to: '2026-02-10' });
    expect(presetRange('last12Months', '2026-09-28')).toEqual({ from: '2025-10-01', to: '2026-09-28' });
  });

  it('resolves presets and chosen dates', () => {
    expect(resolvePeriod(undefined, undefined, undefined, '2026-09-28')).toEqual({ from: '2025-10-01', to: '2026-09-28' });
    expect(resolvePeriod('thisMonth', '2020-01-01', '2020-02-01', '2026-09-28')).toEqual({ from: '2026-09-01', to: '2026-09-28' });
    expect(resolvePeriod('custom', '2026-05-01', '2026-05-31', '2026-09-28')).toEqual({ from: '2026-05-01', to: '2026-05-31' });
    expect(resolvePeriod('custom', '2026-05-31', '2026-05-01', '2026-09-28')).toEqual({ from: '2026-05-01', to: '2026-05-31' });
    expect(resolvePeriod('custom', '2026-05-01', undefined, '2026-09-28')).toEqual({ from: '2026-05-01', to: '2026-09-28' });
  });
});
