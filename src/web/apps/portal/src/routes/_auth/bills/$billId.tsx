import { createFileRoute } from '@tanstack/react-router';
import { PortalPage } from '@/components/PortalPage';

export const Route = createFileRoute('/_auth/bills/$billId')({
  component: StubPage,
});

function StubPage() {
  return <PortalPage title="Bill">{null}</PortalPage>;
}
