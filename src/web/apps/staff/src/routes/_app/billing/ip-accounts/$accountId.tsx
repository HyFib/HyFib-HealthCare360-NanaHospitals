import { createFileRoute, redirect } from '@tanstack/react-router';

/**
 * Server notifications (discharge initiated, admission cancelled, discharged with dues) link to
 * /billing/ip-accounts/{id}; the IP account screen lives at /billing/ip/{id}.
 */
export const Route = createFileRoute('/_app/billing/ip-accounts/$accountId')({
  beforeLoad: ({ params }) => {
    throw redirect({ to: '/billing/ip/$accountId', params: { accountId: params.accountId }, replace: true });
  },
});
