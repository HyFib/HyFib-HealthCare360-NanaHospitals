import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/privacy')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Privacy notice">{null}</PortalPage>;
}
