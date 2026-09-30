import { Button, Caption1, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { ArrowRightRegular } from '@fluentui/react-icons';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { assetQueries } from './api';
import { dueKindLabel } from './labels';
import { dueLabel } from './ticketFlow';

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
    border: 'none',
    cursor: 'pointer',
    textAlign: 'start',
    ':hover': { backgroundColor: tokens.colorNeutralBackground2Hover },
  },
  value: {
    fontSize: tokens.fontSizeBase600,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightBase600,
  },
  alert: { color: tokens.colorPaletteRedForeground1 },
  muted: { color: tokens.colorNeutralForeground3 },
  rows: { display: 'flex', flexDirection: 'column', gap: '6px' },
  row: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: '2px 12px', alignItems: 'baseline' },
});

/** Dashboard widget for biomedical engineering: open breakdowns and what falls due in the next two weeks. */
export function EquipmentAttentionWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const dashboard = useQuery(assetQueries.dashboard());
  const due = useQuery(assetQueries.due(14));

  if (dashboard.error && !dashboard.data) {
    return <ErrorState error={dashboard.error} onRetry={() => void dashboard.refetch()} title="Equipment" />;
  }
  const d = dashboard.data;
  const n = (value: number | undefined) => (value === undefined ? '…' : value.toLocaleString('en-IN'));
  const items = (due.data ?? []).slice(0, 5);

  return (
    <Section
      title="Equipment needing attention"
      description="Breakdowns, preventive maintenance and calibration"
      actions={
        <Button
          appearance="subtle"
          icon={<ArrowRightRegular />}
          iconPosition="after"
          onClick={() => void navigate({ to: '/assets' })}
        >
          Open
        </Button>
      }
    >
      <div className={styles.counts}>
        <button type="button" className={styles.count} onClick={() => void navigate({ to: '/assets/tickets' })}>
          <span className={mergeClasses(styles.value, d && d.criticalOpenTickets > 0 ? styles.alert : '')}>
            {n(d?.openTickets)}
          </span>
          <Caption1 className={styles.muted}>
            {d && d.criticalOpenTickets > 0 ? `open (${d.criticalOpenTickets} critical)` : 'open breakdowns'}
          </Caption1>
        </button>
        <button type="button" className={styles.count} onClick={() => void navigate({ to: '/assets/maintenance' })}>
          <span className={mergeClasses(styles.value, d && d.pmOverdue > 0 ? styles.alert : '')}>
            {n(d?.pmOverdue)}
          </span>
          <Caption1 className={styles.muted}>PM overdue</Caption1>
        </button>
        <button type="button" className={styles.count} onClick={() => void navigate({ to: '/assets/maintenance' })}>
          <span className={styles.value}>{n(d?.pmDueThisWeek)}</span>
          <Caption1 className={styles.muted}>PM this week</Caption1>
        </button>
        <button type="button" className={styles.count} onClick={() => void navigate({ to: '/assets/maintenance' })}>
          <span className={styles.value}>{n(d?.calibrationDue)}</span>
          <Caption1 className={styles.muted}>calibrations due</Caption1>
        </button>
      </div>
      <div className={styles.rows}>
        {due.data && items.length === 0 && <Text className={styles.muted}>Nothing falls due in the next 14 days.</Text>}
        {items.map((item) => (
          <div key={`${item.kind}-${item.assetId}-${item.dueDate}-${item.title}`} className={styles.row}>
            <span>
              <Text weight="semibold">{item.assetName}</Text>{' '}
              <Caption1 className={styles.muted}>
                {item.assetTag} · {dueKindLabel[item.kind] ?? item.kind}: {item.title}
              </Caption1>
            </span>
            <Caption1 className={item.daysToDue < 0 ? styles.alert : styles.muted}>{dueLabel(item.daysToDue)}</Caption1>
          </div>
        ))}
      </div>
    </Section>
  );
}
