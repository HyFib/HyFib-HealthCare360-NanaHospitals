import { makeStyles } from '@fluentui/react-components';
import { ClipboardTaskListLtrRegular, DocumentErrorRegular, FolderOpenRegular } from '@fluentui/react-icons';
import { ErrorState, KpiCard } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { mrdQueries } from './api';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '12px' },
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))' },
});

/** Work waiting in medical records: coding to do, overdue charts and files not returned. */
export function MedicalRecordsWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canCode = usePermission('mrd.coding');
  const canRecords = usePermission('mrd.records');
  const coding = useQuery({ ...mrdQueries.codings({ status: 'Pending' }), enabled: canCode });
  const overdue = useQuery({ ...mrdQueries.records({ overdue: true }), enabled: canRecords });
  const requests = useQuery({ ...mrdQueries.requests({ status: 'Overdue' }), enabled: canRecords });
  // A count that failed to load reads as a dash, never as a count still loading or as nothing waiting.
  const count = (query: { data?: { totalCount: number }; error: unknown }) =>
    query.data ? query.data.totalCount.toLocaleString('en-IN') : query.error ? '—' : '…';
  const failed = [coding, overdue, requests].filter((q) => q.error);

  return (
    <div className={styles.root}>
      {failed.length > 0 && (
        <ErrorState error={failed[0]?.error} onRetry={() => failed.forEach((q) => void q.refetch())} />
      )}
      <div className={styles.kpis}>
        {canCode && (
          <KpiCard
            label="Waiting for coding"
            value={count(coding)}
            icon={<ClipboardTaskListLtrRegular />}
            hint="Discharged episodes"
            onClick={() => void navigate({ to: '/mrd/coding' })}
          />
        )}
        {canRecords && (
          <KpiCard
            label="Overdue charts"
            value={count(overdue)}
            icon={<DocumentErrorRegular />}
            hint="Past their completion date"
            onClick={() => void navigate({ to: '/mrd/records', search: { overdue: true } })}
          />
        )}
        {canRecords && (
          <KpiCard
            label="Files not returned"
            value={count(requests)}
            icon={<FolderOpenRegular />}
            hint="Issued files past due"
            onClick={() => void navigate({ to: '/mrd/requests', search: { status: 'Overdue' } })}
          />
        )}
      </div>
    </div>
  );
}
