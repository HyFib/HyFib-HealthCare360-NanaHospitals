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
  tokens,
} from '@fluentui/react-components';
import { DocumentArrowDownRegular } from '@fluentui/react-icons';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { errorMessage } from './common/errors';
import { ACCEPTED_FILE_TYPES, fileProblem, uploadFile } from './common/files';
import { labApi, labKeys } from './api';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

export interface ReceiveReportTarget {
  itemId: string;
  testName: string;
  patientId: string;
  patientName: string;
  referralLabName?: string | null;
}

/**
 * Receipt of a reference laboratory's report: the scanned or e-mailed PDF is stored encrypted against the patient, then
 * the values are entered and verified in result entry like any other result.
 */
export function ReceiveReportDialog({ target, onClose }: { target: ReceiveReportTarget; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [reference, setReference] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const canUpload = usePermission('files.write');
  const problem = file ? fileProblem(file) : null;

  const receive = useMutation({
    mutationFn: async () => {
      const stored = file
        ? await uploadFile(file, { category: 'lab-external-report', patientId: target.patientId })
        : null;
      await labApi.receiveExternalReport(target.itemId, reference.trim() || null, stored?.id ?? null);
    },
    onSuccess: () => {
      notify.success(
        'External report received',
        'Enter the results from the report in result entry, then verify them.',
      );
      void queryClient.invalidateQueries({ queryKey: labKeys.outsourcing });
      void queryClient.invalidateQueries({ queryKey: labKeys.orders });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !receive.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (!problem && !receive.isPending) {
              receive.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Receive external report
              <Caption1 block className={styles.muted}>
                {target.testName} · {target.patientName}
                {target.referralLabName ? ` · from ${target.referralLabName}` : ''}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {receive.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(receive.error, 'The report could not be recorded.')}</MessageBarBody>
                </MessageBar>
              )}
              {canUpload ? (
                <Field
                  label="Report file (PDF or image, up to 25 MB)"
                  validationMessage={problem ?? undefined}
                  validationState={problem ? 'error' : 'none'}
                >
                  <input
                    type="file"
                    accept={ACCEPTED_FILE_TYPES}
                    onChange={(event) => setFile(event.target.files?.[0] ?? null)}
                  />
                </Field>
              ) : (
                <Caption1 className={styles.muted}>
                  You cannot attach files. Keep the paper report with the sample records, or ask an administrator for
                  document upload access.
                </Caption1>
              )}
              <Field label="Reference laboratory's report number">
                <Input value={reference} onChange={(_, data) => setReference(data.value.slice(0, 64))} autoFocus />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={receive.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={Boolean(problem) || receive.isPending}
                icon={receive.isPending ? <Spinner size="tiny" /> : <DocumentArrowDownRegular />}
              >
                Record receipt
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
