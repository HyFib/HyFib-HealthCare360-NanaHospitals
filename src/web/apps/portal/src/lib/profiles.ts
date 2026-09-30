import type { PortalProfile } from './types';

/**
 * Whose records to show: the family member chosen earlier in this tab if they are still on the number, else the
 * only person on the number, else nobody yet (the portal then asks).
 */
export function resolveActiveProfile(
  profiles: readonly PortalProfile[],
  storedId: string | null,
): PortalProfile | null {
  return profiles.find((profile) => profile.id === storedId) ?? (profiles.length === 1 ? (profiles[0] ?? null) : null);
}

/** The family member a record belongs to, when it is not the person being viewed (null when it is, or unknown). */
export function otherOwner(
  profiles: readonly PortalProfile[],
  activeId: string,
  ownerId: string | null | undefined,
): PortalProfile | null {
  if (!ownerId || ownerId === activeId) {
    return null;
  }
  return profiles.find((profile) => profile.id === ownerId) ?? null;
}
