/**
 * Answer words (pure; unit-tested): the words urine strip readers send and laboratories write for a test's answers,
 * read as the server's AnalyserAnswers reads them (both are tested against answerWords.json). Nana Hospitals writes
 * "-" for nil, "+-" for trace and "++" for 2+: typed into a test's answer list on the result sheet, those words choose
 * the test's own answer (Nil, Trace, 2+), as the server reads them in a value sent by an analyser or entered through
 * the API.
 */

/** Ways of writing one grade, each as `key` reads it (no spaces, upper case): the server's list. */
const GRADES: readonly (readonly string[])[] = [
  ['NIL', 'NEG', 'NEGATIVE', 'ABSENT', 'NOTDETECTED', '-', '−'],
  ['TRACE', 'TR', '+-', '+/-', '±', '-+'],
  ['1+', '+', '+1'],
  ['2+', '++', '+2'],
  ['3+', '+++', '+3'],
  ['4+', '++++', '+4'],
  ['POSITIVE', 'POS', 'PRESENT', 'DETECTED'],
  ['NORMAL', 'NORM'],
  ['INCREASED', 'INCR'],
];

function key(text: string): string {
  return text.replace(/\s/g, '').toUpperCase();
}

/** The test's answer the word names, or null when it names none of them or more than one. */
export function answerFor(options: readonly string[], word: string): string | null {
  const wordKey = key(word);
  const grade = GRADES.find((g) => g.includes(wordKey));
  if (!grade) {
    return null;
  }
  const answers = options.filter((option) => grade.includes(key(option)));
  return answers.length === 1 ? answers[0]! : null;
}

/** The keys a strip's words are typed with, which an answer list would otherwise ignore. */
export const STRIP_KEYS: ReadonlySet<string> = new Set(['+', '-', '±', '/']);

/** A strip word being typed into one test's answer list. */
export interface StripTyping {
  resultId: string;
  word: string;
  at: number;
}

/** How long a pause starts a new word, ms. */
const PAUSE_MS = 1000;

/**
 * The word typed so far into a test's answer list after `typedKey`: the strip keys pressed on the same test within a
 * second of each other ("+" then "-" is "+-"); a pause, or another test, starts a new word.
 */
export function typeStripKey(
  previous: StripTyping | null,
  resultId: string,
  typedKey: string,
  at: number,
): StripTyping {
  const goesOn = previous !== null && previous.resultId === resultId && at - previous.at <= PAUSE_MS;
  return { resultId, word: goesOn ? previous.word + typedKey : typedKey, at };
}
