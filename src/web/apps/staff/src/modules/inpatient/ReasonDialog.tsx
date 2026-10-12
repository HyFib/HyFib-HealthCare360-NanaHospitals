import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  MessageBar,
  MessageBarBody,
  Spinner,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { isApiError } from '@hc360/core';
import { useUnsavedChanges } from '@hc360/ui';
import { useState, type FormEvent, type ReactNode } from 'react';

export interface ReasonDialogProps {
  open: boolean;
  title: string;
  /** What will happen, in plain words. */
  description?: ReactNode;
  label?: string;
  placeholder?: string;
  hint?: string;
  confirmLabel: string;
  destructive?: boolean;
  /** Reason optional (e.g. a cleaning note); required by default. */
  optional?: boolean;
  minLength?: number;
  maxLength?: number;
  busy?: boolean;
  /** Server error shown inside the dialog (business rules are shown in place, not as a toast). */
  error?: unknown;
  onConfirm: (reason: string) => void;
  onCancel: () => void;
}

/**
 * Confirmation that captures a reason (block a bed, cancel an admission, stop a medicine, void an entry…). Enter
 * submits; the confirm button stays disabled until the reason is long enough, so nothing irreversible happens by
 * accident. Remount it with a `key` to reset the text for a new subject.
 */
export function ReasonDialog(props: ReasonDialogProps) {
  const [reason, setReason] = useState('');
  const [wasOpen, setWasOpen] = useState(props.open);
  // A fresh, empty reason every time the dialog opens.
  if (props.open !== wasOpen) {
    setWasOpen(props.open);
    if (props.open) {
      setReason('');
    }
  }
  const minLength = props.optional ? 0 : (props.minLength ?? 3);
  const trimmed = reason.trim();
  const valid = trimmed.length >= minLength;

  const submit = (event: Pick<FormEvent, 'preventDefault'>) => {
    event.preventDefault();
    if (valid && !props.busy) {
      props.onConfirm(trimmed);
    }
  };

  // A typed reason is not thrown away without asking (SRS UX-08).
  const guard = useUnsavedChanges({
    dirty: reason !== '',
    busy: props.busy,
    open: props.open,
    onClose: () => {
      setReason('');
      props.onCancel();
    },
  });

  return (
    <>
      <Dialog open={props.open} onOpenChange={guard.onOpenChange} modalType="alert">
        <DialogSurface style={{ maxWidth: 520 }}>
          <form onSubmit={submit}>
            <DialogBody>
              <DialogTitle>{props.title}</DialogTitle>
              <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
                {props.description && (
                  // A block container, so descriptions may include lists.
                  <div style={{ fontSize: tokens.fontSizeBase300, lineHeight: tokens.lineHeightBase300 }}>
                    {props.description}
                  </div>
                )}
                <Field
                  label={props.label ?? 'Reason'}
                  required={!props.optional}
                  hint={props.hint ?? (minLength > 3 ? `At least ${minLength} characters` : undefined)}
                >
                  <Textarea
                    value={reason}
                    onChange={(_, data) => setReason(data.value)}
                    placeholder={props.placeholder}
                    maxLength={props.maxLength ?? 300}
                    rows={3}
                    resize="vertical"
                    autoFocus
                    onKeyDown={(event) => {
                      if (event.key === 'Enter' && !event.shiftKey) {
                        submit(event);
                      }
                    }}
                  />
                </Field>
                {props.error !== undefined && props.error !== null && (
                  <MessageBar intent="error" layout="multiline">
                    <MessageBarBody>
                      {isApiError(props.error) ? props.error.userMessage : 'The action failed. Please try again.'}
                    </MessageBarBody>
                  </MessageBar>
                )}
              </DialogContent>
              <DialogActions>
                <Button onClick={guard.requestClose} disabled={props.busy}>
                  Cancel
                </Button>
                <Button
                  type="submit"
                  appearance="primary"
                  disabled={!valid || props.busy}
                  // While the request runs the button stays focusable (aria-disabled): a disabled button drops focus out
                  // of the open dialog, and Fluent then returns it to the button that opened the dialog, behind it. It
                  // still submits the form, which `submit` ignores while busy.
                  disabledFocusable={props.busy}
                  icon={props.busy ? <Spinner size="tiny" /> : undefined}
                  style={props.destructive ? { backgroundColor: tokens.colorPaletteRedBackground3 } : undefined}
                >
                  {props.confirmLabel}
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
