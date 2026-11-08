import type { QueueLineDto } from './api';

/** Lines signed over a blocking safety alert and not yet acknowledged by a pharmacist: they are not dispensed. */
export function unacknowledgedOverrides(lines: readonly QueueLineDto[]): QueueLineDto[] {
  return lines.filter((l) => Boolean(l.safetyOverride) && !l.overrideAcknowledgedAt);
}
