import { Badge, Button, Caption1, Field, makeStyles, Select, Text, tokens } from '@fluentui/react-components';
import { CalendarCheckmarkRegular, DismissCircleRegular, PersonAddRegular, VideoRegular } from '@fluentui/react-icons';
import { formatDateTime, formatRelative, useRealtimeEvent, useSession } from '@hc360/core';
import { ConfirmDialog, DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { schedulingApi, schedulingKeys, schedulingQueries, WAITLIST_STATUSES, type AppointmentDetail, type WaitlistEntryDto, type WaitlistStatus } from '@/modules/scheduling/api';
import { useAppointmentDesk } from '@/modules/scheduling/useAppointmentDesk';
import { BookingSuccessDialog } from '@/modules/scheduling/BookingSuccessDialog';
import { formatDayMedium } from '@/modules/scheduling/dates';
import { channelLabel, preferredSessionLabel, waitlistStatusText } from '@/modules/scheduling/labels';
import { useToday } from '@/modules/scheduling/useNow';
import { AddToWaitlistDialog, BookFromWaitlistDialog } from '@/modules/scheduling/WaitlistDialogs';

interface WaitlistSearch {
  departmentId?: string;
  practitionerId?: string;
  status?: WaitlistStatus;
  page?: number;
}

export const Route = createFileRoute('/_app/scheduling/waitlist')({
  validateSearch: (s: Record<string, unknown>): WaitlistSearch => ({
    departmentId: typeof s.departmentId === 'string' && s.departmentId ? s.departmentId : undefined,
    practitionerId: typeof s.practitionerId === 'string' && s.practitionerId ? s.practitionerId : undefined,
    status: WAITLIST_STATUSES.includes(s.status as WaitlistStatus) ? (s.status as WaitlistStatus) : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: WaitlistPage,
});

const useStyles = makeStyles({
  toolbar: { padding: '0 16px 12px' },
  stack: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  offered: { color: tokens.colorBrandForeground1 },
  actions: { display: 'flex', gap: '4px', justifyContent: 'flex-end' },
});

/**
 * Patients waiting for a slot with a doctor (OA-05). When a matching booking is cancelled the first patient is offered
 * the freed slot by SMS/WhatsApp; the desk books it here with one click.
 */
function WaitlistPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  const canBook = usePermission('scheduling.appointments.book');
  const canManage = usePermission('scheduling.appointments.manage');
  const desk = useAppointmentDesk();
  const today = useToday();
  const departments = useDepartments('Clinical');
  const doctors = usePractitioners(search.departmentId ?? null);
  const [adding, setAdding] = useState(false);
  const [booking, setBooking] = useState<WaitlistEntryDto | null>(null);
  const [booked, setBooked] = useState<AppointmentDetail | null>(null);
  const [cancelling, setCancelling] = useState<WaitlistEntryDto | null>(null);

  const entries = useQuery(
    schedulingQueries.waitlist({ departmentId: search.departmentId, practitionerId: search.practitionerId, status: search.status, page: search.page }),
  );

  // A cancellation anywhere may offer a slot to someone on this list.
  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'appointments.changed', () => {
    void queryClient.invalidateQueries({ queryKey: schedulingKeys.waitlist });
  });

  const cancel = useMutation({
    mutationFn: (entry: WaitlistEntryDto) => schedulingApi.cancelWaitlist(entry.id),
    onSuccess: () => {
      notify.success('Removed from the waitlist');
      setCancelling(null);
      void queryClient.invalidateQueries({ queryKey: schedulingKeys.waitlist });
    },
    onError: (error) => notify.error(error, 'Could not remove the request'),
  });

  const set = (patch: Partial<WaitlistSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch, page: undefined }) });
  const openStatuses: WaitlistStatus[] = ['Waiting', 'Offered'];

  return (
    <Page>
      <PageHeader
        title="Waitlist"
        subtitle="Patients waiting for a slot. Offered slots are booked here as soon as the patient agrees."
        breadcrumbs={[{ label: 'Appointments', onClick: () => void navigate({ to: '/scheduling' }) }, { label: 'Waitlist' }]}
        actions={
          canBook && (
            <Button appearance="primary" icon={<PersonAddRegular />} onClick={() => setAdding(true)}>
              Add to waitlist
            </Button>
          )
        }
      />

      <Section flush>
        <div className={styles.toolbar}>
          <FilterBar>
            <Field label="Department">
              <Select value={search.departmentId ?? ''} onChange={(_, d) => set({ departmentId: d.value || undefined, practitionerId: undefined })}>
                <option value="">All departments</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Doctor">
              <Select value={search.practitionerId ?? ''} onChange={(_, d) => set({ practitionerId: d.value || undefined })}>
                <option value="">All doctors</option>
                {toOptions(doctors.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Status">
              <Select value={search.status ?? ''} onChange={(_, d) => set({ status: (d.value || undefined) as WaitlistStatus | undefined })}>
                <option value="">Waiting or offered</option>
                {WAITLIST_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {waitlistStatusText[s].label}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Waitlist"
          rows={entries.data?.items}
          loading={entries.isLoading}
          error={entries.error}
          onRetry={() => void entries.refetch()}
          getRowId={(e) => e.id}
          onRowClick={canBook ? (e) => openStatuses.includes(e.status) && setBooking(e) : undefined}
          columns={[
            {
              key: 'patient',
              header: 'Patient',
              render: (e) => (
                <span className={styles.stack}>
                  <Text weight="semibold" truncate>
                    {e.patientName}
                  </Text>
                  <Caption1 className={styles.muted}>
                    {e.uhid}
                    {e.phoneMasked ? ` · ${e.phoneMasked}` : ''}
                  </Caption1>
                </span>
              ),
            },
            { key: 'doctor', header: 'Doctor', render: (e) => e.practitionerName },
            {
              key: 'when',
              header: 'Wants',
              render: (e) => (
                <span className={styles.stack}>
                  <Text>
                    {e.preferredFrom === e.preferredTo ? formatDayMedium(e.preferredFrom) : `${formatDayMedium(e.preferredFrom)} – ${formatDayMedium(e.preferredTo)}`}
                  </Text>
                  <Caption1 className={styles.muted}>
                    {preferredSessionLabel[e.preferredSession]}
                    {e.isTelehealth ? ' · video' : ''}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'status',
              header: 'Status',
              width: 200,
              render: (e) => (
                <span className={styles.stack}>
                  <span>
                    <StatusBadge status={e.status} tone={waitlistStatusText[e.status].tone} label={waitlistStatusText[e.status].label} />
                    {e.isTelehealth && (
                      <Badge size="small" appearance="tint" color="brand" icon={<VideoRegular />} style={{ marginLeft: 4 }}>
                        Video
                      </Badge>
                    )}
                  </span>
                  {e.status === 'Offered' && e.offeredSlotStartsAt && <Caption1 className={styles.offered}>Slot: {formatDateTime(e.offeredSlotStartsAt)}</Caption1>}
                  {e.preferredTo < today && openStatuses.includes(e.status) && <Caption1 className={styles.muted}>Dates have passed</Caption1>}
                </span>
              ),
            },
            { key: 'channel', header: 'Came by', priority: 'low', render: (e) => channelLabel[e.channel] },
            { key: 'added', header: 'Added', priority: 'low', width: 110, render: (e) => formatRelative(e.createdAt) },
          ]}
          rowActions={(e) =>
            openStatuses.includes(e.status) ? (
              <div className={styles.actions}>
                {canBook && (
                  <Button size="small" appearance={e.status === 'Offered' ? 'primary' : 'secondary'} icon={<CalendarCheckmarkRegular />} onClick={() => setBooking(e)}>
                    Book
                  </Button>
                )}
                {canManage && (
                  <Button size="small" appearance="subtle" icon={<DismissCircleRegular />} aria-label={`Remove ${e.patientName} from the waitlist`} onClick={() => setCancelling(e)} />
                )}
              </div>
            ) : null
          }
          emptyTitle={search.status && !openStatuses.includes(search.status) ? 'No requests with this status' : 'No one is waiting'}
          emptyDescription="When a doctor's days are full, add the patient here and book them when a slot opens."
          page={search.page ?? 1}
          pageSize={25}
          totalCount={entries.data?.totalCount}
          onPageChange={(page) => void navigate({ search: (prev) => ({ ...prev, page: page > 1 ? page : undefined }) })}
          size="small"
        />
      </Section>

      <AddToWaitlistDialog open={adding} defaults={{ departmentId: search.departmentId, practitionerId: search.practitionerId }} onClose={() => setAdding(false)} />
      <BookFromWaitlistDialog
        entry={booking}
        onClose={() => setBooking(null)}
        onBooked={(appointment) => {
          setBooking(null);
          setBooked(appointment);
        }}
      />
      <BookingSuccessDialog appointment={booked} desk={desk} onClose={() => setBooked(null)} />
      <ConfirmDialog
        open={cancelling !== null}
        title="Remove from the waitlist?"
        confirmLabel="Remove"
        destructive
        busy={cancel.isPending}
        onConfirm={() => cancelling && cancel.mutate(cancelling)}
        onCancel={() => setCancelling(null)}
      >
        {cancelling ? `${cancelling.patientName} will no longer be offered freed slots with ${cancelling.practitionerName}.` : null}
      </ConfirmDialog>
      {desk.dialogs}
    </Page>
  );
}
