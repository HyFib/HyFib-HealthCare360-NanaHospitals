/**
 * Imaging workflow rules for the screens (pure; unit-tested). They mirror the server's state machine (ImagingStudy,
 * RadiologyReport) so only possible actions are offered; the server still enforces every rule.
 */
import type { DiagnosticReportStatus } from '@/modules/lab/common/types';
import type { ImagingStudyStatus } from './api';

export type StudyAction = 'schedule' | 'arrive' | 'start' | 'acquire' | 'repeat' | 'cancel' | 'reconcile' | 'report';

/** Technologist actions allowed in a study status, in the order they are usually done. */
export function studyActions(status: ImagingStudyStatus): StudyAction[] {
  switch (status) {
    case 'Ordered':
      return ['schedule', 'arrive', 'start', 'cancel', 'reconcile'];
    case 'Scheduled':
      return ['arrive', 'start', 'schedule', 'cancel', 'reconcile'];
    case 'Arrived':
      return ['start', 'acquire', 'cancel', 'reconcile'];
    case 'InProgress':
      return ['acquire', 'repeat', 'cancel', 'reconcile'];
    case 'Acquired':
      return ['report', 'repeat', 'cancel', 'reconcile'];
    case 'Reported':
    case 'Verified':
      return ['report', 'reconcile'];
    case 'Released':
      return ['report'];
    default:
      return [];
  }
}

/** The one button a technologist most likely needs next (shown on each worklist row). */
export function nextStudyAction(status: ImagingStudyStatus): StudyAction | null {
  switch (status) {
    case 'Ordered':
    case 'Scheduled':
      return 'arrive';
    case 'Arrived':
      return 'start';
    case 'InProgress':
      return 'acquire';
    default:
      return null;
  }
}

export const studyActionLabel: Record<StudyAction, string> = {
  schedule: 'Schedule',
  arrive: 'Patient arrived',
  start: 'Start scan',
  acquire: 'Complete scan',
  repeat: 'Record a repeat',
  cancel: 'Cancel study',
  reconcile: 'Link images from PACS',
  report: 'Open report',
};

/** A report can be written once images exist, and edited until it is signed. */
export function isReportable(status: ImagingStudyStatus): boolean {
  return status === 'Acquired' || status === 'Reported' || status === 'Verified' || status === 'Released';
}

export function isReportEditable(reportStatus: DiagnosticReportStatus | null | undefined): boolean {
  return !reportStatus || reportStatus === 'Draft' || reportStatus === 'Reported';
}

export function isReportSigned(reportStatus: DiagnosticReportStatus | null | undefined): boolean {
  return reportStatus === 'Verified' || reportStatus === 'Released';
}

/** "87%" for an AI engine confidence of 0.87 (null when the engine gives none). */
export function formatConfidence(confidence: number | null | undefined): string | null {
  if (confidence === null || confidence === undefined || !Number.isFinite(confidence)) {
    return null;
  }
  return `${Math.round(Math.min(Math.max(confidence, 0), 1) * 100)}%`;
}

/**
 * Applies a report template. Empty fields take the template text; when the radiologist has already typed something,
 * the template is appended below instead of overwriting it.
 */
export function applyTemplate(
  current: { technique: string; findings: string; impression: string },
  template: { technique: string | null; findings: string; impression: string },
): { technique: string; findings: string; impression: string } {
  const merge = (typed: string, text: string | null) => {
    const addition = (text ?? '').trim();
    if (!addition) {
      return typed;
    }
    return typed.trim() ? `${typed.trimEnd()}\n\n${addition}` : addition;
  };
  return {
    technique: merge(current.technique, template.technique),
    findings: merge(current.findings, template.findings),
    impression: merge(current.impression, template.impression),
  };
}

/** Adds an AI finding to the typed findings as its own paragraph, once. */
export function appendFinding(findings: string, finding: string): string {
  const text = finding.trim();
  if (!text || findings.includes(text)) {
    return findings;
  }
  return findings.trim() ? `${findings.trimEnd()}\n\n${text}` : text;
}
