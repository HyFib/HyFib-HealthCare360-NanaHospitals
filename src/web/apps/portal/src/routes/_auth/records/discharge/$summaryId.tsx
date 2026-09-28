import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/records/discharge/$summaryId')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Discharge summary">{null}</PortalPage>;
}
