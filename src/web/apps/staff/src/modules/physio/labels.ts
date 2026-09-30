import type { StatusTone } from '@hc360/ui';
import type { OrderPriority } from '@/modules/ot/shared/types';
import type { PhysioAssessmentType, PhysioModality, PhysioReferralStatus, PhysioSessionStatus } from './api';

export const referralStatusLabel: Record<PhysioReferralStatus, string> = {
  New: 'New — to be assessed',
  Assessed: 'Assessed — plan needed',
  InTreatment: 'In treatment',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
};

export const referralStatusTone: Record<PhysioReferralStatus, StatusTone> = {
  New: 'warning',
  Assessed: 'informative',
  InTreatment: 'brand',
  Completed: 'success',
  Cancelled: 'subtle',
};

export const modalityLabel: Record<PhysioModality, string> = {
  IFT: 'Interferential therapy (IFT)',
  TENS: 'TENS',
  UST: 'Therapeutic ultrasound (UST)',
  SWD: 'Short-wave diathermy (SWD)',
  HotPack: 'Hot pack',
  ColdPack: 'Cold pack',
  Traction: 'Traction',
  Exercises: 'Exercises',
  Mobilisation: 'Mobilisation',
  ChestPhysiotherapy: 'Chest physiotherapy',
  GaitTraining: 'Gait training',
  WaxBath: 'Wax bath',
  Laser: 'Laser',
  Other: 'Other',
};

export const assessmentTypeLabel: Record<PhysioAssessmentType, string> = {
  Initial: 'Initial assessment',
  Review: 'Review',
  Discharge: 'Discharge assessment',
};

export const physioSessionStatusLabel: Record<PhysioSessionStatus, string> = {
  Scheduled: 'Scheduled',
  Attended: 'Attended',
  Missed: 'Missed',
  Cancelled: 'Cancelled',
};

export const physioSessionStatusTone: Record<PhysioSessionStatus, StatusTone> = {
  Scheduled: 'informative',
  Attended: 'success',
  Missed: 'danger',
  Cancelled: 'subtle',
};

export const orderPriorityLabel: Record<OrderPriority, string> = {
  Routine: 'Routine',
  Urgent: 'Urgent',
  Stat: 'Immediate (stat)',
};

export const orderPriorityTone: Record<OrderPriority, StatusTone> = {
  Routine: 'subtle',
  Urgent: 'warning',
  Stat: 'danger',
};

/** Medical Research Council muscle power scale. */
export const MRC_GRADES: readonly { value: number; label: string }[] = [
  { value: 0, label: '0 — no contraction' },
  { value: 1, label: '1 — flicker of contraction' },
  { value: 2, label: '2 — moves with gravity eliminated' },
  { value: 3, label: '3 — moves against gravity' },
  { value: 4, label: '4 — against some resistance' },
  { value: 5, label: '5 — normal power' },
];
