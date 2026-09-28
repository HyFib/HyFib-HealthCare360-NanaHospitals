import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/appointments/$appointmentId')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Appointment">{null}</PortalPage>;
}
