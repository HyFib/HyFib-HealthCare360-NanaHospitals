import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/my-feedback/$formId')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Feedback form">{null}</PortalPage>;
}
