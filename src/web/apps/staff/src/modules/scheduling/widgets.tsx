import { Button, Caption1, makeStyles, ProgressBar, Text, tokens } from '@fluentui/react-components';
import { CalendarCheckmarkRegular, CalendarClockRegular, CalendarLtrRegular, PersonArrowRightRegular, PersonProhibitedRegular } from '@fluentui/react-icons';
import { useRealtimeEvent, useSession } from '@hc360/core';
import { ErrorState, KpiCard, Section } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { schedulingKeys, schedulingQueries } from './api';
import { utilisation } from './calendarLayout';
import { useToday } from './useNow';

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))' },
  mine: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
    padding: '10px 14px',
    marginBottom: '12px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground2,
  },
  rows: { display: 'flex', flexDirection: 'column', gap: '10px' },
  row: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: '4px 12px', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Today's summary for the facility, refreshed live whenever an appointment or visit changes. */
function useTodaySummary() {
  const facilityId = useSession((s) => s.user?.facilityId);
  const queryClient = useQueryClient();
  const today = useToday();
  const refresh = () => void queryClient.invalidateQueries({ queryKey: schedulingKeys.summary });
  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'appointments.changed', refresh);
  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'visits.changed', refresh);
  return useQuery(schedulingQueries.summary(today));
}

export function AppointmentsTodayWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const practitionerId = useSession((s) => s.user?.practitionerId);
  const summary = useTodaySummary();

  if (summary.error) {
    return <ErrorState error={summary.error} onRetry={() => void summary.refetch()} title="Appointments could not be loaded" />;
  }

  const s = summary.data;
  const value = (n: number | undefined) => (s ? (n ?? 0).toLocaleString('en-IN') : '…');
  const mine = s?.practitioners.find((p) => p.practitionerId === practitionerId);
  const open = (status?: 'Booked' | 'Confirmed' | 'CheckedIn' | 'Completed' | 'NoShow') => void navigate({ to: '/scheduling', search: { status } });

  return (
    <div>
      {mine && (
        <div className={styles.mine} role="status">
          <Text weight="semibold">Your OPD today</Text>
          <Text>
            {mine.booked} booked of {mine.capacity} · {mine.checkedIn} checked in · {mine.completed} seen · {mine.freeSlots} free slots
          </Text>
          <Button size="small" appearance="transparent" onClick={() => void navigate({ to: '/scheduling', search: { practitionerId: mine.practitionerId } })}>
            Open my list
          </Button>
        </div>
      )}
      <div className={styles.kpis}>
        <KpiCard
          label="Appointments today"
          value={value(s ? s.total - s.cancelled : undefined)}
          icon={<CalendarLtrRegular />}
          hint={s ? `${s.telehealth} video · ${s.online} booked online` : undefined}
          onClick={() => open()}
        />
        <KpiCard label="Yet to arrive" value={value(s ? s.booked + s.confirmed : undefined)} icon={<CalendarClockRegular />} hint="Booked or confirmed" onClick={() => open('Booked')} />
        <KpiCard
          label="Checked in"
          value={value(s ? s.checkedIn + s.inConsultation : undefined)}
          icon={<PersonArrowRightRegular />}
          hint={s ? `${s.inConsultation} with the doctor` : undefined}
          onClick={() => open('CheckedIn')}
        />
        <KpiCard label="Completed" value={value(s?.completed)} icon={<CalendarCheckmarkRegular />} onClick={() => open('Completed')} />
        <KpiCard
          label="No-shows"
          value={value(s?.noShow)}
          icon={<PersonProhibitedRegular />}
          hint={s ? `${s.cancelled} cancelled` : undefined}
          onClick={() => open('NoShow')}
        />
      </div>
    </div>
  );
}

/** Booked seats against capacity for each doctor with OPD today. */
export function DoctorLoadWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const summary = useTodaySummary();
  const doctors = [...(summary.data?.practitioners ?? [])].filter((p) => p.capacity > 0 || p.booked > 0).sort((a, b) => b.booked - a.booked);

  return (
    <Section title="Doctors' OPD today" description="Booked patients against available seats">
      {summary.error ? (
        <ErrorState error={summary.error} onRetry={() => void summary.refetch()} />
      ) : doctors.length === 0 ? (
        <Text className={styles.muted}>{summary.isLoading ? 'Loading…' : 'No OPD sessions today.'}</Text>
      ) : (
        <div className={styles.rows}>
          {doctors.slice(0, 8).map((d) => {
            const load = utilisation([{ capacity: d.capacity, booked: d.booked }]);
            return (
              <div key={d.practitionerId} className={styles.row}>
                <Button
                  appearance="transparent"
                  size="small"
                  style={{ justifyContent: 'flex-start', padding: 0, minWidth: 0 }}
                  onClick={() => void navigate({ to: '/scheduling', search: { practitionerId: d.practitionerId } })}
                >
                  {d.practitionerName}
                </Button>
                <Caption1>
                  {d.booked}/{d.capacity} · {d.freeSlots} free
                </Caption1>
                <div style={{ gridColumn: '1 / -1' }}>
                  <ProgressBar
                    value={Math.min(1, d.capacity ? d.booked / d.capacity : 1)}
                    color={load.percent >= 100 ? 'error' : load.percent >= 85 ? 'warning' : 'brand'}
                    thickness="large"
                    aria-label={`${d.practitionerName}: ${d.booked} of ${d.capacity} seats booked`}
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
