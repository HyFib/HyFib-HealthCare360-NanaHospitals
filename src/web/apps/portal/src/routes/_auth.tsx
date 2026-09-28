import { createFileRoute, Outlet, redirect } from '@tanstack/react-router';
import { ensurePortalSession } from '@/lib/session';
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
  return (
    <SignedInFrame requireSession>
      <Outlet />
    </SignedInFrame>
  );
}
