import { Button, Caption1, makeStyles, Spinner, Text, tokens } from '@fluentui/react-components';
import { ArrowSyncRegular } from '@fluentui/react-icons';
import { ErrorState, LoadingState, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { usePermission } from '@/lib/permissions';
import { dpdpApi, dpdpKeys, dpdpQueries } from './dpdpApi';
import { PRIVACY_PERMISSIONS as P } from './labels';
import { PlainTable } from './PlainTable';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '10px' },
  muted: { color: tokens.colorNeutralForeground3 },
  path: { fontFamily: tokens.fontFamilyMonospace, overflowWrap: 'anywhere' },
});

/**
 * Erasure and backups (DPDP Act s.8(7)): how long erased data stays in backups, and the erasure ledger that applies an
 * erasure again after a restore from an earlier backup. Shown on the retention screen to those who read privacy requests.
 */
export function ErasureLedgerSection() {
  const canRead = usePermission(P.requestsRead);
  return canRead ? <Ledger /> : null;
}

function Ledger() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canReplay = usePermission(P.requestsManage);
  const status = useQuery(dpdpQueries.ledger());
  const replay = useMutation({
    mutationFn: () => dpdpApi.replayLedger(),
    onSuccess: async (outcome) => {
      notify.success(
        outcome.reapplied.length > 0
          ? `${outcome.reapplied.length} erasure(s) applied again`
          : 'Every erasure is in place',
        `${outcome.entries} erasure(s) in the ledger.`,
      );
      await queryClient.invalidateQueries({ queryKey: dpdpKeys.ledger });
    },
    onError: (error) => notify.error(error, 'Could not check the erasures'),
  });

  return (
    <Section
      title="Erasure and backups"
      description="Erased data stays in the encrypted backups until they are deleted. Each erasure is kept in a ledger outside the database, and applied again if a backup made before it is restored."
      actions={
        canReplay && (
          <Button
            icon={replay.isPending ? <Spinner size="tiny" aria-hidden /> : <ArrowSyncRegular />}
            disabled={replay.isPending}
            onClick={() => replay.mutate()}
          >
            Check erasures now
          </Button>
        )
      }
    >
      {status.error ? (
        <ErrorState error={status.error} onRetry={() => void status.refetch()} />
      ) : !status.data ? (
        <LoadingState />
      ) : (
        <div className={styles.stack}>
          <Text>
            Backups are kept {status.data.backupRetentionDays} days, so erased data is gone from them within{' '}
            {status.data.backupRetentionDays + 1} days. The ledger holds {status.data.entries} erasure(s)
            {status.data.rejected > 0
              ? `, and ${status.data.rejected} line(s) that are not sealed by this server, never applied`
              : ''}
            .
          </Text>
          <Caption1 className={styles.muted}>
            Ledger: <span className={styles.path}>{status.data.path}</span>
            {status.data.mirrorPath ? (
              <>
                {' '}
                · copy: <span className={styles.path}>{status.data.mirrorPath}</span>
              </>
            ) : (
              ' · no copy: set the backups’ second copy so the ledger outlives this server’s disk.'
            )}
          </Caption1>
          {status.data.replays.length > 0 && (
            <PlainTable
              ariaLabel="Erasures applied again after a restore"
              rows={status.data.replays}
              getRowId={(row) => row.ledgerEntryId}
              columns={[
                { key: 'request', header: 'Request', render: (r) => r.requestNumber, width: 150 },
                { key: 'erased', header: 'Erased on', render: (r) => r.erasedAt.slice(0, 10), width: 120 },
                {
                  key: 'applied',
                  header: 'Applied again',
                  render: (r) =>
                    r.patientMissing ? 'Patient not in the restored database' : r.applied.join(', ') || '—',
                },
                { key: 'held', header: 'Held back', render: (r) => r.heldBack.join(', ') || '—', width: 140 },
              ]}
            />
          )}
        </div>
      )}
    </Section>
  );
}
