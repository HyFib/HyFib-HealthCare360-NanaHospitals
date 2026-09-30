import { StatusBadge } from '@hc360/ui';
import type { DiagnosticReportStatus } from './types';

/** Radiology and cardiology report workflow in words (shared by both departments). */
const diagnosticReportStatusLabel: Record<DiagnosticReportStatus, string> = {
  Draft: 'Draft',
  Reported: 'Awaiting sign-off',
  Verified: 'Signed, not released',
  Released: 'Released',
};

export function DiagnosticReportBadge({ status }: { status: DiagnosticReportStatus | null | undefined }) {
  if (!status) {
    return null;
  }
  const tone =
    status === 'Released'
      ? 'success'
      : status === 'Verified'
        ? 'warning'
        : status === 'Reported'
          ? 'important'
          : 'informative';
  return <StatusBadge status={status} tone={tone} label={diagnosticReportStatusLabel[status]} />;
}
