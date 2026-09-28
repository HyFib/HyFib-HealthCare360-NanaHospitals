import { Button, Caption1, makeStyles, ProgressBar, Text, tokens } from '@fluentui/react-components';
import { formatTime } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { checkupQueries } from './api';
import { countByStatus, isStarted, progress } from './itinerary';

const useStyles = makeStyles({
  counts: { display: 'flex', gap: '16px', flexWrap: 'wrap', marginBottom: '8px' },
  count: { display: 'flex', flexDirection: 'column' },
  value: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  list: { display: 'flex', flexDirection: 'column', gap: '6px', margin: 0, padding: 0, listStyle: 'none' },
  row: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: '2px 8px', alignItems: 'center' },
});

/** Today's health check-ups: how many are waiting, in progress and done, and who is where. */
export function TodaysCheckupsWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const bookings = useQuery(checkupQueries.bookings({}));
  const items = bookings.data?.items ?? [];
  const counts = countByStatus(items);
  const active = items.filter((b) => isStarted(b.status)).slice(0, 5);

  return (
    <Section
      title="Today's health check-ups"
      actions={
        <Button size="small" appearance="subtle" onClick={() => void navigate({ to: '/health-checkups' })}>
          Open day view
        </Button>
      }
    >
      {bookings.error ? (
        <ErrorState error={bookings.error} onRetry={() => void bookings.refetch()} />
      ) : (
        <>
          <div className={styles.counts}>
            {[
              ['Expected', counts.Booked],
              ['In progress', counts.CheckedIn + counts.InProgress],
              ['Completed', counts.Completed],
              ['Did not come', counts.NoShow],
            ].map(([label, value]) => (
              <span key={label} className={styles.count}>
                <span className={styles.value}>{bookings.data ? value : '…'}</span>
                <Caption1 className={styles.muted}>{label}</Caption1>
              </span>
            ))}
          </div>
          {active.length === 0 ? (
            <Text className={styles.muted}>{items.length === 0 ? 'No check-ups booked for today.' : 'Nobody is on the check-up floor right now.'}</Text>
          ) : (
            <ul className={styles.list}>
              {active.map((b) => {
                const p = progress(b.itinerary);
                return (
                  <li key={b.id} className={styles.row}>
                    <Text weight="semibold">{b.patientName}</Text>
                    <Caption1 className={styles.muted}>
                      {p.done + p.skipped}/{p.total} · since {formatTime(b.checkedInAt)}
                    </Caption1>
                    <div style={{ gridColumn: '1 / -1' }}>
                      <ProgressBar value={p.ratio} thickness="large" aria-label={`${b.patientName}: ${p.done + p.skipped} of ${p.total} stations done`} />
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </>
      )}
    </Section>
  );
}
