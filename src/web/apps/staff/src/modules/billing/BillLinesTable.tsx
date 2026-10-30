import { Badge, Button, Caption1, makeStyles, Text, tokens, Tooltip } from '@fluentui/react-components';
import { ArrowUndoRegular, DeleteRegular } from '@fluentui/react-icons';
import { formatDate, formatInr } from '@hc360/core';
import { DataTable, type Column } from '@hc360/ui';
import type { BillDetail, BillLineDto } from './api';
import { deviceDetailsText } from './device';
import { SERVICE_CATEGORY_LABEL } from './labels';
import { formatPercent, fromPaise, toPaise } from './money';
import { Amount } from './ui';

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  service: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  name: { display: 'inline-flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
});

function discountOf(line: BillLineDto): number {
  return fromPaise(
    toPaise(line.tariffDiscountAmount) + toPaise(line.lineDiscountAmount) + toPaise(line.billDiscountShare),
  );
}

/**
 * Services on a bill. Returns (negative lines, e.g. ward stock sent back to pharmacy) are labelled as returns and
 * shown with a minus sign, never hidden or netted away; GST is shown per line with its CGST/SGST split.
 */
export function BillLinesTable({
  bill,
  onRemove,
  removing,
}: {
  bill: BillDetail;
  onRemove?: (line: BillLineDto) => void;
  removing?: string | null;
}) {
  const styles = useStyles();
  const hasPackage = bill.lines.some((line) => line.packageCoveredAmount !== 0);
  const hasTax = bill.lines.some((line) => line.taxRate > 0);

  const columns: Column<BillLineDto>[] = [
    {
      key: 'service',
      header: 'Service',
      // The amounts have widths of their own: without a minimum, where they fill the table's box (OP billing beside
      // the payment panel in a 1,280 px window) the service was left no width at all.
      minWidth: 180,
      render: (line) => (
        <span className={styles.service}>
          <span className={styles.name}>
            <Text weight="semibold">{line.description}</Text>
            {line.isReturn && (
              <Badge size="small" appearance="tint" color="danger" icon={<ArrowUndoRegular />}>
                Return
              </Badge>
            )}
            {!line.isPayerPayable && bill.payerId && (
              <Tooltip
                content="The insurer does not pay for this item; the patient pays it."
                relationship="description"
              >
                <Badge size="small" appearance="outline" color="warning">
                  Patient pays
                </Badge>
              </Tooltip>
            )}
          </span>
          <Caption1 className={styles.muted}>
            {line.serviceCode} · {SERVICE_CATEGORY_LABEL[line.category]}
            {line.practitionerName ? ` · ${line.practitionerName}` : ''} · {formatDate(line.serviceDate)}
            {line.sacCode ? ` · SAC ${line.sacCode}` : ''}
          </Caption1>
          {line.device && <Caption1 className={styles.muted}>{deviceDetailsText(line.device)}</Caption1>}
        </span>
      ),
    },
    {
      key: 'qty',
      header: 'Qty',
      align: 'end',
      width: 70,
      render: (line) => line.quantity.toLocaleString('en-IN', { maximumFractionDigits: 3 }),
    },
    {
      key: 'rate',
      header: 'Rate',
      align: 'end',
      width: 110,
      priority: 'low',
      render: (line) => <Amount value={line.unitPrice} />,
    },
    {
      key: 'amount',
      header: 'Amount',
      align: 'end',
      width: 120,
      render: (line) => <Amount value={line.grossAmount} />,
    },
  ];
  if (hasPackage) {
    columns.push({
      key: 'package',
      header: 'Package covers',
      align: 'end',
      width: 130,
      priority: 'low',
      render: (line) => <Amount value={line.packageCoveredAmount} tone="muted" />,
    });
  }
  columns.push({
    key: 'discount',
    header: 'Discount',
    align: 'end',
    width: 110,
    render: (line) => {
      const discount = discountOf(line);
      return discount === 0 ? (
        <Text className={styles.muted}>—</Text>
      ) : (
        <Tooltip
          relationship="description"
          content={`Tariff ${formatInr(line.tariffDiscountAmount)} · on the service ${formatInr(line.lineDiscountAmount)} · share of bill discount ${formatInr(line.billDiscountShare)}`}
        >
          <span>
            <Amount value={discount} />
          </span>
        </Tooltip>
      );
    },
  });
  if (hasTax) {
    columns.push({
      key: 'gst',
      header: 'GST',
      align: 'end',
      width: 130,
      priority: 'low',
      render: (line) =>
        line.taxRate > 0 ? (
          <Tooltip
            relationship="description"
            content={
              line.igstAmount !== 0
                ? `IGST ${formatInr(line.igstAmount)} on ${formatInr(line.taxableAmount)}`
                : `CGST ${formatInr(line.cgstAmount)} + SGST ${formatInr(line.sgstAmount)} on ${formatInr(line.taxableAmount)}`
            }
          >
            <span>
              {formatPercent(line.taxRate)} ·{' '}
              <Amount
                value={fromPaise(toPaise(line.cgstAmount) + toPaise(line.sgstAmount) + toPaise(line.igstAmount))}
              />
            </span>
          </Tooltip>
        ) : (
          <Text className={styles.muted}>Exempt</Text>
        ),
    });
  }
  columns.push({
    key: 'net',
    header: 'Net',
    align: 'end',
    width: 120,
    render: (line) => <Amount value={line.netAmount} strong tone={line.isReturn ? 'negative' : undefined} />,
  });

  return (
    <DataTable
      ariaLabel="Services on this bill"
      rows={bill.lines}
      getRowId={(line) => line.id}
      columns={columns}
      size="small"
      emptyTitle="No services on this bill yet"
      emptyDescription={
        bill.status === 'Draft'
          ? 'Add a service below, or refresh the bill to pick up services charged since it was started.'
          : undefined
      }
      rowActions={
        onRemove
          ? (line) => (
              <Button
                size="small"
                appearance="subtle"
                icon={<DeleteRegular />}
                aria-label={`Remove ${line.description}`}
                disabled={removing === line.id}
                onClick={() => onRemove(line)}
              />
            )
          : undefined
      }
    />
  );
}
