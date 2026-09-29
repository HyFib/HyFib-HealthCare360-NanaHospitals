import {
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DismissCircleRegular, DismissRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr, todayIso } from '@hc360/core';
import { ErrorState, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { ReasonDialog } from '@/modules/inpatient/ReasonDialog';
import { useLetterhead } from '@/modules/pharmacy/counter';
import { canteenApi, canteenKeys, canteenQueries, CUSTOMER_LABEL, hsnSacLabel, PAYMENT_LABEL } from './api';
import { printCanteenReceipt } from './print';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px', paddingBottom: '12px' },
  totals: { display: 'grid', gridTemplateColumns: '1fr auto', gap: '4px 16px', maxWidth: '320px', marginLeft: 'auto' },
  net: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightBold },
  muted: { color: tokens.colorNeutralForeground3 },
  right: { textAlign: 'end' },
});

/** One canteen bill: lines with GST, payment, reprint and same-day cancellation (stock and staff credit are reversed). */
export function SaleDrawer({ saleId, onClose }: { saleId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const canCancel = usePermission('canteen.sales.cancel');
  const sale = useQuery(canteenQueries.sale(saleId));
  const [cancelling, setCancelling] = useState(false);

  const cancel = useMutation({
    mutationFn: (reason: string) => canteenApi.cancelSale(saleId, reason),
    onSuccess: (s) => {
      notify.success(`Bill ${s.billNumber} cancelled`, s.paymentMode === 'StaffCredit' ? 'Stock restored and the staff credit reversed.' : 'Stock restored. Return the money to the customer.');
      setCancelling(false);
      void queryClient.invalidateQueries({ queryKey: canteenKeys.all });
    },
  });

  const s = sale.data;
  const sameDay = s ? s.businessDate === todayIso() : false;

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}>
          {s ? `Bill ${s.billNumber}` : 'Bill'}
        </DrawerHeaderTitle>
        {s && (
          <Caption1 className={styles.muted}>
            {formatDateTime(s.soldAt)} · {s.cashierName ?? ''}
          </Caption1>
        )}
      </DrawerHeader>
      <DrawerBody>
        {sale.error ? (
          <ErrorState error={sale.error} onRetry={() => void sale.refetch()} />
        ) : !s ? (
          <Spinner label="Loading the bill…" />
        ) : (
          <div className={styles.body}>
            <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
              <StatusBadge status={s.status} tone={s.status === 'Completed' ? 'success' : 'danger'} />
              <Text>
                {CUSTOMER_LABEL[s.customerType]}
                {s.employeeCode ? ` · ${s.employeeCode}` : ''}
                {s.employeeName || s.customerName ? ` · ${s.employeeName ?? s.customerName}` : ''}
              </Text>
            </div>
            {s.status === 'Cancelled' && (
              <MessageBar intent="warning">
                <MessageBarBody>
                  Cancelled {s.cancelledAt ? formatDateTime(s.cancelledAt) : ''}: {s.cancelReason}
                </MessageBarBody>
              </MessageBar>
            )}
            <Table size="small" aria-label="Bill lines">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>Item</TableHeaderCell>
                  <TableHeaderCell className={styles.right}>Qty</TableHeaderCell>
                  <TableHeaderCell className={styles.right}>Rate</TableHeaderCell>
                  <TableHeaderCell className={styles.right}>GST</TableHeaderCell>
                  <TableHeaderCell className={styles.right}>Amount</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {s.lines.map((l) => (
                  <TableRow key={l.menuItemId}>
                    <TableCell>
                      {l.itemName}
                      {l.hsnSac && (
                        <Caption1 block className={styles.muted}>
                          {hsnSacLabel(l.hsnSac)}
                        </Caption1>
                      )}
                    </TableCell>
                    <TableCell className={styles.right}>{l.quantity}</TableCell>
                    <TableCell className={styles.right}>{formatInr(l.unitPrice)}</TableCell>
                    <TableCell className={styles.right}>{l.gstRate}%</TableCell>
                    <TableCell className={styles.right}>{formatInr(l.lineTotal)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <div className={styles.totals}>
              <Text>Taxable value</Text>
              <Text className={styles.right}>{formatInr(s.taxableAmount)}</Text>
              <Text>CGST</Text>
              <Text className={styles.right}>{formatInr(s.cgstAmount)}</Text>
              <Text>SGST</Text>
              <Text className={styles.right}>{formatInr(s.sgstAmount)}</Text>
              <Text>Round off</Text>
              <Text className={styles.right}>{formatInr(s.roundOff)}</Text>
              <Text className={styles.net}>Total</Text>
              <Text className={mergeClasses(styles.net, styles.right)}>{formatInr(s.netAmount)}</Text>
            </div>
            <Text>
              Paid by {PAYMENT_LABEL[s.paymentMode]}
              {s.paymentReference ? ` · ${s.paymentReference}` : ''}
            </Text>
            {s.status === 'Completed' && !sameDay && <Caption1 className={styles.muted}>Bills can be cancelled only on the day they were made.</Caption1>}
          </div>
        )}
      </DrawerBody>
      <DrawerFooter>
        <Button
          icon={<PrintRegular />}
          disabled={!s}
          onClick={() => s && void printCanteenReceipt(s, letterhead, { duplicate: true }).catch((error: unknown) => notify.error(error, 'Printing failed'))}
        >
          Reprint
        </Button>
        {canCancel && s?.status === 'Completed' && sameDay && (
          <Button icon={<DismissCircleRegular />} onClick={() => setCancelling(true)}>
            Cancel bill
          </Button>
        )}
      </DrawerFooter>
      <ReasonDialog
        open={cancelling}
        title={s ? `Cancel bill ${s.billNumber}?` : 'Cancel bill?'}
        description={s ? `${formatInr(s.netAmount)} by ${PAYMENT_LABEL[s.paymentMode]}. Stock is put back${s.paymentMode === 'StaffCredit' ? ' and the staff credit is reversed' : '; return the money to the customer'}.` : undefined}
        minLength={5}
        placeholder="e.g. Wrong item billed, customer changed order"
        confirmLabel="Cancel bill"
        destructive
        busy={cancel.isPending}
        error={cancel.error}
        onConfirm={(reason) => cancel.mutate(reason)}
        onCancel={() => {
          setCancelling(false);
          cancel.reset();
        }}
      />
    </OverlayDrawer>
  );
}
