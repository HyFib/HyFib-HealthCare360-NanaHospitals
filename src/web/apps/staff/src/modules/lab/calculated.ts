/**
 * Results HC360 calculates from others on the same order and sample (indirect bilirubin, globulin, A/G ratio, VLDL…):
 * how the result sheet tells them apart (pure; unit-tested). The server calculates them and keeps the formula with the
 * inputs it was worked from as the result's first held reason, for the verifier; a calculated result is never verified
 * automatically.
 */
import type { ResultSheetLine } from './api';

type CalculatedLine = Pick<ResultSheetLine, 'status' | 'isCalculated' | 'formula' | 'heldReasons'>;

/**
 * What the sheet says under a test a formula fills: the worked formula once calculated ("Calculated by HC360: Total
 * protein − Albumin = 7.2 − 4.1 = 3.1 g/dL."), why it was not ("Not calculated: …"), or, while its inputs are awaited,
 * that it needs no entry. Null for a measured test, and for a value typed in or measured for it, which stands.
 */
export function calculationNote(line: CalculatedLine): string | null {
  if (!line.formula) {
    return null;
  }
  if (line.isCalculated) {
    return line.heldReasons[0] ?? null;
  }
  if (line.status !== 'Pending') {
    return null;
  }
  return line.heldReasons[0] ?? `Calculated by HC360 when its inputs are in: ${line.formula}.`;
}

/** The status badge of an entered result: calculated by HC360, from an analyser, or entered by hand. */
export function enteredLabel(line: CalculatedLine & Pick<ResultSheetLine, 'instrumentCode'>): string {
  if (line.isCalculated) {
    return 'Calculated';
  }
  return line.instrumentCode ? `From ${line.instrumentCode}` : 'Entered';
}
