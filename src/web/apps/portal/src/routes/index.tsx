import { createFileRoute, redirect } from '@tanstack/react-router';
import { ensurePortalSession } from '@/lib/session';

/** /portal/ → the patient's home when signed in, otherwise the sign-in page. */
export const Route = createFileRoute('/')({
  beforeLoad: async () => {
    const state = await ensurePortalSession();
    throw redirect({ to: state === 'patient' ? '/home' : '/sign-in', replace: true });
  },
});
