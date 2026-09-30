import { Caption1, makeStyles, ProgressBar, Text, tokens } from '@fluentui/react-components';
import {
  ChatHelpRegular,
  ClockRegular,
  ContactCardRegular,
  PeopleQueueRegular,
  PersonAddRegular,
  StethoscopeRegular,
} from '@fluentui/react-icons';
import { humanize, useRealtimeEvent, useSession } from '@hc360/core';
import { ErrorState, KpiCard, Section } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { frontOfficeQueries, type FrontOfficeDashboard } from './api';

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))' },
  rows: { display: 'flex', flexDirection: 'column', gap: '10px' },
  row: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: '4px 12px', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  bars: { display: 'flex', flexDirection: 'column', gap: '8px' },
});

/** Keeps front-office numbers live: any visit/queue change in the facility refreshes the dashboard. */
function useLiveDashboard() {
  const facilityId = useSession((s) => s.user?.facilityId);
  const queryClient = useQueryClient();
  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'visits.changed', () => {
    void queryClient.invalidateQueries({ queryKey: ['front-office', 'dashboard'] });
  });
  return useQuery(frontOfficeQueries.dashboard());
}

export function FrontOfficeKpis() {
  const styles = useStyles();
  const navigate = useNavigate();
  const dashboard = useLiveDashboard();

  if (dashboard.error) {
    return <ErrorState error={dashboard.error} onRetry={() => void dashboard.refetch()} />;
  }

  const d = dashboard.data;
  const value = (n: number | undefined) => (d ? (n ?? 0).toLocaleString('en-IN') : '…');

  return (
    <div className={styles.kpis}>
      <KpiCard
        label="New registrations"
        value={value(d?.registrationsToday)}
        icon={<PersonAddRegular />}
        hint="Today"
        onClick={() => void navigate({ to: '/patients' })}
      />
      <KpiCard
        label="OP visits"
        value={value(d?.visitsToday)}
        icon={<StethoscopeRegular />}
        hint={d ? `${d.completedVisits} completed · ${d.openVisits} open` : undefined}
        onClick={() => void navigate({ to: '/front-office/visits' })}
      />
      <KpiCard
        label="Waiting now"
        value={value(d?.waitingTokens)}
        icon={<PeopleQueueRegular />}
        hint="Tokens not yet called"
        onClick={() => void navigate({ to: '/front-office/queue' })}
      />
      <KpiCard
        label="Average wait"
        value={d ? (d.averageWaitMinutes === null ? '—' : `${Math.round(d.averageWaitMinutes)} min`) : '…'}
        icon={<ClockRegular />}
        hint="Token issue to call, today"
      />
      <KpiCard
        label="Open enquiries"
        value={value(d?.openEnquiries)}
        icon={<ChatHelpRegular />}
        onClick={() => void navigate({ to: '/front-office/enquiries' })}
      />
      <KpiCard
        label="Visitors inside"
        value={value(d?.visitorsInside)}
        icon={<ContactCardRegular />}
        onClick={() => void navigate({ to: '/front-office/visitors' })}
      />
    </div>
  );
}

export function DepartmentQueues({ dashboard }: { dashboard?: FrontOfficeDashboard }) {
  const styles = useStyles();
  const live = useLiveDashboard();
  const d = dashboard ?? live.data;
  const departments = d?.departments ?? [];

  return (
    <Section title="Department queues" description="Waiting patients and the token being served">
      {departments.length === 0 ? (
        <Text className={styles.muted}>No tokens issued yet today.</Text>
      ) : (
        <div className={styles.rows}>
          {departments.map((dept) => {
            const total = dept.waiting + dept.served;
            return (
              <div key={dept.departmentId} className={styles.row}>
                <Text weight="semibold">{dept.departmentName}</Text>
                <Caption1>
                  {dept.nowServing ? `Now: ${dept.nowServing}` : 'Not started'} · {dept.waiting} waiting
                </Caption1>
                <div style={{ gridColumn: '1 / -1' }}>
                  <ProgressBar
                    value={total ? dept.served / total : 0}
                    thickness="large"
                    aria-label={`${dept.departmentName}: ${dept.served} of ${total} served`}
                  />
                </div>
              </div>
            );
          })}
        </div>
      )}
    </Section>
  );
}

export function VisitsByType({ dashboard }: { dashboard?: FrontOfficeDashboard }) {
  const styles = useStyles();
  const items = dashboard?.visitsByType ?? [];
  const max = Math.max(1, ...items.map((i) => i.count));
  return (
    <Section title="Visits by type" description="Today">
      {items.length === 0 ? (
        <Text className={styles.muted}>No visits yet today.</Text>
      ) : (
        <div className={styles.bars}>
          {items.map((item) => (
            <div key={item.type} className={styles.row}>
              <Text>{humanize(item.type)}</Text>
              <Text weight="semibold">{item.count}</Text>
              <div style={{ gridColumn: '1 / -1' }}>
                <ProgressBar
                  value={item.count / max}
                  color="brand"
                  aria-label={`${humanize(item.type)}: ${item.count}`}
                />
              </div>
            </div>
          ))}
        </div>
      )}
    </Section>
  );
}
