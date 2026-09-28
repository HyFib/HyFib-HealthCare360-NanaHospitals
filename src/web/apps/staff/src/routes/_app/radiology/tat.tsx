import { LoadingState } from '@hc360/ui';
import { createFileRoute } from '@tanstack/react-router';

// Temporary stub while the screen is being built.
export const Route = createFileRoute('/_app/radiology/tat')({
  component: () => <LoadingState />,
});
