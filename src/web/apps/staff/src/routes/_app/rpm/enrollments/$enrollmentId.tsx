import {
  Badge,
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Subtitle2,
  Tab,
  TabList,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  DeleteRegular,
  EditRegular,
  LinkRegular,
  MoreHorizontalRegular,
  PersonRegular,
  PlugDisconnectedRegular,
  SlideTextRegular,
  TextBulletListSquareEditRegular,
} from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatRelative, isApiError } from '@hc360/core';
import { ConfirmDialog, DataTable, EmptyState, ErrorState, LoadingState, Page, PatientBanner, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { addDays, formatDayMedium } from '@/modules/scheduling/dates';
import { useToday } from '@/modules/scheduling/useNow';
import {
  READING_TYPES,
  rpmApi,
  rpmKeys,
  rpmQueries,
  type DeviceKeyIssued,
  type RpmDeviceDto,
  type RpmEnrollmentDetail,
  type RpmReadingType,
} from '@/modules/virtualcare/rpm/api';
import { AlertsTable } from '@/modules/virtualcare/rpm/AlertsTable';
import { AssignDeviceDialog, DeviceKeyDialog, RegisterDeviceDialog } from '@/modules/virtualcare/rpm/DeviceDialogs';
import { EditEnrollmentDialog, RecordReadingsDialog } from '@/modules/virtualcare/rpm/EnrollmentDialogs';
import { deviceTypeLabel, enrollmentStatusText, formatReading, programKindLabel, readingTypeInfo, severityText, sourceLabel } from '@/modules/virtualcare/rpm/labels';
import { ThresholdsDialog } from '@/modules/virtualcare/rpm/ThresholdsDialog';
import { describeLimits } from '@/modules/virtualcare/rpm/thresholds';
import { AdherenceChart, TrendChart } from '@/modules/virtualcare/rpm/TrendChart';
import { useRpmLive } from '@/modules/virtualcare/rpm/useRpmLive';

const RANGES = [7, 30, 90] as const;
type RangeDays = (typeof RANGES)[number];

interface EnrollmentSearch {
  /** Alert opened from a notification or the alert queue. */
  alert?: string;
  days?: RangeDays;
  type?: RpmReadingType;
  page?: number;
}

export const Route = createFileRoute('/_app/rpm/enrollments/$enrollmentId')({
  validateSearch: (s: Record<string, unknown>): EnrollmentSearch => ({
    alert: typeof s.alert === 'string' && s.alert ? s.alert : undefined,
    days: RANGES.includes(Number(s.days) as RangeDays) && Number(s.days) !== 30 ? (Number(s.days) as RangeDays) : undefined,
    type: READING_TYPES.includes(s.type as RpmReadingType) ? (s.type as RpmReadingType) : undefined,
    page: typeof s.page === 'number' && s.page > 1 ? s.page : undefined,
  }),
  component: EnrollmentPage,
});

const useStyles = makeStyles({
  summary: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(230px, 1fr))' },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow4,
    minWidth: 0,
  },
  label: { color: tokens.colorNeutralForeground3 },
  charts: { display: 'grid', gap: '16px', gridTemplateColumns: 'repeat(auto-fill, minmax(420px, 1fr))', '@media (max-width: 900px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  chart: { display: 'flex', flexDirection: 'column', gap: '4px', minWidth: 0 },
  chartHead: { display: 'flex', justifyContent: 'space-between', gap: '8px', alignItems: 'baseline', flexWrap: 'wrap' },
  two: { display: 'grid', gap: '16px', gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)', '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  devices: { display: 'flex', flexDirection: 'column', gap: '8px' },
  device: { display: 'flex', alignItems: 'center', gap: '8px', justifyContent: 'space-between', flexWrap: 'wrap' },
  limits: { display: 'grid', gridTemplateColumns: 'minmax(120px, auto) 1fr', gap: '6px 12px' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  pre: { whiteSpace: 'pre-wrap' },
  warn: { color: tokens.colorPaletteMarigoldForeground1 },
});

function EnrollmentPage() {
  const { enrollmentId } = Route.useParams();
  const navigate = useNavigate();
  useRpmLive();
  const enrollment = useQuery(rpmQueries.enrollment(enrollmentId));

  if (enrollment.isLoading) {
    return <LoadingState />;
  }
  if (enrollment.error || !enrollment.data) {
    const restricted = isApiError(enrollment.error) && enrollment.error.code === 'patient.restricted';
    return (
      <Page maxWidth={900}>
        <ErrorState error={enrollment.error} title={restricted ? 'This patient record is restricted' : undefined} onRetry={restricted ? undefined : () => void enrollment.refetch()} />
        <div>
          <Button onClick={() => void navigate({ to: '/rpm' })}>Back to remote monitoring</Button>
        </div>
      </Page>
    );
  }
  return <EnrollmentView enrollment={enrollment.data} />;
}

/** One monitored patient (RPM-01..05): alerts, trends against their limits, adherence, readings and devices. */
function EnrollmentView({ enrollment: e }: { enrollment: RpmEnrollmentDetail }) {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('rpm.manage');
  const today = useToday();
  const days: RangeDays = search.days ?? 30;
  const [dialog, setDialog] = useState<'readings' | 'limits' | 'edit' | 'give' | 'register' | 'delete' | null>(null);
  const [moving, setMoving] = useState<RpmDeviceDto | null>(null);
  const [issued, setIssued] = useState<DeviceKeyIssued | null>(null);

  const alerts = useQuery(rpmQueries.alerts({ enrollmentId: e.id, pageSize: 20 }));
  const trends = useQuery(rpmQueries.trends(e.id, { from: addDays(today, -(days - 1)), to: today }));
  const adherence = useQuery(rpmQueries.adherence(e.id, 30));
  const readings = useQuery(rpmQueries.readings(e.id, { type: search.type, page: search.page }));

  const remove = useMutation({
    mutationFn: () => rpmApi.deleteEnrollment(e.id),
    onSuccess: () => {
      notify.success('Enrolment deleted');
      void queryClient.invalidateQueries({ queryKey: rpmKeys.all });
      void navigate({ to: '/rpm' });
    },
    onError: (error) => {
      setDialog(null);
      notify.error(error, 'Could not delete the enrolment');
    },
  });

  const linkedAlert = search.alert ? alerts.data?.items.find((a) => a.id === search.alert) : undefined;
  const open = e.status === 'Active' || e.status === 'Paused';
  const status = enrollmentStatusText[e.status];
  const lead = e.careTeam.find((m) => m.isPrimary) ?? e.careTeam[0];
  const set = (patch: Partial<EnrollmentSearch>) => void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });

  return (
    <Page>
      <PatientBanner
        patient={{ id: e.patientId, uhid: e.uhid, fullName: e.patientName, gender: e.gender, age: e.age, phone: e.phoneMasked }}
        actions={
          <>
            {canManage && open && (
              <Button appearance="primary" icon={<AddRegular />} onClick={() => setDialog('readings')}>
                Record reading
              </Button>
            )}
            {canManage && (
              <Button icon={<TextBulletListSquareEditRegular />} onClick={() => setDialog('limits')}>
                Alert limits
              </Button>
            )}
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button icon={<MoreHorizontalRegular />} aria-label="More actions" />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  {canManage && (
                    <MenuItem icon={<EditRegular />} onClick={() => setDialog('edit')}>
                      Status, care team and goals
                    </MenuItem>
                  )}
                  <MenuItem icon={<PersonRegular />} onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId: e.patientId } })}>
                    Patient profile
                  </MenuItem>
                  {canManage && (
                    <MenuItem icon={<DeleteRegular />} onClick={() => setDialog('delete')}>
                      Delete enrolment
                    </MenuItem>
                  )}
                </MenuList>
              </MenuPopover>
            </Menu>
          </>
        }
      />

      {linkedAlert && linkedAlert.status !== 'Resolved' && (
        <MessageBar intent={linkedAlert.severity === 'Critical' ? 'error' : 'warning'} layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>{linkedAlert.severity === 'Critical' ? 'Critical alert' : 'Alert'}</MessageBarTitle>
            {linkedAlert.message} · raised {formatRelative(linkedAlert.raisedAt)}. Use the alert list below to mark it as seen or resolve it.
          </MessageBarBody>
        </MessageBar>
      )}

      <div className={styles.summary}>
        <div className={styles.card}>
          <Caption1 className={styles.label}>Programme</Caption1>
          <Text weight="semibold">{e.programName}</Text>
          <Caption1>
            {programKindLabel[e.programKind]} · since {formatDateOnly(e.startDate)}
            {e.endDate ? ` · until ${formatDateOnly(e.endDate)}` : ''}
          </Caption1>
          <span>
            <StatusBadge status={e.status} tone={status.tone} label={status.label} />
          </span>
          <Caption1 className={styles.label}>{e.statusReason ?? status.hint}</Caption1>
        </div>
        <div className={styles.card}>
          <Caption1 className={styles.label}>Care team</Caption1>
          <Text weight="semibold">{lead ? `${lead.name}${lead.isPrimary ? ' (primary)' : ''}` : '—'}</Text>
          {e.careTeam
            .filter((m) => m !== lead)
            .map((m) => (
              <Caption1 key={m.practitionerId}>{m.name}</Caption1>
            ))}
        </div>
        <div className={styles.card}>
          <Caption1 className={styles.label}>Last reading</Caption1>
          <Text weight="semibold">{e.lastReadingAt ? formatRelative(e.lastReadingAt) : 'No readings yet'}</Text>
          {adherence.data && (
            <Caption1 className={adherence.data.percent < 70 ? styles.warn : undefined}>
              Readings on {adherence.data.daysWithReadings} of the last {adherence.data.windowDays} days ({Math.round(adherence.data.percent)}%)
            </Caption1>
          )}
        </div>
        <div className={styles.card}>
          <Caption1 className={styles.label}>Devices</Caption1>
          <Text weight="semibold">{e.devices.length === 0 ? 'None given' : `${e.devices.length} device${e.devices.length === 1 ? '' : 's'}`}</Text>
          {e.devices.some((d) => d.silenceAlerted) && (
            <Caption1 className={styles.warn}>
              <PlugDisconnectedRegular aria-hidden /> A device has stopped sending readings
            </Caption1>
          )}
        </div>
      </div>

      <Section title="Alerts" description="New and seen alerts for this patient" flush>
        <AlertsTable
          alerts={alerts.data?.items}
          loading={alerts.isLoading}
          error={alerts.error}
          onRetry={() => void alerts.refetch()}
          showPatient={false}
          emptyTitle="No open alerts"
          emptyDescription="Readings are within the limits, and every device is sending."
        />
      </Section>

      <Section
        title="Trends"
        description="Green band: normal range. Amber: out of range. Red: critical."
        actions={
          <TabList size="small" selectedValue={String(days)} onTabSelect={(_, d) => set({ days: Number(d.value) === 30 ? undefined : (Number(d.value) as RangeDays) })}>
            {RANGES.map((r) => (
              <Tab key={r} value={String(r)}>
                {r} days
              </Tab>
            ))}
          </TabList>
        }
      >
        {trends.error ? (
          <ErrorState error={trends.error} onRetry={() => void trends.refetch()} />
        ) : trends.isLoading ? (
          <LoadingState />
        ) : (trends.data ?? []).length === 0 ? (
          <EmptyState
            title={`No readings in the last ${days} days`}
            description={e.devices.length === 0 ? 'Give the patient a device, or record readings they report.' : 'Check that the device is switched on and connected.'}
            icon={<SlideTextRegular />}
          />
        ) : (
          <div className={styles.charts}>
            {(trends.data ?? []).map((trend) => {
              const latest = trend.points.at(-1);
              const outside = trend.points.filter((p) => p.severity !== 'Normal').length;
              return (
                <div key={trend.type} className={styles.chart}>
                  <div className={styles.chartHead}>
                    <Subtitle2 as="h3">{trend.label}</Subtitle2>
                    {latest && (
                      <Caption1>
                        Latest {formatReading(trend.type, latest.value)} · {formatRelative(latest.measuredAt)}
                      </Caption1>
                    )}
                  </div>
                  <Caption1 className={styles.label}>
                    {trend.threshold ? `Limits: ${describeLimits(trend.threshold, trend.unit)}` : 'No limits set'} · {trend.points.length} readings
                    {outside > 0 ? `, ${outside} outside` : ''}
                  </Caption1>
                  <TrendChart trend={trend} />
                </div>
              );
            })}
          </div>
        )}
      </Section>

      <div className={styles.two}>
        <Section title="Adherence" description="Readings each day, last 30 days">
          {adherence.error ? (
            <ErrorState error={adherence.error} onRetry={() => void adherence.refetch()} />
          ) : !adherence.data ? (
            <LoadingState />
          ) : (
            <>
              <AdherenceChart adherence={adherence.data} today={today} />
              <Caption1 className={styles.label}>
                Expected {adherence.data.expectedReadingsPerDay} a day.{' '}
                {adherence.data.missedDates.length > 0
                  ? `No readings on ${adherence.data.missedDates.length} day${adherence.data.missedDates.length === 1 ? '' : 's'}, most recently ${formatDayMedium(adherence.data.missedDates.at(-1)!)}.`
                  : 'No missed days.'}
              </Caption1>
            </>
          )}
        </Section>

        <Section
          title="Devices"
          actions={
            canManage &&
            open && (
              <>
                <Button size="small" icon={<LinkRegular />} onClick={() => setDialog('give')}>
                  Give a device
                </Button>
                <Button size="small" icon={<AddRegular />} onClick={() => setDialog('register')}>
                  Register new
                </Button>
              </>
            )
          }
        >
          {e.devices.length === 0 ? (
            <Caption1 className={styles.label}>No device given. Readings can still be recorded by staff.</Caption1>
          ) : (
            <div className={styles.devices}>
              {e.devices.map((d) => (
                <div key={d.id} className={styles.device}>
                  <span>
                    <Text weight="semibold">{deviceTypeLabel[d.type]}</Text> <Caption1 className={styles.label}>{d.serialNumber}</Caption1>
                    <Caption1 block className={d.silenceAlerted ? styles.warn : styles.label}>
                      {d.lastSeenAt ? `Last reading ${formatRelative(d.lastSeenAt)}` : 'No reading received yet'}
                      {d.silenceAlerted ? ' · silent' : ''}
                    </Caption1>
                  </span>
                  {canManage && (
                    <Button size="small" appearance="subtle" onClick={() => setMoving(d)}>
                      Move or take back
                    </Button>
                  )}
                </div>
              ))}
            </div>
          )}
        </Section>
      </div>

      <div className={styles.two}>
        <Section
          title="Alert limits"
          description={e.thresholdsUpdatedAt ? `Last changed ${formatDateTime(e.thresholdsUpdatedAt)}` : 'Standard limits of the programme'}
          actions={
            canManage && (
              <Button size="small" icon={<EditRegular />} onClick={() => setDialog('limits')}>
                Change
              </Button>
            )
          }
        >
          {e.thresholds.length === 0 ? (
            <Caption1 className={styles.label}>No limits: readings never raise alerts.</Caption1>
          ) : (
            <div className={styles.limits}>
              {e.thresholds.map((t) => (
                <Row key={t.type} label={t.label}>
                  {describeLimits(t, t.unit)}
                </Row>
              ))}
            </div>
          )}
        </Section>
        <Section title="Goals and notes">
          <div className={styles.limits}>
            <Row label="Goals">{e.goals}</Row>
            <Row label="Notes">{e.notes}</Row>
          </div>
        </Section>
      </div>

      <Section title="Readings" flush>
        <div style={{ padding: '0 16px 12px' }}>
          <Field label="Reading" style={{ maxWidth: 260 }}>
            <Select value={search.type ?? ''} onChange={(_, d) => set({ type: (d.value || undefined) as RpmReadingType | undefined, page: undefined })}>
              <option value="">All readings</option>
              {READING_TYPES.map((t) => (
                <option key={t} value={t}>
                  {readingTypeInfo[t].label}
                </option>
              ))}
            </Select>
          </Field>
        </div>
        <DataTable
          ariaLabel="Readings"
          rows={readings.data?.items}
          loading={readings.isLoading}
          error={readings.error}
          onRetry={() => void readings.refetch()}
          getRowId={(r) => r.id}
          columns={[
            { key: 'when', header: 'Measured', width: 180, render: (r) => formatDateTime(r.measuredAt) },
            { key: 'type', header: 'Reading', render: (r) => readingTypeInfo[r.type].label },
            { key: 'value', header: 'Value', render: (r) => <Text weight="semibold">{formatReading(r.type, r.value, r.textValue)}</Text> },
            {
              key: 'severity',
              header: 'Result',
              width: 130,
              render: (r) => <StatusBadge status={r.severity} tone={severityText[r.severity].tone} label={severityText[r.severity].label} />,
            },
            { key: 'source', header: 'Source', priority: 'low', render: (r) => sourceLabel[r.source] },
          ]}
          emptyTitle="No readings"
          page={search.page ?? 1}
          pageSize={20}
          totalCount={readings.data?.totalCount}
          onPageChange={(page) => set({ page: page > 1 ? page : undefined })}
          size="small"
        />
      </Section>

      <RecordReadingsDialog enrollment={dialog === 'readings' ? e : null} onClose={() => setDialog(null)} />
      <ThresholdsDialog enrollment={dialog === 'limits' ? e : null} onClose={() => setDialog(null)} />
      <EditEnrollmentDialog enrollment={dialog === 'edit' ? e : null} onClose={() => setDialog(null)} />
      <GiveDeviceDialog open={dialog === 'give'} enrollment={e} onClose={() => setDialog(null)} />
      <RegisterDeviceDialog
        open={dialog === 'register'}
        enrollment={{ id: e.id, patientName: e.patientName, uhid: e.uhid, programName: e.programName }}
        onClose={() => setDialog(null)}
        onRegistered={(key) => {
          setDialog(null);
          setIssued(key);
        }}
      />
      <DeviceKeyDialog issued={issued} onClose={() => setIssued(null)} />
      <AssignDeviceDialog device={moving} onClose={() => setMoving(null)} />
      <ConfirmDialog
        open={dialog === 'delete'}
        title="Delete this enrolment?"
        confirmLabel="Delete"
        destructive
        busy={remove.isPending}
        onConfirm={() => remove.mutate()}
        onCancel={() => setDialog(null)}
      >
        Only an enrolment made by mistake, with no readings or alerts, can be deleted. To stop monitoring, set it to completed or withdrawn instead.
      </ConfirmDialog>
    </Page>
  );
}

function Row({ label, children }: { label: string; children: string | null }) {
  const styles = useStyles();
  return (
    <>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text className={styles.pre}>{children || '—'}</Text>
    </>
  );
}

/** Gives one of the unassigned devices to this patient. */
function GiveDeviceDialog({ open, enrollment, onClose }: { open: boolean; enrollment: RpmEnrollmentDetail; onClose: () => void }) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>{open && <GiveDeviceContent enrollment={enrollment} onClose={onClose} />}</DialogSurface>
    </Dialog>
  );
}

function GiveDeviceContent({ enrollment, onClose }: { enrollment: RpmEnrollmentDetail; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const devices = useQuery(rpmQueries.devices({ unassigned: true }));
  const [deviceId, setDeviceId] = useState('');
  const assign = useMutation({
    mutationFn: () => rpmApi.assignDevice(deviceId, enrollment.id),
    onSuccess: () => {
      notify.success('Device given to the patient');
      void queryClient.invalidateQueries({ queryKey: rpmKeys.all });
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not assign the device'),
  });

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        if (deviceId) {
          assign.mutate();
        }
      }}
    >
      <DialogBody>
        <DialogTitle>Give a device to {enrollment.patientName}</DialogTitle>
        <DialogContent>
          {devices.isLoading ? (
            <LoadingState />
          ) : (devices.data ?? []).length === 0 ? (
            <Caption1>No unassigned devices. Register a new device instead.</Caption1>
          ) : (
            <Field label="Device" required>
              <Select value={deviceId} onChange={(_, d) => setDeviceId(d.value)} autoFocus>
                <option value="">Choose a device</option>
                {(devices.data ?? []).map((d) => (
                  <option key={d.id} value={d.id}>
                    {deviceTypeLabel[d.type]} · {d.serialNumber}
                  </option>
                ))}
              </Select>
            </Field>
          )}
          <Badge appearance="tint" color="informative" style={{ marginTop: 12 }}>
            The device sends readings for this patient from now on
          </Badge>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={assign.isPending}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" disabled={!deviceId || assign.isPending}>
            Give device
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
