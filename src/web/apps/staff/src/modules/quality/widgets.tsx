import { Badge, Caption1, makeStyles, Spinner, Text, tokens } from '@fluentui/react-components';
import { AlertUrgentRegular, ChevronRightRegular } from '@fluentui/react-icons';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { qualityQueries } from './api';
import { incidentStatusLabel, INCIDENT_STATUSES, type IncidentStatus } from './labels';

const useStyles = makeStyles({
  head: { display: 'flex', alignItems: 'baseline', gap: '10px', flexWrap: 'wrap' },
  big: {
    fontSize: tokens.fontSizeHero800,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightHero800,
  },
  rows: { display: 'flex', flexDirection: 'column', gap: '2px', marginTop: '8px' },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorNeutralForeground1,
    textDecoration: 'none',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  grow: { flex: 1 },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** "Open incidents": the quality team's queue by stage, sentinel events and overdue actions, each one click away. */
export function OpenIncidentsWidget() {
  const styles = useStyles();
  const dashboard = useQuery(qualityQueries.dashboard());

  if (dashboard.error) {
    return <ErrorState error={dashboard.error} onRetry={() => void dashboard.refetch()} />;
  }

  const d = dashboard.data;
  const byStatus = new Map((d?.openByStatus ?? []).map((item) => [item.key as IncidentStatus, item.count]));

  return (
    <Section title="Open incidents" description="Incidents still being reviewed, investigated or acted on">
      {!d ? (
        <Spinner size="tiny" label="Loading…" labelPosition="after" />
      ) : (
        <>
          <div className={styles.head}>
            <span className={styles.big}>{d.openIncidents}</span>
            <Text className={styles.muted}>open</Text>
            {d.sentinelEvents > 0 && (
              <Badge color="danger" icon={<AlertUrgentRegular />}>
                {d.sentinelEvents} sentinel in the last 12 months
              </Badge>
            )}
            {d.capas.overdue > 0 && (
              <Badge color="warning" appearance="tint">
                {d.capas.overdue} action(s) overdue
              </Badge>
            )}
          </div>
          <div className={styles.rows}>
            {INCIDENT_STATUSES.filter((status) => status !== 'Closed').map((status) => (
              <Link key={status} to="/quality/incidents" search={{ status }} className={styles.row}>
                <Text className={styles.grow}>{incidentStatusLabel[status]}</Text>
                <Text weight="semibold">{byStatus.get(status) ?? 0}</Text>
                <ChevronRightRegular aria-hidden />
              </Link>
            ))}
          </div>
          <Caption1 className={styles.muted}>{d.totalIncidents} reported in the last 12 months.</Caption1>
        </>
      )}
    </Section>
  );
}
