import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/bills/')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Bills">{null}</PortalPage>;
}
