import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/records/')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Health records">{null}</PortalPage>;
}
