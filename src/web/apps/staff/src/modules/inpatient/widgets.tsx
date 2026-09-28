import { Button, Caption1, makeStyles, ProgressBar, Skeleton, SkeletonItem, Text, tokens } from '@fluentui/react-components';
import { ArrowExitRegular, BedRegular, ChevronRightRegular } from '@fluentui/react-icons';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { inpatientQueries } from './api';
import { occupancyBand } from './bedState';
import { useInpatientRealtime } from './hooks';
import { formatDuration } from './time';

const useStyles = makeStyles({
  rows: { display: 'flex', flexDirection: 'column', gap: '10px' },
  row: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: '2px 12px', alignItems: 'center' },
  full: { gridColumn: '1 / -1' },
  muted: { color: tokens.colorNeutralForeground3 },
  total: { display: 'flex', alignItems: 'baseline', gap: '8px', marginBottom: '12px', flexWrap: 'wrap' },
  big: { fontSize: tokens.fontSizeHero700, fontWeight: tokens.fontWeightSemibold, lineHeight: tokens.lineHeightHero700 },
  item: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: '2px 12px',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    textDecoration: 'none',
    color: tokens.colorNeutralForeground1,
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  late: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
});

const barColor = (percent: number): 'error' | 'warning' | 'brand' => {
  const band = occupancyBand(percent);
  return band === 'full' ? 'error' : band === 'busy' ? 'warning' : 'brand';
};

function Loading() {
  return (
    <Skeleton aria-label="Loading">
      {Array.from({ length: 4 }, (_, i) => (
        <SkeletonItem key={i} style={{ height: 28, marginBottom: 10 }} />
      ))}
    </Skeleton>
  );
}

/** Live bed occupancy by ward (busiest first), for bed managers, nurses and the front desk. */
export function WardOccupancyWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  useInpatientRealtime();
  const occupancy = useQuery(inpatientQueries.occupancy());

  const report = occupancy.data;
  const wards = [...(report?.wards ?? [])].filter((w) => w.counts.total > 0).sort((a, b) => b.counts.occupancyPercent - a.counts.occupancyPercent);
  const shown = wards.slice(0, 8);

  return (
    <Section
      title="Ward occupancy"
      description="Live. Blocked and maintenance beds are not counted as in service."
      actions={
        <Button size="small" appearance="subtle" icon={<BedRegular />} onClick={() => void navigate({ to: '/inpatient/beds' })}>
          Bed board
        </Button>
      }
    >
      {occupancy.error ? (
        <ErrorState error={occupancy.error} onRetry={() => void occupancy.refetch()} />
      ) : !report ? (
        <Loading />
      ) : (
        <>
          <div className={styles.total}>
            <span className={styles.big}>{report.total.occupancyPercent}%</span>
            <Text>
              {report.total.occupied} of {report.total.operational} beds occupied · <strong>{report.total.vacant} vacant</strong>
              {report.total.cleaning > 0 ? ` · ${report.total.cleaning} being cleaned` : ''}
            </Text>
          </div>
          {shown.length === 0 ? (
            <Text className={styles.muted}>No wards are set up yet.</Text>
          ) : (
            <div className={styles.rows}>
              {shown.map((ward) => (
                <div key={ward.wardId} className={styles.row}>
                  <Text weight="semibold">{ward.name}</Text>
                  <Caption1>
                    {ward.counts.occupied}/{ward.counts.operational} · {ward.counts.vacant} vacant · {ward.counts.occupancyPercent}%
                  </Caption1>
                  <div className={styles.full}>
                    <ProgressBar
                      value={Math.min(1, ward.counts.occupancyPercent / 100)}
                      color={barColor(ward.counts.occupancyPercent)}
                      thickness="large"
                      aria-label={`${ward.name}: ${ward.counts.occupancyPercent}% occupied, ${ward.counts.vacant} vacant`}
                    />
                  </div>
                </div>
              ))}
              {wards.length > shown.length && (
                <Caption1 className={styles.muted}>
                  {wards.length - shown.length} more wards on the <Link to="/inpatient/beds">bed board</Link>.
                </Caption1>
              )}
            </div>
          )}
        </>
      )}
    </Section>
  );
}

/** Discharges waiting for billing clearance, longest wait first (nursing, billing and the insurance desk). */
export function PendingDischargesWidget() {
  const styles = useStyles();
  useInpatientRealtime();
  const pending = useQuery(inpatientQueries.pendingDischarges());
  const items = pending.data ?? [];

  return (
    <Section title="Pending discharges" description={pending.data ? `${items.length} waiting for billing clearance` : 'Discharges in progress'}>
      {pending.error ? (
        <ErrorState error={pending.error} onRetry={() => void pending.refetch()} />
      ) : !pending.data ? (
        <Loading />
      ) : items.length === 0 ? (
        <Text className={styles.muted}>No discharges in progress. Started discharges appear here until they are completed.</Text>
      ) : (
        <div className={styles.rows} style={{ gap: 2 }}>
          {items.slice(0, 6).map((item) => (
            <Link key={item.admissionId} to="/inpatient/admissions/$admissionId" params={{ admissionId: item.admissionId }} className={styles.item}>
              <Text weight="semibold">{item.patientName}</Text>
              <Caption1 className={item.waitingMinutes >= 180 ? styles.late : undefined}>
                <ArrowExitRegular aria-hidden /> {formatDuration(item.waitingMinutes)}
              </Caption1>
              <Caption1 className={styles.muted}>
                {[item.wardName, item.bedLabel].filter(Boolean).join(' · ')}
                {item.consultantName ? ` · ${item.consultantName}` : ''}
              </Caption1>
              <ChevronRightRegular aria-hidden />
            </Link>
          ))}
          {items.length > 6 && (
            <Caption1 className={styles.muted}>
              And {items.length - 6} more on the <Link to="/inpatient">census page</Link>.
            </Caption1>
          )}
        </div>
      )}
    </Section>
  );
}
