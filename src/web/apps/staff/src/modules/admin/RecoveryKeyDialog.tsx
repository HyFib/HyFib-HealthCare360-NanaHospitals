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
  Text,
  tokens,
} from '@fluentui/react-components';
import { formatDateTime, isApiError } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  adminQueries,
  confirmRecoveryKey,
  createRecoveryKey,
  custodyAdvice,
  proveRecoveryKey,
  type NewRecoveryKey,
  type RecoveryKeyStatus,
} from './api';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  key: {
    display: 'flex',
    flexWrap: 'wrap',
    columnGap: '14px',
    rowGap: '6px',
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase500,
    padding: '12px 16px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  muted: { color: tokens.colorNeutralForeground3 },
  list: { margin: '4px 0 0', paddingLeft: '20px' },
});

type Step = 'status' | 'password' | 'show' | 'confirm' | 'prove';

/** The server's message for one field of a failed request, if it sent one. */
function fieldError(error: unknown, field: string): string | undefined {
  return isApiError(error) ? error.fieldErrors[field]?.[0] : undefined;
}

function CurrentKey({ status }: { status: RecoveryKeyStatus }) {
  const styles = useStyles();
  if (status.original && !status.originalShown) {
    return (
      <MessageBar intent="error" layout="multiline">
        <MessageBarBody>
          Nobody holds the recovery key for these backups, so they cannot be restored on new hardware. Make a recovery
          key now and keep it offline.
        </MessageBarBody>
      </MessageBar>
    );
  }
  if (status.original) {
    return (
      <MessageBar intent="warning" layout="multiline">
        <MessageBarBody>
          Backups still use the original recovery key, which any administrator with the backups permission could display
          until now. Make a new one, and keep the original key until the backups made with it are deleted.
        </MessageBarBody>
      </MessageBar>
    );
  }
  return (
    <Text>
      Backups use key <span className={styles.mono}>{status.keyId}</span>
      {status.since ? `, in use since ${formatDateTime(status.since)}` : ''}
      {status.by ? ` (made by ${status.by})` : ''}.
    </Text>
  );
}

/**
 * Who has shown they hold the current key, by typing it in, and what is missing: two people, one copy outside the
 * building, and someone typing it in every quarter (System health warns of the same).
 */
function Custodians({ status }: { status: RecoveryKeyStatus }) {
  const styles = useStyles();
  const custodians = status.custodians ?? [];
  const advice = custodyAdvice(status);
  return (
    <>
      {custodians.length > 0 && (
        <div>
          <Text weight="semibold">Shown to be held by</Text>
          <ul className={styles.list} aria-label="Key holders">
            {custodians.map((custodian) => (
              <li key={custodian.userId}>
                {custodian.name}
                <Caption1 className={styles.muted}> (last typed in {formatDateTime(custodian.lastProvenAt)})</Caption1>
              </li>
            ))}
          </ul>
        </div>
      )}
      {advice && (
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>{advice}</MessageBarBody>
        </MessageBar>
      )}
    </>
  );
}

/**
 * The backup recovery key (ASVS V11.1.1, V13.3.2): which key new backups use and who has shown they hold it, a way for
 * any administrator who keeps a copy to show they do by typing it in, and, for holders of `admin.system.manage`, a new
 * key. The new key is shown once, after the password is given again, and backups use it only once it is typed back from
 * the copy written down, so a key nobody recorded never becomes the one the backups need. No key is ever shown again.
 */
export function RecoveryKeyDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('admin.system.manage');
  const status = useQuery({ ...adminQueries.recoveryKey(), enabled: open });
  const [step, setStep] = useState<Step>('status');
  const [password, setPassword] = useState('');
  const [typed, setTyped] = useState('');
  const [held, setHeld] = useState('');
  const [missing, setMissing] = useState(false);
  const [created, setCreated] = useState<NewRecoveryKey | null>(null);

  // Start from the beginning each time it opens; the new key, and any key typed, are forgotten once it closes.
  const [wasOpen, setWasOpen] = useState(open);
  if (wasOpen !== open) {
    setWasOpen(open);
    setStep('status');
    setPassword('');
    setTyped('');
    setHeld('');
    setMissing(false);
    setCreated(null);
  }

  const create = useMutation({
    mutationFn: (value: string) => createRecoveryKey(value),
    onSuccess: (result) => {
      setPassword('');
      setCreated(result);
      setStep('show');
    },
    onError: (error) => {
      if (!fieldError(error, 'password')) {
        notify.error(error, 'Could not make a new recovery key');
      }
    },
  });

  const confirm = useMutation({
    mutationFn: ({ keyId, key }: { keyId: string; key: string }) => confirmRecoveryKey(keyId, key),
    onSuccess: (result) => {
      queryClient.setQueryData(adminQueries.recoveryKey().queryKey, result);
      notify.success(
        'New recovery key in use',
        `Backups from now on open with key ${result.keyId}. Keep the previous key as long as the backups made with it are kept.`,
      );
      close();
    },
    onError: (error) => {
      if (fieldError(error, 'key')) {
        return;
      }
      // Expired or replaced: nothing changed, and a new key has to be made.
      notify.error(error, 'The new recovery key was not put to use');
      setCreated(null);
      setTyped('');
      setStep('status');
    },
  });

  const prove = useMutation({
    mutationFn: (key: string) => proveRecoveryKey(key),
    onSuccess: (result) => {
      queryClient.setQueryData(adminQueries.recoveryKey().queryKey, result);
      notify.success(
        'Recorded: you hold the recovery key',
        `You typed in key ${result.keyId} from your copy. Keep it where you can find it, and type it in again at the next quarterly check.`,
      );
      setHeld('');
      goTo('status');
    },
    onError: (error) => {
      if (!fieldError(error, 'key')) {
        notify.error(error, 'Could not record that you hold the key');
      }
    },
  });

  function close() {
    create.reset();
    confirm.reset();
    prove.reset();
    onClose();
  }

  function goTo(next: Step) {
    create.reset();
    confirm.reset();
    prove.reset();
    setMissing(false);
    setStep(next);
  }

  function submitHeld(event: FormEvent) {
    event.preventDefault();
    if (!held.trim()) {
      setMissing(true);
    } else if (!prove.isPending) {
      prove.mutate(held);
    }
  }

  function submitPassword(event: FormEvent) {
    event.preventDefault();
    if (!password) {
      setMissing(true);
    } else if (!create.isPending) {
      create.mutate(password);
    }
  }

  function submitKey(event: FormEvent) {
    event.preventDefault();
    if (!typed.trim()) {
      setMissing(true);
    } else if (created && !confirm.isPending) {
      confirm.mutate({ keyId: created.keyId, key: typed });
    }
  }

  const passwordMessage = missing ? 'Enter your password.' : fieldError(create.error, 'password');
  const keyMessage = missing ? 'Type the key from your copy.' : fieldError(confirm.error, 'key');
  const heldMessage = missing ? 'Type the key from your copy.' : fieldError(prove.error, 'key');
  const current = status.data && !status.data.original ? status.data : null;

  return (
    <Dialog
      open={open}
      // Once a new key is on screen, only the buttons close the dialog, not a stray click beside it.
      modalType={step === 'status' ? 'modal' : 'alert'}
      onOpenChange={(_, data) => !data.open && close()}
    >
      <DialogSurface>
        {step === 'status' && (
          <DialogBody>
            <DialogTitle>Backup recovery key</DialogTitle>
            <DialogContent className={styles.body}>
              {status.data && <CurrentKey status={status.data} />}
              {current && <Custodians status={current} />}
              <Text>
                The recovery key opens the backups, on this server or on replacement hardware. Each backup opens with
                the key that was in use when it was made; the list of backups names it. Two administrators keep a copy
                each, one of them outside the building, and type it in every quarter to show they still can.
              </Text>
              {!canManage && (
                <Caption1 className={styles.muted}>
                  Only holders of the system management permission make new keys (SuperAdmin by default).
                </Caption1>
              )}
            </DialogContent>
            <DialogActions>
              {current && (
                <Button appearance={canManage ? 'secondary' : 'primary'} onClick={() => goTo('prove')}>
                  I hold this key
                </Button>
              )}
              {canManage && (
                <Button appearance="primary" onClick={() => goTo('password')}>
                  Make a new key
                </Button>
              )}
              <Button onClick={close}>Close</Button>
            </DialogActions>
          </DialogBody>
        )}

        {step === 'password' && (
          <form onSubmit={submitPassword} noValidate>
            <DialogBody>
              <DialogTitle>Make a new recovery key</DialogTitle>
              <DialogContent className={styles.body}>
                <Text>
                  HC360 shows the new key once, to you. Write it down, then type it back from your copy. Until you do,
                  backups go on using the current key.
                </Text>
                <Field
                  label="Your password"
                  required
                  validationMessage={passwordMessage}
                  validationState={passwordMessage ? 'error' : 'none'}
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
                <Button appearance="primary" type="submit" disabled={create.isPending}>
                  Show the new key
                </Button>
                <Button onClick={() => goTo('status')}>Back</Button>
              </DialogActions>
            </DialogBody>
          </form>
        )}

        {step === 'show' && created && (
          <DialogBody>
            <DialogTitle>Write down the new recovery key</DialogTitle>
            <DialogContent className={styles.body}>
              <div className={styles.key} role="group" aria-label="New recovery key">
                {created.key.split('-').map((group, index) => (
                  <span key={index}>{group}</span>
                ))}
              </div>
              <Text>
                Key ID <span className={styles.mono}>{created.keyId}</span>
              </Text>
              <MessageBar intent="warning" layout="multiline">
                <MessageBarBody>
                  Write the key and its ID on paper and keep it in a locked place away from the server. It is not shown
                  again. Do not email it, photograph it or save it on a computer.
                </MessageBarBody>
              </MessageBar>
              <Caption1 className={styles.muted}>
                The key has no letters I, L, O or U, so 0 is always a zero and 1 a one.
              </Caption1>
            </DialogContent>
            <DialogActions>
              <Button appearance="primary" onClick={() => goTo('confirm')}>
                I have written it down
              </Button>
              <Button onClick={close}>Cancel</Button>
            </DialogActions>
          </DialogBody>
        )}

        {step === 'prove' && current && (
          <form onSubmit={submitHeld} noValidate>
            <DialogBody>
              <DialogTitle>Show that you hold the key</DialogTitle>
              <DialogContent className={styles.body}>
                <Text>
                  Type recovery key <span className={styles.mono}>{current.keyId}</span> from your copy. HC360 records
                  that you hold it, and when; nothing else changes, and the key is not shown.
                </Text>
                <Field
                  label="Recovery key"
                  required
                  validationMessage={heldMessage}
                  validationState={heldMessage ? 'error' : 'none'}
                >
                  <Input
                    className={styles.mono}
                    value={held}
                    onChange={(_, data) => {
                      setHeld(data.value);
                      setMissing(false);
                    }}
                    autoComplete="off"
                    spellCheck={false}
                  />
                </Field>
              </DialogContent>
              <DialogActions>
                <Button appearance="primary" type="submit" disabled={prove.isPending}>
                  Record that I hold it
                </Button>
                <Button onClick={() => goTo('status')}>Back</Button>
              </DialogActions>
            </DialogBody>
          </form>
        )}

        {step === 'confirm' && created && (
          <form onSubmit={submitKey} noValidate>
            <DialogBody>
              <DialogTitle>Type the key from your copy</DialogTitle>
              <DialogContent className={styles.body}>
                <Text>
                  This checks your copy. Backups use the new key once it matches, which has to be by{' '}
                  {formatDateTime(created.expiresAt)}; until then they go on using the current key.
                </Text>
                <Field
                  label="Recovery key"
                  required
                  validationMessage={keyMessage}
                  validationState={keyMessage ? 'error' : 'none'}
                >
                  <Input
                    className={styles.mono}
                    value={typed}
                    onChange={(_, data) => {
                      setTyped(data.value);
                      setMissing(false);
                    }}
                    autoComplete="off"
                    spellCheck={false}
                  />
                </Field>
              </DialogContent>
              <DialogActions>
                <Button appearance="primary" type="submit" disabled={confirm.isPending}>
                  Use the new key
                </Button>
                <Button onClick={() => goTo('show')}>Back to the key</Button>
              </DialogActions>
            </DialogBody>
          </form>
        )}
      </DialogSurface>
    </Dialog>
  );
}
