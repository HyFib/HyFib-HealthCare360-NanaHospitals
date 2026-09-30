import {
  Badge,
  Button,
  Caption1,
  Field,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  SearchBox,
  Select,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  KeyResetRegular,
  LinkRegular,
  MoreHorizontalRegular,
  PlugDisconnectedRegular,
} from '@fluentui/react-icons';
import { formatDate, formatRelative, useDebouncedValue } from '@hc360/core';
import { ConfirmDialog, DataTable, FilterBar, Page, PageHeader, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useMemo, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { rpmApi, rpmKeys, rpmQueries, type DeviceKeyIssued, type RpmDeviceDto } from '@/modules/virtualcare/rpm/api';
import { AssignDeviceDialog, DeviceKeyDialog, RegisterDeviceDialog } from '@/modules/virtualcare/rpm/DeviceDialogs';
import { deviceTypeLabel } from '@/modules/virtualcare/rpm/labels';

interface DevicesSearch {
  show?: 'all';
  unassigned?: boolean;
}

export const Route = createFileRoute('/_app/rpm/devices')({
  validateSearch: (s: Record<string, unknown>): DevicesSearch => ({
    show: s.show === 'all' ? 'all' : undefined,
    unassigned: s.unassigned === true || s.unassigned === 'true' ? true : undefined,
  }),
  component: DevicesPage,
});

const useStyles = makeStyles({
  toolbar: { padding: '0 16px 12px' },
  stack: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  actions: { display: 'flex', gap: '4px', justifyContent: 'flex-end' },
});

type Pending = { device: RpmDeviceDto; action: 'rotate' | 'deactivate' };

/** Monitoring devices (RPM-02): register with a one-time key, give to patients, rotate keys and retire devices. */
function DevicesPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate({ from: Route.fullPath });
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('rpm.manage');
  const [term, setTerm] = useState('');
  const filter = useDebouncedValue(term.trim().toLowerCase(), 200);
  const [registering, setRegistering] = useState(false);
  const [issued, setIssued] = useState<DeviceKeyIssued | null>(null);
  const [assigning, setAssigning] = useState<RpmDeviceDto | null>(null);
  const [pending, setPending] = useState<Pending | null>(null);

  const devices = useQuery(
    rpmQueries.devices({ activeOnly: search.show === 'all' ? false : undefined, unassigned: search.unassigned }),
  );
  // Names for assigned devices (the device list carries only ids).
  const enrollments = useQuery({ ...rpmQueries.enrollments({ pageSize: 200 }), refetchInterval: false });
  const byEnrollment = useMemo(
    () => new Map((enrollments.data?.items ?? []).map((e) => [e.id, e])),
    [enrollments.data],
  );
  const rows = useMemo(
    () =>
      (devices.data ?? []).filter(
        (d) =>
          !filter ||
          d.serialNumber.toLowerCase().includes(filter) ||
          (d.enrollmentId && byEnrollment.get(d.enrollmentId)?.patientName.toLowerCase().includes(filter)),
      ),
    [devices.data, filter, byEnrollment],
  );

  const act = useMutation({
    mutationFn: async ({ device, action }: Pending): Promise<DeviceKeyIssued | null> => {
      if (action === 'rotate') {
        return rpmApi.rotateKey(device.id);
      }
      await rpmApi.deactivateDevice(device.id);
      return null;
    },
    onSuccess: (key, { action }) => {
      setPending(null);
      void queryClient.invalidateQueries({ queryKey: rpmKeys.all });
      if (key) {
        setIssued(key);
      } else {
        notify.success(
          action === 'deactivate' ? 'Device retired' : 'Done',
          action === 'deactivate' ? 'Its key no longer works.' : undefined,
        );
      }
    },
    onError: (error) => notify.error(error, 'The action failed'),
  });

  const set = (patch: Partial<DevicesSearch>) =>
    void navigate({ search: (prev) => ({ ...prev, ...patch }), replace: true });

  return (
    <Page>
      <PageHeader
        title="Monitoring devices"
        subtitle="Blood pressure monitors, glucometers, oximeters and other devices that send readings from home."
        breadcrumbs={[
          { label: 'Remote monitoring', onClick: () => void navigate({ to: '/rpm' }) },
          { label: 'Devices' },
        ]}
        actions={
          canManage && (
            <Button appearance="primary" icon={<AddRegular />} onClick={() => setRegistering(true)}>
              Register device
            </Button>
          )
        }
      />
      <Section flush>
        <div className={styles.toolbar}>
          <FilterBar>
            <Field label="Search" style={{ flex: '1 1 240px' }}>
              <SearchBox value={term} onChange={(_, d) => setTerm(d.value)} placeholder="Serial number or patient" />
            </Field>
            <Field label="Show">
              <Select
                value={search.show ?? 'active'}
                onChange={(_, d) => set({ show: d.value === 'all' ? 'all' : undefined })}
              >
                <option value="active">Devices in use</option>
                <option value="all">Including retired</option>
              </Select>
            </Field>
            <Switch
              checked={Boolean(search.unassigned)}
              onChange={(_, d) => set({ unassigned: d.checked || undefined })}
              label="Only devices not given to a patient"
            />
          </FilterBar>
        </div>
        <DataTable
          ariaLabel="Monitoring devices"
          rows={rows}
          loading={devices.isLoading}
          error={devices.error}
          onRetry={() => void devices.refetch()}
          getRowId={(d) => d.id}
          columns={[
            {
              key: 'serial',
              header: 'Device',
              render: (d) => (
                <span className={styles.stack}>
                  <Text weight="semibold" className={styles.mono}>
                    {d.serialNumber}
                  </Text>
                  <Caption1 className={styles.muted}>
                    {[deviceTypeLabel[d.type], d.manufacturer, d.model].filter(Boolean).join(' · ')}
                  </Caption1>
                </span>
              ),
            },
            {
              key: 'patient',
              header: 'Patient',
              render: (d) => {
                if (!d.enrollmentId) {
                  return <Text className={styles.muted}>Not given to a patient</Text>;
                }
                const enrollment = byEnrollment.get(d.enrollmentId);
                return (
                  <Button
                    appearance="transparent"
                    size="small"
                    style={{ padding: 0, minWidth: 0 }}
                    onClick={() =>
                      void navigate({ to: '/rpm/enrollments/$enrollmentId', params: { enrollmentId: d.enrollmentId! } })
                    }
                  >
                    {enrollment ? `${enrollment.patientName} (${enrollment.uhid})` : 'Open monitored patient'}
                  </Button>
                );
              },
            },
            {
              key: 'seen',
              header: 'Last reading received',
              width: 190,
              render: (d) => (
                <span className={styles.stack}>
                  <Text>{d.lastSeenAt ? formatRelative(d.lastSeenAt) : 'Never'}</Text>
                  {d.silenceAlerted && (
                    <Badge color="warning" appearance="tint" size="small" icon={<PlugDisconnectedRegular />}>
                      Silent
                    </Badge>
                  )}
                </span>
              ),
            },
            {
              key: 'key',
              header: 'Key issued',
              priority: 'low',
              width: 130,
              render: (d) => (d.apiKeyIssuedAt ? formatDate(d.apiKeyIssuedAt) : '—'),
            },
            {
              key: 'status',
              header: 'Status',
              width: 100,
              render: (d) => (
                <StatusBadge
                  status={d.isActive ? 'Active' : 'Retired'}
                  tone={d.isActive ? 'success' : 'informative'}
                  label={d.isActive ? 'In use' : 'Retired'}
                />
              ),
            },
          ]}
          rowActions={
            canManage
              ? (d) =>
                  d.isActive ? (
                    <div className={styles.actions}>
                      <Button size="small" icon={<LinkRegular />} onClick={() => setAssigning(d)}>
                        {d.enrollmentId ? 'Move' : 'Assign'}
                      </Button>
                      <Menu>
                        <MenuTrigger disableButtonEnhancement>
                          <Button
                            size="small"
                            appearance="subtle"
                            icon={<MoreHorizontalRegular />}
                            aria-label={`More actions for ${d.serialNumber}`}
                          />
                        </MenuTrigger>
                        <MenuPopover>
                          <MenuList>
                            <MenuItem
                              icon={<KeyResetRegular />}
                              onClick={() => setPending({ device: d, action: 'rotate' })}
                            >
                              Issue a new key
                            </MenuItem>
                            <MenuItem
                              icon={<PlugDisconnectedRegular />}
                              onClick={() => setPending({ device: d, action: 'deactivate' })}
                            >
                              Retire device
                            </MenuItem>
                          </MenuList>
                        </MenuPopover>
                      </Menu>
                    </div>
                  ) : null
              : undefined
          }
          emptyTitle={filter ? 'No devices match' : 'No devices registered'}
          emptyDescription={canManage ? 'Register a device, then give it to a monitored patient.' : undefined}
          size="small"
        />
      </Section>

      <RegisterDeviceDialog
        open={registering}
        onClose={() => setRegistering(false)}
        onRegistered={(key) => {
          setRegistering(false);
          setIssued(key);
        }}
      />
      <DeviceKeyDialog issued={issued} onClose={() => setIssued(null)} />
      <AssignDeviceDialog device={assigning} onClose={() => setAssigning(null)} />
      <ConfirmDialog
        open={pending !== null}
        title={pending?.action === 'rotate' ? 'Issue a new key?' : 'Retire this device?'}
        confirmLabel={pending?.action === 'rotate' ? 'Issue new key' : 'Retire device'}
        destructive={pending?.action === 'deactivate'}
        busy={act.isPending}
        onConfirm={() => pending && act.mutate(pending)}
        onCancel={() => setPending(null)}
      >
        {pending?.action === 'rotate'
          ? `The current key of ${pending.device.serialNumber} stops working at once. Enter the new key in the device straight after.`
          : pending
            ? `${pending.device.serialNumber} is taken back from the patient and its key stops working. A retired device cannot be put back into use.`
            : null}
      </ConfirmDialog>
    </Page>
  );
}
