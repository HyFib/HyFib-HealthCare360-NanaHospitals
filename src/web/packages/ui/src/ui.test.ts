import { ApiError } from '@hc360/core';
import { describe, expect, it, vi } from 'vitest';
import { statusTone } from './feedback/status';
import { applyServerErrors } from './forms/serverErrors';
import { barcodeSvg } from './print/barcode';

describe('statusTone', () => {
  it.each([
    ['Completed', 'success'],
    ['PartiallyPaid', 'warning'],
    ['in_progress', 'brand'],
    ['Cancelled', 'danger'],
    ['Something else', 'informative'],
  ])('%s -> %s', (status, tone) => {
    expect(statusTone(status)).toBe(tone);
  });
});

describe('applyServerErrors', () => {
  it('maps validation field errors onto the form', () => {
    const setError = vi.fn();
    const error = new ApiError({
      status: 400,
      title: 'Validation failed',
      code: 'validation',
      errors: { mobile: ['Enter a valid Indian mobile number'], 'address.pinCode': ['Invalid PIN code'] },
    });

    expect(applyServerErrors(error, setError)).toBe(true);
    expect(setError).toHaveBeenCalledWith('mobile', { type: 'server', message: 'Enter a valid Indian mobile number' });
    expect(setError).toHaveBeenCalledWith('address.pinCode', { type: 'server', message: 'Invalid PIN code' });
  });

  it('ignores non-validation errors', () => {
    const setError = vi.fn();
    expect(applyServerErrors(new ApiError({ status: 409, title: 'Conflict', code: 'duplicate' }), setError)).toBe(false);
    expect(applyServerErrors(new Error('boom'), setError)).toBe(false);
    expect(setError).not.toHaveBeenCalled();
  });
});

describe('barcodeSvg', () => {
  it('renders Code 128 and QR codes as SVG', async () => {
    expect(await barcodeSvg('NH26000123')).toMatch(/^<svg[\s\S]*<\/svg>\s*$/);
    expect(await barcodeSvg('https://hc360.local/verify/abc', 'qrcode')).toContain('<svg');
  });
});
