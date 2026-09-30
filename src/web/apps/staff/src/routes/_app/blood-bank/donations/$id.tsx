import { Button } from '@fluentui/react-components';
import { PrintRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { bloodQueries, type DonationDetail } from '@/modules/blood-bank/api';
import { ComponentsTable, LabResultsPanel, SeparationPanel } from '@/modules/blood-bank/DonationPanels';
import { bagTypeLabel, donationStatusLabel, donationTypeLabel } from '@/modules/blood-bank/labels';
import { printUnitLabels } from '@/modules/blood-bank/print';
import { Detail, DetailGrid, NoAccess, RecordLoadError } from '@/modules/ot/shared/feedback';
import { useNow } from '@/modules/ot/shared/hooks';

export const Route = createFileRoute('/_app/blood-bank/donations/$id')({
  component: DonationPage,
});

function DonationPage() {
  const { id } = Route.useParams();
  const canManage = usePermission('bloodbank.inventory.manage');
  const donation = useQuery({ ...bloodQueries.donation(id), enabled: canManage });
  if (!canManage) {
    return (
      <Page>
        <NoAccess what="donations" />
      </Page>
    );
  }
  if (donation.isLoading) {
    return <LoadingState label="Loading the donation…" />;
  }
  if (donation.error || !donation.data) {
    return (
      <Page maxWidth={900}>
        <RecordLoadError error={donation.error} onRetry={() => void donation.refetch()} what="donation" />
      </Page>
    );
  }
  return <DonationView donation={donation.data} />;
}

function DonationView({ donation: d }: { donation: DonationDetail }) {
  const navigate = useNavigate();
  const notify = useNotify();
  const now = useNow(60_000);
  const canManage = usePermission('bloodbank.inventory.manage');

  return (
    <Page>
      <PageHeader
        title={`Bag ${d.bagNumber}`}
        subtitle={`${d.donorName} (${d.donorNumber}) · collected ${formatDateTime(d.collectedAt)}${d.collectedByName ? ` by ${d.collectedByName}` : ''}`}
        breadcrumbs={[
          { label: 'Blood stock', onClick: () => void navigate({ to: '/blood-bank' }) },
          { label: 'Donations', onClick: () => void navigate({ to: '/blood-bank/donations' }) },
          { label: d.bagNumber },
        ]}
        actions={
          <>
            <StatusBadge
              status={d.status}
              tone={d.status === 'Discarded' ? 'danger' : d.status === 'Separated' ? 'success' : 'warning'}
              label={donationStatusLabel[d.status]}
            />
            {d.components.length > 0 && (
              <Button
                icon={<PrintRegular />}
                onClick={() =>
                  void printUnitLabels(d.components).catch((error: unknown) => notify.error(error, 'Printing failed'))
                }
              >
                Print all labels
              </Button>
            )}
            <Button onClick={() => void navigate({ to: '/blood-bank/donors/$id', params: { id: d.donorId } })}>
              Open donor
            </Button>
          </>
        }
      />

      <Section title="Collection">
        <DetailGrid>
          <Detail label="Bag">{`${bagTypeLabel[d.bagType]} · ${d.volumeMl} ml`}</Detail>
          <Detail label="Donation">{donationTypeLabel[d.type]}</Detail>
          <Detail label="Separated">{d.separatedAt ? formatDateTime(d.separatedAt) : null}</Detail>
          <Detail label="Donor reaction">{d.donorReaction}</Detail>
          <Detail label="Notes">{d.notes}</Detail>
        </DetailGrid>
      </Section>

      <Section
        title="Grouping and TTI screening"
        description="Units stay in quarantine until the group is recorded and all five markers are non-reactive."
      >
        <LabResultsPanel donation={d} canManage={canManage} />
      </Section>

      {d.status === 'Collected' && canManage && (
        <Section
          title="Separate components"
          description="Whole blood alone, or packed red cells with plasma, platelets or cryoprecipitate."
        >
          <SeparationPanel donation={d} now={now} />
        </Section>
      )}

      <Section title={`Units (${d.components.length})`} flush>
        <ComponentsTable units={d.components} now={now} canManage={canManage} />
      </Section>
    </Page>
  );
}
