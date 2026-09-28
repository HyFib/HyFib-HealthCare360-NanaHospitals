import { Field, Select } from '@fluentui/react-components';
import { FilterBar, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { ALERT_SEVERITIES, ALERT_STATUSES, rpmQueries, type RpmAlertSeverity, type RpmAlertStatus } from '@/modules/virtualcare/rpm/api';
import { AlertsTable } from '@/modules/virtualcare/rpm/AlertsTable';
import { alertSeverityText, alertStatusText } from '@/modules/virtualcare/rpm/labels';
import { useRpmLive } from '@/modules/virtualcare/rpm/useRpmLive';

interface AlertsSearch {
  status?: RpmAlertStatus;
  severity?: RpmAlertSeverity;
  page?: number;
}

export const Route = createFileRoute('/_app/rpm/alerts')({
  validateSearch: (s: Record<string, unknown>): AlertsSearch => ({
    status: ALERT_STATUSES.includes(s.status as RpmAlertStatus) ? (s.status as RpmAlertStatus) : undefined,
    severity: ALERT_SEVERITIES.includes(s.severity as RpmAlertSeverity) ? (s.severity as RpmAlertSeverity) : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: RpmAlertsPage,
});

/** The whole alert queue (RPM-03/05), including resolved alerts for review. */
function RpmAlertsPage() {
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  useRpmLive();
  const alerts = useQuery(rpmQueries.alerts({ status: search.status, severity: search.severity, page: search.page }));
  const set = (patch: Partial<AlertsSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });

  return (
    <Page>
      <PageHeader
        title="Monitoring alerts"
        subtitle="Mark alerts as seen when someone is looking at them, and resolve them with what was done."
        breadcrumbs={[{ label: 'Remote monitoring', onClick: () => void navigate({ to: '/rpm' }) }, { label: 'Alerts' }]}
      />
      <Section flush>
        <div style={{ padding: '0 16px 12px' }}>
          <FilterBar>
            <Field label="Status">
              <Select value={search.status ?? ''} onChange={(_, d) => set({ status: (d.value || undefined) as RpmAlertStatus | undefined })}>
                <option value="">New or seen</option>
                {ALERT_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {alertStatusText[s].label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Severity">
              <Select value={search.severity ?? ''} onChange={(_, d) => set({ severity: (d.value || undefined) as RpmAlertSeverity | undefined })}>
                <option value="">Any</option>
                {ALERT_SEVERITIES.map((s) => (
                  <option key={s} value={s}>
                    {alertSeverityText[s].label}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <AlertsTable
          alerts={alerts.data?.items}
          loading={alerts.isLoading}
          error={alerts.error}
          onRetry={() => void alerts.refetch()}
          onOpen={(a) => void navigate({ to: '/rpm/enrollments/$enrollmentId', params: { enrollmentId: a.enrollmentId }, search: { alert: a.id } })}
          emptyTitle={search.status === 'Resolved' ? 'No resolved alerts' : 'No alerts need attention'}
          page={search.page ?? 1}
          pageSize={25}
          totalCount={alerts.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
        />
      </Section>
    </Page>
  );
}
