import { Caption1, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { formatInr } from '@hc360/core';
import type { BillDetail } from './api';
import { formatPercent, fromPaise, toPaise } from './money';
import { Amount, SummaryList, SummaryRow } from './ui';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '14px' },
  hero: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '14px 16px',
    borderRadius: tokens.borderRadiusLarge,
    backgroundColor: tokens.colorBrandBackground2,
    border: `1px solid ${tokens.colorBrandStroke2}`,
  },
  heroPaid: {
    backgroundColor: tokens.colorPaletteGreenBackground1,
    border: `1px solid ${tokens.colorPaletteGreenBorder1}`,
  },
  heroLabel: { textTransform: 'uppercase', letterSpacing: '0.5px', color: tokens.colorNeutralForeground2 },
  heroValue: {
    fontSize: '40px',
    lineHeight: '46px',
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorNeutralForeground1,
    fontVariantNumeric: 'tabular-nums',
  },
  muted: { color: tokens.colorNeutralForeground3 },
  gst: {
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
});

const nonZero = (value: number) => toPaise(value) !== 0n;

/** What the counter must collect, in large type, with the full breakup (discounts, GST split, payer share, payments). */
export function BillTotalsCard({ bill }: { bill: BillDetail }) {
  const styles = useStyles();
  const staffDiscount = fromPaise(toPaise(bill.lineDiscountAmount) + toPaise(bill.billDiscountAmount));
  const isDraft = bill.status === 'Draft';
  const cancelled = bill.status === 'Cancelled' || bill.status === 'Refunded';
  const settled = !isDraft && !cancelled && bill.dueAmount === 0;

  const heroLabel = isDraft
    ? bill.payerAmount > 0
      ? 'Payable by patient'
      : 'Amount payable'
    : cancelled
      ? bill.refundableAmount > 0
        ? 'To refund'
        : 'Cancelled'
      : bill.refundableAmount > 0
        ? 'Refundable to patient'
        : settled
          ? 'Fully paid'
          : 'Balance due';
  const heroValue = isDraft ? bill.patientAmount : bill.refundableAmount > 0 ? bill.refundableAmount : bill.dueAmount;

  return (
    <div className={styles.root}>
      <div className={mergeClasses(styles.hero, settled && styles.heroPaid)} aria-live="polite">
        <Caption1 className={styles.heroLabel}>{heroLabel}</Caption1>
        <span className={styles.heroValue}>{formatInr(heroValue)}</span>
        {bill.payerAmount > 0 && (
          <Caption1>
            {bill.payerName ?? 'Insurer'} pays <Amount value={bill.payerAmount} strong />
            {bill.coPayPercent > 0 ? ` · co-pay ${formatPercent(bill.coPayPercent)}` : ''}
          </Caption1>
        )}
      </div>

      <SummaryList>
        <SummaryRow label="Services">
          <Amount value={bill.grossAmount} />
        </SummaryRow>
        {nonZero(bill.packageCoveredAmount) && (
          <SummaryRow label="Covered by package">
            <Amount value={-bill.packageCoveredAmount} />
          </SummaryRow>
        )}
        {nonZero(bill.tariffDiscountAmount) && (
          <SummaryRow label="Tariff concession">
            <Amount value={-bill.tariffDiscountAmount} />
          </SummaryRow>
        )}
        {nonZero(staffDiscount) && (
          <SummaryRow label="Discount" hint={bill.discountReason ?? undefined}>
            <Amount value={-staffDiscount} />
          </SummaryRow>
        )}
        {nonZero(bill.taxAmount) && (
          <div className={styles.gst}>
            <SummaryList>
              <SummaryRow label="Taxable value">
                <Amount value={bill.taxableAmount} />
              </SummaryRow>
              <SummaryRow label="CGST">
                <Amount value={bill.cgstAmount} />
              </SummaryRow>
              <SummaryRow label="SGST">
                <Amount value={bill.sgstAmount} />
              </SummaryRow>
            </SummaryList>
          </div>
        )}
        {nonZero(bill.roundOffAmount) && (
          <SummaryRow label="Round off" hint="The patient's share is rounded to the rupee">
            <Amount value={bill.roundOffAmount} />
          </SummaryRow>
        )}
        <SummaryRow label="Bill total" strong>
          <Amount value={bill.netAmount} strong />
        </SummaryRow>
        {bill.payerAmount > 0 && (
          <>
            <SummaryRow
              label={`Payable by ${bill.payerName ?? 'insurer'}`}
              hint={
                bill.nonPayableAmount > 0
                  ? `Items the insurer does not pay: ${formatInr(bill.nonPayableAmount)}`
                  : undefined
              }
            >
              <Amount value={bill.payerAmount} />
            </SummaryRow>
            <SummaryRow label="Payable by patient">
              <Amount value={bill.patientAmount} strong />
            </SummaryRow>
          </>
        )}
        {!isDraft && (
          <>
            {nonZero(bill.creditAmount) && (
              <SummaryRow label="Credit notes and write-offs">
                <Amount value={-bill.creditAmount} />
              </SummaryRow>
            )}
            <SummaryRow label="Paid">
              <Amount value={bill.paidAmount} />
            </SummaryRow>
            {nonZero(bill.refundedAmount) && (
              <SummaryRow label="Refunded">
                <Amount value={-bill.refundedAmount} />
              </SummaryRow>
            )}
          </>
        )}
      </SummaryList>
      {bill.isTaxInvoice && (
        <Text className={styles.muted}>
          GST is charged on the discounted value and split equally into CGST and SGST.
        </Text>
      )}
    </div>
  );
}
