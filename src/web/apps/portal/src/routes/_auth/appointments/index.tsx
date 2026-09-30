import { Button, makeStyles, Tab, TabList, tokens } from '@fluentui/react-components';
import { CalendarAddRegular, CalendarLtrRegular, HourglassRegular } from '@fluentui/react-icons';
import { EmptyState } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { ConfirmDialog } from '@/components/ConfirmDialog';
import { ButtonLink } from '@/components/links';
import { ListRow } from '@/components/ListRow';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, LoadingCards } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { useToast } from '@/lib/toast';
import type { PortalWaitlistEntry } from '@/lib/types';
import { appointmentApi, appointmentQueries, type AppointmentScope } from '@/modules/appointments/api';
import { AppointmentCard } from '@/modules/appointments/AppointmentCard';
import { PREFERRED_SESSION } from '@/modules/appointments/labels';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/appointments/')({
  validateSearch: (search: Record<string, unknown>): { tab?: 'past' } => ({
    tab: search.tab === 'past' ? 'past' : undefined,
  }),
  component: AppointmentsPage,
});

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px' },
  section: { display: 'flex', flexDirection: 'column', gap: '10px' },
  sectionTitle: { margin: '8px 0 0', fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  waitRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '14px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  waitHead: { display: 'flex', alignItems: 'center', gap: '12px' },
  waitActions: { display: 'flex', flexWrap: 'wrap', gap: '8px' },
});

function AppointmentsPage() {
  const styles = useStyles();
  const t = useT();
  const { tab } = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const { profile } = useActiveProfile();
  const scope: AppointmentScope = tab === 'past' ? 'past' : 'upcoming';
  const appointments = useQuery(appointmentQueries.list(profile.id, scope));

  return (
    <PortalPage
      title={t('appointments.title')}
      subtitle={t('appointments.subtitle')}
      actions={
        <Button
          appearance="primary"
          size="large"
          icon={<CalendarAddRegular />}
          onClick={() => void navigate({ to: '/doctors' })}
        >
          {t('appointments.book')}
        </Button>
      }
    >
      <TabList
        selectedValue={scope}
        onTabSelect={(_, data) =>
          void navigate({ search: { tab: data.value === 'past' ? 'past' : undefined }, replace: true })
        }
        aria-label={t('appointments.tabs')}
        size="large"
      >
        <Tab value="upcoming">{t('appointments.tab.upcoming')}</Tab>
        <Tab value="past">{t('appointments.tab.past')}</Tab>
      </TabList>

      <div className={styles.list} role="tabpanel" aria-busy={appointments.isFetching}>
        {appointments.isPending ? (
          <LoadingCards count={3} />
        ) : appointments.isError ? (
          <ErrorBlock error={appointments.error} onRetry={() => void appointments.refetch()} />
        ) : appointments.data.length === 0 ? (
          <EmptyState
            icon={<CalendarLtrRegular />}
            title={t(scope === 'past' ? 'appointments.empty.past.title' : 'appointments.empty.upcoming.title')}
            description={t(scope === 'past' ? 'appointments.empty.past.body' : 'appointments.empty.upcoming.body')}
            action={
              scope === 'upcoming' ? (
                <Button appearance="primary" size="large" onClick={() => void navigate({ to: '/doctors' })}>
                  {t('nav.findDoctor')}
                </Button>
              ) : undefined
            }
          />
        ) : (
          appointments.data.map((appointment) => <AppointmentCard key={appointment.id} appointment={appointment} />)
        )}
      </div>

      {scope === 'upcoming' && <Waitlist />}
    </PortalPage>
  );
}

/** Open waiting-list requests for this person; an offered time can be booked straight away. */
function Waitlist() {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const toast = useToast();
  const queryClient = useQueryClient();
  const { profile } = useActiveProfile();
  const waitlist = useQuery(appointmentQueries.waitlist(profile.id));
  const [leaving, setLeaving] = useState<PortalWaitlistEntry | null>(null);
  const [busy, setBusy] = useState(false);
  const open = (waitlist.data ?? []).filter((entry) => entry.status === 'Waiting' || entry.status === 'Offered');

  if (open.length === 0) {
    return null;
  }

  const leave = async () => {
    if (!leaving) {
      return;
    }
    setBusy(true);
    try {
      await appointmentApi.leaveWaitlist(leaving.id);
      toast.success(t('appointments.waitlist.left'));
      setLeaving(null);
      void queryClient.invalidateQueries({ queryKey: appointmentQueries.all(profile.id) });
    } catch (error) {
      toast.failure(error, t);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className={styles.section} aria-labelledby="waitlist-title">
      <h2 id="waitlist-title" className={styles.sectionTitle}>
        {t('appointments.waitlist.title')}
      </h2>
      {open.map((entry) => (
        <div key={entry.id} className={styles.waitRow}>
          <div className={styles.waitHead}>
            <ListRow
              icon={<HourglassRegular />}
              tone={entry.status === 'Offered' ? 'success' : 'warning'}
              title={entry.practitionerName}
              subtitle={t('appointments.waitlist.item', {
                from: f.calendarDate(entry.preferredFrom),
                to: f.calendarDate(entry.preferredTo),
                session: t(PREFERRED_SESSION[entry.preferredSession]),
              })}
              chevron={false}
              trailing={
                entry.isTelehealth ? <StatusPill tone="brand">{t('appointment.mode.video')}</StatusPill> : undefined
              }
            />
          </div>
          {entry.status === 'Offered' && entry.offeredSlotStartsAt && (
            <StatusPill tone="success">
              {t('appointments.waitlist.offered', { when: f.dateTime(entry.offeredSlotStartsAt) })}
            </StatusPill>
          )}
          <div className={styles.waitActions}>
            {entry.status === 'Offered' && (
              <ButtonLink
                appearance="primary"
                to="/doctors/$doctorId"
                params={{ doctorId: entry.practitionerId }}
                search={entry.isTelehealth ? { mode: 'video' } : {}}
              >
                {t('appointments.waitlist.book')}
              </ButtonLink>
            )}
            <Button appearance="secondary" onClick={() => setLeaving(entry)}>
              {t('appointments.waitlist.leave')}
            </Button>
          </div>
        </div>
      ))}
      <ConfirmDialog
        open={leaving !== null}
        title={t('appointments.waitlist.leaveTitle')}
        confirmLabel={t('appointments.waitlist.leave')}
        destructive
        busy={busy}
        onConfirm={() => void leave()}
        onDismiss={() => setLeaving(null)}
      >
        {leaving && t('appointments.waitlist.leaveBody', { doctor: leaving.practitionerName })}
      </ConfirmDialog>
    </section>
  );
}
