import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/video/$sessionId')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Video consultation">{null}</PortalPage>;
}
