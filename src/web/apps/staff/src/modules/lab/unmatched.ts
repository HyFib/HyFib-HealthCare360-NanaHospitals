/** Helpers for the analyser results kept aside for the lab to match (pure; tested with the screen). */
import type { UnmatchedResultDto } from './api';

/** The analyser's value as it sent it: "98 mg/dL · flag H · status P". */
export function analyserReading(
  result: Pick<UnmatchedResultDto, 'value' | 'instrumentUnit' | 'instrumentFlags' | 'instrumentStatus'>,
): string {
  return [
    result.instrumentUnit ? `${result.value} ${result.instrumentUnit}` : result.value,
    result.instrumentFlags ? `flag ${result.instrumentFlags}` : null,
    result.instrumentStatus ? `status ${result.instrumentStatus}` : null,
  ]
    .filter(Boolean)
    .join(' · ');
}
