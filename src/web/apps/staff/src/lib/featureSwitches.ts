/**
 * Feature switches: parts of HC360 an administrator can turn off under Hospital setup > Features (server catalogue:
 * Platform/Features; docs/modules/features.md). Pure helpers, so navigation filtering can be tested without React.
 */
export const FEATURE_KEYS = ['patientPortal', 'telehealth'] as const;

export type FeatureKey = (typeof FEATURE_KEYS)[number];

/** The switches as the server enforces them for the user's facility. A feature that is not listed counts as on. */
export type FeatureSwitches = Partial<Record<FeatureKey, boolean>>;

/** The setting key of a feature, as `GET /api/v1/settings/public` names it. */
export function featureSettingKey(feature: FeatureKey): string {
  return `features.${feature}`;
}

/** The switches in the public settings: only `true` and `false` count, anything else leaves the feature on. */
export function featureSwitches(settings: Record<string, unknown> | undefined): FeatureSwitches {
  const switches: FeatureSwitches = {};
  for (const feature of FEATURE_KEYS) {
    const value = settings?.[featureSettingKey(feature)];
    if (typeof value === 'boolean') {
      switches[feature] = value;
    }
  }
  return switches;
}

/** On, unless it is known to be off. Something that names no feature is always on. */
export function isFeatureOn(features: FeatureSwitches | undefined, feature: FeatureKey | undefined): boolean {
  return !feature || features?.[feature] !== false;
}

/** What staff read where a switched-off feature would be. */
export const FEATURE_OFF_TEXT: Record<FeatureKey, { title: string; description: string }> = {
  telehealth: {
    title: 'Video consultations are turned off',
    description:
      'Book an in-person visit instead. Video appointments already booked stay in the diary: move them to an in-person visit or cancel them.',
  },
  patientPortal: {
    title: 'The patient portal is turned off',
    description: 'Patients cannot sign in, register or book online until it is turned back on.',
  },
};
