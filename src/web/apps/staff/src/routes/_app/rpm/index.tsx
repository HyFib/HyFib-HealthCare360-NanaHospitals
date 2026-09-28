import { Badge, Button, Caption1, Field, makeStyles, SearchBox, Select, Text, tokens } from '@fluentui/react-components';
import {
  AlertUrgentRegular,
  ClipboardTaskListLtrRegular,
  DataTrendingRegular,
  HeartPulseRegular,
  PersonAddRegular,
  PlugDisconnectedRegular,
  WarningRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatRelative, useDebouncedValue } from '@hc360/core';
import { DataTable, ErrorState, FilterBar, KpiCard, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { istDate } from '@/modules/scheduling/dates';
import { useToday } from '@/modules/scheduling/useNow';
import { ENROLLMENT_STATUSES, rpmQueries, type RpmEnrollmentStatus } from '@/modules/virtualcare/rpm/api';
import { AlertsTable } from '@/modules/virtualcare/rpm/AlertsTable';
import { EnrollDialog } from '@/modules/virtualcare/rpm/EnrollmentDialogs';
import { enrollmentStatusText, programKindLabel } from '@/modules/virtualcare/rpm/labels';
import { useRpmLive } from '@/modules/virtualcare/rpm/useRpmLive';

interface RpmSearch {
  q?: string;
  status?: RpmEnrollmentStatus;
  programId?: string;
  page?: number;
}

export const Route = createFileRoute('/_app/rpm/')({
  validateSearch: (s: Record<string, unknown>): RpmSearch => ({
    q: typeof s.q === 'string' && s.q ? s.q : undefined,
    status: ENROLLMENT_STATUSES.includes(s.status as RpmEnrollmentStatus) ? (s.status as RpmEnrollmentStatus) : undefined,
    programId: typeof s.programId === 'string' && s.programId ? s.programId : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: RpmDashboardPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))' },
  toolbar: { padding: '0 16px 12px' },
  stack: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  stale: { color: tokens.colorPaletteMarigoldForeground1 },
  badges: { display: 'flex', gap: '4px', flexWrap: 'wrap' },
  danger: { color: tokens.colorPaletteRedForeground1 },
});

/** Remote patient monitoring (RPM-01..05): alerts first by severity, then the monitored patients. */
function RpmDashboardPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const canManage = usePermission('rpm.manage');
  const today = useToday();
  const [enrolling, setEnrolling] = useState(false);
  const [term, setTerm] = useState(search.q ?? '');
  const debounced = useDebouncedValue(term.trim(), 300);
  useRpmLive();

  const dashboard = useQuery(rpmQueries.dashboard());
  const alerts = useQuery(rpmQueries.alerts({ pageSize: 8 }));
  const programs = useQuery(rpmQueries.programs());
  const enrollments = useQuery(rpmQueries.enrollments({ search: search.q, status: search.status, programId: search.programId, page: search.page }));

  useEffect(() => {
    if ((debounced || undefined) !== search.q) {
      void navigate({ search: (prev) => ({ ...prev, q: debounced || undefined, page: undefined }), replace: true });
    }
  }, [debounced, search.q, navigate]);

  const set = (patch: Partial<RpmSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const d = dashboard.data;
  const value = (n: number | undefined) => (d ? (n ?? 0).toLocaleString('en-IN') : '…');
  const openEnrollment = (id: string, alertId?: string) =>
    void navigate({ to: '/rpm/enrollments/$enrollmentId', params: { enrollmentId: id }, search: alertId ? { alert: alertId } : {} });

  return (
    <Page>
      <PageHeader
        title="Remote monitoring"
        subtitle="Patients monitored at home. Critical alerts are listed first."
        actions={
          <>
            <Button icon={<PlugDisconnectedRegular />} onClick={() => void navigate({ to: '/rpm/devices' })}>
              Devices
            </Button>
            <Button icon={<ClipboardTaskListLtrRegular />} onClick={() => void navigate({ to: '/rpm/programs' })}>
              Programmes
            </Button>
            {canManage && (
              <Button appearance="primary" icon={<PersonAddRegular />} onClick={() => setEnrolling(true)}>
                Enrol patient
              </Button>
            )}
          </>
        }
      />

      {dashboard.error ? (
        <ErrorState error={dashboard.error} onRetry={() => void dashboard.refetch()} />
      ) : (
        <div className={styles.kpis}>
          <KpiCard
            label="Critical alerts"
            value={<span className={d && d.criticalAlerts > 0 ? styles.danger : undefined}>{value(d?.criticalAlerts)}</span>}
            icon={<AlertUrgentRegular />}
            hint={d ? `${d.unacknowledgedCritical} not yet seen` : undefined}
            onClick={() => void navigate({ to: '/rpm/alerts', search: { severity: 'Critical' } })}
          />
          <KpiCard label="Open alerts" value={value(d?.openAlerts)} icon={<WarningRegular />} onClick={() => void navigate({ to: '/rpm/alerts' })} />
          <KpiCard label="Patients monitored" value={value(d?.activeEnrollments)} icon={<HeartPulseRegular />} hint={d ? `${d.pausedEnrollments} paused` : undefined} />
          <KpiCard label="No reading today" value={value(d?.enrollmentsWithoutReadingToday)} icon={<DataTrendingRegular />} hint="Active patients" />
          <KpiCard label="Silent devices" value={value(d?.silentDevices)} icon={<PlugDisconnectedRegular />} hint="No contact for a day" onClick={() => void navigate({ to: '/rpm/devices' })} />
          <KpiCard label="Readings, last 24 h" value={value(d?.readingsLast24Hours)} icon={<DataTrendingRegular />} />
        </div>
      )}

      <Section
        title="Alerts needing attention"
        description="New and seen alerts, critical first"
        flush
        actions={
          <Button appearance="transparent" onClick={() => void navigate({ to: '/rpm/alerts' })}>
            All alerts
          </Button>
        }
      >
        <AlertsTable
          alerts={alerts.data?.items}
          loading={alerts.isLoading}
          error={alerts.error}
          onRetry={() => void alerts.refetch()}
          onOpen={(a) => openEnrollment(a.enrollmentId, a.id)}
          emptyTitle="No open alerts"
          emptyDescription="Readings outside a patient's limits and devices that stop sending appear here."
        />
      </Section>

      <Section title="Monitored patients" flush>
        <div className={styles.toolbar}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 240px' }}>
              <SearchBox value={term} onChange={(_, v) => setTerm(v.value)} placeholder="Name, UHID or mobile" />
            </Field>
            <Field label="Programme">
              <Select value={search.programId ?? ''} onChange={(_, v) => set({ programId: v.value || undefined })}>
                <option value="">All programmes</option>
                {(programs.data ?? []).map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Status">
              <Select value={search.status ?? ''} onChange={(_, v) => set({ status: (v.value || undefined) as RpmEnrollmentStatus | undefined })}>
                <option value="">Active or paused</option>
                {ENROLLMENT_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {enrollmentStatusText[s].label}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Monitored patients"
          rows={enrollments.data?.items}
          loading={enrollments.isLoading}
          error={enrollments.error}
          onRetry={() => void enrollments.refetch()}
          getRowId={(e) => e.id}
          onRowClick={(e) => openEnrollment(e.id)}
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (e) => (
                <span className={styles.stack}>
                  <Text weight="semibold" truncate>
                    {e.patientName}{' '}
                    <Text size={200} weight="regular">
                      ({e.age ?? '—'} / {e.gender.charAt(0)})
                    </Text>
                  </Text>
                  <Caption1 className={styles.muted}>{e.uhid}</Caption1>
                </span>
              ),
            },
            {
              key: 'program',
              header: 'Programme',
              render: (e) => (
                <span className={styles.stack}>
                  <Text truncate>{e.programName}</Text>
                  <Caption1 className={styles.muted}>{programKindLabel[e.programKind]}</Caption1>
                </span>
              ),
            },
            {
              key: 'alerts',
              header: 'Alerts',
              width: 150,
              render: (e) =>
                e.openAlerts === 0 ? (
                  <Text className={styles.muted}>None</Text>
                ) : (
                  <span className={styles.badges}>
                    {e.criticalAlerts > 0 && (
                      <Badge color="danger" icon={<AlertUrgentRegular />}>
                        {e.criticalAlerts} critical
                      </Badge>
                    )}
                    {e.openAlerts - e.criticalAlerts > 0 && (
                      <Badge color="warning" appearance="tint">
                        {e.openAlerts - e.criticalAlerts} warning
                      </Badge>
                    )}
                  </span>
                ),
            },
            {
              key: 'last',
              header: 'Last reading',
              width: 150,
              render: (e) =>
                e.lastReadingAt ? (
                  <Text className={istDate(e.lastReadingAt) !== today && e.status === 'Active' ? styles.stale : undefined}>{formatRelative(e.lastReadingAt)}</Text>
                ) : (
                  <Text className={styles.stale}>No readings yet</Text>
                ),
            },
            {
              key: 'status',
              header: 'Status',
              width: 110,
              render: (e) => <StatusBadge status={e.status} tone={enrollmentStatusText[e.status].tone} label={enrollmentStatusText[e.status].label} />,
            },
            { key: 'since', header: 'Since', priority: 'low', width: 120, render: (e) => formatDateOnly(e.startDate) },
          ]}
          emptyTitle={search.q || search.programId || search.status ? 'No patients match' : 'No one is being monitored yet'}
          emptyDescription={canManage ? 'Enrol a patient in a programme and give them a device.' : undefined}
          emptyAction={
            canManage ? (
              <Button appearance="primary" icon={<PersonAddRegular />} onClick={() => setEnrolling(true)}>
                Enrol patient
              </Button>
            ) : undefined
          }
          page={search.page ?? 1}
          pageSize={25}
          totalCount={enrollments.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>

      <EnrollDialog
        open={enrolling}
        onClose={() => setEnrolling(false)}
        onEnrolled={(id) => {
          setEnrolling(false);
          openEnrollment(id);
        }}
      />
    </Page>
  );
}
