import { useSession, type PagedResult } from '@hc360/core';
import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { usePermission } from '@/lib/permissions';

/**
 * Staff users are referenced by their sign-in (Platform user) id: incident assignees, CAPA and risk owners, lead
 * owners, complaint owners. The only directory the server exposes today is the user administration list, which needs
 * `admin.users.read`; without it people can still pick themselves.
 */
export const USER_DIRECTORY_PERMISSION = 'admin.users.read';

export interface PickedUser {
  id: string;
  name: string;
}

export interface DirectoryUser {
  id: string;
  userName: string;
  displayName: string;
  roles: string[];
  isActive: boolean;
}

export function searchUsers(search: string, signal?: AbortSignal): Promise<PagedResult<DirectoryUser>> {
  return api.get<PagedResult<DirectoryUser>>('/api/v1/admin/users', { query: { search: search || undefined, active: true, pageSize: 20 }, signal });
}

/** Display name for a user id: "You" for the signed-in user, the directory name when readable, else a neutral label. */
export function useUserName(id: string | null | undefined): string | null {
  const me = useSession((s) => s.user);
  const canRead = usePermission(USER_DIRECTORY_PERMISSION);
  const isMe = Boolean(id && me && id === me.id);
  const query = useQuery({
    queryKey: ['users', 'name', id],
    queryFn: ({ signal }) => api.get<{ displayName: string }>(`/api/v1/admin/users/${id}`, { signal }),
    enabled: Boolean(id) && !isMe && canRead,
    staleTime: 10 * 60_000,
    retry: false,
  });
  if (!id) {
    return null;
  }
  if (isMe) {
    return 'You';
  }
  if (query.data) {
    return query.data.displayName;
  }
  return canRead && query.isLoading ? '…' : 'Another staff member';
}
