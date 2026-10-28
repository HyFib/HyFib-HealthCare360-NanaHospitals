import { auth, RealtimeProvider, useSession } from '@hc360/core';
import { LoadingState } from '@hc360/ui';
import { createFileRoute, Outlet, redirect, useNavigate, useRouter } from '@tanstack/react-router';
import { useEffect } from 'react';
import { AppShell } from '@/shell/AppShell';
import { LockScreen } from '@/shell/LockScreen';
import { RestrictedSession } from '@/shell/RestrictedSession';
import { useLockSync } from '@/shell/lockSync';

/** Every staff screen lives under this layout: it requires a session and renders the application frame. */
export const Route = createFileRoute('/_app')({
  beforeLoad: async ({ location }) => {
    const { status } = useSession.getState();
    const signedIn = status === 'authenticated' || (status === 'unknown' && (await auth.restore()));
    if (!signedIn) {
      throw redirect({ to: '/login', search: { redirect: location.href } });
    }
  },
  component: AppLayout,
});

function AppLayout() {
  const status = useSession((s) => s.status);
  const user = useSession((s) => s.user);
  const locked = useSession((s) => s.locked);
  const navigate = useNavigate();
  const router = useRouter();
  useLockSync();

  // Session ended elsewhere (signed out, revoked by an administrator, idle timeout on the server), or here from the
  // account menu. Once, when it ends: the screen is read then, not followed. This layout stays on screen until the
  // sign-in page is ready, and following the screen would send the sign-in page to itself again and again, each time
  // "returning" to the one before.
  useEffect(() => {
    if (status === 'anonymous') {
      const { pathname, href } = router.state.location;
      if (pathname !== '/login') {
        // The session is over: unsaved changes cannot be saved any more, so nothing holds the sign-in page back.
        void navigate({ to: '/login', search: { redirect: href }, replace: true, ignoreBlocker: true });
      }
    }
  }, [status, navigate, router]);

  if (!user) {
    return <LoadingState label="Signing you in…" />;
  }

  // A locked session keeps its screens, as they were, behind the lock screen.
  if (user.restriction && user.restriction !== 'locked') {
    return <RestrictedSession restriction={user.restriction} />;
  }

  // No live updates reach a locked screen: the server refuses the connection until it is unlocked.
  return (
    <RealtimeProvider
      accessTokenFactory={() => useSession.getState().accessToken ?? ''}
      enabled={status === 'authenticated' && !locked}
    >
      <AppShell>
        <Outlet />
      </AppShell>
      <LockScreen />
    </RealtimeProvider>
  );
}
