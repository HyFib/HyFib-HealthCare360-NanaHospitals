import { makeStyles } from '@fluentui/react-components';
import { ClipboardTaskListLtrRegular, DocumentErrorRegular, FolderOpenRegular } from '@fluentui/react-icons';
import { KpiCard } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { mrdQueries } from './api';

const useStyles = makeStyles({
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
  const count = (data: { totalCount: number } | undefined) => (data ? data.totalCount.toLocaleString('en-IN') : '…');

  return (
    <div className={styles.kpis}>
      {canCode && (
        <KpiCard
          label="Waiting for coding"
          value={count(coding.data)}
          icon={<ClipboardTaskListLtrRegular />}
          hint="Discharged episodes"
          onClick={() => void navigate({ to: '/mrd/coding' })}
        />
      )}
      {canRecords && (
        <KpiCard
          label="Overdue charts"
          value={count(overdue.data)}
          icon={<DocumentErrorRegular />}
          hint="Past their completion date"
          onClick={() => void navigate({ to: '/mrd/records', search: { overdue: true } })}
        />
      )}
      {canRecords && (
        <KpiCard
          label="Files not returned"
          value={count(requests.data)}
          icon={<FolderOpenRegular />}
          hint="Issued files past due"
          onClick={() => void navigate({ to: '/mrd/requests', search: { status: 'Overdue' } })}
        />
      )}
    </div>
  );
}
