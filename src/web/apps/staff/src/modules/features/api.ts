import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * Feature switches (Hospital setup > Features): parts of HealthCare360 an administrator turns on or off for the whole
 * hospital. Mirrors `FeatureEndpoints` (`/api/v1/admin/features`, permission `admin.settings.manage`); the catalogue
 * and its words come from the server (Platform/Features), so a new switch appears here without a change to this app.
 */

/** Mirrors the server's `FeatureDto`; `enabled` is the hospital-wide value. */
export interface FeatureDto {
  /** The setting key, `features.<code>`. */
  key: string;
  /** The key without its prefix, as the apps name it (`telehealth`, `patientPortal`). */
  code: string;
  name: string;
  description: string;
  /** What stops, and what keeps working, while the feature is off. */
  whenOff: string;
  defaultEnabled: boolean;
  enabled: boolean;
  /** The saved value's version, sent back with a change (REL-04); null while the feature is at its default. */
  version: number | null;
}

const BASE = '/api/v1/admin/features';

export const featureKeys = { all: ['admin', 'features'] as const };

export const featureQueries = {
  list: () =>
    queryOptions({
      queryKey: featureKeys.all,
      queryFn: ({ signal }) => api.get<FeatureDto[]>(BASE, { signal }),
    }),
};

export const featuresApi = {
  /**
   * Turns the feature on or off for the whole hospital; the server records who did it. `version` is the one the switch
   * was shown at: the server refuses the change (409) when another administrator changed it since.
   */
  set: (key: string, enabled: boolean, version: number | null) =>
    api.put<FeatureDto>(`${BASE}/${encodeURIComponent(key)}`, { enabled, version: version ?? undefined }),
};

/** The question asked before a feature is turned off: what it is and what stops. */
export function turnOffQuestion(feature: Pick<FeatureDto, 'name' | 'whenOff'>): {
  title: string;
  body: string;
  confirmLabel: string;
} {
  return {
    title: `Turn off ${lowerFirst(feature.name)}?`,
    body: `${feature.whenOff} You can turn it back on here at any time.`,
    confirmLabel: 'Turn off',
  };
}

/** The toast after a change: "Video consultations turned off". */
export function changedMessage(feature: Pick<FeatureDto, 'name'>, enabled: boolean): string {
  return `${feature.name} turned ${enabled ? 'on' : 'off'}`;
}

/** The words next to a switch. */
export function stateLabel(enabled: boolean): string {
  return enabled ? 'On' : 'Off';
}

/** "Video consultations" in the middle of a sentence: "video consultations". */
function lowerFirst(text: string): string {
  return text.charAt(0).toLowerCase() + text.slice(1);
}
