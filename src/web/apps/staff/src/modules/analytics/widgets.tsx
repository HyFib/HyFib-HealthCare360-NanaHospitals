import { Caption1, makeStyles, tokens } from '@fluentui/react-components';
import { ChevronRightRegular } from '@fluentui/react-icons';
import { formatRelative } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { analyticsQueries } from './api';
import { KpiTile, KpiTileSkeleton } from './KpiTile';

const useStyles = makeStyles({
  grid: { display: 'grid', gap: '8px', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))' },
  footer: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '8px', marginTop: '10px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  link: { display: 'inline-flex', alignItems: 'center', gap: '2px', color: tokens.colorBrandForeground1, textDecoration: 'none', ':hover': { textDecoration: 'underline' } },
});

/** Home-page snapshot: this month's headline KPIs against last month's same days, one click from the full dashboard. */
export function AnalyticsSnapshotWidget() {
  const styles = useStyles();
  const kpis = useQuery(analyticsQueries.kpis(undefined, undefined, undefined));

  if (kpis.error && !kpis.data) {
    return <ErrorState error={kpis.error} onRetry={() => void kpis.refetch()} />;
  }

  const shown = kpis.data?.kpis.slice(0, 4) ?? [];
  return (
    <Section title="Hospital at a glance" description="This month so far, compared with the same number of days before">
      <div className={styles.grid}>
        {kpis.data ? shown.map((kpi) => <KpiTile key={kpi.key} kpi={kpi} inputs={{ period: 'thisMonth' }} compact />) : [0, 1, 2, 3].map((i) => <KpiTileSkeleton key={i} />)}
      </div>
      <div className={styles.footer}>
        <Caption1 className={styles.muted}>{kpis.data ? `Updated ${formatRelative(kpis.data.generatedAt)}` : ' '}</Caption1>
        <Link to="/analytics" className={styles.link}>
          Executive dashboard
          <ChevronRightRegular aria-hidden />
        </Link>
      </div>
    </Section>
  );
}
