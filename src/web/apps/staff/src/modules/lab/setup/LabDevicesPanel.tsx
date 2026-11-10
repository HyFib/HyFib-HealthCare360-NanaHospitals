import {
  Button,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { labDevicesApi, type LabDeviceAdded, type LabDeviceDto } from '../api';

const useStyles = makeStyles({
  list: { margin: 0, paddingLeft: '20px', display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  row: { display: 'flex', flexWrap: 'wrap', gap: tokens.spacingHorizontalS, alignItems: 'center' },
  secret: { fontFamily: tokens.fontFamilyMonospace, wordBreak: 'break-all' },
});

const STATUS_WORDS: Record<LabDeviceDto['status'], string> = {
  Seen: 'Refused: not known here',
  PendingApproval: 'Waiting for someone else to approve it',
  Approved: 'Approved',
  Revoked: 'Revoked',
};

/** A device in a line: what it is, how it proves itself, and where it was last seen. */
export function describeDevice(device: LabDeviceDto): string {
  const how =
    device.method === 'MutualTls'
      ? `certificate ${device.subject ?? ''} (SHA-256 ${device.identifier.slice(0, 16)}…)`
      : device.method === 'LabAgent'
        ? 'lab agent'
        : `key id ${device.identifier}`;
  const seen = device.lastSeenAt
    ? `, last seen ${formatDateTime(device.lastSeenAt)} from ${device.lastAddress ?? 'an unknown address'}`
    : '';
  const refused =
    device.refusedAttempts > 0
      ? `, refused ${device.refusedAttempts} time${device.refusedAttempts === 1 ? '' : 's'}`
      : '';
  return `${device.name ?? 'Unnamed device'}: ${how}${seen}${refused}`;
}

/**
 * The devices of one analyser connection: those refused at the door (to add), those waiting for approval (someone other
 * than who added them approves them in Approvals), and those approved (which can be revoked at once).
 */
export function LabDevicesPanel({ connectionCode }: { connectionCode: string }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [added, setAdded] = useState<LabDeviceAdded | null>(null);
  const key = ['lab', 'devices', connectionCode];
  const devices = useQuery({ queryKey: key, queryFn: ({ signal }) => labDevicesApi.list(connectionCode, signal) });
  const refresh = () => queryClient.invalidateQueries({ queryKey: key });
  const add = useMutation({
    mutationFn: (device: LabDeviceDto) =>
      labDevicesApi.add({
        connectionCode,
        name: name.trim() || device.identifier,
        method: device.method,
        fingerprint: null,
        seenDeviceId: device.id,
        reason: `Recognised as ${name.trim() || 'this analyser'} by the lab.`,
      }),
    onSuccess: (result) => {
      setAdded(result);
      notify.success('Device added', 'Someone else approves it in Approvals before results are accepted from it.');
      void refresh();
    },
    onError: (error) => notify.error(error, 'The device could not be added'),
  });
  const revoke = useMutation({
    mutationFn: (device: LabDeviceDto) => labDevicesApi.revoke(device.id, device.version, 'Revoked in Lab setup.'),
    onSuccess: () => void refresh(),
    onError: (error) => notify.error(error, 'The device could not be revoked'),
  });

  const list = devices.data ?? [];
  return (
    <section aria-label="Devices of this connection">
      {added?.secret && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>Enter this on the device now: it is not shown again</MessageBarTitle>
            Key id <span className={styles.secret}>{added.keyId}</span>, secret{' '}
            <span className={styles.secret}>{added.secret}</span>
          </MessageBarBody>
        </MessageBar>
      )}
      {list.length === 0 ? (
        <Text>No device has tried to connect yet.</Text>
      ) : (
        <ul className={styles.list}>
          {list.map((device) => (
            <li key={device.id}>
              <div className={styles.row}>
                <Text>
                  {describeDevice(device)}. {STATUS_WORDS[device.status]}
                  {device.addedByName ? `, added by ${device.addedByName}` : ''}
                  {device.approvedByName ? `, approved by ${device.approvedByName}` : ''}.
                </Text>
                {device.status === 'Seen' && (
                  <Button size="small" disabled={add.isPending} onClick={() => add.mutate(device)}>
                    Add this device
                  </Button>
                )}
                {device.status === 'Approved' && (
                  <Button size="small" disabled={revoke.isPending} onClick={() => revoke.mutate(device)}>
                    Revoke
                  </Button>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
      {list.some((d) => d.status === 'Seen') && (
        <Field label="Name for the device you add" hint="Such as the analyser's model and serial number.">
          <Input value={name} maxLength={100} onChange={(_, data) => setName(data.value)} />
        </Field>
      )}
    </section>
  );
}
