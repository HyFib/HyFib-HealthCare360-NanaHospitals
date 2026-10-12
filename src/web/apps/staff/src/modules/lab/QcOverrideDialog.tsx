import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Textarea,
} from '@fluentui/react-components';
import { ShieldErrorRegular } from '@fluentui/react-icons';
import { useState } from 'react';
import { westgardRuleText } from './labels';
import { useUnsavedChanges } from '@hc360/ui';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  list: { margin: '4px 0 0', paddingLeft: '18px' },
});

export interface LockedAssay {
  testName: string;
  violations: readonly string[];
}

/**
 * Supervisor override of a QC lockout (SRS LIS-06). Today's QC failed for the assay, so its results cannot be verified
 * unless a pathologist records why releasing them is still safe. The reason is audited and bound to the failed run.
 */
export function QcOverrideDialog({
  assays,
  busy,
  confirmLabel = 'Override and continue',
  onConfirm,
  onCancel,
}: {
  assays: readonly LockedAssay[];
  busy?: boolean;
  confirmLabel?: string;
  onConfirm: (reason: string) => void;
  onCancel: () => void;
}) {
  const styles = useStyles();
  const [reason, setReason] = useState('');
  const valid = reason.trim().length >= 10;

  const guard = useUnsavedChanges({ dirty: reason !== '', busy, onClose: onCancel });

  return (
    <Dialog open onOpenChange={guard.onOpenChange} modalType="alert">
      <DialogSurface style={{ maxWidth: 600 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !busy) {
              onConfirm(reason.trim());
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Quality control failed today</DialogTitle>
            <DialogContent className={styles.content}>
              <MessageBar intent="warning" layout="multiline" icon={<ShieldErrorRegular />}>
                <MessageBarBody>
                  <MessageBarTitle>Verification is locked for</MessageBarTitle>
                  <ul className={styles.list}>
                    {assays.map((assay) => (
                      <li key={assay.testName}>
                        <strong>{assay.testName}</strong>
                        {assay.violations.length > 0 &&
                          `: ${assay.violations.map((rule) => westgardRuleText[rule] ?? rule).join('; ')}`}
                      </li>
                    ))}
                  </ul>
                  The safest course is to correct the problem and repeat QC. Override only if you are sure the patient
                  results are reliable.
                </MessageBarBody>
              </MessageBar>
              <Field
                label="Why is it safe to release these results?"
                required
                hint="At least 10 characters. Recorded in the audit trail with your name."
              >
                <Textarea
                  value={reason}
                  onChange={(_, data) => setReason(data.value.slice(0, 500))}
                  rows={3}
                  resize="vertical"
                  autoFocus
                />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={busy}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || busy}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={busy}
                icon={busy ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                {confirmLabel}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
