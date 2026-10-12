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
  makeStyles,
  Spinner,
  Text,
  Textarea,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { MicRegular } from '@fluentui/react-icons';
import { bridge } from '@hc360/core';
import { CloseRequestScope, useCloseRequests, useNotify, useUnsavedChanges } from '@hc360/ui';
import { useState, type ReactNode } from 'react';

const useStyles = makeStyles({
  detail: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  label: { color: tokens.colorNeutralForeground3 },
  value: { wordBreak: 'break-word' },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/** Label above value, used on detail panels. Empty values show a dash. */
export function Detail({ label, children }: { label: string; children: ReactNode }) {
  const styles = useStyles();
  const empty = children === null || children === undefined || children === '' || children === false;
  return (
    <div className={styles.detail}>
      <Caption1 className={styles.label}>{label}</Caption1>
      <Text className={styles.value}>{empty ? '—' : children}</Text>
    </div>
  );
}

export interface ReasonDialogProps {
  open: boolean;
  title: string;
  /** What will happen, in plain words. */
  description?: ReactNode;
  label?: string;
  hint?: string;
  placeholder?: string;
  minLength?: number;
  maxLength?: number;
  /** Allow confirming without a reason (the reason is then sent as null). */
  optional?: boolean;
  confirmLabel: string;
  destructive?: boolean;
  busy?: boolean;
  onConfirm: (reason: string) => void;
  onCancel: () => void;
}

/**
 * Confirmation that asks for a reason (cancel, deactivate, entered in error, amend…). The reason is recorded in the
 * audit trail, so the dialog explains why it is needed. The text resets every time the dialog opens.
 */
export function ReasonDialog(props: ReasonDialogProps) {
  const closing = useCloseRequests({ onClose: props.onCancel, busy: props.busy });
  return (
    <Dialog open={props.open} onOpenChange={closing.onOpenChange} modalType="alert">
      <DialogSurface>
        <CloseRequestScope value={closing}>{props.open && <ReasonDialogBody {...props} />}</CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function ReasonDialogBody({
  title,
  description,
  label = 'Reason',
  hint,
  placeholder,
  minLength = 1,
  maxLength = 300,
  optional,
  confirmLabel,
  destructive,
  busy,
  onConfirm,
  onCancel,
}: ReasonDialogProps) {
  const styles = useStyles();
  const [reason, setReason] = useState('');
  // A typed reason is not thrown away without asking (SRS UX-08).
  const guard = useUnsavedChanges({ dirty: reason !== '', busy, onClose: onCancel });
  const length = reason.trim().length;
  const valid = optional ? length === 0 || length >= minLength : length >= minLength;
  const submit = () => {
    if (valid && !busy) {
      onConfirm(reason.trim());
    }
  };
  return (
    <>
      <DialogBody>
        <DialogTitle>{title}</DialogTitle>
        <DialogContent className={styles.form}>
          {description && <Text>{description}</Text>}
          <Field
            label={label}
            required={!optional}
            hint={hint ?? (minLength > 1 ? `At least ${minLength} characters (${length} so far).` : undefined)}
          >
            <Textarea
              autoFocus
              value={reason}
              onChange={(_, data) => setReason(data.value)}
              maxLength={maxLength}
              rows={3}
              resize="vertical"
              placeholder={placeholder}
              onKeyDown={(event) => {
                if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
                  event.preventDefault();
                  submit();
                }
              }}
            />
          </Field>
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={busy}>
            Go back
          </Button>
          <Button
            appearance="primary"
            disabled={!valid || busy}
            // While the request runs the button stays focusable (aria-disabled): a disabled button drops focus out of the
            // open dialog, and Fluent then returns it to the button that opened the dialog, behind it.
            disabledFocusable={busy}
            onClick={submit}
            icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
            style={destructive ? { backgroundColor: tokens.colorPaletteRedBackground3 } : undefined}
          >
            {confirmLabel}
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </>
  );
}

/**
 * Voice dictation through HC360 Desktop (Windows speech, en-IN). Renders nothing in a plain browser.
 * The recognised text is appended by the caller.
 */
export function DictateButton({ onText, label = 'Dictate' }: { onText: (text: string) => void; label?: string }) {
  const notify = useNotify();
  const [busy, setBusy] = useState(false);
  if (!bridge.isDesktop) {
    return null;
  }
  const dictate = () => {
    setBusy(true);
    bridge
      .dictate('en-IN')
      .then((result) => {
        if (result.text.trim()) {
          onText(result.text.trim());
        }
      })
      .catch((error: unknown) => notify.error(error, 'Dictation did not work'))
      .finally(() => setBusy(false));
  };
  return (
    <Tooltip content="Speak; the text is added at the end" relationship="description">
      <Button
        size="small"
        appearance="subtle"
        icon={busy ? <Spinner size="extra-tiny" aria-hidden /> : <MicRegular />}
        onClick={dictate}
        disabled={busy}
      >
        {label}
      </Button>
    </Tooltip>
  );
}
