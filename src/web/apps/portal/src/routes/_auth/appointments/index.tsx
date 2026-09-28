import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/appointments/')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Appointments">{null}</PortalPage>;
}
