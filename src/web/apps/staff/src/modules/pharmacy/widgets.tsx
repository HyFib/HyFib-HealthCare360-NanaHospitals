import { Badge, Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { ArrowRightRegular, ShieldKeyholeRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { pharmacyQueries } from './api';
import { usePharmacyLive } from './counter';
import { PHARMACY_PERMISSIONS as P, QUEUE_READERS } from './permissions';

const useStyles = makeStyles({
  counts: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(110px, 1fr))',
    gap: '8px',
    marginBottom: '12px',
  },
  count: {
    display: 'flex',
    flexDirection: 'column',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    cursor: 'pointer',
    border: 'none',
    textAlign: 'start',
    ':hover': { backgroundColor: tokens.colorNeutralBackground2Hover },
  },
  value: {
    fontSize: tokens.fontSizeBase600,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightBase600,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  rows: { display: 'flex', flexDirection: 'column', gap: '6px' },
  row: { display: 'flex', justifyContent: 'space-between', gap: '8px', alignItems: 'center' },
  late: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
});

/** Dashboard widget: what is waiting at the pharmacy counters, oldest first. */
export function PharmacyQueueWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canSeeQueue = usePermission(QUEUE_READERS);
  const canSeeDashboard = usePermission([P.queueRead, P.sales, P.ipIssue]);
  usePharmacyLive();
  const dashboard = useQuery({ ...pharmacyQueries.dashboard(null), enabled: canSeeDashboard });
  const queue = useQuery({ ...pharmacyQueries.queue({ inpatient: false }), enabled: canSeeQueue });

  if (dashboard.error && !dashboard.data) {
    return <ErrorState error={dashboard.error} onRetry={() => void dashboard.refetch()} title="Pharmacy queue" />;
  }
  const d = dashboard.data;
  const oldest = (queue.data?.items ?? []).slice(0, 5);
  const value = (n: number | undefined) => (d ? (n ?? 0).toLocaleString('en-IN') : '…');

  return (
    <Section
      title="Pharmacy queue"
      description={
        d?.averageWaitMinutes != null
          ? `Average wait today ${Math.round(d.averageWaitMinutes)} min`
          : 'Prescriptions waiting to be dispensed'
      }
      actions={
        <Button
          appearance="subtle"
          icon={<ArrowRightRegular />}
          iconPosition="after"
          onClick={() => void navigate({ to: '/pharmacy/queue' })}
        >
          Open queue
        </Button>
      }
    >
      <div className={styles.counts}>
        <button type="button" className={styles.count} onClick={() => void navigate({ to: '/pharmacy/queue' })}>
          <span className={styles.value}>{value(d?.pendingOutpatient)}</span>
          <Caption1 className={styles.muted}>OP waiting</Caption1>
        </button>
        <button
          type="button"
          className={styles.count}
          onClick={() => void navigate({ to: '/pharmacy/queue', search: { ip: true } })}
        >
          <span className={styles.value}>{value(d?.pendingInpatient)}</span>
          <Caption1 className={styles.muted}>IP prescriptions</Caption1>
        </button>
        <button type="button" className={styles.count} onClick={() => void navigate({ to: '/pharmacy/ward-indents' })}>
          <span className={styles.value}>{value(d?.pendingWardIndents)}</span>
          <Caption1 className={styles.muted}>Ward indents</Caption1>
        </button>
        <button type="button" className={styles.count} onClick={() => void navigate({ to: '/pharmacy' })}>
          <span className={styles.value}>{d ? formatInr(d.salesAmountToday, { whole: true }) : '…'}</span>
          <Caption1 className={styles.muted}>{d ? `${d.salesToday} sales today` : 'Sales today'}</Caption1>
        </button>
      </div>
      {canSeeQueue && (
        <div className={styles.rows}>
          {oldest.length === 0 && queue.data && (
            <Text className={styles.muted}>
              No one is waiting. New e-prescriptions appear here as doctors sign them.
            </Text>
          )}
          {oldest.map((e) => (
            <div key={e.id} className={styles.row}>
              <span>
                <Text weight="semibold">{e.patientName}</Text>{' '}
                <Caption1 className={styles.muted}>
                  {e.prescriptionNumber} · {e.openLines} of {e.lineCount} items
                </Caption1>{' '}
                {e.hasControlledDrugs && (
                  <Badge size="small" color="danger" icon={<ShieldKeyholeRegular />}>
                    Controlled
                  </Badge>
                )}
              </span>
              <Caption1 className={e.waitingMinutes >= 20 ? styles.late : styles.muted}>
                {e.waitingMinutes} min
              </Caption1>
            </div>
          ))}
        </div>
      )}
    </Section>
  );
}
