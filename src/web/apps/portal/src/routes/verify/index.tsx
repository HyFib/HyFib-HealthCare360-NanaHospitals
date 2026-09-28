import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/verify/')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Check a document">{null}</PortalPage>;
}
