import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { useState, type ReactNode } from 'react';

export interface ReasonDialogProps {
  open: boolean;
  title: string;
  description?: ReactNode;
  /** Label of the text box, e.g. "Reason" or "Note for the employee". */
  label?: string;
  hint?: string;
  /** A reason is required unless this is false. */
  required?: boolean;
  minLength?: number;
  maxLength?: number;
  confirmLabel: string;
  destructive?: boolean;
  busy?: boolean;
  initialValue?: string;
  /** Extra fields shown above the text box. */
  children?: ReactNode;
  /** Blocks confirming while extra fields are incomplete. */
  confirmDisabled?: boolean;
  onConfirm: (text: string) => void;
  onCancel: () => void;
}

/**
 * Asks for a reason or note before an action (reject, cancel, reopen, close…). Ctrl+Enter confirms; the dialog never
 * closes while the request is running so a slow WiFi response cannot be confirmed twice.
 */
export function ReasonDialog(props: ReasonDialogProps) {
  const { open, busy, onCancel } = props;
  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && !busy && onCancel()}>
      <DialogSurface style={{ maxWidth: 560 }}>{open && <ReasonBody {...props} />}</DialogSurface>
    </Dialog>
  );
}

function ReasonBody({
  title,
  description,
  label = 'Reason',
  hint,
  required = true,
  minLength = 1,
  maxLength = 500,
  confirmLabel,
  destructive,
  busy,
  initialValue,
  children,
  confirmDisabled,
  onConfirm,
  onCancel,
}: ReasonDialogProps) {
  const [text, setText] = useState(initialValue ?? '');
  const trimmed = text.trim();
  const missing = required && trimmed.length < minLength;
  const blocked = Boolean(busy || missing || confirmDisabled);

  const submit = () => {
    if (!blocked) {
      onConfirm(trimmed);
    }
  };

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <DialogBody>
        <DialogTitle>{title}</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          {description && <Text>{description}</Text>}
          {children}
          <Field
            label={label}
            required={required}
            hint={hint ?? (required && minLength > 1 ? `At least ${minLength} characters.` : undefined)}
          >
            <Textarea
              value={text}
              onChange={(_, data) => setText(data.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
                  event.preventDefault();
                  submit();
                }
              }}
              rows={3}
              maxLength={maxLength}
              resize="vertical"
              autoFocus
            />
          </Field>
        </DialogContent>
        <DialogActions>
          <Button onClick={onCancel} disabled={busy}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={blocked}
            // While the request runs the button stays focusable (aria-disabled): a disabled button drops focus out of
            // the open dialog, and Fluent then returns it to the button that opened the dialog, behind it. It still
            // submits the form, which `submit` ignores while busy.
            disabledFocusable={busy}
            icon={busy ? <Spinner size="tiny" /> : undefined}
            style={destructive && !blocked ? { backgroundColor: tokens.colorPaletteRedBackground3 } : undefined}
          >
            {confirmLabel}
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
