import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/telehealth/$sessionId')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Video consultation">{null}</PortalPage>;
}
