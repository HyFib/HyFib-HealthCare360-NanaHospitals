import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/video/')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Video consultations">{null}</PortalPage>;
}
