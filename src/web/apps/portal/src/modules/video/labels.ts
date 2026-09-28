import type { Tone } from '@/components/ListRow';
import type { MessageKey } from '@/i18n';
import type { TelehealthStatus } from '@/lib/types';

export const VIDEO_STATUS: Record<TelehealthStatus, { key: MessageKey; tone: Tone }> = {
  Scheduled: { key: 'video.status.Scheduled', tone: 'brand' },
  WaitingRoom: { key: 'video.status.WaitingRoom', tone: 'warning' },
  InProgress: { key: 'video.status.InProgress', tone: 'success' },
  Completed: { key: 'video.status.Completed', tone: 'neutral' },
  Cancelled: { key: 'video.status.Cancelled', tone: 'danger' },
  Missed: { key: 'video.status.Missed', tone: 'warning' },
};

export function isClosedSession(status: TelehealthStatus): boolean {
  return status === 'Completed' || status === 'Cancelled' || status === 'Missed';
}
