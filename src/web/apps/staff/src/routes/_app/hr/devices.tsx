import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarBody,
  Text,
  tokens,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  AddRegular,
  ArrowDownloadRegular,
  ClockRegular,
  CopyRegular,
  EditRegular,
  FingerprintRegular,
  HandRightRegular,
  KeyResetRegular,
  MoreHorizontalRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatRelative, isApiError } from '@hc360/core';
import {
  applyServerErrors,
  ConfirmDialog,
  DataTable,
  FormGrid,
  NumberField,
  Page,
  PageHeader,
  Section,
  SelectField,
  StatusBadge,
  SwitchField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments } from '@/lib/lookups';
import { hrApi, hrKeys, hrQueries, type BiometricDeviceDto, type DeviceKeyIssued } from '@/modules/hr/api';
import {
  clockDrift,
  connectionText,
  DEFAULT_PULL_MINUTES,
  DEFAULT_PULL_PORT,
  everyText,
  MAX_PULL_MINUTES,
  readSummary,
  terminalStatus,
} from '@/modules/hr/terminals';
import { HR_PERMISSIONS as P } from '@/modules/hr/labels';
import { EditorDrawer } from '@/modules/hr/shared/EditorDrawer';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { Detail, DetailGrid } from '@/modules/hr/shared/DetailGrid';

export const Route = createFileRoute('/_app/hr/devices')({
  component: () => (
    <RequirePermission permission={P.attendanceManage} what="attendance terminals">
      <DevicesPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  key: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase400,
    padding: '12px 16px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    wordBreak: 'break-all',
    userSelect: 'all',
  },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  muted: { color: tokens.colorNeutralForeground3 },
  problem: { color: tokens.colorPaletteRedForeground1, maxWidth: '320px' },
});

function DevicesPage() {
  const styles = useStyles();
  // Closing a dialog opened from a row's menu returns focus to that row's menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const devices = useQuery(hrQueries.devices());
  const reading = useQuery(hrQueries.terminalReading());
  const [editing, setEditing] = useState<BiometricDeviceDto | 'new' | null>(null);
  const [rotating, setRotating] = useState<BiometricDeviceDto | null>(null);
  const [issued, setIssued] = useState<DeviceKeyIssued | null>(null);
  const [settingClock, setSettingClock] = useState<BiometricDeviceDto | null>(null);
  const [punching, setPunching] = useState<BiometricDeviceDto | null>(null);
  const training = reading.data?.provider === 'simulator';

  const read = useMutation({
    mutationFn: (device: BiometricDeviceDto) => hrApi.readDevice(device.id),
    onSuccess: (result, device) => {
      const summary = readSummary(result);
      if (result.success) {
        notify.success(`${device.name}: ${summary.title}`, summary.detail ?? undefined);
      } else {
        notify.warning(`${device.name}: ${summary.title}`, summary.detail ?? undefined);
      }
      void queryClient.invalidateQueries({ queryKey: hrKeys.devices });
    },
    onError: (error) => notify.error(error, 'Could not read the terminal'),
  });

  const setClock = useMutation({
    mutationFn: (id: string) => hrApi.setDeviceClock(id),
    onSuccess: (result) => {
      setSettingClock(null);
      if (result.success) {
        notify.success('Clock set to the hospital’s time');
      } else {
        notify.warning('The clock was not set', result.error ?? undefined);
      }
      void queryClient.invalidateQueries({ queryKey: hrKeys.devices });
    },
    onError: (error) => notify.error(error, 'Could not set the clock'),
  });

  const rotate = useMutation({
    mutationFn: (id: string) => hrApi.rotateDeviceKey(id),
    onSuccess: (key) => {
      setRotating(null);
      setIssued(key);
      void queryClient.invalidateQueries({ queryKey: hrKeys.devices });
    },
    onError: (error) => notify.error(error, 'Could not issue a new key'),
  });

  const origin = typeof window === 'undefined' ? '' : window.location.origin;

  return (
    <Page maxWidth={1300}>
      <PageHeader
        title="Attendance terminals"
        subtitle="Fingerprint and face terminals that send their punches to HealthCare360, or that HealthCare360 reads. Only attendance records are taken; fingerprints never leave the terminal."
        icon={<FingerprintRegular />}
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
            Register terminal
          </Button>
        }
      />
      <Section flush>
        <DataTable
          ariaLabel="Attendance terminals"
          rows={devices.data}
          loading={devices.isLoading}
          error={devices.error}
          onRetry={() => void devices.refetch()}
          getRowId={(d) => d.id}
          onRowClick={(d) => setEditing(d)}
          columns={[
            {
              key: 'name',
              header: 'Terminal',
              render: (d) => (
                <span>
                  <Text weight="semibold">{d.name}</Text>
                  <Caption1 block className={styles.muted}>
                    {d.location ?? 'Location not recorded'}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'serial',
              header: 'Serial number',
              render: (d) => <Text className={styles.mono}>{d.serialNumber}</Text>,
            },
            { key: 'model', header: 'Model', priority: 'low', render: (d) => d.model ?? '—' },
            {
              key: 'online',
              header: 'Connection',
              render: (d) => {
                const status = terminalStatus(d);
                const drift = d.connection === 'Pull' ? clockDrift(d.clockOffsetSeconds) : null;
                return (
                  <span>
                    <StatusBadge status={status.label} tone={status.tone} label={status.label} />
                    <Caption1 block className={styles.muted}>
                      {connectionText(d)}
                    </Caption1>
                    {status.detail && (
                      <Caption1 block className={styles.problem}>
                        {status.detail}
                      </Caption1>
                    )}
                    {drift && (
                      <Caption1 block className={styles.problem}>
                        {drift}
                      </Caption1>
                    )}
                  </span>
                );
              },
            },
            {
              key: 'last',
              header: 'Last contact',
              priority: 'low',
              render: (d) =>
                formatRelative(
                  d.connection === 'Pull' ? (d.lastHandshakeAt ?? d.lastPullAt) : (d.lastPushAt ?? d.lastHandshakeAt),
                ),
            },
            {
              key: 'ip',
              header: 'Allowed address',
              priority: 'low',
              render: (d) => (d.connection === 'Pull' ? '—' : (d.allowedIpAddress ?? 'First address it calls from')),
            },
          ]}
          rowActions={(d) => (
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<MoreHorizontalRegular />}
                  aria-label={`Actions for ${d.name}`}
                  {...restoreFocusTarget}
                />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem icon={<EditRegular />} onClick={() => setEditing(d)}>
                    Edit
                  </MenuItem>
                  {d.connection === 'Pull' && (
                    <>
                      <MenuItem
                        icon={<ArrowDownloadRegular />}
                        disabled={!d.isActive || read.isPending}
                        onClick={() => read.mutate(d)}
                      >
                        Read now
                      </MenuItem>
                      <MenuItem icon={<ClockRegular />} disabled={!d.isActive} onClick={() => setSettingClock(d)}>
                        Set the terminal’s clock
                      </MenuItem>
                      {training && (
                        <MenuItem icon={<HandRightRegular />} disabled={!d.isActive} onClick={() => setPunching(d)}>
                          Punch on the training terminal
                        </MenuItem>
                      )}
                    </>
                  )}
                  <MenuItem icon={<KeyResetRegular />} onClick={() => setRotating(d)}>
                    Issue a new key
                  </MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
          emptyTitle="No terminals registered"
          emptyDescription="Register each terminal with the serial number printed on it before connecting it."
          emptyAction={
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setEditing('new')}>
              Register terminal
            </Button>
          }
          size="small"
        />
      </Section>

      <Section title="Connecting a terminal" description="For the IT or biomedical team">
        <DetailGrid minColumnWidth={260}>
          <Detail label="ADMS / push server address (ZKTeco)" mono>
            {origin}
          </Detail>
          <Detail label="JSON punch upload (middleware)" mono>
            POST {origin}/api/v1/hr/attendance/device-punches
          </Detail>
          <Detail label="Headers for JSON upload">
            X-Device-Serial and X-Device-Key (the key shown once when registering)
          </Detail>
          <Detail label="Online">Heard from in the last 10 minutes</Detail>
          <Detail label="Terminals HC360 reads (ZKTeco / eSSL)">
            Give the terminal a fixed IP address. HC360 reads it on TCP port 4370 every few minutes; set the provider
            and the terminals’ COMM Key in Administration › Integrations › Biometric attendance.
          </Detail>
          <Detail label="Reading terminals">
            {reading.isLoading
              ? 'Checking…'
              : reading.data?.provider
                ? reading.data.provider === 'simulator'
                  ? 'With the training terminal: punch on it from a terminal’s menu, then Read now.'
                  : 'Set up.'
                : (reading.data?.problem ?? 'Not set up.')}
          </Detail>
        </DetailGrid>
      </Section>

      {editing && (
        <DeviceDrawer
          device={editing === 'new' ? null : editing}
          readingProblem={reading.data?.provider ? null : (reading.data?.problem ?? null)}
          onClose={() => setEditing(null)}
          onIssued={(key) => {
            setEditing(null);
            if (key.apiKey) {
              setIssued(key);
            } else {
              notify.success('Terminal registered', 'HC360 reads it at the next read; use Read now to try it at once.');
            }
          }}
        />
      )}

      {punching && <TrainingPunchDialog device={punching} onClose={() => setPunching(null)} />}

      <ConfirmDialog
        open={settingClock !== null}
        title="Set the terminal’s clock?"
        confirmLabel="Set the clock"
        busy={setClock.isPending}
        onConfirm={() => settingClock && setClock.mutate(settingClock.id)}
        onCancel={() => setSettingClock(null)}
      >
        {settingClock?.name}’s clock is set to the hospital’s time.{' '}
        {clockDrift(settingClock?.clockOffsetSeconds)
          ? `It is now: ${clockDrift(settingClock?.clockOffsetSeconds)}. `
          : ''}
        Punches already made keep the times they were given. The change is recorded in the audit trail.
      </ConfirmDialog>

      <ConfirmDialog
        open={rotating !== null}
        title="Issue a new key?"
        confirmLabel="Issue new key"
        destructive
        busy={rotate.isPending}
        onConfirm={() => rotating && rotate.mutate(rotating.id)}
        onCancel={() => setRotating(null)}
      >
        The current key of {rotating?.name} stops working immediately. Enter the new key on the terminal or middleware
        straight away, or its punches will be refused. The change is recorded in the audit trail.
      </ConfirmDialog>

      <Dialog open={issued !== null} onOpenChange={(_, d) => !d.open && setIssued(null)} modalType="alert">
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Device key for {issued?.serialNumber}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <div className={styles.key}>{issued?.apiKey}</div>
              <MessageBar intent="warning" layout="multiline">
                <MessageBarBody>
                  This key is shown only once and is stored only as a fingerprint (hash). Copy it into the terminal or
                  middleware now. If it is lost, issue a new key.
                </MessageBarBody>
              </MessageBar>
            </DialogContent>
            <DialogActions>
              <Button
                icon={<CopyRegular />}
                onClick={() =>
                  issued?.apiKey &&
                  void navigator.clipboard?.writeText(issued.apiKey).then(
                    () => notify.success('Key copied'),
                    () => notify.warning('Copy failed', 'Select the key and copy it by hand.'),
                  )
                }
              >
                Copy key
              </Button>
              <Button appearance="primary" onClick={() => setIssued(null)}>
                I have saved it
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </Page>
  );
}

const ipAddress = (v: string) => /^(\d{1,3}\.){3}\d{1,3}$/.test(v) || /^[0-9a-fA-F:]+$/.test(v);

const schema = z
  .object({
    serialNumber: z
      .string()
      .trim()
      .min(1, 'Enter the serial number exactly as shown on the terminal.')
      .max(64)
      .regex(/^[A-Za-z0-9_-]+$/, 'Use the serial number exactly as shown on the terminal.'),
    name: z.string().trim().min(1, 'Give the terminal a name, e.g. Main gate.').max(100),
    location: z.string().trim().max(150),
    departmentId: z.string().nullable(),
    model: z.string().trim().max(64),
    allowedIpAddress: z
      .string()
      .trim()
      .refine((v) => !v || ipAddress(v), 'Enter a valid IP address.'),
    isActive: z.boolean(),
    connection: z.enum(['Push', 'Pull']),
    pullAddress: z.string().trim().max(45),
    pullPort: z.number({ error: 'Enter the terminal’s port.' }).int().min(1).max(65535).nullable(),
    pullEveryMinutes: z
      .number({ error: 'Enter how often to read the terminal.' })
      .int()
      .min(1, 'Read the terminal at least every minute.')
      .max(MAX_PULL_MINUTES, 'Read the terminal at least once a day.')
      .nullable(),
  })
  .superRefine((v, ctx) => {
    // A pushing terminal is known by the serial number printed on it, so its address must match too.
    if (v.connection === 'Push' && !v.allowedIpAddress) {
      ctx.addIssue({
        code: 'custom',
        path: ['allowedIpAddress'],
        message: 'Enter the terminal’s IP address: punches from any other address are refused.',
      });
    }
    if (v.connection === 'Pull' && !ipAddress(v.pullAddress)) {
      ctx.addIssue({
        code: 'custom',
        path: ['pullAddress'],
        message: 'Enter the terminal’s IP address on the device network, for example 192.168.1.201.',
      });
    }
  });

type Values = z.infer<typeof schema>;

function DeviceDrawer({
  device,
  readingProblem,
  onClose,
  onIssued,
}: {
  device: BiometricDeviceDto | null;
  /** Why HC360 cannot read terminals yet, shown when this one is to be read. */
  readingProblem: string | null;
  onClose: () => void;
  onIssued: (key: DeviceKeyIssued) => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      serialNumber: device?.serialNumber ?? '',
      name: device?.name ?? '',
      location: device?.location ?? '',
      departmentId: device?.departmentId ?? null,
      model: device?.model ?? '',
      allowedIpAddress: device?.allowedIpAddress ?? '',
      isActive: device?.isActive ?? true,
      connection: device?.connection ?? 'Push',
      pullAddress: device?.pullAddress ?? '',
      pullPort: device?.pullPort ?? DEFAULT_PULL_PORT,
      pullEveryMinutes: device?.pullEveryMinutes ?? DEFAULT_PULL_MINUTES,
    },
  });
  const pull = useWatch({ control: form.control, name: 'connection' }) === 'Pull';
  const every = useWatch({ control: form.control, name: 'pullEveryMinutes' });

  const save = useMutation({
    mutationFn: async (v: Values) => {
      const request = {
        serialNumber: v.serialNumber.trim(),
        name: v.name.trim(),
        location: v.location.trim() || null,
        departmentId: v.departmentId,
        model: v.model.trim() || null,
        allowedIpAddress: v.connection === 'Pull' ? null : v.allowedIpAddress.trim() || null,
        isActive: v.isActive,
        version: device?.version ?? null,
        connection: v.connection,
        pullAddress: v.connection === 'Pull' ? v.pullAddress.trim() : null,
        pullPort: v.connection === 'Pull' ? (v.pullPort ?? DEFAULT_PULL_PORT) : null,
        pullEveryMinutes: v.connection === 'Pull' ? (v.pullEveryMinutes ?? DEFAULT_PULL_MINUTES) : null,
      };
      if (device) {
        await hrApi.updateDevice(device.id, request);
        return null;
      }
      return hrApi.registerDevice(request);
    },
    onSuccess: (key) => {
      void queryClient.invalidateQueries({ queryKey: hrKeys.devices });
      if (key) {
        onIssued(key);
      } else {
        notify.success('Terminal saved');
        onClose();
      }
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'device.serial_in_use') {
        form.setError('serialNumber', { type: 'server', message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the terminal');
      }
    },
  });

  return (
    <EditorDrawer
      open
      title={device ? device.name : 'Register terminal'}
      dirty={form.formState.isDirty}
      onClose={onClose}
      onSubmit={form.handleSubmit((v) => save.mutate(v))}
      busy={save.isPending}
      submitLabel={device ? 'Save' : pull ? 'Register' : 'Register and show key'}
    >
      <TextField
        control={form.control}
        name="serialNumber"
        label="Serial number"
        required
        disabled={Boolean(device)}
        autoFocus={!device}
        hint={
          device
            ? 'A registered serial number cannot change; register a replacement terminal instead.'
            : 'Printed on the terminal and shown in its system menu.'
        }
      />
      <TextField control={form.control} name="name" label="Name" required autoFocus={Boolean(device)} />
      <FormGrid columns={2} minColumnWidth={180}>
        <TextField control={form.control} name="location" label="Where it is" />
        <TextField control={form.control} name="model" label="Model" />
      </FormGrid>
      <SelectField
        control={form.control}
        name="departmentId"
        label="Department"
        placeholder="Hospital-wide"
        options={toOptions(departments.data)}
      />
      <SelectField
        control={form.control}
        name="connection"
        label="How punches reach HealthCare360"
        options={[
          { value: 'Push', label: 'The terminal sends them (ADMS or the punch API)' },
          { value: 'Pull', label: 'HealthCare360 reads the terminal (ZKTeco / eSSL, TCP 4370)' },
        ]}
      />
      {pull ? (
        <>
          {readingProblem && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                HealthCare360 cannot read terminals yet: {readingProblem} Ask the administrator to set up Biometric
                attendance.
              </MessageBarBody>
            </MessageBar>
          )}
          <FormGrid columns={2} minColumnWidth={180}>
            <TextField
              control={form.control}
              name="pullAddress"
              label="Terminal’s IP address"
              required
              hint="Fixed address on the device network, shown in the terminal’s Comm. menu."
            />
            <NumberField control={form.control} name="pullPort" label="Port" min={1} max={65535} hint="Usually 4370." />
          </FormGrid>
          <NumberField
            control={form.control}
            name="pullEveryMinutes"
            label="Read it every (minutes)"
            min={1}
            max={MAX_PULL_MINUTES}
            hint={
              typeof every === 'number' && every >= 1
                ? `Every ${everyText(every)}. HR is told if it cannot be read for a while.`
                : undefined
            }
          />
        </>
      ) : (
        <TextField
          control={form.control}
          name="allowedIpAddress"
          label="Terminal's IP address"
          hint="Punches are accepted only from this address. Give the terminal a fixed address on the LAN."
        />
      )}
      <SwitchField control={form.control} name="isActive" label="Accept punches from this terminal" />
    </EditorDrawer>
  );
}

const punchSchema = z.object({
  pin: z
    .string()
    .trim()
    .min(1, 'Enter the employee’s number on the terminal.')
    .max(24)
    .regex(/^[A-Za-z0-9]+$/, 'Use letters and digits only, as on the terminal.'),
});

/** Training: punch with an employee's number on the simulated terminal, then read it in. */
function TrainingPunchDialog({ device, onClose }: { device: BiometricDeviceDto; onClose: () => void }) {
  const notify = useNotify();
  const form = useForm<z.infer<typeof punchSchema>>({ resolver: zodResolver(punchSchema), defaultValues: { pin: '' } });
  const punch = useMutation({
    mutationFn: (pin: string) => hrApi.trainingPunch(device.id, pin),
    onSuccess: (_, pin) => {
      notify.success(`Punched ${pin} on ${device.name}`, 'Read the terminal to bring the punch in.');
      form.reset({ pin: '' });
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not punch on the training terminal');
      }
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <form onSubmit={form.handleSubmit((v) => punch.mutate(v.pin))}>
          <DialogBody>
            <DialogTitle>Punch on {device.name}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Text>
                As an employee would at a real terminal. The punch is made now and stays on the training terminal until
                it is read.
              </Text>
              <TextField
                control={form.control}
                name="pin"
                label="Employee’s number on the terminal"
                required
                autoFocus
                hint="The biometric PIN on the employee’s record."
              />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose}>Close</Button>
              <Button appearance="primary" type="submit" disabled={punch.isPending}>
                Punch
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
