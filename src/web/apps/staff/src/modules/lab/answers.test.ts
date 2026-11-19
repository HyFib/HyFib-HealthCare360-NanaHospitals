import { describe, expect, it } from 'vitest';
import answerWords from './answerWords.json';
import { answerFor, typeStripKey } from './answers';

// Urine strip words (N9: "-" is nil, "+-" trace, "++" 2+), read as the server reads them: its AnalyserAnswersTests read
// the same list, so a word typed on the result sheet, one entered through the API and one an analyser sends agree.

const rows = answerWords.read as [string, string[], string | null][];

describe('answer words', () => {
  it.each(rows)('reads %j against %j as %j, as the server does', (word, options, answer) => {
    expect(answerFor(options, word)).toBe(answer);
  });

  it('reads Nana Hospitals’ strip words as the urine tests’ answers', () => {
    const grades = ['Nil', 'Trace', '1+', '2+', '3+', '4+'];

    expect(answerFor(grades, '-')).toBe('Nil');
    expect(answerFor(grades, '+-')).toBe('Trace');
    expect(answerFor(grades, '++')).toBe('2+');
    expect(answerFor(['Negative', 'Positive'], '-')).toBe('Negative');
  });
});

describe('a strip word typed key by key into an answer list', () => {
  it('runs on while the keys come within a second, on the same test', () => {
    const plus = typeStripKey(null, 'protein', '+', 1000);
    const minus = typeStripKey(plus, 'protein', '-', 1400);

    expect(plus.word).toBe('+');
    expect(minus).toEqual({ resultId: 'protein', word: '+-', at: 1400 });
    expect(typeStripKey(typeStripKey(null, 'blood', '+', 0), 'blood', '+', 300).word).toBe('++');
  });

  it('starts a new word after a pause, or on another test', () => {
    const plus = typeStripKey(null, 'protein', '+', 1000);

    expect(typeStripKey(plus, 'protein', '-', 2500).word).toBe('-');
    expect(typeStripKey(plus, 'glucose', '-', 1100).word).toBe('-');
  });
});
