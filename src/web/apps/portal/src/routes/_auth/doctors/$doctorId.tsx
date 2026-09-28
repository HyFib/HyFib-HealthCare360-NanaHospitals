import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/doctors/$doctorId')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Doctor">{null}</PortalPage>;
}
