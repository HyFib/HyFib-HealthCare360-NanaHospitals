import { Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { InfoRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import type { DocumentTotals } from '@/modules/inventory/gst';
import type { GstSplit } from './gstPreview';

const useStyles = makeStyles({
  note: {
    display: 'flex',
    gap: '10px',
    alignItems: 'flex-start',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  icon: { color: tokens.colorBrandForeground1, fontSize: '20px', flexShrink: 0, marginTop: '2px' },
  rows: { display: 'grid', gridTemplateColumns: '1fr auto', gap: '4px 16px', alignItems: 'baseline' },
  right: { textAlign: 'end', fontVariantNumeric: 'tabular-nums' },
  net: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightBold },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Why this document carries CGST + SGST or IGST, in one sentence. */
export function GstSplitNote({ split }: { split: GstSplit }) {
  const styles = useStyles();
  return (
    <div className={styles.note} role="note">
      <InfoRegular className={styles.icon} aria-hidden />
      <div>
        <Text weight="semibold">GST: {split.headline}</Text>
        <Caption1 block>{split.detail}</Caption1>
      </div>
    </div>
  );
}

/** Totals block for purchase documents (preview or server figures), showing CGST/SGST or IGST as the split says. */
export function PurchaseTotals({ totals, interState, preview }: { totals: DocumentTotals; interState: boolean; preview?: boolean }) {
  const styles = useStyles();
  const showIgst = interState || totals.igst !== 0;
  const showCgst = !interState || totals.cgst !== 0 || totals.sgst !== 0;
  return (
    <div className={styles.rows} aria-live="polite">
      <Text>Gross (rate × quantity)</Text>
      <Text className={styles.right}>{formatInr(totals.gross)}</Text>
      {totals.discount !== 0 && (
        <>
          <Text>Trade discount</Text>
          <Text className={styles.right}>−{formatInr(totals.discount)}</Text>
        </>
      )}
      <Text>Taxable value</Text>
      <Text className={styles.right}>{formatInr(totals.taxable)}</Text>
      {showCgst && (
        <>
          <Text>CGST</Text>
          <Text className={styles.right}>{formatInr(totals.cgst)}</Text>
          <Text>SGST</Text>
          <Text className={styles.right}>{formatInr(totals.sgst)}</Text>
        </>
      )}
      {showIgst && (
        <>
          <Text>IGST</Text>
          <Text className={styles.right}>{formatInr(totals.igst)}</Text>
        </>
      )}
      {totals.roundOff !== 0 && (
        <>
          <Caption1 className={styles.muted}>Round off</Caption1>
          <Caption1 className={`${styles.muted} ${styles.right}`}>{formatInr(totals.roundOff)}</Caption1>
        </>
      )}
      <Text weight="semibold">{preview ? 'Estimated total' : 'Total'}</Text>
      <span className={`${styles.net} ${styles.right}`}>{formatInr(totals.net)}</span>
    </div>
  );
}
