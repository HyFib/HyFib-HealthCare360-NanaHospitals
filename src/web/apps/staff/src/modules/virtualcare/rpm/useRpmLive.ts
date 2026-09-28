import { useRealtimeEvent, useSession } from '@hc360/core';
import { useQueryClient } from '@tanstack/react-query';
import { rpmKeys } from './api';

/**
 * RPM has no realtime topic of its own; queues poll every minute. New or escalated alerts also send an in-app
 * notification (category "rpm") to alert handlers, so those refresh the screens at once.
 */
export function useRpmLive(): void {
  const userId = useSession((s) => s.user?.id);
  const queryClient = useQueryClient();
  useRealtimeEvent<{ category?: string }>(userId ? `user:${userId}` : null, 'notification.new', (notification) => {
    if (notification.category === 'rpm') {
      void queryClient.invalidateQueries({ queryKey: rpmKeys.all });
    }
  });
}
