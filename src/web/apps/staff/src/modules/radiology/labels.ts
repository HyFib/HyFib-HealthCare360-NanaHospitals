/** Plain-language labels for radiology enums (pure data). */
import type { DiagnosticReportStatus } from '@/modules/lab/common/types';
import type { AiFindingStatus, ImageRepeatReason, ImagingStudyStatus, Modality } from './api';

export const modalityLabel: Record<Modality, string> = {
  XR: 'X-ray',
  CT: 'CT scan',
  MR: 'MRI',
  US: 'Ultrasound',
  MG: 'Mammography',
  RF: 'Fluoroscopy',
  DX: 'Digital X-ray',
};

export const studyStatusLabel: Record<ImagingStudyStatus, string> = {
  Ordered: 'Ordered',
  Scheduled: 'Scheduled',
  Arrived: 'Patient arrived',
  InProgress: 'Scanning',
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

export const repeatReasonLabel: Record<ImageRepeatReason, string> = {
  Positioning: 'Positioning',
  Exposure: 'Exposure',
  PatientMotion: 'Patient moved',
  Artefact: 'Artefact',
  EquipmentFault: 'Equipment fault',
  WrongProtocol: 'Wrong protocol',
  PatientRelated: 'Patient related',
  Other: 'Other reason',
};

export const aiStatusLabel: Record<AiFindingStatus, string> = {
  Preliminary: 'Awaiting review',
  Acknowledged: 'Accepted',
  Dismissed: 'Rejected',
};
