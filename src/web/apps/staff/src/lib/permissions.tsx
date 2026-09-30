import { hasPermission, useSession } from '@hc360/core';
import type { ReactNode } from 'react';

/** True when the signed-in user holds the permission (or any of them, for an array). */
export function usePermission(permission: string | readonly string[] | undefined): boolean {
  const user = useSession((state) => state.user);
  return hasPermission(user, permission);
}

/** Renders children only for users with the permission — the server enforces it again on every call. */
export function Can({
  permission,
  children,
  fallback = null,
}: {
  permission: string | readonly string[];
  children: ReactNode;
  fallback?: ReactNode;
}) {
  return usePermission(permission) ? children : fallback;
}
