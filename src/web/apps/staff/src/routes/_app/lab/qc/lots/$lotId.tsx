import { createFileRoute, redirect } from '@tanstack/react-router';

/** Target of the "QC rejected" staff alert (/lab/qc/lots/{id}): opens the QC screen on that lot's chart. */
export const Route = createFileRoute('/_app/lab/qc/lots/$lotId')({
  beforeLoad: ({ params }) => {
    throw redirect({ to: '/lab/qc', search: { lotId: params.lotId }, replace: true });
  },
});
