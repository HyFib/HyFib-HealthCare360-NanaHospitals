import { ApiError } from '@hc360/core';
import type { UseFormReturn } from 'react-hook-form';
import { describe, expect, it, vi } from 'vitest';
import { facilityField, showSaveError } from './saveErrors';

function setup(values: Record<string, unknown>) {
  const setError = vi.fn();
  const form = { getValues: () => values, setError } as unknown as UseFormReturn<Record<string, unknown>>;
  const notify = { success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() };
  const onConflict = vi.fn();
  return { form, notify, onConflict, setError };
}

describe('a failed save of a master', () => {
  it('puts field messages beside their fields, address parts included, focusing the first', () => {
    const { form, notify, onConflict, setError } = setup({ pinCode: '', logoFileId: null });
    const error = new ApiError({
      status: 400,
      title: 'Please correct the highlighted fields.',
      errors: { 'address.pinCode': ['PIN code must be 6 digits.'], logoFileId: ['Use a PNG, JPEG or WebP image.'] },
    });

    showSaveError(error, { form, notify, what: 'facility', onConflict, fieldFor: facilityField });

    expect(setError).toHaveBeenCalledWith(
      'pinCode',
      { type: 'server', message: 'PIN code must be 6 digits.' },
      { shouldFocus: true },
    );
    expect(setError).toHaveBeenCalledWith(
      'logoFileId',
      { type: 'server', message: 'Use a PNG, JPEG or WebP image.' },
      { shouldFocus: false },
    );
    expect(notify.error).not.toHaveBeenCalled();
  });

  it('puts a duplicate code on the code', () => {
    const { form, notify, onConflict, setError } = setup({ code: 'GM' });
    const error = new ApiError({
      status: 409,
      code: 'department.duplicate_code',
      title: 'Another department in this facility uses that code.',
    });

    showSaveError(error, { form, notify, what: 'department', onConflict });

    expect(setError).toHaveBeenCalledWith(
      'code',
      { type: 'server', message: 'Another department in this facility uses that code.' },
      { shouldFocus: true },
    );
  });

  it('explains the facility rules on "In use"', () => {
    const { form, notify, onConflict, setError } = setup({ isActive: false });
    const error = new ApiError({
      status: 422,
      code: 'facility.current',
      title: 'You are signed in to this facility. Sign in to another facility to take this one out of use.',
    });

    showSaveError(error, { form, notify, what: 'facility', onConflict });

    expect(setError.mock.calls[0]?.[0]).toBe('isActive');
  });

  it('warns that someone else changed the record, and refreshes it', () => {
    const { form, notify, onConflict } = setup({});

    showSaveError(new ApiError({ status: 409, code: 'concurrency', title: 'Changed' }), {
      form,
      notify,
      what: 'service',
      onConflict,
    });

    expect(notify.warning).toHaveBeenCalledWith('Someone else changed this service', expect.any(String));
    expect(onConflict).toHaveBeenCalled();
  });

  it('shows anything else as a toast, also a field the form does not have', () => {
    const { form, notify, onConflict } = setup({ name: '' });
    const unknownField = new ApiError({ status: 400, title: 'Invalid', errors: { timeZone: ['Unknown time zone.'] } });

    showSaveError(unknownField, { form, notify, what: 'facility', onConflict });
    showSaveError(new Error('offline'), { form, notify, what: 'facility', onConflict });

    expect(notify.error).toHaveBeenCalledTimes(2);
    expect(notify.error).toHaveBeenCalledWith(unknownField, 'Could not save the facility');
  });
});
