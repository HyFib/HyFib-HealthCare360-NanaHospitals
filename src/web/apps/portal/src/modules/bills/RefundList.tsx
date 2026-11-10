import { makeStyles } from '@fluentui/react-components';
import { ArrowUndoRegular } from '@fluentui/react-icons';
import { ListRow, RowCard } from '@/components/ListRow';
import { StatusPill } from '@/components/StatusPill';
import { useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import type { PortalRefund } from '@/lib/types';
import { REFUND_PURPOSE, REFUND_STAGE, refundReferenceText, refundStatusText } from '@/modules/bills/labels';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '8px' },
});

/**
 * The patient's refunds: how much, for what, where each stands in words, and the reference their bank or the counter
 * can trace it by. On the bills page each names its bill; on a bill's page that would repeat the page.
 */
export function RefundList({ refunds, showBill }: { refunds: readonly PortalRefund[]; showBill: boolean }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();

  return (
    <div className={styles.list}>
      {refunds.map((refund) => {
        const stage = REFUND_STAGE[refund.stage];
        const meta = [
          t('refunds.number', { number: refund.refundNumber }),
          showBill && refund.billNumber ? t('refunds.forBill', { number: refund.billNumber }) : null,
          refundReferenceText(refund, t),
        ].filter((part): part is string => part !== null);
        return (
          <RowCard key={refund.id}>
            <ListRow
              icon={<ArrowUndoRegular />}
              tone={stage.tone}
              title={`${f.inr(refund.amount)} · ${t(REFUND_PURPOSE[refund.purpose])}`}
              subtitle={refundStatusText(refund, t, f)}
              meta={meta.join(' · ')}
              chevron={false}
              trailing={<StatusPill tone={stage.tone}>{t(stage.key)}</StatusPill>}
            />
          </RowCard>
        );
      })}
    </div>
  );
}
