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
  Spinner,
  Switch,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { CriticalCallDialog } from '@/modules/lab/common/CriticalCallDialog';
import { errorMessage, hasErrorCode } from '@/modules/lab/common/errors';
import type { CriticalCallDetails } from '@/modules/lab/common/types';
import { radiologyApi, radiologyKeys } from './api';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Signed addition to a signed report (SRS IMG-03): the original text is never edited. A released report is
 * re-announced so the doctor sees the addendum; a new critical finding needs its own documented call.
 */
export function AddendumDialog({ studyId, studyName, onClose }: { studyId: string; studyName: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [text, setText] = useState('');
  const [reason, setReason] = useState('');
  const [critical, setCritical] = useState(false);
  const [calling, setCalling] = useState(false);

  const add = useMutation({
    mutationFn: (criticalCall: CriticalCallDetails | null) =>
      radiologyApi.addAddendum(studyId, { text: text.trim(), reason: reason.trim() || null, isCriticalFinding: critical, criticalCall }),
    onSuccess: () => {
      notify.success('Addendum added', 'The doctor is informed of the addition.');
      void queryClient.invalidateQueries({ queryKey: radiologyKeys.all });
      onClose();
    },
    onError: (error) => {
      if (hasErrorCode(error, 'radiology.critical_call_required')) {
        setCalling(true);
      }
    },
  });

  if (calling) {
    return (
      <CriticalCallDialog
        open
        title="Inform the doctor about the critical finding"
        findings={[{ id: studyId, label: `${studyName} (addendum): ${text.trim().slice(0, 200)}` }]}
        submitLabel="Save call and add addendum"
        busy={add.isPending}
        onCancel={() => setCalling(false)}
        onSubmit={(call) => add.mutate(call)}
      />
    );
  }

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !add.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (!text.trim() || add.isPending) {
              return;
            }
            if (critical) {
              setCalling(true);
            } else {
              add.mutate(null);
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Add an addendum
              <Caption1 block className={styles.muted}>
                {studyName}. The signed report stays as it is; the addendum is printed below it with your name.
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {add.error && !hasErrorCode(add.error, 'radiology.critical_call_required') && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(add.error, 'The addendum could not be added.')}</MessageBarBody>
                </MessageBar>
              )}
              <Field label="Addendum" required>
                <Textarea value={text} onChange={(_, data) => setText(data.value.slice(0, 4000))} rows={5} resize="vertical" autoFocus />
              </Field>
              <Field label="Reason" hint="e.g. Comparison with previous films, clinical correlation requested">
                <Input value={reason} onChange={(_, data) => setReason(data.value.slice(0, 300))} />
              </Field>
              <Switch checked={critical} onChange={(_, data) => setCritical(data.checked)} label="This addendum reports a critical finding (the doctor must be called)" />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={add.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={!text.trim() || add.isPending} icon={add.isPending ? <Spinner size="tiny" /> : undefined}>
                {critical ? 'Continue to the call' : 'Sign and add'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
