import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/my-feedback/')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Feedback">{null}</PortalPage>;
}
