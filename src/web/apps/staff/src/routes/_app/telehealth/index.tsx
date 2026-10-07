import {
  Button,
  Caption1,
  Field,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Select,
  Switch,
  Tab,
  TabList,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  AddRegular,
  DismissCircleRegular,
  MoreHorizontalRegular,
  OpenRegular,
  SendRegular,
  VideoRegular,
} from '@fluentui/react-icons';
import { formatTime, isFeatureDisabled, useRealtimeEvent, useSession } from '@hc360/core';
import { DataTable, FilterBar, Page, PageHeader, Section, StatusBadge } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useFeatureOn } from '@/lib/features';
import { toOptions, usePractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { addDays, formatDuration, istDate, minutesSince, relativeDayLabel } from '@/modules/scheduling/dates';
import { useNow, useToday } from '@/modules/scheduling/useNow';
import {
  TELEHEALTH_STATUSES,
  telehealthKeys,
  telehealthQueries,
  type TelehealthLinkIssued,
  type TelehealthSessionListItem,
  type TelehealthStatus,
} from '@/modules/virtualcare/telehealth/api';
import { isSessionOpen, sessionStatusText } from '@/modules/virtualcare/telehealth/labels';
import { LinkIssuedDialog, NewSessionDialog } from '@/modules/virtualcare/telehealth/SessionDialogs';
import { useSessionActions } from '@/modules/virtualcare/telehealth/useSessionActions';
import { VideoOffPage } from '@/modules/virtualcare/telehealth/VideoOffPage';

type Range = 'today' | 'upcoming' | 'past';

interface TelehealthSearch {
  range?: Range;
  scope?: 'mine' | 'all';
  practitionerId?: string;
  status?: TelehealthStatus;
}

export const Route = createFileRoute('/_app/telehealth/')({
  validateSearch: (s: Record<string, unknown>): TelehealthSearch => ({
    range: s.range === 'upcoming' || s.range === 'past' ? s.range : undefined,
    scope: s.scope === 'mine' || s.scope === 'all' ? s.scope : undefined,
    practitionerId: typeof s.practitionerId === 'string' && s.practitionerId ? s.practitionerId : undefined,
    status: TELEHEALTH_STATUSES.includes(s.status as TelehealthStatus) ? (s.status as TelehealthStatus) : undefined,
  }),
  component: TelehealthRoute,
});

const useStyles = makeStyles({
  toolbar: { padding: '0 16px 12px', display: 'flex', flexDirection: 'column', gap: '12px' },
  stack: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  waiting: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  actions: { display: 'flex', gap: '4px', justifyContent: 'flex-end', alignItems: 'center' },
});

/** Video consultations, or a plain explanation while an administrator has switched them off. */
function TelehealthRoute() {
  return useFeatureOn('telehealth') ? <TelehealthPage /> : <VideoOffPage />;
}

/** Today's and upcoming video consultations (TEL-01): who is waiting, links, and the way into each call. */
function TelehealthPage() {
  const styles = useStyles();
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  const myPractitionerId = useSession((s) => s.user?.practitionerId);
  const canConsult = usePermission('telehealth.consult');
  const actions = useSessionActions();
  const today = useToday();
  const now = useNow();
  const doctors = usePractitioners(null);
  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<TelehealthLinkIssued | null>(null);

  const range: Range = search.range ?? 'today';
  const scope = search.scope ?? (myPractitionerId ? 'mine' : 'all');
  const from = range === 'past' ? addDays(today, -7) : today;
  const to = range === 'today' ? today : range === 'upcoming' ? addDays(today, 6) : addDays(today, -1);
  const mine = scope === 'mine' && Boolean(myPractitionerId) && !search.practitionerId;
  const sessions = useQuery(
    telehealthQueries.sessions({
      date: from,
      to,
      practitionerId: search.practitionerId,
      status: search.status,
      mine: mine || undefined,
    }),
  );

  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'telehealth.changed', () => {
    void queryClient.invalidateQueries({ queryKey: telehealthKeys.sessions });
  });

  const set = (patch: Partial<TelehealthSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });
  const open = (s: TelehealthSessionListItem) =>
    void navigate({ to: '/telehealth/$sessionId', params: { sessionId: s.id } });
  const rows = sessions.data ?? [];
  const waiting = rows.filter((s) => s.status === 'WaitingRoom');

  // Switched off since the menu last looked: the server says so.
  if (isFeatureDisabled(sessions.error)) {
    return <VideoOffPage />;
  }

  return (
    <Page>
      <PageHeader
        title="Video consultations"
        subtitle="Patients join from the SMS/WhatsApp link or the patient portal and wait until the doctor admits them."
        actions={
          actions.canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setCreating(true)}>
              New video consultation
            </Button>
          )
        }
      />

      {waiting.length > 0 && range !== 'past' && (
        <MessageBar intent="warning">
          <MessageBarBody>
            {waiting.length === 1 ? `${waiting[0]!.patientName} is waiting` : `${waiting.length} patients are waiting`}{' '}
            for {waiting.length === 1 ? waiting[0]!.practitionerName : 'their doctors'}.
          </MessageBarBody>
          {canConsult && (
            <MessageBarActions>
              <Button size="small" appearance="primary" icon={<VideoRegular />} onClick={() => open(waiting[0]!)}>
                Join
              </Button>
            </MessageBarActions>
          )}
        </MessageBar>
      )}

      <Section flush>
        <div className={styles.toolbar}>
          <TabList
            selectedValue={range}
            onTabSelect={(_, d) => set({ range: d.value === 'today' ? undefined : (d.value as Range) })}
          >
            <Tab value="today">Today</Tab>
            <Tab value="upcoming">Next 7 days</Tab>
            <Tab value="past">Last 7 days</Tab>
          </TabList>
          <FilterBar>
            {myPractitionerId && (
              <Switch
                checked={scope === 'mine'}
                onChange={(_, d) => set({ scope: d.checked ? 'mine' : 'all', practitionerId: undefined })}
                label="Only my consultations"
              />
            )}
            {scope === 'all' && (
              <Field label="Doctor">
                <Select
                  value={search.practitionerId ?? ''}
                  onChange={(_, d) => set({ practitionerId: d.value || undefined })}
                >
                  <option value="">All doctors</option>
                  {toOptions(doctors.data).map((o) => (
                    <option key={o.value} value={o.value}>
                      {o.label}
                    </option>
                  ))}
                </Select>
              </Field>
            )}
            <Field label="Status">
              <Select
                value={search.status ?? ''}
                onChange={(_, d) => set({ status: (d.value || undefined) as TelehealthStatus | undefined })}
              >
                <option value="">Any status</option>
                {TELEHEALTH_STATUSES.map((status) => (
                  <option key={status} value={status}>
                    {sessionStatusText[status].label}
                  </option>
                ))}
              </Select>
            </Field>
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Video consultations"
          rows={sessions.data}
          loading={sessions.isLoading}
          error={sessions.error}
          onRetry={() => void sessions.refetch()}
          getRowId={(s) => s.id}
          onRowClick={open}
          columns={[
            {
              key: 'when',
              header: 'Time',
              width: 120,
              render: (s) => (
                <span className={styles.stack}>
                  <Text weight="semibold">
                    {formatTime(s.scheduledStartAt)} – {formatTime(s.scheduledEndAt)}
                  </Text>
                  {range !== 'today' && (
                    <Caption1 className={styles.muted}>{relativeDayLabel(istDate(s.scheduledStartAt), today)}</Caption1>
                  )}
                </span>
              ),
            },
            {
              key: 'patient',
              header: 'Patient',
              render: (s) => (
                <span className={styles.stack}>
                  <Text weight="semibold" truncate>
                    {s.patientName}{' '}
                    <Text size={200} weight="regular">
                      ({s.age ?? '—'} / {s.gender.charAt(0)})
                    </Text>
                  </Text>
                  <Caption1 className={styles.muted}>
                    {s.uhid}
                    {s.appointmentNumber ? ` · ${s.appointmentNumber}` : ''}
                  </Caption1>
                </span>
              ),
            },
            { key: 'doctor', header: 'Doctor', render: (s) => s.practitionerName },
            {
              key: 'status',
              header: 'Status',
              width: 170,
              render: (s) => (
                <span className={styles.stack}>
                  <StatusBadge
                    status={s.status}
                    tone={sessionStatusText[s.status].tone}
                    label={sessionStatusText[s.status].label}
                  />
                  {s.status === 'WaitingRoom' && s.patientWaitingSince && (
                    <Caption1 className={styles.waiting}>
                      waiting {formatDuration(minutesSince(s.patientWaitingSince, now))}
                    </Caption1>
                  )}
                  {s.status === 'Completed' && s.durationSeconds !== null && (
                    <Caption1 className={styles.muted}>{formatDuration(s.durationSeconds / 60)}</Caption1>
                  )}
                </span>
              ),
            },
            {
              key: 'link',
              header: 'Join link',
              priority: 'low',
              width: 100,
              render: (s) => (s.linkSent ? 'Sent' : <Text className={styles.muted}>Not sent</Text>),
            },
          ]}
          rowActions={(s) => (
            <div className={styles.actions}>
              {canConsult && isSessionOpen(s.status) && (
                <Button
                  size="small"
                  appearance={s.status === 'WaitingRoom' ? 'primary' : 'secondary'}
                  icon={<VideoRegular />}
                  onClick={() => open(s)}
                >
                  {s.status === 'InProgress' ? 'Rejoin' : 'Join'}
                </Button>
              )}
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<MoreHorizontalRegular />}
                    aria-label={`More actions for ${s.patientName}`}
                    {...restoreFocusTarget}
                  />
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>
                    <MenuItem icon={<OpenRegular />} onClick={() => open(s)}>
                      Open
                    </MenuItem>
                    {actions.canManage && isSessionOpen(s.status) && (
                      <MenuItem
                        icon={<SendRegular />}
                        disabled={actions.resending}
                        onClick={() => actions.resendLink(s.id)}
                      >
                        Send a new link
                      </MenuItem>
                    )}
                    {actions.canManage && (s.status === 'Scheduled' || s.status === 'WaitingRoom') && (
                      <MenuItem
                        icon={<DismissCircleRegular />}
                        onClick={() =>
                          actions.cancel({ id: s.id, patientName: s.patientName, scheduledStartAt: s.scheduledStartAt })
                        }
                      >
                        Cancel consultation
                      </MenuItem>
                    )}
                  </MenuList>
                </MenuPopover>
              </Menu>
            </div>
          )}
          emptyTitle={
            range === 'today'
              ? 'No video consultations today'
              : range === 'upcoming'
                ? 'No video consultations in the next 7 days'
                : 'No video consultations in the last 7 days'
          }
          emptyDescription="Book an appointment as a video consultation, or create one here for a follow-up call."
          size="small"
        />
      </Section>

      <NewSessionDialog
        open={creating}
        onClose={() => setCreating(false)}
        onCreated={(link) => {
          setCreating(false);
          setCreated(link);
        }}
      />
      <LinkIssuedDialog link={created} onClose={() => setCreated(null)} />
      {actions.dialogs}
    </Page>
  );
}
