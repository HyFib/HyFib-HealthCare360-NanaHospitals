import { useMemo } from 'react';
import { featureSwitches, isFeatureOn, type FeatureKey, type FeatureSwitches } from './featureSwitches';
import { usePublicSettings } from './lookups';

/** The feature switches for the user's facility, from the public settings (every feature counts as on until known). */
export function useFeatureSwitches(): FeatureSwitches {
  const settings = usePublicSettings();
  return useMemo(() => featureSwitches(settings.data), [settings.data]);
}

/** True unless an administrator switched the feature off. */
export function useFeatureOn(feature: FeatureKey): boolean {
  return isFeatureOn(useFeatureSwitches(), feature);
}
