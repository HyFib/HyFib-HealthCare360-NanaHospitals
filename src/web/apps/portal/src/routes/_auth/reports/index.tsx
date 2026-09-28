import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/reports/')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Reports">{null}</PortalPage>;
}
