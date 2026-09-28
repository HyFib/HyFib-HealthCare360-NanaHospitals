import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/feedback/form/$token')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Feedback">{null}</PortalPage>;
}
