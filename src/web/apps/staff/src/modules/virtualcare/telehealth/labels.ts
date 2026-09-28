import type { StatusTone } from '@hc360/ui';
import type { TelehealthRole, TelehealthStatus } from './api';

export const sessionStatusText: Record<TelehealthStatus, { label: string; tone: StatusTone }> = {
  Scheduled: { label: 'Scheduled', tone: 'informative' },
  WaitingRoom: { label: 'Patient waiting', tone: 'warning' },
  InProgress: { label: 'In progress', tone: 'brand' },
  Completed: { label: 'Completed', tone: 'success' },
  Cancelled: { label: 'Cancelled', tone: 'danger' },
  Missed: { label: 'Missed', tone: 'danger' },
};

export const roleLabel: Record<TelehealthRole, string> = {
  Clinician: 'Doctor',
  Patient: 'Patient (portal)',
  Guest: 'Patient (SMS link)',
};

/** Sessions that can still be joined, started or changed. */
export function isSessionOpen(status: TelehealthStatus): boolean {
  return status === 'Scheduled' || status === 'WaitingRoom' || status === 'InProgress';
}
