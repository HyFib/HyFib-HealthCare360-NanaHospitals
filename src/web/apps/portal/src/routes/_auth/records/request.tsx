import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/records/request')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Request records">{null}</PortalPage>;
}
