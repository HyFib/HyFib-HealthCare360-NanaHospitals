import { describe, expect, it } from 'vitest';
import { addLine, applySuggestions, codingProblems, removeLine, setPrimary, toRequestLines, verification, type CodingLine } from './coding';

const dm = { kind: 'Diagnosis' as const, code: 'e11.9', title: 'Type 2 diabetes mellitus without complications' };
const htn = { kind: 'Diagnosis' as const, code: 'I10', title: 'Essential (primary) hypertension' };
const appendectomy = { kind: 'Procedure' as const, code: '0DTJ4ZZ', title: 'Resection of appendix, laparoscopic' };

describe('coding lines', () => {
  it('makes the first diagnosis primary and ignores repeats', () => {
    let lines: CodingLine[] = [];
    lines = addLine(lines, appendectomy);
    lines = addLine(lines, dm);
    lines = addLine(lines, htn);
    lines = addLine(lines, { ...dm, code: 'E11.9' });
    expect(lines.map((l) => [l.code, l.isPrimary])).toEqual([
      ['0DTJ4ZZ', false],
      ['E11.9', true],
      ['I10', false],
    ]);
  });

  it('moves the primary flag and keeps procedures non-primary', () => {
    let lines = addLine(addLine([], dm), htn);
    lines = setPrimary(lines, htn);
    expect(lines.filter((l) => l.isPrimary).map((l) => l.code)).toEqual(['I10']);
    expect(setPrimary(addLine(lines, appendectomy), appendectomy).filter((l) => l.isPrimary).map((l) => l.code)).toEqual(['I10']);
  });

  it('hands the primary flag on when the primary is removed', () => {
    const lines = removeLine(addLine(addLine([], dm), htn), { kind: 'Diagnosis', code: 'E11.9' });
    expect(lines).toEqual([{ ...htn, isPrimary: true }]);
  });

  it('orders the request primary first, procedures last', () => {
    const lines = setPrimary(addLine(addLine(addLine([], appendectomy), dm), htn), htn);
    expect(toRequestLines(lines).map((l) => l.code)).toEqual(['I10', 'E11.9', '0DTJ4ZZ']);
  });
});

describe('codingProblems', () => {
  it('passes a correct coding', () => {
    expect(codingProblems(addLine(addLine([], dm), appendectomy))).toEqual([]);
  });

  it('explains what is wrong', () => {
    expect(codingProblems([])).toEqual(['Add at least one diagnosis code.']);
    expect(codingProblems([{ ...appendectomy, isPrimary: false }])[0]).toContain('principal diagnosis');
    expect(codingProblems([{ ...dm, isPrimary: true }, { ...htn, isPrimary: true }])).toContain('Mark exactly one diagnosis as the principal diagnosis.');
    expect(codingProblems([{ ...dm, isPrimary: true }, { ...dm, isPrimary: false }]).some((p) => p.includes('more than once'))).toBe(true);
  });
});

describe('suggestions and verification', () => {
  it('adds coded suggestions, primary first', () => {
    const lines = applySuggestions([], [
      { code: 'I10', title: htn.title, isPrimary: false },
      { code: null, title: 'Viral fever', isPrimary: false },
      { code: 'E11.9', title: dm.title, isPrimary: true },
    ]);
    expect(lines.map((l) => [l.code, l.isPrimary])).toEqual([
      ['E11.9', true],
      ['I10', false],
    ]);
  });

  it('enforces the four-eyes rule', () => {
    expect(verification({ status: 'Coded', coderUserId: 'u1' }, 'u1').allowed).toBe(false);
    expect(verification({ status: 'Coded', coderUserId: 'u1' }, 'u2')).toEqual({ allowed: true, reason: null });
    expect(verification({ status: 'Pending', coderUserId: null }, 'u2').allowed).toBe(false);
    expect(verification({ status: 'Verified', coderUserId: 'u1' }, 'u2').reason).toBe('Already verified.');
  });
});
