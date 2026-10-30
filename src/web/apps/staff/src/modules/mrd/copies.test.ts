import { describe, expect, it } from 'vitest';
import { fileSize, isCopyRequest, issueChoice, normalizeSha256, sha256Hex } from './copies';

const ABC = 'BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD';

describe('record copies', () => {
  it("works out a file's SHA-256 as the server keeps it", async () => {
    await expect(sha256Hex(new Blob(['abc']))).resolves.toBe(ABC);
    await expect(sha256Hex(new Blob([]))).resolves.toBe(
      'E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855',
    );
  });

  it('reads a SHA-256 however it was copied, and nothing else', () => {
    expect(normalizeSha256(ABC.toLowerCase())).toBe(ABC);
    expect(normalizeSha256(` ${ABC.match(/.{8}/g)!.join(' ')} `)).toBe(ABC);
    expect(normalizeSha256(ABC.match(/.{2}/g)!.join(':'))).toBe(ABC);
    expect(normalizeSha256(ABC.slice(1))).toBeNull();
    expect(normalizeSha256(`${ABC.slice(1)}G`)).toBeNull();
    expect(normalizeSha256('')).toBeNull();
  });

  it('gives a size people read', () => {
    expect(fileSize(512)).toBe('512 bytes');
    expect(fileSize(86_000)).toBe('84 KB');
    expect(fileSize(1_258_291)).toBe('1.2 MB');
  });

  it('lends a chart until the day chosen, and gives a copy, or documents, for keeps', () => {
    const report = { kind: 'diagnostics.lab-report', id: 'r1' };
    const ward = { isPatientRequest: false, purpose: 'PatientCare' } as const;
    const court = { isPatientRequest: false, purpose: 'Legal' } as const;
    const portal = { isPatientRequest: true, purpose: 'Other' } as const;

    expect(
      [ward, court, portal, { isPatientRequest: false, purpose: 'PatientCopy' } as const].map(isCopyRequest),
    ).toEqual([false, true, true, true]);
    expect(issueChoice(ward, [], '2026-11-06')).toEqual({ dueBackOn: '2026-11-06', documents: [] });
    expect(issueChoice(ward, [report], '2026-11-06')).toEqual({ dueBackOn: null, documents: [report] });
    expect(issueChoice(court, [], '2026-11-06')).toEqual({ dueBackOn: null, documents: [] });
    expect(issueChoice(portal, [report], '2026-11-06')).toEqual({ dueBackOn: null, documents: [report] });
  });
});
