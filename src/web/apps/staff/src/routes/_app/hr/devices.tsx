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
} from '@fluentui/react-components';
import { AddRegular, CopyRegular, EditRegular, FingerprintRegular, KeyResetRegular, MoreHorizontalRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatRelative, isApiError } from '@hc360/core';
import { applyServerErrors, ConfirmDialog, DataTable, FormGrid, Page, PageHeader, Section, SelectField, StatusBadge, SwitchField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments } from '@/lib/lookups';
import { hrApi, hrKeys, hrQueries, type BiometricDeviceDto, type DeviceKeyIssued } from '@/modules/hr/api';
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
});

function DevicesPage() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const devices = useQuery(hrQueries.devices());
  const [editing, setEditing] = useState<BiometricDeviceDto | 'new' | null>(null);
  const [rotating, setRotating] = useState<BiometricDeviceDto | null>(null);
  const [issued, setIssued] = useState<DeviceKeyIssued | null>(null);

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
        subtitle="Fingerprint and face terminals that send punches to HealthCare360. Only attendance logs are received; fingerprints never leave the terminal."
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
            { key: 'serial', header: 'Serial number', render: (d) => <Text className={styles.mono}>{d.serialNumber}</Text> },
            { key: 'model', header: 'Model', priority: 'low', render: (d) => d.model ?? '—' },
            {
              key: 'online',
              header: 'Connection',
              render: (d) =>
                !d.isActive ? (
                  <StatusBadge status="Inactive" tone="informative" label="Switched off here" />
                ) : d.isOnline ? (
                  <StatusBadge status="Online" tone="success" label="Online" />
                ) : (
                  <StatusBadge status="Offline" tone="danger" label={d.lastPushAt || d.lastHandshakeAt ? 'Not heard from' : 'Never connected'} />
                ),
            },
            { key: 'last', header: 'Last contact', priority: 'low', render: (d) => formatRelative(d.lastPushAt ?? d.lastHandshakeAt) },
            { key: 'ip', header: 'Allowed address', priority: 'low', render: (d) => d.allowedIpAddress ?? 'Any' },
          ]}
          rowActions={(d) => (
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button size="small" appearance="subtle" icon={<MoreHorizontalRegular />} aria-label={`Actions for ${d.name}`} />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem icon={<EditRegular />} onClick={() => setEditing(d)}>
                    Edit
                  </MenuItem>
                  <MenuItem icon={<KeyResetRegular />} onClick={() => setRotating(d)}>
                    Issue a new key
                  </MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
          emptyTitle="No terminals registered"
          emptyDescription="Register each terminal with the serial number printed on it before connecting it."
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
          <Detail label="Headers for JSON upload">X-Device-Serial and X-Device-Key (the key shown once when registering)</Detail>
          <Detail label="Online">Heard from in the last 10 minutes</Detail>
        </DetailGrid>
      </Section>

      {editing && (
        <DeviceDrawer
          device={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onIssued={(key) => {
            setEditing(null);
            setIssued(key);
          }}
        />
      )}

      <ConfirmDialog
        open={rotating !== null}
        title="Issue a new key?"
        confirmLabel="Issue new key"
        destructive
        busy={rotate.isPending}
        onConfirm={() => rotating && rotate.mutate(rotating.id)}
        onCancel={() => setRotating(null)}
      >
        The current key of {rotating?.name} stops working immediately. Enter the new key on the terminal or middleware straight away, or its punches will be refused.
        The change is recorded in the audit trail.
      </ConfirmDialog>

      <Dialog open={issued !== null} onOpenChange={(_, d) => !d.open && setIssued(null)} modalType="alert">
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Device key for {issued?.serialNumber}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <div className={styles.key}>{issued?.apiKey}</div>
              <MessageBar intent="warning" layout="multiline">
                <MessageBarBody>
                  This key is shown only once and is stored only as a fingerprint (hash). Copy it into the terminal or middleware now. If it is lost, issue a new key.
                </MessageBarBody>
              </MessageBar>
            </DialogContent>
            <DialogActions>
              <Button
                icon={<CopyRegular />}
                onClick={() =>
                  issued &&
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

const schema = z.object({
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
    .refine((v) => !v || /^(\d{1,3}\.){3}\d{1,3}$/.test(v) || /^[0-9a-fA-F:]+$/.test(v), 'Enter a valid IP address.'),
  isActive: z.boolean(),
});

type Values = z.infer<typeof schema>;

function DeviceDrawer({ device, onClose, onIssued }: { device: BiometricDeviceDto | null; onClose: () => void; onIssued: (key: DeviceKeyIssued) => void }) {
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
    },
  });

  const save = useMutation({
    mutationFn: async (v: Values) => {
      const request = {
        serialNumber: v.serialNumber.trim(),
        name: v.name.trim(),
        location: v.location.trim() || null,
        departmentId: v.departmentId,
        model: v.model.trim() || null,
        allowedIpAddress: v.allowedIpAddress.trim() || null,
        isActive: v.isActive,
        version: device?.version ?? null,
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
      onClose={onClose}
      onSubmit={form.handleSubmit((v) => save.mutate(v))}
      busy={save.isPending}
      submitLabel={device ? 'Save' : 'Register and show key'}
    >
      <TextField
        control={form.control}
        name="serialNumber"
        label="Serial number"
        required
        disabled={Boolean(device)}
        autoFocus={!device}
        hint={device ? 'A registered serial number cannot change; register a replacement terminal instead.' : 'Printed on the terminal and shown in its system menu.'}
      />
      <TextField control={form.control} name="name" label="Name" required autoFocus={Boolean(device)} />
      <FormGrid columns={2} minColumnWidth={180}>
        <TextField control={form.control} name="location" label="Where it is" />
        <TextField control={form.control} name="model" label="Model" />
      </FormGrid>
      <SelectField control={form.control} name="departmentId" label="Department" placeholder="Hospital-wide" options={toOptions(departments.data)} />
      <TextField control={form.control} name="allowedIpAddress" label="Only accept from this IP address" hint="Optional: pins the terminal to its fixed LAN address." />
      <SwitchField control={form.control} name="isActive" label="Accept punches from this terminal" />
    </EditorDrawer>
  );
}
