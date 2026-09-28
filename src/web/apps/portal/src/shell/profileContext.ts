import { createContext, useContext } from 'react';
import type { PortalProfile } from '@/lib/types';

export interface ActiveProfile {
  /** The family member whose records every signed-in screen shows. */
  profile: PortalProfile;
  /** Everyone registered with the verified mobile number (always contains `profile`). */
  profiles: PortalProfile[];
}

export const ProfileContext = createContext<ActiveProfile | null>(null);

/** The family member being viewed. Only available inside the signed-in layout. */
export function useActiveProfile(): ActiveProfile {
  const value = useContext(ProfileContext);
  if (!value) {
    throw new Error('useActiveProfile() is only available on signed-in pages.');
  }
  return value;
}

/** On pages open to everyone (doctors, document checks): the family member when signed in, otherwise null. */
export function useOptionalProfile(): ActiveProfile | null {
  return useContext(ProfileContext);
}
