import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  DesktopRegular,
  ProhibitedRegular,
  SerialPortRegular,
  TabletRegular,
  TvRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatRelative, humanize } from '@hc360/core';
import {
  ConfirmDialog,
  DataTable,
  Page,
  PageHeader,
  Section,
  StatusBadge,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { api } from '@/lib/api';
import { toOptions, useDepartments } from '@/lib/lookups';
import { adminQueries, type DeviceDto, type DeviceKind } from '@/modules/admin/api';
import { desktopUpdateSummary } from '@/modules/admin/desktopVersions';
import { DeviceVersion } from '@/modules/admin/DeviceVersion';

export const Route = createFileRoute('/_app/admin/devices')({
  component: DevicesPage,
});

const KINDS: DeviceKind[] = ['Workstation', 'Tablet', 'Kiosk', 'DisplayBoard', 'LabAgent'];

function kindLabel(kind: DeviceKind): string {
  return kind === 'DisplayBoard' ? 'Queue display (TV)' : kind === 'LabAgent' ? 'Lab analyser agent' : humanize(kind);
}

const useStyles = makeStyles({
  code: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: '40px',
    lineHeight: '48px',
    letterSpacing: '6px',
    textAlign: 'center',
    padding: '16px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorNeutralBackground3,
    userSelect: 'all',
  },
  weak: { color: tokens.colorPaletteMarigoldForeground1, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
});

function kindIcon(kind: DeviceKind) {
  return kind === 'DisplayBoard' ? (
    <TvRegular />
  ) : kind === 'Tablet' ? (
    <TabletRegular />
  ) : kind === 'LabAgent' ? (
    <SerialPortRegular />
  ) : (
    <DesktopRegular />
  );
}

/**
 * Enrolled HC360 Desktop installations (SRS DED-05): enrolment codes, WiFi quality per workstation, revocation, and
 * each one's version against the one the server offers them (SRS DED-18).
 */
function DevicesPage() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const devices = useQuery(adminQueries.devices());
  const desktopUpdate = useQuery(adminQueries.desktopUpdate());
  const offeredVersion = desktopUpdate.data?.offeredVersion;
  const updateSummary = desktopUpdateSummary(devices.data, desktopUpdate.data);
  const [enrolling, setEnrolling] = useState(false);
  const [editing, setEditing] = useState<DeviceDto | null>(null);
  const [revoking, setRevoking] = useState<DeviceDto | null>(null);

  const revoke = useMutation({
    mutationFn: (device: DeviceDto) => api.post(`/api/v1/admin/devices/${device.id}/revoke`),
    onSuccess: () => {
      setRevoking(null);
      notify.success('Device revoked', 'It can no longer be used for sign-ins that require an enrolled device.');
      void queryClient.invalidateQueries({ queryKey: ['admin', 'devices'] });
    },
    onError: (error) => notify.error(error, 'Could not revoke the device'),
  });

  return (
    <Page>
      <PageHeader
        title="Devices"
        subtitle="Computers, tablets, kiosks and TV screens running HC360 Desktop, and lab PCs running the HC360 lab agent"
        actions={
          <Button appearance="primary" icon={<AddRegular />} onClick={() => setEnrolling(true)}>
            Enrol a device
          </Button>
        }
      />
      {updateSummary && (
        <MessageBar intent={updateSummary.intent}>
          <MessageBarBody>{updateSummary.text}</MessageBarBody>
        </MessageBar>
      )}
      <Section flush>
        <DataTable
          ariaLabel="Devices"
          rows={devices.data}
          loading={devices.isLoading}
          error={devices.error}
          onRetry={() => void devices.refetch()}
          getRowId={(d) => d.id}
          onRowClick={(d) => !d.isRevoked && setEditing(d)}
          columns={[
            {
              key: 'name',
              header: 'Device',
              render: (d) => (
                <span style={{ display: 'inline-flex', gap: 8, alignItems: 'center' }}>
                  {kindIcon(d.kind)}
                  <span>
                    <Text weight="semibold">{d.name}</Text>
                    <Caption1 block className={styles.muted}>
                      {[d.kind === 'DisplayBoard' ? 'Queue display' : kindLabel(d.kind), d.machineName, d.location]
                        .filter(Boolean)
                        .join(' · ')}
                    </Caption1>
                  </span>
                </span>
              ),
            },
            {
              key: 'wifi',
              header: 'WiFi',
              render: (d) =>
                d.lastWifiSsid ? (
                  <span className={d.lastWifiSignal !== null && d.lastWifiSignal < 40 ? styles.weak : undefined}>
                    {d.lastWifiSsid}
                    {d.lastWifiSignal !== null ? ` · ${d.lastWifiSignal}%` : ''}
                  </span>
                ) : (
                  '—'
                ),
            },
            {
              key: 'seen',
              header: 'Last seen',
              render: (d) => (d.lastSeenAt ? formatRelative(d.lastSeenAt) : 'Never'),
            },
            { key: 'ip', header: 'Address', priority: 'low', render: (d) => d.lastIpAddress ?? '—' },
            {
              key: 'version',
              header: 'App version',
              render: (d) => <DeviceVersion device={d} offeredVersion={offeredVersion} />,
            },
            { key: 'enrolled', header: 'Enrolled', priority: 'low', render: (d) => formatDateTime(d.enrolledAt) },
            {
              key: 'status',
              header: 'Status',
              width: 110,
              render: (d) => <StatusBadge status={d.isRevoked ? 'Revoked' : 'Active'} />,
            },
          ]}
          rowActions={(d) =>
            d.isRevoked ? null : (
              <Button size="small" appearance="subtle" icon={<ProhibitedRegular />} onClick={() => setRevoking(d)}>
                Revoke
              </Button>
            )
          }
          emptyTitle="No devices enrolled yet"
          emptyDescription="Install HC360 Desktop on a workstation, then enter an enrolment code from here."
        />
      </Section>

      {enrolling && <EnrolDialog onClose={() => setEnrolling(false)} />}
      {editing && <EditDeviceDialog device={editing} onClose={() => setEditing(null)} />}
      <ConfirmDialog
        open={revoking !== null}
        title={`Revoke ${revoking?.name}?`}
        confirmLabel="Revoke"
        destructive
        busy={revoke.isPending}
        onConfirm={() => revoking && revoke.mutate(revoking)}
        onCancel={() => setRevoking(null)}
      >
        Revoke a device that was lost, stolen or replaced. To use it again it must be enrolled with a new code.
      </ConfirmDialog>
    </Page>
  );
}

function EnrolDialog({ onClose }: { onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [kind, setKind] = useState<DeviceKind>('Workstation');
  const [note, setNote] = useState('');
  const [validMinutes, setValidMinutes] = useState(30);
  const create = useMutation({
    mutationFn: () =>
      api.post<{ code: string; expiresAt: string }>('/api/v1/admin/devices/enrollment-codes', {
        kind,
        facilityId: null,
        note: note || null,
        validMinutes,
      }),
    onError: (error) => notify.error(error, 'Could not create an enrolment code'),
  });

  const close = () => {
    void queryClient.invalidateQueries({ queryKey: ['admin', 'devices'] });
    onClose();
  };

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && close()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Enrol a device</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {create.data ? (
              <>
                <Text>
                  {kind === 'LabAgent'
                    ? 'On the PC the analyser is cabled to, run Install-HC360LabAgent.ps1 as an administrator with this one-time code (-EnrolmentCode), the server address and the hospital CA fingerprint:'
                    : 'On the new device, open HC360 Desktop and enter this one-time code:'}
                </Text>
                <div className={styles.code}>{create.data.code}</div>
                <MessageBar intent="info">
                  <MessageBarBody>
                    The code works once and expires at {formatDateTime(create.data.expiresAt)}.
                    {kind === 'LabAgent' &&
                      ' Then set up each analyser on that PC in Lab setup › Analysers › Connection.'}
                  </MessageBarBody>
                </MessageBar>
              </>
            ) : (
              <>
                <Field
                  label="Device type"
                  hint={
                    kind === 'LabAgent'
                      ? 'A Windows service on the PC a serial analyser is cabled to. It relays the analyser to HC360, even when nobody is signed in.'
                      : undefined
                  }
                >
                  <Select value={kind} onChange={(_, d) => setKind(d.value as DeviceKind)}>
                    {KINDS.map((k) => (
                      <option key={k} value={k}>
                        {kindLabel(k)}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Note" hint="For example: OPD reception counter 2">
                  <Input value={note} onChange={(_, d) => setNote(d.value)} maxLength={200} />
                </Field>
                <Field label="Code valid for">
                  <Select value={String(validMinutes)} onChange={(_, d) => setValidMinutes(Number(d.value))}>
                    <option value="15">15 minutes</option>
                    <option value="30">30 minutes</option>
                    <option value="60">1 hour</option>
                    <option value="240">4 hours</option>
                  </Select>
                </Field>
              </>
            )}
          </DialogContent>
          <DialogActions>
            {create.data ? (
              <Button appearance="primary" onClick={close}>
                Done
              </Button>
            ) : (
              <>
                <Button onClick={close}>Cancel</Button>
                <Button appearance="primary" disabled={create.isPending} onClick={() => create.mutate()}>
                  Create code
                </Button>
              </>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

function EditDeviceDialog({ device, onClose }: { device: DeviceDto; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const [name, setName] = useState(device.name);
  const [location, setLocation] = useState(device.location ?? '');
  const [departmentId, setDepartmentId] = useState(device.departmentId ?? '');
  const save = useMutation({
    mutationFn: () =>
      api.put(`/api/v1/admin/devices/${device.id}`, {
        name: name.trim(),
        departmentId: departmentId || null,
        location: location.trim() || null,
        version: device.version,
      }),
    onSuccess: () => {
      notify.success('Device updated');
      void queryClient.invalidateQueries({ queryKey: ['admin', 'devices'] });
      onClose();
    },
    onError: (error) => notify.error(error, 'Could not update the device'),
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ name, location, departmentId }),
    busy: save.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{device.name}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label="Name" required>
              <Input value={name} onChange={(_, d) => setName(d.value)} maxLength={100} />
            </Field>
            <Field label="Department">
              <Select value={departmentId} onChange={(_, d) => setDepartmentId(d.value)}>
                <option value="">None</option>
                {toOptions(departments.data).map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </Field>
            <Field label="Location" hint="Where to find it, e.g. Ground floor, OPD counter 2">
              <Input value={location} onChange={(_, d) => setLocation(d.value)} maxLength={200} />
            </Field>
          </DialogContent>
          <DialogActions>
            <Button onClick={guard.requestClose}>Cancel</Button>
            <Button appearance="primary" disabled={!name.trim() || save.isPending} onClick={() => save.mutate()}>
              Save
            </Button>
          </DialogActions>
        </DialogBody>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
