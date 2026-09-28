import { createFileRoute, redirect } from '@tanstack/react-router';

/** Target of the "provisional test created" staff alert (/lab/masters/tests/{id}): opens the test in lab setup. */
export const Route = createFileRoute('/_app/lab/masters/tests/$testId')({
  beforeLoad: ({ params }) => {
    throw redirect({ to: '/lab/setup', search: { tab: 'tests', testId: params.testId }, replace: true });
  },
});
