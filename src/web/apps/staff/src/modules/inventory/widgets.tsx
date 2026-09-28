import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { ArrowRightRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { pharmacyQueries } from '@/modules/pharmacy/api';
import { PHARMACY_PERMISSIONS } from '@/modules/pharmacy/permissions';
import { inventoryQueries, type ExpiringBatch } from './api';
import { expiryLabel, formatQty } from './format';
import { SUPPLY_PERMISSIONS as S } from './permissions';

const useStyles = makeStyles({
  summary: { display: 'flex', gap: '24px', flexWrap: 'wrap', marginBottom: '12px' },
  value: { fontSize: tokens.fontSizeBase600, fontWeight: tokens.fontWeightSemibold, lineHeight: tokens.lineHeightBase600 },
  muted: { color: tokens.colorNeutralForeground3 },
  rows: { display: 'flex', flexDirection: 'column', gap: '6px' },
  row: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: '2px 12px', alignItems: 'baseline' },
  soon: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  later: { color: tokens.colorPaletteMarigoldForeground1 },
});

function ExpiringList({ batches }: { batches: readonly ExpiringBatch[] }) {
  const styles = useStyles();
  if (batches.length === 0) {
    return <Text className={styles.muted}>Nothing expires in the next 90 days.</Text>;
  }
  return (
    <div className={styles.rows}>
      {batches.slice(0, 6).map((b) => (
        <div key={b.batchId} className={styles.row}>
          <span>
            <Text weight="semibold">{b.itemName}</Text>{' '}
            <Caption1 className={styles.muted}>
              {b.batchNumber} · {formatQty(b.quantity)} · {b.storeName}
            </Caption1>
          </span>
          <Caption1 className={b.daysToExpiry <= 30 ? styles.soon : styles.later}>{expiryLabel(b.daysToExpiry)}</Caption1>
        </div>
      ))}
    </div>
  );
}

/**
 * Dashboard widget: stock expiring in the next 90 days (value at MRP), soonest first, so it can be used, returned
 * to the vendor or moved before it becomes a write-off. Store staff see every store; pharmacy staff see the pharmacies.
 */
export function NearExpiryStockWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canReadStock = usePermission(S.stockRead);
  const canReadPharmacy = usePermission([PHARMACY_PERMISSIONS.queueRead, PHARMACY_PERMISSIONS.sales, PHARMACY_PERMISSIONS.ipIssue]);
  const report = useQuery({ ...inventoryQueries.nearExpiry(null, 90), enabled: canReadStock });
  const pharmacy = useQuery({ ...pharmacyQueries.dashboard(null), enabled: !canReadStock && canReadPharmacy });

  const error = canReadStock ? report.error : pharmacy.error;
  if (error && !report.data && !pharmacy.data) {
    return <ErrorState error={error} onRetry={() => void (canReadStock ? report.refetch() : pharmacy.refetch())} title="Near-expiry stock" />;
  }

  const batches = canReadStock ? report.data?.batches : pharmacy.data?.nearExpiry;
  const count = canReadStock ? report.data?.batchCount : pharmacy.data?.nearExpiryBatches;
  const valueAtMrp = canReadStock ? report.data?.valueAtMrp : pharmacy.data?.nearExpiryValueAtMrp;

  return (
    <Section
      title="Near-expiry stock"
      description={canReadStock ? 'All stores, next 90 days' : 'Pharmacy stores, next 90 days'}
      actions={
        canReadStock ? (
          <Button appearance="subtle" icon={<ArrowRightRegular />} iconPosition="after" onClick={() => void navigate({ to: '/inventory/reports', search: { tab: 'near-expiry' } })}>
            Report
          </Button>
        ) : undefined
      }
    >
      <div className={styles.summary}>
        <div>
          <div className={styles.value}>{count === undefined ? '…' : count.toLocaleString('en-IN')}</div>
          <Caption1 className={styles.muted}>batches</Caption1>
        </div>
        <div>
          <div className={styles.value}>{valueAtMrp === undefined ? '…' : formatInr(valueAtMrp, { whole: true })}</div>
          <Caption1 className={styles.muted}>value at MRP</Caption1>
        </div>
      </div>
      {batches && <ExpiringList batches={batches} />}
    </Section>
  );
}
