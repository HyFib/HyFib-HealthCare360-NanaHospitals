import { describe, expect, it } from 'vitest';
import { documentLabel, irnProblems } from './einvoiceApi';

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

describe('documentLabel', () => {
  it('names an invoice by where it comes from', () => {
    expect(documentLabel({ number: 'NHOB260000123', source: 'bill', type: 'INV', against: null })).toBe(
      'NHOB260000123 (Bill)',
    );
  });

  it('names a credit note with the invoice it reduces', () => {
    expect(documentLabel({ number: 'NHCN26000007', source: 'creditnote', type: 'CRN', against: 'NHOB260000123' })).toBe(
      'NHCN26000007 (Credit note against NHOB260000123)',
    );
    expect(
      documentLabel({ number: 'NHPHR26000004', source: 'pharmacyreturn', type: 'CRN', against: 'NHPH260000456' }),
    ).toBe('NHPHR26000004 (Pharmacy return against NHPH260000456)');
  });
});
