import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/unsubscribe/$token')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Unsubscribe">{null}</PortalPage>;
}
