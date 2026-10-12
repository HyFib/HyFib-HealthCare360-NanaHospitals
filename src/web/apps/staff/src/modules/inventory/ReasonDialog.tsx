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
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { isApiError } from '@hc360/core';
import { useUnsavedChanges } from '@hc360/ui';
import { useState, type ReactNode } from 'react';

/**
 * Asks why before an irreversible step (reject, cancel, close, block). Mount it only while needed, so the text
 * box always starts empty. Enter confirms.
 */
export function ReasonDialog({
  title,
  children,
  label = 'Reason',
  placeholder,
  confirmLabel,
  destructive,
  busy,
  error,
  minLength = 3,
  maxLength = 300,
  optional,
  onSubmit,
  onClose,
}: {
  title: string;
  children?: ReactNode;
  label?: string;
  placeholder?: string;
  confirmLabel: string;
  destructive?: boolean;
  busy?: boolean;
  /** Server refusal to show inside the dialog. */
  error?: unknown;
  minLength?: number;
  maxLength?: number;
  optional?: boolean;
  onSubmit: (reason: string) => void;
  onClose: () => void;
}) {
  const [reason, setReason] = useState('');
  // A typed reason is not thrown away without asking (SRS UX-08).
  const guard = useUnsavedChanges({ dirty: reason !== '', busy, onClose });
  const trimmed = reason.trim();
  const valid = optional ? trimmed.length === 0 || trimmed.length >= minLength : trimmed.length >= minLength;
  const submit = () => {
    if (valid && !busy) {
      onSubmit(trimmed);
    }
  };
  return (
    <>
      <Dialog open modalType="alert" onOpenChange={guard.onOpenChange}>
        <DialogSurface style={{ maxWidth: 520 }}>
          <form
            onSubmit={(event) => {
              event.preventDefault();
              submit();
            }}
          >
            <DialogBody>
              <DialogTitle>{title}</DialogTitle>
              <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
                {children && <Text>{children}</Text>}
                <Field
                  label={label}
                  required={!optional}
                  hint={optional ? 'Optional' : `At least ${minLength} characters. It is kept on the record.`}
                >
                  <Input
                    value={reason}
                    onChange={(_, d) => setReason(d.value)}
                    placeholder={placeholder}
                    maxLength={maxLength}
                    autoFocus
                  />
                </Field>
                {Boolean(error) && (
                  <MessageBar intent="error" layout="multiline">
                    <MessageBarBody>
                      {isApiError(error) ? error.userMessage : 'The action failed. Try again.'}
                    </MessageBarBody>
                  </MessageBar>
                )}
              </DialogContent>
              <DialogActions>
                <Button onClick={guard.requestClose} disabled={busy}>
                  Go back
                </Button>
                <Button
                  type="submit"
                  appearance="primary"
                  disabled={!valid || busy}
                  // While the request runs the button stays focusable (aria-disabled): a disabled button drops focus out
                  // of the open dialog, and Fluent then returns it to the button that opened the dialog, behind it. It
                  // still submits the form, which `submit` ignores while busy.
                  disabledFocusable={busy}
                  icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
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
