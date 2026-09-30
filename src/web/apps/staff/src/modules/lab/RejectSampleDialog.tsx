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
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Spinner,
  Switch,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular } from '@fluentui/react-icons';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { errorMessage } from './common/errors';
import { labApi, labKeys, REJECTION_REASONS, type SampleRejected, type SampleRejectionReason } from './api';
import { rejectionReasonLabel } from './labels';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '14px' },
  reasons: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(190px, 1fr))' },
  done: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '6px', textAlign: 'center' },
  barcode: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

export interface RejectTarget {
  sampleId: string;
  barcode: string;
  patientName: string;
  tests: string[];
}

/**
 * Sample rejection with a standard reason (a NABH quality indicator) and, by default, a request for a new sample: the
 * new tube appears on the collection list with the same tests.
 */
export function RejectSampleDialog({
  target,
  onClose,
  onDone,
}: {
  target: RejectTarget;
  onClose: () => void;
  onDone?: (result: SampleRejected) => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [reason, setReason] = useState<SampleRejectionReason | ''>('');
  const [remarks, setRemarks] = useState('');
  const [recollect, setRecollect] = useState(true);

  const reject = useMutation({
    mutationFn: () =>
      labApi.reject(target.sampleId, {
        reason: reason as SampleRejectionReason,
        remarks: remarks.trim() || null,
        recollect,
      }),
    onSuccess: (result) => {
      void queryClient.invalidateQueries({ queryKey: labKeys.samples });
      void queryClient.invalidateQueries({ queryKey: labKeys.worklist });
      void queryClient.invalidateQueries({ queryKey: labKeys.orders });
      onDone?.(result);
    },
  });

  const needsRemarks = reason === 'Other' && !remarks.trim();
  const done = reject.data;

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !reject.isPending && onClose()} modalType="alert">
      <DialogSurface style={{ maxWidth: 640 }}>
        {done ? (
          <DialogBody>
            <DialogTitle>Sample rejected</DialogTitle>
            <DialogContent>
              <div className={styles.done} role="status">
                <CheckmarkCircleRegular fontSize={36} color={tokens.colorPaletteGreenForeground1} />
                {done.recollectionBarcode ? (
                  <>
                    <Text>A new sample was requested. It is on the collection list now.</Text>
                    <Caption1 className={styles.muted}>New tube barcode</Caption1>
                    <span className={styles.barcode}>{done.recollectionBarcode}</span>
                  </>
                ) : (
                  <Text>No new sample was requested. You can request one later from the order.</Text>
                )}
              </div>
            </DialogContent>
            <DialogActions>
              <Button appearance="primary" onClick={onClose} autoFocus>
                Done
              </Button>
            </DialogActions>
          </DialogBody>
        ) : (
          <form
            onSubmit={(event) => {
              event.preventDefault();
              if (reason && !needsRemarks && !reject.isPending) {
                reject.mutate();
              }
            }}
          >
            <DialogBody>
              <DialogTitle>
                Reject sample {target.barcode}
                <Caption1 block className={styles.muted}>
                  {target.patientName} · {target.tests.join(', ')}
                </Caption1>
              </DialogTitle>
              <DialogContent className={styles.content}>
                {reject.error && (
                  <MessageBar intent="error">
                    <MessageBarBody>{errorMessage(reject.error, 'The sample could not be rejected.')}</MessageBarBody>
                  </MessageBar>
                )}
                <Field label="Why is the sample rejected?" required>
                  <RadioGroup
                    value={reason}
                    onChange={(_, data) => setReason(data.value as SampleRejectionReason)}
                    className={styles.reasons}
                  >
                    {REJECTION_REASONS.map((value) => (
                      <Radio key={value} value={value} label={rejectionReasonLabel[value]} />
                    ))}
                  </RadioGroup>
                </Field>
                <Field
                  label="Remarks"
                  required={reason === 'Other'}
                  hint={
                    reason === 'Other'
                      ? 'Describe the problem.'
                      : 'Optional, e.g. grossly haemolysed, sample 0.5 mL only'
                  }
                  validationState={needsRemarks && remarks ? 'error' : 'none'}
                >
                  <Textarea
                    value={remarks}
                    onChange={(_, data) => setRemarks(data.value.slice(0, 500))}
                    rows={2}
                    resize="vertical"
                  />
                </Field>
                <Switch
                  checked={recollect}
                  onChange={(_, data) => setRecollect(data.checked)}
                  label="Request a new sample (the tests move to a new tube on the collection list)"
                />
              </DialogContent>
              <DialogActions>
                <Button onClick={onClose} disabled={reject.isPending}>
                  Cancel
                </Button>
                <Button
                  type="submit"
                  appearance="primary"
                  disabled={!reason || needsRemarks || reject.isPending}
                  icon={reject.isPending ? <Spinner size="tiny" /> : undefined}
                  style={{ backgroundColor: !reason || needsRemarks ? undefined : tokens.colorPaletteRedBackground3 }}
                >
                  {recollect ? 'Reject and request a new sample' : 'Reject sample'}
                </Button>
              </DialogActions>
            </DialogBody>
          </form>
        )}
      </DialogSurface>
    </Dialog>
  );
}
