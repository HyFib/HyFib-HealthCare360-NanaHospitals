import type { BadgeProps } from '@fluentui/react-components';

export type StatusTone = NonNullable<BadgeProps['color']>;

const TONES: [RegExp, StatusTone][] = [
  [/^(completed|paid|active|approved|verified|released|available|present|resolved|closed|succeeded|signed|dispensed|settled|compatible|pass|vacant|delivered|converted|good)$/i, 'success'],
  [/^(cancelled|rejected|failed|expired|revoked|discarded|absent|critical|incompatible|reactive|lost|deceased|blocked|nottaken|noshow)$/i, 'danger'],
  [/^(pending|waiting|draft|requested|submitted|query|scheduled|booked|open|new|reserved|cleaning|quarantine|due|partiallypaid|partiallyapproved|halfday|warning|skipped)$/i, 'warning'],
  [/^(inprogress|called|inservice|confirmed|checkedin|processing|admitted|occupied|arrived|acquired|reported|issued|contacted|qualified|investigation|checkedout)$/i, 'brand'],
];

/** Maps a workflow status (e.g. "PartiallyPaid", "in_progress") to a badge colour. */
export function statusTone(status: string): StatusTone {
  const key = status.replace(/[\s_-]/g, '');
  return TONES.find(([pattern]) => pattern.test(key))?.[1] ?? 'informative';
}
