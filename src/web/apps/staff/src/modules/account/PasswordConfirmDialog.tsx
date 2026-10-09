import { Field, Input, makeStyles, Text } from '@fluentui/react-components';
import { isApiError } from '@hc360/core';
import { ConfirmDialog } from '@hc360/ui';
import { useState, type ReactNode } from 'react';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/** The message for the password field: the server's answer about it, else nothing. */
function passwordError(error: unknown): string | undefined {
  if (!isApiError(error)) {
    return undefined;
  }
  return error.fieldErrors.password?.[0] ?? (error.isValidation ? undefined : error.userMessage);
}

/**
 * A confirmation that asks for the signed-in user's password again, for an action that must not be done by whoever
 * finds a workstation signed in, such as signing out other devices (ASVS V7.5.2).
 */
export function PasswordConfirmDialog({
  open,
  title,
  confirmLabel,
  busy,
  error,
  onConfirm,
  onCancel,
  children,
}: {
  open: boolean;
  title: string;
  confirmLabel: string;
  busy?: boolean;
  /** The failed request's error, if any. */
  error?: unknown;
  onConfirm: (password: string) => void;
  onCancel: () => void;
  children: ReactNode;
}) {
  const styles = useStyles();
  const [password, setPassword] = useState('');
  const [missing, setMissing] = useState(false);

  // Start empty each time the dialog opens.
  const [wasOpen, setWasOpen] = useState(open);
  if (wasOpen !== open) {
    setWasOpen(open);
    if (open) {
      setPassword('');
      setMissing(false);
    }
  }

  const message = missing ? 'Enter your password.' : passwordError(error);
  const confirm = () => (password ? onConfirm(password) : setMissing(true));

  return (
    <ConfirmDialog
      open={open}
      title={title}
      confirmLabel={confirmLabel}
      destructive
      busy={busy}
      onConfirm={confirm}
      onCancel={onCancel}
    >
      <form
        className={styles.stack}
        onSubmit={(event) => {
          event.preventDefault();
          if (!busy) {
            confirm();
          }
        }}
      >
        <Text>{children}</Text>
        <Field label="Your password" required validationMessage={message} validationState={message ? 'error' : 'none'}>
          <Input
            type="password"
            value={password}
            onChange={(_, d) => {
              setPassword(d.value);
              setMissing(false);
            }}
            autoComplete="current-password"
          />
        </Field>
      </form>
    </ConfirmDialog>
  );
}
