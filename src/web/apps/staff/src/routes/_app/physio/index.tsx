import {
  Button,
  Field,
  Input,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Select,
  Text,
  Tooltip,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  CalendarArrowRightRegular,
  CheckmarkRegular,
  ChevronLeftRegular,
  ChevronRightRegular,
  MoreHorizontalRegular,
  PersonAddRegular,
  PersonProhibitedRegular,
} from '@fluentui/react-icons';
import { formatTime, todayIso, useHotkey } from '@hc360/core';
import { DataTable, FilterBar, LoadingState, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { addDays, dayLabel, isDateOnly } from '@/modules/ot/shared/datetime';
import { NoAccess, ReasonDialog } from '@/modules/ot/shared/feedback';
import { useFacilityId, useLiveInvalidation } from '@/modules/ot/shared/hooks';
import {
  physioApi,
  physioKeys,
  physioQueries,
  PHYSIO_SESSION_STATUSES,
  type PhysioSessionStatus,
  type PhysioWorklistItem,
} from '@/modules/physio/api';
import { physioSessionStatusLabel, physioSessionStatusTone } from '@/modules/physio/labels';
import { AttendDialog, NewReferralDialog, RescheduleDialog } from '@/modules/physio/PhysioDialogs';

interface WorklistSearch {
  date?: string;
  status?: PhysioSessionStatus;
}

export const Route = createFileRoute('/_app/physio/')({
  validateSearch: (s: Record<string, unknown>): WorklistSearch => ({
    date: isDateOnly(s.date) ? s.date : undefined,
    status: PHYSIO_SESSION_STATUSES.includes(s.status as PhysioSessionStatus)
      ? (s.status as PhysioSessionStatus)
      : undefined,
  }),
  component: PhysioWorklistPage,
});

const useStyles = makeStyles({
  toolbar: { padding: '0 16px 12px' },
  nav: { display: 'flex', gap: '4px', alignItems: 'end' },
  actions: { display: 'inline-flex', gap: '4px' },
});

function PhysioWorklistPage() {
  const styles = useStyles();
  // Closing a dialog opened from a row's buttons returns focus to the button that opened it.
  const restoreFocusTarget = useRestoreFocusTarget();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityId = useFacilityId();
  const canRead = usePermission('physio.read');
  const canTreat = usePermission('physio.treatment.manage');
  const canRefer = usePermission('physio.referrals.manage');
  const today = todayIso();
  const date = search.date ?? today;
  const worklist = useQuery({ ...physioQueries.worklist(date, search.status), enabled: canRead });
  const [referring, setReferring] = useState(false);
  const [attending, setAttending] = useState<PhysioWorklistItem | null>(null);
  const [moving, setMoving] = useState<PhysioWorklistItem | null>(null);
  const [closing, setClosing] = useState<PhysioWorklistItem | null>(null);

  useLiveInvalidation(facilityId ? `facility:${facilityId}` : null, 'physio.worklist.changed', [
    physioKeys.worklists,
    physioKeys.referralLists,
  ]);

  const close = useMutation({
    mutationFn: ({ id, reason, missed }: { id: string; reason: string; missed: boolean }) =>
      physioApi.close(id, reason, missed),
    onSuccess: (_, { missed }) => {
      setClosing(null);
      notify.success(missed ? 'Recorded as missed' : 'Session cancelled');
      void queryClient.invalidateQueries({ queryKey: physioKeys.all });
    },
  });

  const set = (patch: Partial<WorklistSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }) });
  const go = (days: number) => {
    const next = addDays(date, days);
    set({ date: next === today ? undefined : next });
  };
  useHotkey('alt+arrowleft', () => go(-1));
  useHotkey('alt+arrowright', () => go(1));

  if (!canRead) {
    return (
      <Page>
        <NoAccess what="the physiotherapy worklist" />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Physiotherapy worklist"
        subtitle={`${dayLabel(date)}${date === today ? ' · today' : ''}. Record each session as it is given; Alt+← / Alt+→ change the day.`}
        actions={
          canRefer && (
            <Button appearance="primary" icon={<PersonAddRegular />} onClick={() => setReferring(true)}>
              New referral
            </Button>
          )
        }
      />
      <Section flush>
        <div className={styles.toolbar}>
          <FilterBar>
            <div className={styles.nav}>
              <Tooltip content="Previous day (Alt+←)" relationship="label">
                <Button icon={<ChevronLeftRegular />} onClick={() => go(-1)} />
              </Tooltip>
              <Button onClick={() => set({ date: undefined })} disabled={date === today}>
                Today
              </Button>
              <Tooltip content="Next day (Alt+→)" relationship="label">
                <Button icon={<ChevronRightRegular />} onClick={() => go(1)} />
              </Tooltip>
            </div>
            <Field label="Date">
              <Input
                type="date"
                value={date}
                onChange={(_, d) => set({ date: d.value && d.value !== today ? d.value : undefined })}
              />
            </Field>
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as PhysioSessionStatus | undefined })}
              >
                <option value="">All</option>
                {PHYSIO_SESSION_STATUSES.map((s) => (
                  <option key={s} value={s}>
                    {physioSessionStatusLabel[s]}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Physiotherapy sessions"
          rows={worklist.data}
          loading={worklist.isLoading}
          error={worklist.error}
          onRetry={() => void worklist.refetch()}
          getRowId={(i) => i.sessionId}
          onRowClick={(i) => void navigate({ to: '/physio/referrals/$id', params: { id: i.referralId } })}
          columns={[
            {
              key: 'time',
              header: 'Time',
              width: 90,
              render: (i) => <Text weight="semibold">{formatTime(i.scheduledAt)}</Text>,
            },
            { key: 'patient', header: 'Patient', render: (i) => <Text weight="semibold">{i.patientName}</Text> },
            { key: 'uhid', header: 'UHID', priority: 'low', render: (i) => <Text font="monospace">{i.uhid}</Text> },
            {
              key: 'session',
              header: 'Session',
              width: 110,
              render: (i) => `${i.sessionNumber}${i.sessionsPlanned ? ` of ${i.sessionsPlanned}` : ''}`,
            },
            {
              key: 'status',
              header: 'Status',
              render: (i) => (
                <StatusBadge
                  status={i.status}
                  tone={physioSessionStatusTone[i.status]}
                  label={physioSessionStatusLabel[i.status]}
                />
              ),
            },
            { key: 'therapist', header: 'Therapist', priority: 'low', render: (i) => i.therapistName ?? '—' },
          ]}
          rowActions={
            canTreat
              ? (i) =>
                  i.status === 'Scheduled' ? (
                    <span className={styles.actions}>
                      <Button
                        size="small"
                        appearance="primary"
                        icon={<CheckmarkRegular />}
                        onClick={() => setAttending(i)}
                        {...restoreFocusTarget}
                      >
                        Record
                      </Button>
                      <Menu>
                        <MenuTrigger disableButtonEnhancement>
                          <Button
                            size="small"
                            appearance="subtle"
                            icon={<MoreHorizontalRegular />}
                            aria-label={`More actions for ${i.patientName}`}
                            {...restoreFocusTarget}
                          />
                        </MenuTrigger>
                        <MenuPopover>
                          <MenuList>
                            <MenuItem icon={<CalendarArrowRightRegular />} onClick={() => setMoving(i)}>
                              Move to another time
                            </MenuItem>
                            <MenuItem icon={<PersonProhibitedRegular />} onClick={() => setClosing(i)}>
                              Did not come / cancel
                            </MenuItem>
                          </MenuList>
                        </MenuPopover>
                      </Menu>
                    </span>
                  ) : null
              : undefined
          }
          emptyTitle="No sessions for this day"
          emptyDescription="Sessions are booked from each referral's treatment plan."
          size="small"
        />
      </Section>

      {referring && <NewReferralDialog onClose={() => setReferring(false)} />}
      {attending && <WorklistAttend item={attending} onClose={() => setAttending(null)} />}
      {moving && (
        <RescheduleDialog
          sessionId={moving.sessionId}
          scheduledAt={moving.scheduledAt}
          version={moving.version}
          onClose={() => setMoving(null)}
        />
      )}
      <ReasonDialog
        open={closing !== null}
        title={`Close session ${closing?.sessionNumber ?? ''} for ${closing?.patientName ?? ''}?`}
        confirmLabel="Save"
        busy={close.isPending}
        error={close.error}
        option={{
          label: 'The patient did not come (record as missed)',
          defaultChecked: true,
          hint: 'Untick if the department cancelled it.',
        }}
        presets={['Patient did not come', 'Patient unwell', 'Therapist unavailable', 'Patient discharged']}
        onConfirm={(reason, missed) => closing && close.mutate({ id: closing.sessionId, reason, missed })}
        onCancel={() => {
          setClosing(null);
          close.reset();
        }}
      />
    </Page>
  );
}

/** Loads the referral's plan so the session form starts with the planned modalities and duration. */
function WorklistAttend({ item, onClose }: { item: PhysioWorklistItem; onClose: () => void }) {
  const referral = useQuery(physioQueries.referral(item.referralId));
  if (referral.isLoading) {
    return <LoadingState label="Loading the treatment plan…" />;
  }
  return (
    <AttendDialog session={item} plan={referral.data?.plan ?? null} patientName={item.patientName} onClose={onClose} />
  );
}
