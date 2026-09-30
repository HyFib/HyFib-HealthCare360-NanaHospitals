/**
 * Cardiology workflow rules for the screens (pure; unit-tested), mirroring the server's CardiologyStudy state machine
 * so only possible actions are offered. The server enforces them again.
 */
import type { DiagnosticReportStatus } from '@/modules/lab/common/types';
import type { CardiologyStudyStatus } from './api';

export type CardiologyAction = 'schedule' | 'start' | 'attach' | 'complete' | 'cancel';

export function cardiologyActions(status: CardiologyStudyStatus): CardiologyAction[] {
  switch (status) {
    case 'Ordered':
      return ['start', 'schedule', 'attach', 'complete', 'cancel'];
    case 'Scheduled':
      return ['start', 'schedule', 'attach', 'complete', 'cancel'];
    case 'InProgress':
      return ['attach', 'complete', 'cancel'];
    case 'Acquired':
      return ['attach', 'cancel'];
    case 'Reported':
      return ['attach'];
    default:
      return [];
  }
}

/** The next step for the technician: start, attach the tracing, then complete. */
export function nextCardiologyAction(status: CardiologyStudyStatus, fileCount: number): CardiologyAction | null {
  switch (status) {
    case 'Ordered':
    case 'Scheduled':
      return 'start';
    case 'InProgress':
      return fileCount > 0 ? 'complete' : 'attach';
    default:
      return null;
  }
}

export const cardiologyActionLabel: Record<CardiologyAction, string> = {
  schedule: 'Schedule',
  start: 'Start recording',
  attach: 'Attach tracing',
  complete: 'Complete study',
  cancel: 'Cancel study',
};

/** The report can be written once the device output is attached and the study is completed. */
export function isCardiologyReportable(status: CardiologyStudyStatus): boolean {
  return status === 'Acquired' || status === 'Reported' || status === 'Verified' || status === 'Released';
}

export function isCardiologyReportEditable(
  reportStatus: DiagnosticReportStatus | null | undefined,
  studyStatus: CardiologyStudyStatus,
): boolean {
  return (
    (studyStatus === 'Acquired' || studyStatus === 'Reported') &&
    (!reportStatus || reportStatus === 'Draft' || reportStatus === 'Reported')
  );
}
