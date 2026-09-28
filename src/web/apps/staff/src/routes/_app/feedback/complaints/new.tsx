import { createFileRoute, redirect } from '@tanstack/react-router';

/** Link target for "Record a complaint" (dashboard quick action): opens the complaint list with the form open. */
export const Route = createFileRoute('/_app/feedback/complaints/new')({
  beforeLoad: () => {
    throw redirect({ to: '/feedback/complaints', search: { new: true }, replace: true });
  },
});
