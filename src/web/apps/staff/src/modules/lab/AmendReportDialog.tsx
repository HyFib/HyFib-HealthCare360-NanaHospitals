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
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Fragment, useState } from 'react';
import { CriticalCallDialog } from './common/CriticalCallDialog';
import { errorMessage, hasErrorCode } from './common/errors';
import type { CriticalCallDetails } from './common/types';
import { labApi, labKeys, type AmendmentChange, type LabReportView, type ResultFlag } from './api';
import { flagDisplay } from './flags';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  table: { width: '100%', borderCollapse: 'collapse' },
  th: {
    textAlign: 'start',
    padding: '4px 6px',
    color: tokens.colorNeutralForeground3,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    fontWeight: tokens.fontWeightSemibold,
  },
  td: { padding: '4px 6px', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, verticalAlign: 'middle' },
  test: { padding: '8px 6px 2px', fontWeight: tokens.fontWeightSemibold, color: tokens.colorBrandForeground1 },
  muted: { color: tokens.colorNeutralForeground3 },
  changed: { backgroundColor: tokens.colorPaletteYellowBackground1 },
});

interface AmendRow {
  resultId: string;
  testName: string;
  name: string;
  value: string | null;
  unit: string | null;
  flag: ResultFlag | null;
  organism: string | null;
  colonyCount: string | null;
  remarks: string | null;
}

/** Released results that can be corrected (unreleased ones are corrected through result entry). */
function amendableRows(view: LabReportView): AmendRow[] {
  return view.sections.flatMap((section) =>
    section.tests
      .filter((test) => test.status === 'Released')
      .flatMap((test) =>
        test.results
          .filter((line) => line.status === 'Verified')
          .map((line) => ({
            resultId: line.resultId,
            testName: test.name,
            name: line.name,
            value: line.value,
            unit: line.unit,
            flag: line.flag,
            organism: line.organism,
            colonyCount: line.colonyCount,
            remarks: line.remarks,
          })),
      ),
  );
}

/**
 * Post-release correction (SRS LIS-07): a numbered amendment with the reason and old → new values, re-announced to the
 * doctor, the EHR and the patient as "(amended)". A corrected value that is critical needs a documented call.
 */
export function AmendReportDialog({ view, onClose }: { view: LabReportView; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const rows = amendableRows(view);
  const [values, setValues] = useState<Record<string, string>>({});
  const [reason, setReason] = useState('');
  const [needCall, setNeedCall] = useState(false);

  const changes: AmendmentChange[] = rows
    .filter(
      (row) =>
        values[row.resultId] !== undefined &&
        values[row.resultId]!.trim() !== '' &&
        values[row.resultId]!.trim() !== (row.value ?? '').trim(),
    )
    .map((row) => ({
      resultId: row.resultId,
      value: values[row.resultId]!.trim(),
      remarks: row.remarks,
      organism: row.organism,
      colonyCount: row.colonyCount,
    }));
  const valid = changes.length > 0 && reason.trim().length >= 10;

  const amend = useMutation({
    mutationFn: (criticalCall: CriticalCallDetails | null) =>
      labApi.amendReport(view.reportId, { reason: reason.trim(), changes, criticalCall }),
    onSuccess: () => {
      notify.success(
        `Report amended (revision ${view.revision + 1})`,
        'The doctor and the patient are informed of the corrected report.',
      );
      void queryClient.invalidateQueries({ queryKey: labKeys.reports });
      void queryClient.invalidateQueries({ queryKey: labKeys.orders });
      onClose();
    },
    onError: (error) => {
      if (hasErrorCode(error, 'lab.critical_call_required')) {
        setNeedCall(true);
      }
    },
  });

  if (needCall) {
    return (
      <CriticalCallDialog
        open
        title="A corrected value is critical"
        intro="Call the treating doctor about the corrected value, then save the call to complete the amendment."
        findings={changes.map((change) => {
          const row = rows.find((r) => r.resultId === change.resultId);
          return {
            id: change.resultId,
            label: `${row?.name ?? 'Result'}: ${row?.value ?? '—'} → ${change.value}${row?.unit ? ` ${row.unit}` : ''}`,
          };
        })}
        submitLabel="Save call and amend"
        busy={amend.isPending}
        onCancel={() => setNeedCall(false)}
        onSubmit={(call) => amend.mutate(call)}
      />
    );
  }

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !amend.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 820 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !amend.isPending) {
              amend.mutate(null);
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              Amend report {view.reportNumber}
              <Caption1 block className={styles.muted}>
                Type the corrected value only for results that change. Everything else stays as released.
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.content}>
              {amend.error && !hasErrorCode(amend.error, 'lab.critical_call_required') && (
                <MessageBar intent="error">
                  <MessageBarBody>{errorMessage(amend.error, 'The report could not be amended.')}</MessageBarBody>
                </MessageBar>
              )}
              {rows.length === 0 ? (
                <Text>No released results on this report. Correct unreleased results in result entry.</Text>
              ) : (
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th className={styles.th}>Result</th>
                      <th className={styles.th}>Released value</th>
                      <th className={styles.th}>Corrected value</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map((row, index) => {
                      const changed = changes.some((change) => change.resultId === row.resultId);
                      return (
                        <Fragment key={row.resultId}>
                          {row.testName !== rows[index - 1]?.testName && (
                            <tr>
                              <td className={styles.test} colSpan={3}>
                                {row.testName}
                              </td>
                            </tr>
                          )}
                          <tr className={changed ? styles.changed : undefined}>
                            <td className={styles.td}>{row.name}</td>
                            <td className={styles.td}>
                              {row.value} {row.unit}{' '}
                              {flagDisplay(row.flag)?.code ? `(${flagDisplay(row.flag)?.code})` : ''}
                            </td>
                            <td className={styles.td}>
                              <Input
                                value={values[row.resultId] ?? ''}
                                onChange={(_, data) =>
                                  setValues((current) => ({ ...current, [row.resultId]: data.value.slice(0, 2000) }))
                                }
                                aria-label={`Corrected value for ${row.name}`}
                                placeholder="Unchanged"
                                contentAfter={row.unit ? <Caption1>{row.unit}</Caption1> : undefined}
                              />
                            </td>
                          </tr>
                        </Fragment>
                      );
                    })}
                  </tbody>
                </table>
              )}
              <Field
                label="Reason for the amendment"
                required
                hint="At least 10 characters. Printed on the report and sent to the doctor."
              >
                <Textarea
                  value={reason}
                  onChange={(_, data) => setReason(data.value.slice(0, 500))}
                  rows={2}
                  resize="vertical"
                />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={amend.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || amend.isPending}
                icon={amend.isPending ? <Spinner size="tiny" /> : undefined}
              >
                {changes.length > 0
                  ? `Amend ${changes.length} ${changes.length === 1 ? 'result' : 'results'}`
                  : 'Amend report'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
