/** Plain-language labels for cardiology enums (pure data). */
import type { DiagnosticReportStatus } from '@/modules/lab/common/types';
import type { CardiologyStudyStatus, CardiologyStudyType, TmtResult } from './api';

export const studyTypeLabel: Record<CardiologyStudyType, string> = {
  Ecg: 'ECG',
  Echo: 'Echo',
  Tmt: 'TMT',
  Holter: 'Holter',
  Abpm: 'ABPM',
  Other: 'Other',
};

export const studyTypeLongLabel: Record<CardiologyStudyType, string> = {
  Ecg: '12-lead ECG',
  Echo: '2D echocardiography',
  Tmt: 'Treadmill test (TMT)',
  Holter: 'Holter monitoring',
  Abpm: 'Ambulatory BP monitoring',
  Other: 'Other cardiac test',
};

export const cardiologyStatusLabel: Record<CardiologyStudyStatus, string> = {
  Ordered: 'Ordered',
  Scheduled: 'Scheduled',
  InProgress: 'Recording',
  Acquired: 'Awaiting report',
  Reported: 'Awaiting sign-off',
  Verified: 'Signed, not released',
  Released: 'Released',
  Cancelled: 'Cancelled',
};

export const reportStatusLabel: Record<DiagnosticReportStatus, string> = {
  Draft: 'Draft',
  Reported: 'Awaiting sign-off',
  Verified: 'Signed, not released',
  Released: 'Released',
};

export const tmtResultLabel: Record<TmtResult, string> = {
  Negative: 'Negative for inducible ischaemia',
  Positive: 'Positive for inducible ischaemia',
  Inconclusive: 'Inconclusive',
};
