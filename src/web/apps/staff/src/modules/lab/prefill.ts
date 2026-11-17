/**
 * Values the result sheet fills in for a person to confirm (pure; unit-tested): the FiO2 of an arterial blood gas, 21 %
 * (room air), from the server's `suggestedValue`. It is an unsaved edit like a typed one, shown in the field with a note:
 * saving it is the confirmation, and until then HC360 neither stores it nor calculates from it (the P/F ratio, the A-a
 * gradient). It is not kept as a draft on this computer, nor does it hold the user on the screen: a value that was never
 * confirmed is filled in again next time.
 */
import type { ResultSheetLine } from './api';

type PrefillLine = Pick<ResultSheetLine, 'resultId' | 'status' | 'value' | 'suggestedValue'>;

/** An edit of the sheet: the value, and whatever else was typed beside it (remarks, organism…). */
interface Edit {
  value: string;
}

/** The edits a sheet opens with: the suggested value of each test still awaiting one, when the user may enter it. */
export function prefilledEdits(lines: readonly PrefillLine[], canEnter: boolean): Record<string, { value: string }> {
  if (!canEnter) {
    return {};
  }
  const edits: Record<string, { value: string }> = {};
  for (const line of lines) {
    if (line.suggestedValue && line.status === 'Pending' && line.value === null) {
      edits[line.resultId] = { value: line.suggestedValue };
    }
  }
  return edits;
}

/** Whether the edit is the value filled in, untouched: nothing typed over it, nothing typed beside it. */
export function isUntouchedPrefill(line: PrefillLine, edit: Edit | undefined): boolean {
  return (
    edit !== undefined &&
    !!line.suggestedValue &&
    line.value === null &&
    edit.value === line.suggestedValue &&
    Object.keys(edit).length === 1
  );
}

/** The edits a person made: what is kept as a draft, and what makes leaving the screen ask first. */
export function typedEdits<E extends Edit>(edits: Record<string, E>, lines: readonly PrefillLine[]): Record<string, E> {
  const byId = new Map(lines.map((line) => [line.resultId, line]));
  return Object.fromEntries(
    Object.entries(edits).filter(([id, edit]) => {
      const line = byId.get(id);
      return !line || !isUntouchedPrefill(line, edit);
    }),
  );
}

/** A restored draft over the sheet's edits: it replaces what was filled in, never what was typed since. */
export function withRestoredDraft<E extends Edit>(
  current: Record<string, E>,
  saved: Record<string, E>,
  lines: readonly PrefillLine[],
): Record<string, E> {
  return { ...current, ...saved, ...typedEdits(current, lines) };
}

/** The note under a test whose value was filled in and not yet confirmed; null otherwise. */
export function prefillNote(line: PrefillLine, edit: Edit | undefined): string | null {
  if (!isUntouchedPrefill(line, edit)) {
    return null;
  }
  return `Filled in with ${line.suggestedValue} % (room air): change it if the patient was on oxygen. Nothing is calculated from it until it is saved.`;
}
