import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback } from 'react';
import { systemQueries } from '@/modules/profiles/api';
import { isPortalFeatureOn, useFeatureRefusals } from './featureSwitches';

export interface PortalFeatures {
  /** The patient portal is on: sign-in, the signed-in pages and online booking. */
  portal: boolean;
  /** Video consultations are on. */
  video: boolean;
  /** The server has answered (or could not be reached): until then both count as on. */
  settled: boolean;
  /** Asks the server again (the "Try again" of the not-available pages). */
  recheck(): Promise<void>;
}

/** Whether the portal and video consultations are switched on (see `./featureSwitches`). */
export function usePortalFeatures(): PortalFeatures {
  const queryClient = useQueryClient();
  const info = useQuery(systemQueries.info());
  const refused = useFeatureRefusals((state) => state.refused);
  // The server's answer replaces what was refused earlier: a switch turned back on shows the page again.
  const recheck = useCallback(async () => {
    try {
      await queryClient.fetchQuery({ ...systemQueries.info(), staleTime: 0 });
      useFeatureRefusals.getState().clear();
    } catch {
      // Still unreachable: the page stays as it is.
    }
  }, [queryClient]);
  return {
    portal: isPortalFeatureOn(info.data, refused, 'patientPortal'),
    video: isPortalFeatureOn(info.data, refused, 'telehealth'),
    settled: !info.isPending,
    recheck,
  };
}
