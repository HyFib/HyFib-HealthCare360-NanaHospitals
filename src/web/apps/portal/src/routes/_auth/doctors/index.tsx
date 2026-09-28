import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/doctors/')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Find a doctor">{null}</PortalPage>;
}
