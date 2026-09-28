import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueries, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { errorData, errorMessage, hasErrorCode } from './common/errors';
import { labApi, labKeys, labQueries, type QcLockout, type ResultSheet, type VerifyResultsRequest } from './api';
import { QcOverrideDialog } from './QcOverrideDialog';
import { planBulkVerification } from './worklist';

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px' },
  list: { margin: 0, paddingLeft: '18px', display: 'flex', flexDirection: 'column', gap: '4px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Pathologist bulk verification across tubes: only normal results are verified together; anything flagged, critical,
 * delta-failed or with remarks stays on the worklist for an individual look. QC lockouts still apply.
 */
export function BulkVerifyDialog({ sampleIds, onClose, onDone }: { sampleIds: readonly string[]; onClose: () => void; onDone: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [release, setRelease] = useState(true);
  const [lockouts, setLockouts] = useState<{ items: QcLockout[]; request: VerifyResultsRequest } | null>(null);
  const sheets = useQueries({ queries: sampleIds.map((id) => labQueries.sheet(id)) });
  const loading = sheets.some((sheet) => sheet.isLoading);
  const failed = sheets.filter((sheet) => sheet.error).length;
  const plans = sheets.map((sheet) => sheet.data).filter((sheet): sheet is ResultSheet => Boolean(sheet)).map(planBulkVerification);
  const ids = plans.flatMap((plan) => plan.ready).slice(0, 200);
  const held = plans.reduce((sum, plan) => sum + plan.held, 0);

  const verify = useMutation({
    mutationFn: (request: VerifyResultsRequest) => labApi.verifyResults(request),
    onSuccess: (outcome) => {
      notify.success(`${outcome.verified} normal results verified`, outcome.releasedItemIds.length ? `${outcome.releasedItemIds.length} tests released.` : undefined);
      void queryClient.invalidateQueries({ queryKey: labKeys.sheets });
      void queryClient.invalidateQueries({ queryKey: labKeys.worklist });
      void queryClient.invalidateQueries({ queryKey: labKeys.orders });
      void queryClient.invalidateQueries({ queryKey: labKeys.reports });
      onDone();
    },
    onError: (error, request) => {
      if (hasErrorCode(error, 'lab.qc_lockout')) {
        setLockouts({ items: errorData<{ lockouts?: QcLockout[] }>(error)?.lockouts ?? [], request });
      }
    },
  });

  if (lockouts) {
    return (
      <QcOverrideDialog
        assays={lockouts.items.map((item) => ({ testName: item.testName, violations: item.violations }))}
        busy={verify.isPending}
        onCancel={() => setLockouts(null)}
        onConfirm={(reason) => {
          const request = { ...lockouts.request, qcOverrideReason: reason };
          setLockouts(null);
          verify.mutate(request);
        }}
      />
    );
  }

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !verify.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 620 }}>
        <DialogBody>
          <DialogTitle>Verify normal results</DialogTitle>
          <DialogContent className={styles.content}>
            {loading ? (
              <Spinner label="Checking the selected samples…" />
            ) : (
              <>
                {failed > 0 && (
                  <MessageBar intent="warning">
                    <MessageBarBody>{failed} of the selected samples could not be opened and are left out.</MessageBarBody>
                  </MessageBar>
                )}
                {verify.error && !hasErrorCode(verify.error, 'lab.qc_lockout') && (
                  <MessageBar intent="error">
                    <MessageBarBody>{errorMessage(verify.error, 'The results could not be verified.')}</MessageBarBody>
                  </MessageBar>
                )}
                {ids.length === 0 ? (
                  <Text>None of the selected samples has normal results waiting for verification. Open each sample to review its results.</Text>
                ) : (
                  <>
                    <Text>
                      {ids.length} normal {ids.length === 1 ? 'result' : 'results'} on {plans.filter((plan) => plan.ready.length > 0).length} samples will be
                      verified.
                    </Text>
                    <ul className={styles.list}>
                      {plans
                        .filter((plan) => plan.ready.length > 0)
                        .map((plan) => (
                          <li key={plan.sheet.sampleId}>
                            <Text weight="semibold">{plan.sheet.patientName}</Text>{' '}
                            <Caption1 className={styles.muted}>
                              {plan.sheet.barcode} · {plan.ready.length} to verify{plan.held ? ` · ${plan.held} left for review` : ''}
                            </Caption1>
                          </li>
                        ))}
                    </ul>
                  </>
                )}
                {held > 0 && (
                  <Caption1 className={styles.muted}>
                    {held} {held === 1 ? 'result is' : 'results are'} left for individual review (flagged, critical, large change from the previous result, or with
                    remarks).
                  </Caption1>
                )}
                <Switch checked={release} onChange={(_, data) => setRelease(data.checked)} label="Release tests that become complete" />
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={verify.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={loading || ids.length === 0 || verify.isPending}
              icon={verify.isPending ? <Spinner size="tiny" /> : undefined}
              onClick={() => verify.mutate({ resultIds: ids, qcOverrideReason: null, criticalCall: null, release })}
            >
              Verify {ids.length} {ids.length === 1 ? 'result' : 'results'}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
