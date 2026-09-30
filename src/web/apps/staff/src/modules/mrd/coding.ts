/**
 * Clinical coding rules for the coder's editor (pure, unit-tested). They mirror the server so the coder sees problems
 * before saving: exactly one primary (principal) diagnosis, procedures are never primary, and no code twice.
 */

export type CodingKind = 'Diagnosis' | 'Procedure';

export interface CodingLine {
  kind: CodingKind;
  code: string;
  title: string;
  isPrimary: boolean;
}

export function sameLine(a: Pick<CodingLine, 'kind' | 'code'>, b: Pick<CodingLine, 'kind' | 'code'>): boolean {
  return a.kind === b.kind && a.code.trim().toUpperCase() === b.code.trim().toUpperCase();
}

/** Adds a code once; the first diagnosis becomes the primary one automatically. */
export function addLine(lines: readonly CodingLine[], line: Omit<CodingLine, 'isPrimary'>): CodingLine[] {
  if (lines.some((existing) => sameLine(existing, line))) {
    return [...lines];
  }
  const firstDiagnosis = line.kind === 'Diagnosis' && !lines.some((l) => l.kind === 'Diagnosis');
  return [...lines, { ...line, code: line.code.trim().toUpperCase(), isPrimary: firstDiagnosis }];
}

/** Removes a code; if it was the primary diagnosis, the next diagnosis takes over. */
export function removeLine(lines: readonly CodingLine[], line: Pick<CodingLine, 'kind' | 'code'>): CodingLine[] {
  const removed = lines.find((l) => sameLine(l, line));
  const rest = lines.filter((l) => !sameLine(l, line));
  if (removed?.isPrimary) {
    const next = rest.findIndex((l) => l.kind === 'Diagnosis');
    return rest.map((l, index) => (index === next ? { ...l, isPrimary: true } : l));
  }
  return rest;
}

export function setPrimary(lines: readonly CodingLine[], line: Pick<CodingLine, 'kind' | 'code'>): CodingLine[] {
  if (line.kind !== 'Diagnosis') {
    return [...lines];
  }
  return lines.map((l) => ({ ...l, isPrimary: l.kind === 'Diagnosis' && sameLine(l, line) }));
}

/** Primary first, then diagnoses, then procedures (the order the server stores). */
export function ordered(lines: readonly CodingLine[]): CodingLine[] {
  return lines.toSorted(
    (a, b) => Number(b.isPrimary) - Number(a.isPrimary) || (a.kind === b.kind ? 0 : a.kind === 'Diagnosis' ? -1 : 1),
  );
}

/** Problems that stop saving, in plain words (empty = ready). */
export function codingProblems(lines: readonly CodingLine[]): string[] {
  const problems: string[] = [];
  const diagnoses = lines.filter((l) => l.kind === 'Diagnosis');
  if (lines.length === 0) {
    problems.push('Add at least one diagnosis code.');
    return problems;
  }
  if (lines.length > 40) {
    problems.push('At most 40 codes per episode.');
  }
  const primaries = diagnoses.filter((l) => l.isPrimary).length;
  if (primaries !== 1) {
    problems.push(
      diagnoses.length === 0
        ? 'Add the principal diagnosis.'
        : 'Mark exactly one diagnosis as the principal diagnosis.',
    );
  }
  if (lines.some((l) => l.kind === 'Procedure' && l.isPrimary)) {
    problems.push('Only a diagnosis can be the principal code.');
  }
  const seen = new Set<string>();
  for (const line of lines) {
    const key = `${line.kind}:${line.code.trim().toUpperCase()}`;
    if (seen.has(key)) {
      problems.push(`${line.code} is listed more than once.`);
    }
    seen.add(key);
  }
  return problems;
}

export function toRequestLines(lines: readonly CodingLine[]): { kind: CodingKind; code: string; isPrimary: boolean }[] {
  return ordered(lines).map((l) => ({ kind: l.kind, code: l.code.trim().toUpperCase(), isPrimary: l.isPrimary }));
}

export interface SuggestionLike {
  code: string | null;
  title: string;
  isPrimary: boolean;
}

/** Adds the doctors' coded diagnoses as starting lines (uncoded working diagnoses need a code picked by the coder). */
export function applySuggestions(lines: readonly CodingLine[], suggestions: readonly SuggestionLike[]): CodingLine[] {
  let next = [...lines];
  const withCodes = suggestions
    .filter((s): s is SuggestionLike & { code: string } => Boolean(s.code))
    .toSorted((a, b) => Number(b.isPrimary) - Number(a.isPrimary));
  for (const suggestion of withCodes) {
    next = addLine(next, { kind: 'Diagnosis', code: suggestion.code, title: suggestion.title });
  }
  return next;
}

/** Four-eyes rule: coding is verified by a different coder, and only once it is coded. */
export function verification(
  coding: { status: 'Pending' | 'Coded' | 'Verified'; coderUserId: string | null },
  userId: string | null | undefined,
): { allowed: boolean; reason: string | null } {
  if (coding.status === 'Pending') {
    return { allowed: false, reason: 'Save the codes before verifying.' };
  }
  if (coding.status === 'Verified') {
    return { allowed: false, reason: 'Already verified.' };
  }
  if (userId && coding.coderUserId === userId) {
    return { allowed: false, reason: 'A different coder must verify your coding.' };
  }
  return { allowed: true, reason: null };
}
