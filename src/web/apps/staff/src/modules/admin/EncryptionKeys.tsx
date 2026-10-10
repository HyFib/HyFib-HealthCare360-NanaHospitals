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
  Text,
  tokens,
} from '@fluentui/react-components';
import { DeleteRegular, KeyResetRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { ConfirmDialog, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useState, type FormEvent } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  adminQueries,
  retireDataKey,
  rotateDataKey,
  rotateSigningKey,
  type EarlierDataKey,
  type KeyRotationEvent,
} from './api';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '16px' },
  key: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: '4px' },
  earlier: { display: 'flex', flexDirection: 'column', gap: '8px', alignSelf: 'stretch' },
  earlierKey: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', columnGap: '12px', rowGap: '4px' },
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  muted: { color: tokens.colorNeutralForeground3 },
});

type Rotating = 'data' | 'signing' | null;

/** What is still on an earlier key, in words: "1,240 value(s) and 56 document(s)". */
function leftText(values: number, documents: number, expiring = 0): string {
  const parts = [
    values > 0 ? `${values.toLocaleString('en-IN')} value(s)` : null,
    documents > 0 ? `${documents.toLocaleString('en-IN')} document(s)` : null,
    expiring > 0 ? `${expiring.toLocaleString('en-IN')} stored answer(s) to repeated requests` : null,
  ].filter((part): part is string => part !== null);
  return parts.length > 1 ? `${parts.slice(0, -1).join(', ')} and ${parts[parts.length - 1]}` : (parts[0] ?? '');
}

function rotationText(last: KeyRotationEvent | null, since: string | null): string {
  if (last) {
    return `Rotated ${formatDateTime(last.at)}${last.by ? ` by ${last.by}` : ''}`;
  }
  return since ? `In use since ${formatDateTime(since)}` : 'Not rotated since installation';
}

/** Where an earlier key stands: what it still encrypts, or when it can be retired. */
function earlierText(key: EarlierDataKey): string {
  const left = leftText(key.values, key.documents, key.expiring);
  if (left) {
    return key.expiring > 0 && key.values + key.documents === 0
      ? `Still encrypts ${left}, which expire within a day.`
      : `Still encrypts ${left}, moving to the current key.`;
  }
  return key.retireFrom
    ? `Nothing is encrypted with it any more. It can be retired from ${formatDateTime(key.retireFrom)}.`
    : 'Nothing is encrypted with it any more.';
}

/** The server's message for one field of a failed request, if it sent one. */
function fieldError(error: unknown, field: string): string | undefined {
  return isApiError(error) ? error.fieldErrors[field]?.[0] : undefined;
}

/**
 * Asks for the password again, then retires an earlier data key: the server deletes it, once nothing is encrypted with
 * it any more (ASVS V11.1.1).
 */
function RetireKeyDialog({ keyId, onClose }: { keyId: string | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [password, setPassword] = useState('');
  const [missing, setMissing] = useState(false);

  // Start empty each time it opens.
  const [shownFor, setShownFor] = useState(keyId);
  if (shownFor !== keyId) {
    setShownFor(keyId);
    setPassword('');
    setMissing(false);
  }

  const retire = useMutation({
    mutationFn: ({ id, value }: { id: string; value: string }) => retireDataKey(id, value),
    onSuccess: (result, { id }) => {
      queryClient.setQueryData(adminQueries.encryptionKeys().queryKey, result);
      notify.success(
        'Data key retired',
        `Key ${id} was deleted from the server. Backups made before keep their own copy of it, so they still restore.`,
      );
      close();
    },
    onError: (error) => {
      if (fieldError(error, 'password')) {
        return;
      }
      // Something is encrypted with it again, or the key changed a moment ago: nothing was deleted.
      notify.error(error, 'The key was not retired');
      void queryClient.invalidateQueries({ queryKey: adminQueries.encryptionKeys().queryKey });
      close();
    },
  });

  function close() {
    retire.reset();
    onClose();
  }

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!password) {
      setMissing(true);
    } else if (keyId && !retire.isPending) {
      retire.mutate({ id: keyId, value: password });
    }
  }

  const message = missing ? 'Enter your password.' : fieldError(retire.error, 'password');
  return (
    <Dialog
      open={keyId !== null}
      modalType="alert"
      onOpenChange={(_, data) => !data.open && !retire.isPending && close()}
    >
      <DialogSurface>
        <form onSubmit={submit} noValidate>
          <DialogBody>
            <DialogTitle>Retire key {keyId}?</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>
                HC360 deletes this key from the server. Nothing is encrypted with it any more, so nothing stops being
                readable, and it is never used again.
              </Text>
              <Text className={styles.muted}>
                Backups made before keep their own copy of the key, so they still restore, and restoring one puts it
                back.
              </Text>
              <Field
                label="Your password"
                required
                validationMessage={message}
                validationState={message ? 'error' : 'none'}
              >
                <Input
                  type="password"
                  value={password}
                  onChange={(_, data) => {
                    setPassword(data.value);
                    setMissing(false);
                  }}
                  autoComplete="current-password"
                />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button appearance="primary" type="submit" disabled={retire.isPending}>
                Retire key
              </Button>
              <Button onClick={close} disabled={retire.isPending}>
                Cancel
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/**
 * The encryption keys of System health (SRS SEC-07): the data encryption key (identifiers, secrets, documents) and the
 * key that signs sign-in tokens, with when each was last rotated and, for holders of `admin.system.manage`, a button to
 * rotate it. After a data key rotation, what was encrypted before moves to the new key in the background, and the count
 * left goes down here; an earlier key that nothing is encrypted with any more can then be retired.
 */
export function EncryptionKeys() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('admin.system.manage');
  const status = useQuery(adminQueries.encryptionKeys());
  const [rotating, setRotating] = useState<Rotating>(null);
  const [retiring, setRetiring] = useState<string | null>(null);
  const earlierId = useId();

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
          {dataKey.keys > 1 ? `${dataKey.keys} keys kept, so data encrypted before stays readable.` : ''}
        </Caption1>
        {left > 0 && (
          <Text>Moving to the new key: {leftText(dataKey.valuesToMove, dataKey.documentsToMove)} left.</Text>
        )}
        {canManage && (
          <Button icon={<KeyResetRegular />} onClick={() => setRotating('data')} disabled={rotate.isPending}>
            Rotate data key
          </Button>
        )}
        {dataKey.earlier.length > 0 && (
          <div className={styles.earlier}>
            <Text weight="semibold" id={earlierId}>
              Earlier keys
            </Text>
            <div className={styles.earlier} role="list" aria-labelledby={earlierId}>
              {dataKey.earlier.map((key) => (
                <div key={key.keyId} className={styles.earlierKey} role="listitem">
                  <span className={styles.mono}>{key.keyId}</span>
                  <Text>{earlierText(key)}</Text>
                  {canManage && key.canRetire && (
                    <Button
                      size="small"
                      icon={<DeleteRegular />}
                      onClick={() => setRetiring(key.keyId)}
                      aria-label={`Retire key ${key.keyId}`}
                    >
                      Retire
                    </Button>
                  )}
                </div>
              ))}
            </div>
            <Caption1 className={styles.muted}>
              The original key is always kept: searching by ABHA or Aadhaar number depends on it.
            </Caption1>
          </div>
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
          Earlier keys are kept until nothing is encrypted with them, and can then be retired. The backup recovery key
          does not change. Rotate every two years, or at once if the server or a backup may have been exposed.
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
      <RetireKeyDialog keyId={retiring} onClose={() => setRetiring(null)} />
    </div>
  );
}
