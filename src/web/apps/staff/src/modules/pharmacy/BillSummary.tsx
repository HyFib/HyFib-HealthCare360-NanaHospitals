import { Caption1, Divider, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { formatInr } from '@hc360/core';
import { formatPercent } from '@/modules/inventory/format';
import type { SaleTotals } from './pricing';

const useStyles = makeStyles({
  rows: { display: 'grid', gridTemplateColumns: '1fr auto', gap: '4px 12px', alignItems: 'baseline' },
  muted: { color: tokens.colorNeutralForeground3 },
  net: { fontSize: tokens.fontSizeHero700, lineHeight: tokens.lineHeightHero700, fontWeight: tokens.fontWeightBold, color: tokens.colorBrandForeground1 },
  right: { textAlign: 'end', fontVariantNumeric: 'tabular-nums' },
});

/**
 * The bill as it will be printed: MRP value, discount, taxable value with CGST and SGST (GST is included in
 * MRP), round-off and the amount to collect.
 */
export function BillSummary({ totals, discountPercent, label = 'To collect' }: { totals: SaleTotals; discountPercent: number; label?: string }) {
  const styles = useStyles();
  return (
    <div className={styles.rows} aria-live="polite">
      <Text>MRP value</Text>
      <Text className={styles.right}>{formatInr(totals.gross)}</Text>
      {totals.discount > 0 && (
        <>
          <Text>Discount ({formatPercent(discountPercent)})</Text>
          <Text className={styles.right}>−{formatInr(totals.discount)}</Text>
        </>
      )}
      {totals.gstKnown ? (
        <>
          <Caption1 className={styles.muted}>Taxable value</Caption1>
          <Caption1 className={mergeClasses(styles.muted, styles.right)}>{formatInr(totals.taxable)}</Caption1>
          <Caption1 className={styles.muted}>CGST (included)</Caption1>
          <Caption1 className={mergeClasses(styles.muted, styles.right)}>{formatInr(totals.cgst)}</Caption1>
          <Caption1 className={styles.muted}>SGST (included)</Caption1>
          <Caption1 className={mergeClasses(styles.muted, styles.right)}>{formatInr(totals.sgst)}</Caption1>
        </>
      ) : (
        <Caption1 className={styles.muted} style={{ gridColumn: '1 / -1' }}>
          GST (CGST + SGST) is included in the MRP and itemised on the invoice.
        </Caption1>
      )}
      {totals.roundOff !== 0 && (
        <>
          <Caption1 className={styles.muted}>Round off</Caption1>
          <Caption1 className={mergeClasses(styles.muted, styles.right)}>{formatInr(totals.roundOff)}</Caption1>
        </>
      )}
      <Divider style={{ gridColumn: '1 / -1' }} />
      <Text weight="semibold">{label}</Text>
      <span className={mergeClasses(styles.net, styles.right)}>{formatInr(totals.net)}</span>
    </div>
  );
}
