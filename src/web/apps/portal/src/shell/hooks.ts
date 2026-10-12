import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback } from 'react';
import { useT } from '@/i18n';
import { signOut } from '@/lib/session';
import { markSessionEnd, type SessionEndReason } from '@/lib/sessionEnd';
import { systemQueries } from '@/modules/profiles/api';
import { confirmLeaving } from '@hc360/ui';

/** The hospital's name from the server (falls back to the group name while offline). */
export function useHospitalName(): string {
  const t = useT();
  const info = useQuery(systemQueries.info());
  return info.data?.facilityName?.trim() || t('app.hospitalFallback');
}

/**
 * Signs out: the server revokes the session, every cached answer is dropped and the signed-in layout then moves to
 * the sign-in page (which says why). Signing out on request asks first when a form has unsaved changes (SRS UX-08);
 * signing out after a quiet spell does not wait for anyone.
 */
export function useSignOut(): (reason?: SessionEndReason) => Promise<void> {
  const queryClient = useQueryClient();
  return useCallback(
    async (reason: SessionEndReason = 'signed-out') => {
      if (reason === 'signed-out' && !(await confirmLeaving())) {
        return;
      }
      markSessionEnd(reason);
      await signOut(queryClient);
    },
    [queryClient],
  );
}
