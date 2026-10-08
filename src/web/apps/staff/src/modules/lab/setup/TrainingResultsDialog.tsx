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
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  tokens,
} from '@fluentui/react-components';
import { BarcodeScannerRegular, SendRegular } from '@fluentui/react-icons';
import { useNotify } from '@hc360/ui';
import { useState } from 'react';
import { normalizeScan } from '@/modules/lab/scan';
import { labApi, type LabInstrumentDto, type TrainingSampleDto } from '../api';
import { trainingValueProblem } from './connections';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  find: { display: 'flex', gap: '8px', alignItems: 'flex-end' },
  code: { fontFamily: tokens.fontFamilyMonospace },
  value: { width: '140px' },
  units: { width: '100px' },
});

/**
 * The training analyser (INT-LAB simulator): find a received sample, enter its results for this analyser's tests, and
 * HC360 sends them as the analyser would (HL7 or ASTM, per Lab setup). They are then checked and released like any
 * analyser's results.
 */
export function TrainingResultsDialog({ instrument, onClose }: { instrument: LabInstrumentDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const [barcode, setBarcode] = useState('');
  const [sample, setSample] = useState<TrainingSampleDto | null>(null);
  const [values, setValues] = useState<Record<string, { value: string; units: string }>>({});
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);

  const find = async () => {
    const code = normalizeScan(barcode);
    if (!code || busy) {
      return;
    }
    setBusy(true);
    setProblem(null);
    try {
      const found = await labApi.trainingSample(instrument.code, code);
      setSample(found);
      setValues(Object.fromEntries(found.channels.map((channel) => [channel, { value: '', units: '' }])));
    } catch (error) {
      setSample(null);
      notify.error(error, 'Could not find the sample');
    } finally {
      setBusy(false);
    }
  };

  const entered = sample
    ? sample.channels
        .map((channel) => ({
          channel,
          value: values[channel]?.value.trim() ?? '',
          units: values[channel]?.units.trim() ?? '',
        }))
        .filter((r) => r.value.length > 0)
    : [];
  const invalid = entered.some((r) => trainingValueProblem(r.value) || trainingValueProblem(r.units));

  const send = async () => {
    if (!sample || entered.length === 0 || invalid) {
      return;
    }
    setBusy(true);
    setProblem(null);
    try {
      const outcome = await labApi.sendTrainingResults(instrument.code, {
        sampleBarcode: sample.sampleBarcode,
        results: entered.map((r) => ({ channel: r.channel, value: r.value, units: r.units || null })),
      });
      if (outcome.accepted) {
        notify.success('Results received', outcome.message);
        onClose();
      } else {
        setProblem(outcome.message);
      }
    } catch (error) {
      notify.error(error, 'The results were not sent');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <DialogBody>
          <DialogTitle>Send results from {instrument.code} (training)</DialogTitle>
          <DialogContent className={styles.body}>
            <MessageBar intent="info">
              <MessageBarBody>
                Enter the results the analyser would report. They reach the sample as {instrument.code}’s results, and
                are checked and released as usual.
              </MessageBarBody>
            </MessageBar>
            <div className={styles.find}>
              <Field label="Sample barcode" style={{ flex: 1 }}>
                <Input
                  contentBefore={<BarcodeScannerRegular aria-hidden />}
                  value={barcode}
                  autoComplete="off"
                  placeholder="Scan the tube"
                  onChange={(_, d) => setBarcode(d.value)}
                  onKeyDown={(e) => {
                    if (e.key === 'Enter') {
                      e.preventDefault();
                      void find();
                    }
                  }}
                />
              </Field>
              <Button onClick={() => void find()} disabled={busy || !barcode.trim()}>
                Find
              </Button>
            </div>
            {sample && (
              <>
                <Caption1>
                  {sample.patientName}
                  {sample.patientId ? ` · ${sample.patientId}` : ''} · sample{' '}
                  <span className={styles.code}>{sample.sampleBarcode}</span>
                </Caption1>
                <Table size="small" aria-label="Results to send">
                  <TableHeader>
                    <TableRow>
                      <TableHeaderCell>Channel</TableHeaderCell>
                      <TableHeaderCell>Result</TableHeaderCell>
                      <TableHeaderCell>Units</TableHeaderCell>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {sample.channels.map((channel) => (
                      <TableRow key={channel}>
                        <TableCell className={styles.code}>{channel}</TableCell>
                        <TableCell>
                          <Input
                            className={styles.value}
                            aria-label={`Result for ${channel}`}
                            value={values[channel]?.value ?? ''}
                            onChange={(_, d) =>
                              setValues((prev) => ({ ...prev, [channel]: { ...prev[channel]!, value: d.value } }))
                            }
                          />
                        </TableCell>
                        <TableCell>
                          <Input
                            className={styles.units}
                            aria-label={`Units for ${channel}`}
                            value={values[channel]?.units ?? ''}
                            onChange={(_, d) =>
                              setValues((prev) => ({ ...prev, [channel]: { ...prev[channel]!, units: d.value } }))
                            }
                          />
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
                {invalid && (
                  <Caption1>Results and units cannot hold | ^ ~ \ &amp;, which analyser messages reserve.</Caption1>
                )}
              </>
            )}
            {problem && (
              <MessageBar intent="error">
                <MessageBarBody>{problem}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={busy}>
              Close
            </Button>
            <Button
              appearance="primary"
              icon={<SendRegular />}
              disabled={busy || !sample || entered.length === 0 || invalid}
              onClick={() => void send()}
            >
              Send {entered.length > 0 ? `${entered.length} result${entered.length === 1 ? '' : 's'}` : 'results'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
