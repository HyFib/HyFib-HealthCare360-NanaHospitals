import { makeStyles, Tab, TabList, tokens } from '@fluentui/react-components';
import { DocumentTextRegular } from '@fluentui/react-icons';
import { EmptyState } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { CardLink } from '@/components/links';
import { ListRow } from '@/components/ListRow';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useT, type MessageKey } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import type { PortalReportItem } from '@/lib/types';
import { isReportKind, kindSegment, reportQueries, type ReportKindSegment } from '@/modules/reports/api';
import { REPORT_KIND } from '@/modules/reports/icons';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/reports/')({
  validateSearch: (search: Record<string, unknown>): { kind?: ReportKindSegment } => ({ kind: isReportKind(search.kind) ? search.kind : undefined }),
  component: ReportsPage,
});

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px' },
  month: { margin: '12px 0 2px', fontSize: tokens.fontSizeBase300, fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground3 },
});

const FILTERS: { value: 'all' | ReportKindSegment; label: MessageKey }[] = [
  { value: 'all', label: 'reports.filter.all' },
  { value: 'lab', label: 'reports.filter.lab' },
  { value: 'radiology', label: 'reports.filter.radiology' },
  { value: 'cardiology', label: 'reports.filter.cardiology' },
];

function ReportsPage() {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { kind } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const { profile } = useActiveProfile();
  const reports = useQuery(reportQueries.list(profile.id));
  const rows = (reports.data ?? []).filter((item) => !kind || kindSegment(item.kind) === kind);

  // Month headings help people find "the blood test from March".
  const groups: { month: string; items: PortalReportItem[] }[] = [];
  for (const item of rows) {
    const month = f.monthYear(item.releasedAt);
    const last = groups.at(-1);
    if (last && last.month === month) {
      last.items.push(item);
    } else {
      groups.push({ month, items: [item] });
    }
  }

  return (
    <PortalPage title={t('reports.title')} subtitle={t('reports.subtitle')}>
      <TabList
        selectedValue={kind ?? 'all'}
        onTabSelect={(_, data) => void navigate({ search: { kind: data.value === 'all' ? undefined : (data.value as ReportKindSegment) }, replace: true })}
        aria-label={t('reports.filter.label')}
        size="large"
      >
        {FILTERS.map((filter) => (
          <Tab key={filter.value} value={filter.value}>
            {t(filter.label)}
          </Tab>
        ))}
      </TabList>

      <div className={styles.list} role="tabpanel">
        {reports.isPending ? (
          <LoadingCards count={4} />
        ) : reports.isError ? (
          <ErrorBlock error={reports.error} onRetry={() => void reports.refetch()} />
        ) : rows.length === 0 ? (
          <EmptyState icon={<DocumentTextRegular />} title={t('reports.empty.title')} description={t('reports.empty.body')} />
        ) : (
          groups.map((group) => (
            <section key={group.month} className={styles.list} aria-label={group.month}>
              <h2 className={styles.month}>{group.month}</h2>
              {group.items.map((item) => {
                const kindInfo = REPORT_KIND[item.kind];
                const Icon = kindInfo.icon;
                return (
                  <CardLink key={`${item.kind}-${item.id}`} to="/reports/$kind/$reportId" params={{ kind: kindSegment(item.kind), reportId: item.id }}>
                    <ListRow
                      icon={<Icon />}
                      tone={kindInfo.tone}
                      title={item.title || t(kindInfo.label)}
                      subtitle={`${t(kindInfo.label)} · ${t('reports.released', { date: f.date(item.releasedAt) })}`}
                      meta={t('reports.number', { number: item.reference })}
                      trailing={
                        item.isAmended || item.isPartial ? (
                          <>
                            {item.isAmended && <StatusPill tone="warning">{t('reports.amended')}</StatusPill>}
                            {item.isPartial && <StatusPill tone="neutral">{t('reports.partial')}</StatusPill>}
                          </>
                        ) : undefined
                      }
                    />
                  </CardLink>
                );
              })}
            </section>
          ))
        )}
      </div>
    </PortalPage>
  );
}
