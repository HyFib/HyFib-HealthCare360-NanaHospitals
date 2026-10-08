import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { KeyResetRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { ConfirmDialog, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { adminQueries, rotateDataKey, rotateSigningKey, type KeyRotationEvent } from './api';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '16px' },
  key: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: '4px' },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  muted: { color: tokens.colorNeutralForeground3 },
});

type Rotating = 'data' | 'signing' | null;

function rotationText(last: KeyRotationEvent | null, since: string | null): string {
  if (last) {
    return `Rotated ${formatDateTime(last.at)}${last.by ? ` by ${last.by}` : ''}`;
  }
  return since ? `In use since ${formatDateTime(since)}` : 'Not rotated since installation';
}

/**
 * The encryption keys of System health (SRS SEC-07): the data encryption key (identifiers, secrets, documents) and the
 * key that signs sign-in tokens, with when each was last rotated and, for holders of `admin.system.manage`, a button to
 * rotate it. After a data key rotation, what was encrypted before moves to the new key in the background, and the count
 * left goes down here.
 */
export function EncryptionKeys() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('admin.system.manage');
  const status = useQuery(adminQueries.encryptionKeys());
  const [rotating, setRotating] = useState<Rotating>(null);

  const rotate = useMutation({
    mutationFn: (which: Exclude<Rotating, null>) => (which === 'data' ? rotateDataKey() : rotateSigningKey()),
    onSuccess: (result, which) => {
      setRotating(null);
      queryClient.setQueryData(adminQueries.encryptionKeys().queryKey, result);
      notify.success(
        which === 'data' ? 'Data encryption key rotated' : 'Sign-in token key rotated',
        which === 'data'
          ? 'New data is encrypted with the new key. Data encrypted before moves to it in the background.'
          : 'New sign-in tokens are signed with the new key. Nobody is signed out.',
      );
    },
    onError: (error) => {
      setRotating(null);
      notify.error(error, 'The key was not rotated');
    },
  });

  if (!status.data) {
    return null;
  }

  const { dataKey, signingKey } = status.data;
  const left = dataKey.valuesToMove + dataKey.documentsToMove;
  return (
    <div className={styles.root}>
      <div className={styles.key}>
        <Text weight="semibold">Data encryption key</Text>
        <Text>
          <span className={styles.mono}>{dataKey.keyId}</span> · {rotationText(dataKey.lastRotation, dataKey.since)}
        </Text>
        <Caption1 className={styles.muted}>
          Encrypts ABHA and policy numbers, staff bank details, two-step verification secrets, integration secrets and
          stored documents.{' '}
          {dataKey.keys > 1 ? `${dataKey.keys} keys kept, so older data and backups stay readable.` : ''}
        </Caption1>
        {left > 0 && (
          <Text>
            Moving to the new key: {dataKey.valuesToMove.toLocaleString('en-IN')} value(s) and{' '}
            {dataKey.documentsToMove.toLocaleString('en-IN')} document(s) left.
          </Text>
        )}
        {canManage && (
          <Button icon={<KeyResetRegular />} onClick={() => setRotating('data')} disabled={rotate.isPending}>
            Rotate data key
          </Button>
        )}
      </div>
      <div className={styles.key}>
        <Text weight="semibold">Sign-in token key</Text>
        <Text>
          <span className={styles.mono}>{signingKey.keyId}</span> ·{' '}
          {rotationText(signingKey.lastRotation, signingKey.since)}
        </Text>
        {canManage && (
          <Button icon={<KeyResetRegular />} onClick={() => setRotating('signing')} disabled={rotate.isPending}>
            Rotate sign-in key
          </Button>
        )}
      </div>
      <ConfirmDialog
        open={rotating === 'data'}
        title="Rotate the data encryption key?"
        confirmLabel="Rotate data key"
        busy={rotate.isPending}
        onConfirm={() => rotate.mutate('data')}
        onCancel={() => setRotating(null)}
      >
        <Text block>
          HC360 makes a new key and encrypts new data with it at once. Data encrypted before moves to the new key in the
          background, a little at a time; screens keep working meanwhile.
        </Text>
        <Text block className={styles.muted}>
          Earlier keys are kept, so older backups and anything not yet moved stay readable. The backup recovery key does
          not change. Rotate every two years, or at once if the server or a backup may have been exposed.
        </Text>
      </ConfirmDialog>
      <ConfirmDialog
        open={rotating === 'signing'}
        title="Rotate the sign-in token key?"
        confirmLabel="Rotate sign-in key"
        busy={rotate.isPending}
        onConfirm={() => rotate.mutate('signing')}
        onCancel={() => setRotating(null)}
      >
        <Text block>
          HC360 signs new sign-in tokens with a new key. Tokens already issued stay valid until they expire, within
          minutes, so nobody is signed out.
        </Text>
        <Text block className={styles.muted}>
          Rotate every year, or at once if the server may have been exposed.
        </Text>
      </ConfirmDialog>
    </div>
  );
}
