import { createFileRoute, Outlet, redirect } from '@tanstack/react-router';
import { PortalUnavailablePage } from '@/components/FeatureOff';
import { usePortalFeatures } from '@/lib/features';
import { ensurePortalSession } from '@/lib/session';
import { useCallActivity } from '@/lib/sessionEnd';
import { SignedInFrame } from '@/shell/SignedInFrame';

/** Signed-in pages: they need a patient-portal session and a chosen family member. */
export const Route = createFileRoute('/_auth')({
  beforeLoad: async ({ location }) => {
    const state = await ensurePortalSession();
    if (state !== 'patient') {
      throw redirect({ to: '/sign-in', search: { redirect: location.href } });
    }
  },
  component: AuthLayout,
});

function AuthLayout() {
  // Turned off by the hospital while this patient was signed in: say so instead of failing page by page. A video
  // call already on screen is never cut off.
  const { portal } = usePortalFeatures();
  const inCall = useCallActivity((s) => s.inCall);
  if (!portal && !inCall) {
    return <PortalUnavailablePage />;
  }
  return (
    <SignedInFrame requireSession>
      <Outlet />
    </SignedInFrame>
  );
}
