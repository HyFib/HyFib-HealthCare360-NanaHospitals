import { Badge, Caption1, makeStyles, ProgressBar, Text, tokens } from '@fluentui/react-components';
import { formatDateOnly, formatDateTime } from '@hc360/core';
import { DataTable, ErrorState, LoadingState, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { checkupQueries } from '@/modules/health-checkups/api';
import { BOOKING_STATUSES, bookingStatusLabel, countByStatus, progress } from '@/modules/health-checkups/itinerary';

export const Route = createFileRoute('/_app/health-checkups/corporate/$batchId')({
  component: BatchPage,
});

const useStyles = makeStyles({
  counts: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  facts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))', gap: '12px 24px' },
  fact: { display: 'flex', flexDirection: 'column', gap: '2px' },
  muted: { color: tokens.colorNeutralForeground3 },
  progress: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: '120px' },
});

/** One corporate drive: who is booked and how far each employee has got. */
function BatchPage() {
  const styles = useStyles();
  const { batchId } = Route.useParams();
  const navigate = useNavigate();
  const detail = useQuery(checkupQueries.batch(batchId));
  const packages = useQuery(checkupQueries.packages('', true));

  if (detail.isLoading) {
    return <LoadingState />;
  }
  if (detail.error || !detail.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={detail.error} onRetry={() => void detail.refetch()} />
      </Page>
    );
  }

  const { batch, bookings } = detail.data;
  const counts = countByStatus(bookings);
  const pkg = packages.data?.find((p) => p.id === batch.packageId);

  return (
    <Page>
      <PageHeader
        title={batch.corporateName}
        subtitle={`${batch.batchNumber} · ${formatDateOnly(batch.scheduledDate)}${pkg ? ` · ${pkg.name}` : ''}`}
        breadcrumbs={[
          { label: 'Corporate batches', onClick: () => void navigate({ to: '/health-checkups/corporate' }) },
          { label: batch.batchNumber },
        ]}
      />
      <Section>
        <div className={styles.facts}>
          <div className={styles.fact}>
            <Caption1 className={styles.muted}>Booked</Caption1>
            <Text weight="semibold">{batch.bookedCount}</Text>
          </div>
          <div className={styles.fact}>
            <Caption1 className={styles.muted}>Not booked (ineligible)</Caption1>
            <Text>{batch.skippedCount}</Text>
          </div>
          <div className={styles.fact}>
            <Caption1 className={styles.muted}>Contact</Caption1>
            <Text>{[batch.contactPerson, batch.contactPhone].filter(Boolean).join(' · ') || '—'}</Text>
          </div>
          <div className={styles.fact}>
            <Caption1 className={styles.muted}>Reference</Caption1>
            <Text>{batch.reference ?? '—'}</Text>
          </div>
          <div className={styles.fact}>
            <Caption1 className={styles.muted}>Created</Caption1>
            <Text>{formatDateTime(batch.createdAt)}</Text>
          </div>
        </div>
        {batch.notes && (
          <Text block style={{ marginTop: 12 }}>
            {batch.notes}
          </Text>
        )}
      </Section>
      <div className={styles.counts}>
        {BOOKING_STATUSES.filter((s) => counts[s] > 0).map((s) => (
          <Badge key={s} appearance="tint" size="large">
            {bookingStatusLabel[s]}: {counts[s]}
          </Badge>
        ))}
      </div>
      <Section title={`Employees (${bookings.length})`} flush>
        <DataTable
          ariaLabel="Employees in this batch"
          rows={bookings}
          getRowId={(b) => b.id}
          onRowClick={(b) => void navigate({ to: '/health-checkups/bookings/$bookingId', params: { bookingId: b.id } })}
          size="small"
          columns={[
            {
              key: 'patient',
              header: 'Employee',
              render: (b) => (
                <span>
                  <Text weight="semibold">{b.patientName ?? '—'}</Text> <Text size={200}>· {b.uhid ?? ''}</Text>
                </span>
              ),
            },
            {
              key: 'progress',
              header: 'Stations',
              width: 160,
              render: (b) => {
                const p = progress(b.itinerary);
                return (
                  <span className={styles.progress}>
                    <Caption1>
                      {p.done + p.skipped} of {p.total}
                    </Caption1>
                    <ProgressBar value={p.ratio} aria-label={`${p.done + p.skipped} of ${p.total} stations done`} />
                  </span>
                );
              },
            },
            {
              key: 'number',
              header: 'Booking',
              width: 140,
              priority: 'low',
              render: (b) => <Text font="monospace">{b.bookingNumber}</Text>,
            },
            {
              key: 'status',
              header: 'Status',
              width: 120,
              render: (b) => <StatusBadge status={b.status} label={bookingStatusLabel[b.status]} />,
            },
          ]}
          emptyTitle="No employees booked"
        />
      </Section>
    </Page>
  );
}
