/**
 * Patient block and footnotes of the lab report as the screen shows them (pure; unit-tested). The printed report and
 * its PDF are made by the server, which prints the same rows and notes.
 */
import { formatDateTime, genderShort } from '@hc360/core';
import type { LabReportView } from './api';

/** Patient block rows: identity, referral and the key times. */
export function labReportPatientRows(view: LabReportView): [string, string][] {
  const collected = view.sections
    .flatMap((s) => s.tests.map((t) => t.collectedAt))
    .filter((v): v is string => Boolean(v))
    .sort()[0];
  const received = view.sections
    .flatMap((s) => s.tests.map((t) => t.receivedAt))
    .filter((v): v is string => Boolean(v))
    .sort()[0];
  return [
    ['Patient', view.patient.name],
    ['Age / sex', `${view.patient.age ?? '—'} / ${genderShort[view.patient.gender]}`],
    ['UHID', view.patient.uhid],
    ['Report no.', view.revision > 1 ? `${view.reportNumber} (revision ${view.revision})` : view.reportNumber],
    ['Referred by', view.referredBy ?? 'Self'],
    ['Ordered', formatDateTime(view.orderedAt)],
    ['Collected', collected ? formatDateTime(collected) : '—'],
    ['Received', received ? formatDateTime(received) : '—'],
    ['Released', view.releasedAt ? formatDateTime(view.releasedAt) : 'Not released'],
  ];
}

/** Footnotes: partial report, auto-validation and amendment notices. */
export function labReportNotes(view: LabReportView): string[] {
  const notes: string[] = [];
  if (view.isPartial) {
    notes.push('Partial report: some tests are still in progress and will follow in an updated report.');
  }
  if (view.hasAutoValidatedResults) {
    notes.push(
      '* Verified automatically under the laboratory auto-validation rules (in range, QC accepted, delta check passed).',
    );
  }
  if (view.status === 'Amended') {
    notes.push('This report was amended after release. The amendments and their reasons are listed above.');
  }
  return notes;
}
