import { describe, expect, it } from 'vitest';
import { verificationLink } from './verification';

describe('verificationLink', () => {
  const origin = 'https://hc360.nana.local:7360';

  it('prints the public portal page when the server gives one', () => {
    expect(verificationLink('https://care.nanahospitals.in/portal/verify/lab/ABCD2345', '/api/v1/lab/reports/verify/ABCD2345', origin)).toBe(
      'https://care.nanahospitals.in/portal/verify/lab/ABCD2345',
    );
  });

  it('falls back to the verification path on this server', () => {
    expect(verificationLink(null, '/api/v1/public/prescriptions/verify/tok', origin)).toBe('https://hc360.nana.local:7360/api/v1/public/prescriptions/verify/tok');
    expect(verificationLink(undefined, '/api/v1/radiology/reports/verify/XYZ', origin)).toBe('https://hc360.nana.local:7360/api/v1/radiology/reports/verify/XYZ');
  });

  it('prints no QR code without either', () => {
    expect(verificationLink(null, null, origin)).toBeNull();
    expect(verificationLink('', '', origin)).toBeNull();
  });
});
