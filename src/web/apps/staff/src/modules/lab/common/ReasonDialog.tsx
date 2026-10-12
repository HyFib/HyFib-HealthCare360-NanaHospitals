import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { useUnsavedChanges } from '@hc360/ui';
import { useState, type ReactNode } from 'react';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  suggestions: { display: 'flex', flexWrap: 'wrap', gap: '6px' },
});

/**
 * Asks for the reason of an irreversible action (cancel a test, cancel a study, reopen a verified result). The reason is
 * written to the audit trail, so the button stays disabled until a meaningful reason is typed. Mount it with a `key`
 * per target so every opening starts empty.
 */
export function ReasonDialog({
  open,
  title,
  description,
  label = 'Reason',
  hint,
  suggestions,
  minLength = 3,
  maxLength = 300,
  confirmLabel,
  destructive,
  busy,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  title: string;
  description?: ReactNode;
  label?: string;
  hint?: string;
  suggestions?: readonly string[];
  minLength?: number;
  maxLength?: number;
  confirmLabel: string;
  destructive?: boolean;
  busy?: boolean;
  onConfirm: (reason: string) => void;
  onCancel: () => void;
}) {
  const styles = useStyles();
  const [reason, setReason] = useState('');
  const trimmed = reason.trim();
  const tooShort = trimmed.length < minLength;
  // A typed reason is not thrown away without asking (SRS UX-08).
  const guard = useUnsavedChanges({ dirty: reason !== '', busy, open, onClose: onCancel });

  return (
    <>
      <Dialog open={open} onOpenChange={guard.onOpenChange} modalType="alert">
        <DialogSurface style={{ maxWidth: 560 }}>
          <form
            onSubmit={(event) => {
              event.preventDefault();
              if (!tooShort && !busy) {
                onConfirm(trimmed);
              }
            }}
          >
            <DialogBody>
              <DialogTitle>{title}</DialogTitle>
              <DialogContent className={styles.content}>
                {description}
                {suggestions && suggestions.length > 0 && (
                  <div role="group" aria-label="Common reasons" className={styles.suggestions}>
                    {suggestions.map((suggestion) => (
                      <Button
                        key={suggestion}
                        size="small"
                        shape="circular"
                        appearance={reason === suggestion ? 'primary' : 'outline'}
                        onClick={() => setReason(suggestion)}
                      >
                        {suggestion}
                      </Button>
                    ))}
                  </div>
                )}
                <Field
                  label={label}
                  required
                  hint={
                    hint ??
                    (minLength > 3
                      ? `At least ${minLength} characters. This is recorded in the audit trail.`
                      : 'This is recorded in the audit trail.')
                  }
                  validationState={reason && tooShort ? 'warning' : 'none'}
                >
                  <Input
                    value={reason}
                    onChange={(_, data) => setReason(data.value.slice(0, maxLength))}
                    autoFocus
                    maxLength={maxLength}
                  />
                </Field>
              </DialogContent>
              <DialogActions>
                <Button onClick={guard.requestClose} disabled={busy}>
                  Go back
                </Button>
                <Button
                  type="submit"
                  appearance="primary"
                  disabled={tooShort || busy}
                  // While the request runs the button stays focusable (aria-disabled): a disabled button drops focus out
                  // of the open dialog, and Fluent then returns it to the button that opened the dialog, behind it. It
                  // still submits the form, which is ignored while busy.
                  disabledFocusable={busy}
                  icon={busy ? <Spinner size="tiny" /> : undefined}
                  style={destructive ? { backgroundColor: tokens.colorPaletteRedBackground3 } : undefined}
                >
                  {confirmLabel}
                </Button>
              </DialogActions>
            </DialogBody>
          </form>
        </DialogSurface>
      </Dialog>
      {guard.dialogs}
    </>
  );
}
