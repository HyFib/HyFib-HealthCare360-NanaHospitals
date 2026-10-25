import { describe, expect, it } from 'vitest';
import { irnProblems } from './einvoiceApi';

describe('irnProblems', () => {
  it('accepts the acknowledgement number and a signed QR code as the portal gives them', () => {
    expect(irnProblems('112610012345678', 'eyJhbGciOiJSUzI1NiJ9.eyJkYXRhIjoie30ifQ.c2ln')).toEqual([]);
  });

  it('asks for the digits and for the whole code', () => {
    expect(irnProblems('11261-001', 'not a code')).toEqual([
      'Enter the acknowledgement number: the 10 to 20 digits the portal gave.',
      'Paste the signed QR code exactly as the portal gave it (three parts separated by dots).',
    ]);
  });
});
