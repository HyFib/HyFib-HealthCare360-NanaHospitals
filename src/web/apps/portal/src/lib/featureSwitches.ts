import { create } from 'zustand';
import type { SystemInfo } from './types';

/**
 * The hospital can turn the patient portal or video consultations off (Hospital setup > Features in the staff app).
 * The portal learns it before anyone signs in, from `GET /api/v1/system/info`, and again from any answer refused
 * with `feature.disabled` (a switch turned while this page was open). Either way it explains calmly instead of
 * showing an error. Pure state here (no API imports), so the API client can report refusals.
 */
export type PortalFeature = 'patientPortal' | 'telehealth';

interface RefusalState {
  /** Features the server refused since this page loaded. */
  refused: Partial<Record<PortalFeature, true>>;
  refuse(feature: string | undefined): void;
  clear(): void;
}

/** What the server refused. An answer that names no feature came from the portal's own API. */
export const useFeatureRefusals = create<RefusalState>((set) => ({
  refused: {},
  refuse: (feature) => {
    const key: PortalFeature = feature === 'telehealth' ? 'telehealth' : 'patientPortal';
    set((state) => (state.refused[key] ? state : { refused: { ...state.refused, [key]: true } }));
  },
  clear: () => set({ refused: {} }),
}));

/** On unless the server says it is off (a server that does not say counts as on). */
export function isPortalFeatureOn(
  info: Pick<SystemInfo, 'features'> | undefined,
  refused: Partial<Record<PortalFeature, true>>,
  feature: PortalFeature,
): boolean {
  return info?.features?.[feature] !== false && !refused[feature];
}
