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
  Radio,
  RadioGroup,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { BarcodeScannerRegular, LinkRegular } from '@fluentui/react-icons';
import { formatDateTime, isApiError } from '@hc360/core';
import { useNotify, useUnsavedChanges } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { errorMessage } from './common/errors';
import { labApi, labKeys, labQueries, type ResultSheetLine, type UnmatchedResultDto } from './api';
import { unmatchedReasonLabel } from './labels';
import { normalizeScan } from './scan';
import { analyserReading } from './unmatched';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '14px' },
  reading: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  value: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400 },
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  find: { display: 'flex', gap: '8px', alignItems: 'end' },
  grow: { flex: '1 1 auto' },
  lines: { display: 'flex', flexDirection: 'column' },
});

/** How a sample's test reads in the choice of where the result goes. */
function lineLabel(line: ResultSheetLine): string {
  const value = line.value ? `${line.value}${line.unit ? ` ${line.unit}` : ''}` : null;
  if (line.status === 'Verified') {
    return `${line.testName}: verified as ${value ?? '—'} (a pathologist reopens it first)`;
  }
  return value ? `${line.testName}: ${value} entered, to be replaced` : `${line.testName}: no result yet`;
}

/**
 * Puts an analyser's unmatched result on the right sample's test (INT-LAB). The sample is found by its barcode, the
 * one the analyser sent or the one on the tube; the test is chosen from the sample's result sheet. The result is then
 * an entered result with the analyser's reading, which a person verifies: it is never auto-verified.
 */
export function AssignUnmatchedDialog({ result, onClose }: { result: UnmatchedResultDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [barcode, setBarcode] = useState(result.sampleId ? (result.sampleBarcode ?? '') : '');
  const [lookedUp, setLookedUp] = useState(result.sampleId ? (result.sampleBarcode ?? '') : '');
  const [chosen, setChosen] = useState<string>(result.labResultId ?? '');

  const sample = useQuery({ ...labQueries.sampleByBarcode(lookedUp), enabled: lookedUp !== '', retry: false });
  const sheet = useQuery({
    ...labQueries.sheet(sample.data?.id ?? ''),
    enabled: Boolean(sample.data?.id),
    retry: false,
  });
  const lines = sheet.data?.lines ?? [];
  const target = lines.find((line) => line.resultId === chosen && line.status !== 'Verified');

  const assign = useMutation({
    mutationFn: (resultId: string) => labApi.assignUnmatched(result.id, resultId),
    onSuccess: () => {
      notify.success(
        'Result assigned',
        `${target?.testName ?? 'The test'} of sample ${sheet.data?.barcode ?? ''} has the analyser's result. A person verifies it.`,
      );
      void queryClient.invalidateQueries({ queryKey: labKeys.unmatched });
      void queryClient.invalidateQueries({ queryKey: labKeys.sheets });
      void queryClient.invalidateQueries({ queryKey: labKeys.worklist });
      onClose();
    },
  });

  const guard = useUnsavedChanges({ dirty: chosen !== (result.labResultId ?? ''), busy: assign.isPending, onClose });

  const find = () => {
    const code = normalizeScan(barcode);
    setBarcode(code);
    setLookedUp(code);
    setChosen('');
  };

  const notFound = sample.error && isApiError(sample.error) && sample.error.status === 404;

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (target && !assign.isPending) {
              assign.mutate(target.resultId);
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Assign the analyser&apos;s result to a test
              <Caption1 block className={styles.muted}>
                {result.instrumentCode} · channel {result.channelCode} · {unmatchedReasonLabel[result.reason]}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              <div className={styles.reading}>
                <Text className={styles.value}>{analyserReading(result)}</Text>
                <Caption1 className={styles.muted}>
                  Received {formatDateTime(result.receivedAt)}
                  {result.sampleBarcode ? ` for sample ${result.sampleBarcode}` : ' without a sample id'}
                </Caption1>
                <Caption1>{result.detail}</Caption1>
                {result.instrumentComment && <Caption1>Analyser comment: {result.instrumentComment}</Caption1>}
              </div>
              {assign.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(assign.error, 'The result could not be assigned.')}</MessageBarBody>
                </MessageBar>
              )}
              <div className={styles.find}>
                <Field
                  label="Sample barcode"
                  hint="Scan the tube the analyser measured, then Enter"
                  className={styles.grow}
                  validationMessage={notFound ? `No sample has barcode ${lookedUp}. Check the label.` : undefined}
                >
                  <Input
                    value={barcode}
                    onChange={(_, data) => setBarcode(data.value.slice(0, 64))}
                    onKeyDown={(event) => {
                      if (event.key === 'Enter') {
                        event.preventDefault();
                        find();
                      }
                    }}
                    contentBefore={<BarcodeScannerRegular />}
                    autoComplete="off"
                    autoFocus={!result.sampleId}
                  />
                </Field>
                <Button onClick={find} disabled={!barcode.trim()}>
                  Find sample
                </Button>
              </div>
              {(sample.isFetching || sheet.isFetching) && <Spinner size="tiny" label="Opening the sample…" />}
              {sample.error && !notFound && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(sample.error, 'The sample could not be opened.')}</MessageBarBody>
                </MessageBar>
              )}
              {sheet.error && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {errorMessage(sheet.error, "The sample's tests could not be opened.")}
                  </MessageBarBody>
                </MessageBar>
              )}
              {sheet.data && (
                <Field
                  label={`Test on sample ${sheet.data.barcode} · ${sheet.data.patientName} (${sheet.data.uhid})`}
                  required
                  hint="Only a test without a verified result can take it."
                >
                  <RadioGroup value={chosen} onChange={(_, data) => setChosen(data.value)} className={styles.lines}>
                    {lines.map((line) => (
                      <Radio
                        key={line.resultId}
                        value={line.resultId}
                        label={lineLabel(line)}
                        disabled={line.status === 'Verified'}
                      />
                    ))}
                  </RadioGroup>
                </Field>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={assign.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!target || assign.isPending}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={assign.isPending}
                icon={assign.isPending ? <Spinner size="tiny" aria-hidden /> : <LinkRegular />}
              >
                Assign to {target?.testName ?? 'the test'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
