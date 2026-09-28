import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/verify/$kind/$code')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Check result">{null}</PortalPage>;
}
