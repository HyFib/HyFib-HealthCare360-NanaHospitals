import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/prescriptions/$prescriptionId')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Prescription">{null}</PortalPage>;
}
