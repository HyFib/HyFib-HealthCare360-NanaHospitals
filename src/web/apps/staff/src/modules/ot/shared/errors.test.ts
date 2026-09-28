import { ApiError } from '@hc360/core';
import { describe, expect, it, vi } from 'vitest';
import { applyFormErrors, errorMessage, isConcurrencyConflict, isRuleViolation, toFormPath } from './errors';

describe('toFormPath', () => {
  it.each([
    ['rows[0].heartRate', 'rows.0.heartRate'],
    ['items[12].remarks', 'items.12.remarks'],
    ['specimens[1].description', 'specimens.1.description'],
    ['procedureName', 'procedureName'],
  ])('%s → %s', (input, expected) => {
    expect(toFormPath(input)).toBe(expected);
  });
});

describe('applyFormErrors', () => {
  it('puts server messages on array fields using react-hook-form paths', () => {
    const setError = vi.fn();
    const error = new ApiError({
      status: 400,
      title: 'Please correct the highlighted fields.',
      errors: { 'rows[0].systolic': ['Enter a systolic pressure between 40 and 300 mmHg.'], lotNumber: ['Enter the lot or serial number.'] },
    });
    expect(applyFormErrors(error, setError)).toBe(true);
    expect(setError).toHaveBeenCalledWith('rows.0.systolic', { type: 'server', message: 'Enter a systolic pressure between 40 and 300 mmHg.' });
    expect(setError).toHaveBeenCalledWith('lotNumber', { type: 'server', message: 'Enter the lot or serial number.' });
  });

  it('ignores rule violations and conflicts', () => {
    const setError = vi.fn();
    expect(applyFormErrors(new ApiError({ status: 422, title: 'Cannot complete this action', code: 'surgery.phase_order' }), setError)).toBe(false);
    expect(applyFormErrors(new Error('boom'), setError)).toBe(false);
    expect(setError).not.toHaveBeenCalled();
  });
});

describe('error classification', () => {
  it('recognises optimistic concurrency conflicts only', () => {
    expect(isConcurrencyConflict(new ApiError({ status: 409, title: 'Conflict', code: 'concurrency' }))).toBe(true);
    expect(isConcurrencyConflict(new ApiError({ status: 409, title: 'Conflict', code: 'surgery.slot_conflict' }))).toBe(false);
  });

  it('recognises business rule violations', () => {
    expect(isRuleViolation(new ApiError({ status: 422, title: 'Cannot complete this action' }))).toBe(true);
    expect(isRuleViolation(new ApiError({ status: 400, title: 'Bad' }))).toBe(false);
  });

  it('picks the most useful message', () => {
    expect(errorMessage(new ApiError({ status: 422, title: 'Cannot complete this action', detail: 'Complete the WHO Sign-in first.' }))).toBe(
      'Complete the WHO Sign-in first.',
    );
    expect(errorMessage('x', 'fallback')).toBe('fallback');
  });
});
