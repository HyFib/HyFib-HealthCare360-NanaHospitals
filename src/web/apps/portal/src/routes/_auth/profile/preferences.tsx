import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/profile/preferences')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Messages">{null}</PortalPage>;
}
