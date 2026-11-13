import {
  Button,
  Caption1,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  Select,
  Spinner,
  Subtitle2,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  ArrowUndoRegular,
  DismissCircleRegular,
  DismissRegular,
  MoneyRegular,
  PaymentRegular,
  PrintRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatInr, isApiError, todayIso } from '@hc360/core';
import {
  DataTable,
  ErrorState,
  LoadingState,
  StatusBadge,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { terminalDetailsLine } from '@/lib/terminal/labels';
import { TerminalSaleDialog } from '@/lib/terminal/TerminalSaleDialog';
import { toTerminalPaid, type TerminalPaid } from '@/lib/terminal/types';
import { approvalKeys } from '@/modules/approvals/api';
import { usePermission } from '@/lib/permissions';
import { dec, sum, toNumber } from '@/modules/inventory/decimal';
import { formatPercent, formatQty, istDateOf, parseNumber } from '@/modules/inventory/format';
import { gstStateName } from '@/modules/inventory/gst';
import { ReasonDialog } from '@/modules/inventory/ReasonDialog';
import { Detail, DetailGrid, ExpiryText, ScheduleBadge } from '@/modules/inventory/StockBits';
import {
  isApprovalRequest,
  pharmacyApi,
  pharmacyTerminalApi,
  pharmacyKeys,
  pharmacyQueries,
  paymentModeLabel,
  type PaymentMode,
  type PharmacySaleDetail,
  type PharmacySaleItemDto,
  type SaleReturnDto,
} from './api';
import { PHARMACY_PERMISSIONS as P, REFUNDS_APPROVE } from './permissions';
import { refundFor, refundSplit, refundWords } from './pricing';
import { printReceipt } from './print';
import { WaitingForApproval } from './WaitingForApproval';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '16px' },
  totals: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: '8px 16px' },
  muted: { color: tokens.colorNeutralForeground3 },
  net: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightBold },
  due: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  returnRow: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 110px 150px 110px',
    gap: '8px',
    alignItems: 'center',
    padding: '6px 0',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
});

function refundedByItem(returns: readonly SaleReturnDto[] | undefined): Map<string, number> {
  const totals = new Map<string, number>();
  for (const r of returns ?? []) {
    for (const item of r.items) {
      totals.set(item.saleItemId, toNumber(dec(totals.get(item.saleItemId) ?? 0) + dec(item.refundAmount)));
    }
  }
  return totals;
}

/**
 * Invoice detail with reprint, collection of dues, returns and same-day cancellation. A cancellation, and a return that
 * pays money out or writes stock off, are asked for here and wait for someone who approves refunds; whoever sold the
 * invoice or took its payment does not ask (the server refuses them too).
 */
export function SaleDrawer({ saleId, onClose }: { saleId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const letterhead = useLetterhead();
  const canCollect = usePermission(P.sales);
  const canReturn = usePermission(P.returns);
  const canApproveRefunds = usePermission(REFUNDS_APPROVE);
  const sale = useQuery(pharmacyQueries.sale(saleId));
  const returns = useQuery(pharmacyQueries.saleReturns(saleId));
  const [dialog, setDialog] = useState<'collect' | 'return' | 'cancel' | null>(null);

  const s = sale.data;
  const returnable = s ? s.items.filter((i) => i.quantity - i.returnedQuantity > 0) : [];
  const soldToday = s ? istDateOf(s.soldAt) === todayIso() : false;
  const hasReturns = s ? s.returnedAmount > 0 || s.items.some((i) => i.returnedQuantity > 0) : false;
  const ownSale = s?.ownSale === true;
  const cancellationWaits = s?.pending?.some((p) => p.kind === 'pharmacy.sale_cancel') === true;
  const returnWaits = s?.pending?.some((p) => p.kind === 'pharmacy.sale_return') === true;
  const notYours =
    'You sold this invoice or took its payment: another pharmacist asks for its cancellation or takes its returns.';

  const reprint = () => {
    if (s) {
      void printReceipt(s, letterhead, 'Duplicate copy').catch((error: unknown) =>
        notify.error(error, 'Printing failed'),
      );
    }
  };

  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={(_, d) => !d.open && onClose()}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          {s ? `Invoice ${s.invoiceNumber}` : 'Invoice'}
        </DrawerHeaderTitle>
        {s && (
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <StatusBadge status={s.status} />
            <Caption1>
              {formatDateTime(s.soldAt)} · {s.storeName} · {s.type === 'Prescription' ? 'Prescription' : 'Counter sale'}
            </Caption1>
          </div>
        )}
      </DrawerHeader>
      <DrawerBody>
        {sale.isLoading && <LoadingState />}
        {sale.error && <ErrorState error={sale.error} onRetry={() => void sale.refetch()} />}
        {s && (
          <div className={styles.stack}>
            <WaitingForApproval
              pending={s.pending}
              decidedBy="someone who approves refunds"
              canDecide={canApproveRefunds}
            />
            {s.status === 'Cancelled' && (
              <MessageBar intent="error">
                <MessageBarBody>
                  <MessageBarTitle>Cancelled</MessageBarTitle>
                  {s.cancelReason}
                </MessageBarBody>
              </MessageBar>
            )}
            <DetailGrid>
              <Detail label="Patient / customer">
                {s.customerName ?? 'Walk-in customer'}
                {s.patientUhid ? ` (${s.patientUhid})` : ''}
              </Detail>
              <Detail label="Prescriber">
                {s.prescriberName
                  ? `${s.prescriberName}${s.prescriberRegistrationNumber ? ` · Reg. ${s.prescriberRegistrationNumber}` : ''}`
                  : null}
              </Detail>
              <Detail label="Prescription">{s.prescriptionNumber}</Detail>
              <Detail label="Sold by">{s.soldBy}</Detail>
            </DetailGrid>

            <DataTable<PharmacySaleItemDto>
              ariaLabel="Invoice lines"
              rows={s.items}
              getRowId={(i) => i.id}
              size="small"
              columns={[
                {
                  key: 'item',
                  header: 'Medicine',
                  render: (i) => (
                    <span>
                      <Text weight="semibold">{i.itemName}</Text> <ScheduleBadge schedule={i.schedule} />
                      {i.isSubstitution && (
                        <Tooltip
                          content={`Substituted for ${i.prescribedDrugName ?? 'the prescribed drug'}: ${i.substitutionReason ?? ''}`}
                          relationship="description"
                        >
                          <Caption1 block className={styles.muted}>
                            Substitute for {i.prescribedDrugName}
                          </Caption1>
                        </Tooltip>
                      )}
                    </span>
                  ),
                },
                { key: 'batch', header: 'Batch', render: (i) => <Text font="monospace">{i.batchNumber}</Text> },
                { key: 'exp', header: 'Expiry', priority: 'low', render: (i) => <ExpiryText date={i.expiryDate} /> },
                {
                  key: 'qty',
                  header: 'Qty',
                  align: 'end',
                  render: (i) => (
                    <span>
                      {formatQty(i.quantity)}
                      {i.returnedQuantity > 0 && (
                        <Caption1 block className={styles.muted}>
                          {formatQty(i.returnedQuantity)} returned
                        </Caption1>
                      )}
                    </span>
                  ),
                },
                { key: 'mrp', header: 'MRP', align: 'end', priority: 'low', render: (i) => formatInr(i.unitMrp) },
                {
                  key: 'gst',
                  header: 'GST',
                  align: 'end',
                  priority: 'low',
                  render: (i) => (
                    <Tooltip
                      content={
                        i.igstAmount !== 0
                          ? `Taxable ${formatInr(i.taxableAmount)} · IGST ${formatInr(i.igstAmount)}`
                          : `Taxable ${formatInr(i.taxableAmount)} · CGST ${formatInr(i.cgstAmount)} · SGST ${formatInr(i.sgstAmount)}`
                      }
                      relationship="description"
                    >
                      <span>{formatPercent(i.gstRate)}</span>
                    </Tooltip>
                  ),
                },
                { key: 'net', header: 'Amount', align: 'end', render: (i) => formatInr(i.netAmount) },
              ]}
            />

            <div className={styles.totals}>
              <Detail label="MRP value">{formatInr(s.grossAmount)}</Detail>
              <Detail label={`Discount (${formatPercent(s.discountPercent)})`}>{formatInr(s.discountAmount)}</Detail>
              <Detail label="Taxable value">{formatInr(s.taxableAmount)}</Detail>
              {s.igstAmount !== 0 ? (
                <Detail label="IGST">{formatInr(s.igstAmount)}</Detail>
              ) : (
                <>
                  <Detail label="CGST">{formatInr(s.cgstAmount)}</Detail>
                  <Detail label="SGST">{formatInr(s.sgstAmount)}</Detail>
                </>
              )}
              {s.buyerGstin && (
                <Detail label="B2B buyer">
                  {s.buyerLegalName} · GSTIN {s.buyerGstin}
                  {s.placeOfSupply ? ` · place of supply ${gstStateName(s.placeOfSupply) ?? s.placeOfSupply}` : ''}
                </Detail>
              )}
              <Detail label="Round off">{formatInr(s.roundOff)}</Detail>
              <div>
                <Caption1 className={styles.muted}>Net amount</Caption1>
                <div className={styles.net}>{formatInr(s.netAmount)}</div>
              </div>
              <Detail label="Paid">{formatInr(s.paidAmount)}</Detail>
              <div>
                <Caption1 className={styles.muted}>Balance due</Caption1>
                <div className={s.dueAmount > 0 ? styles.due : undefined}>{formatInr(s.dueAmount)}</div>
              </div>
              {s.returnedAmount > 0 && <Detail label="Returned">{formatInr(s.returnedAmount)}</Detail>}
              {s.refundedAmount > 0 && <Detail label="Refunded">{formatInr(s.refundedAmount)}</Detail>}
            </div>

            <div>
              <Subtitle2 as="h3">Payments</Subtitle2>
              <DataTable
                ariaLabel="Payments"
                rows={s.payments}
                getRowId={(p) => p.id}
                size="small"
                columns={[
                  { key: 'at', header: 'When', render: (p) => formatDateTime(p.receivedAt) },
                  {
                    key: 'mode',
                    header: 'Mode',
                    render: (p) => `${p.isRefund ? 'Refund · ' : ''}${paymentModeLabel[p.mode]}`,
                  },
                  {
                    key: 'ref',
                    header: 'Reference',
                    render: (p) =>
                      [p.reference, terminalDetailsLine({ cardLast4: p.cardLast4, approvalCode: p.approvalCode })]
                        .filter(Boolean)
                        .join(' · ') || '—',
                  },
                  {
                    key: 'amount',
                    header: 'Amount',
                    align: 'end',
                    render: (p) => formatInr(p.isRefund ? -p.amount : p.amount),
                  },
                ]}
                emptyTitle="No payments"
              />
            </div>

            {(returns.data?.length ?? 0) > 0 && (
              <div>
                <Subtitle2 as="h3">Returns</Subtitle2>
                <DataTable<SaleReturnDto>
                  ariaLabel="Returns"
                  rows={returns.data}
                  getRowId={(r) => r.id}
                  size="small"
                  columns={[
                    { key: 'no', header: 'Return', render: (r) => <Text font="monospace">{r.returnNumber}</Text> },
                    { key: 'at', header: 'When', render: (r) => formatDateTime(r.returnedAt) },
                    {
                      key: 'items',
                      header: 'Items',
                      render: (r) =>
                        r.items
                          .map(
                            (i) => `${i.itemName} × ${formatQty(i.quantity)}${i.restocked ? '' : ' (not restocked)'}`,
                          )
                          .join(', '),
                    },
                    { key: 'reason', header: 'Reason', priority: 'low', render: (r) => r.reason },
                    {
                      key: 'refund',
                      header: 'Refund',
                      align: 'end',
                      render: (r) => (
                        <span>
                          {formatInr(r.refundAmount)}
                          {r.adjustedAgainstDue > 0 && (
                            <Caption1 block className={styles.muted}>
                              {formatInr(r.adjustedAgainstDue)} off the due
                            </Caption1>
                          )}
                        </span>
                      ),
                    },
                  ]}
                />
              </div>
            )}
          </div>
        )}
      </DrawerBody>
      <DrawerFooter>
        {s && (
          <>
            <Button icon={<PrintRegular />} onClick={reprint}>
              Print again
            </Button>
            {canCollect && s.status !== 'Cancelled' && s.dueAmount > 0 && (
              <Button appearance="primary" icon={<MoneyRegular />} onClick={() => setDialog('collect')}>
                Collect {formatInr(s.dueAmount)}
              </Button>
            )}
            {canReturn && s.status !== 'Cancelled' && returnable.length > 0 && (
              <Tooltip
                relationship="description"
                content={
                  ownSale
                    ? notYours
                    : returnWaits
                      ? 'A return of this invoice is waiting for approval.'
                      : 'Takes medicines back; money paid back, or stock not restocked, waits for approval'
                }
              >
                <Button
                  icon={<ArrowUndoRegular />}
                  disabled={ownSale || returnWaits}
                  onClick={() => setDialog('return')}
                >
                  Return items
                </Button>
              </Tooltip>
            )}
            {canReturn && s.status !== 'Cancelled' && (
              <Tooltip
                relationship="description"
                content={
                  ownSale
                    ? notYours
                    : !soldToday
                      ? "Only today's invoices can be cancelled. Use a return instead."
                      : hasReturns
                        ? 'Invoices with returns cannot be cancelled.'
                        : cancellationWaits
                          ? 'A cancellation of this invoice is waiting for approval.'
                          : 'Asks for approval to void the invoice, pay the money back the way it was paid and put the stock back'
                }
              >
                <Button
                  icon={<DismissCircleRegular />}
                  disabled={ownSale || !soldToday || hasReturns || cancellationWaits}
                  onClick={() => setDialog('cancel')}
                >
                  Cancel invoice
                </Button>
              </Tooltip>
            )}
          </>
        )}
      </DrawerFooter>

      {s && dialog === 'collect' && <CollectDialog sale={s} onClose={() => setDialog(null)} />}
      {s && dialog === 'return' && <ReturnDialog sale={s} returns={returns.data} onClose={() => setDialog(null)} />}
      {s && dialog === 'cancel' && <CancelSale sale={s} onClose={() => setDialog(null)} />}
    </OverlayDrawer>
  );
}

function useSaleRefresh(saleId: string) {
  const queryClient = useQueryClient();
  return () => {
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.sales });
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.dashboard });
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.stock });
    void queryClient.invalidateQueries({ queryKey: pharmacyKeys.queue });
    void queryClient.invalidateQueries({ queryKey: ['pharmacy', 'sales', 'detail', saleId] });
    void queryClient.invalidateQueries({ queryKey: approvalKeys.all });
  };
}

function CollectDialog({ sale, onClose }: { sale: PharmacySaleDetail; onClose: () => void }) {
  const notify = useNotify();
  const refresh = useSaleRefresh(sale.id);
  const [mode, setMode] = useState<Exclude<PaymentMode, 'Credit'>>('Cash');
  const [amount, setAmount] = useState(String(sale.dueAmount));
  const [reference, setReference] = useState('');
  const [terminal, setTerminal] = useState<TerminalPaid | null>(null);
  const [terminalOpen, setTerminalOpen] = useState(false);
  const value = terminal ? terminal.amount : (parseNumber(amount) ?? 0);
  const needsReference = mode !== 'Cash' && !terminal;
  const valid = value > 0 && value <= sale.dueAmount && (!needsReference || reference.trim().length > 0);
  const collect = useMutation({
    mutationFn: () =>
      pharmacyApi.collectPayment(sale.id, {
        mode,
        amount: value,
        reference: terminal ? null : reference.trim() || null,
        terminalSaleId: terminal?.id ?? null,
      }),
    onSuccess: () => {
      notify.success(`${formatInr(value)} collected`, `Invoice ${sale.invoiceNumber}`);
      refresh();
      onClose();
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ mode, amount, reference, terminal }),
    busy: collect.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid) {
              collect.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Collect balance due</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Text>
                {sale.customerName ?? 'Customer'} owes {formatInr(sale.dueAmount)} on invoice {sale.invoiceNumber}.
              </Text>
              <Field label="Paid by">
                <Select value={mode} disabled={terminal !== null} onChange={(_, d) => setMode(d.value as typeof mode)}>
                  <option value="Cash">Cash</option>
                  <option value="Card">Card</option>
                  <option value="Upi">UPI</option>
                </Select>
              </Field>
              <Field
                label="Amount"
                required
                validationMessage={value > sale.dueAmount ? `At most ${formatInr(sale.dueAmount)}` : undefined}
              >
                <Input
                  type="number"
                  contentBefore="₹"
                  value={terminal ? String(terminal.amount) : amount}
                  disabled={terminal !== null}
                  onChange={(_, d) => setAmount(d.value)}
                  autoFocus
                />
              </Field>
              {needsReference && (
                <Field
                  label={mode === 'Card' ? 'RRN or approval code' : 'UPI reference (UTR)'}
                  hint={mode === 'Card' ? 'From the charge slip' : 'The 12-digit number on the payer’s screen'}
                  required
                >
                  <Input value={reference} maxLength={64} onChange={(_, d) => setReference(d.value)} />
                </Field>
              )}
              {mode !== 'Cash' && !terminal && (
                <Button
                  icon={<PaymentRegular />}
                  disabled={!(value > 0) || value > sale.dueAmount}
                  onClick={() => setTerminalOpen(true)}
                >
                  Take on terminal
                </Button>
              )}
              {terminal && (
                <Field
                  label="Paid on the terminal"
                  hint="Removing it does not return the money: it stays on the terminal and can be used again from “Already paid on the terminal”."
                >
                  <Text>{terminalDetailsLine(terminal) || 'Approved'}</Text>
                  <Button size="small" appearance="subtle" onClick={() => setTerminal(null)}>
                    Remove
                  </Button>
                </Field>
              )}
              {terminalOpen && (
                <TerminalSaleDialog
                  open
                  api={pharmacyTerminalApi}
                  counter="pharmacy"
                  amount={value}
                  initialMode={mode === 'Upi' ? 'Upi' : 'Card'}
                  patientId={sale.patientId}
                  maxAmount={sale.dueAmount}
                  exact={false}
                  approveLabel="Use this payment"
                  onClose={() => setTerminalOpen(false)}
                  onApproved={(paid) => {
                    setTerminal(toTerminalPaid(paid));
                    setMode(paid.mode);
                    setAmount(String(paid.amount));
                    setTerminalOpen(false);
                  }}
                />
              )}
              {collect.error && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    {isApiError(collect.error) ? collect.error.userMessage : 'Could not record the payment.'}
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={collect.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || collect.isPending}
                icon={collect.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record payment
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

interface ReturnLine {
  quantity: string;
  restock: boolean;
}

function ReturnDialog({
  sale,
  returns,
  onClose,
}: {
  sale: PharmacySaleDetail;
  returns: SaleReturnDto[] | undefined;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const refresh = useSaleRefresh(sale.id);
  const today = todayIso();
  const refunded = refundedByItem(returns);
  const items = sale.items.filter((i) => i.quantity - i.returnedQuantity > 0);
  const [lines, setLines] = useState<Record<string, ReturnLine>>({});
  const [reason, setReason] = useState('');

  const planned = items
    .map((item) => {
      const line = lines[item.id];
      const quantity = parseNumber(line?.quantity ?? '') ?? 0;
      const max = toNumber(dec(item.quantity) - dec(item.returnedQuantity));
      const expired = item.expiryDate !== null && item.expiryDate < today;
      const restock = !expired && (line?.restock ?? true);
      const refund =
        quantity > 0 && quantity <= max
          ? refundFor(item.netAmount, item.quantity, item.returnedQuantity, refunded.get(item.id) ?? 0, quantity)
          : 0;
      return { item, quantity, max, expired, restock, refund, invalid: quantity < 0 || quantity > max };
    })
    .filter((p) => p.quantity !== 0 || lines[p.item.id]);
  const chosen = planned.filter((p) => p.quantity > 0 && !p.invalid);
  const totalRefund = toNumber(sum(chosen.map((p) => dec(p.refund))));
  const againstDue = Math.min(totalRefund, sale.dueAmount);
  const payBack = toNumber(dec(totalRefund) - dec(againstDue));
  // The money goes back the way the sale was paid, as the server will pay it: a card payment to the card, never cash.
  const paidBack = refundSplit(sale.payments, payBack);
  const writesOff = chosen.some((p) => !p.restock);
  const waits = payBack > 0 || writesOff;
  const invalid = planned.some((p) => p.invalid);
  const valid = chosen.length > 0 && !invalid && reason.trim().length >= 3;

  const submit = useMutation({
    mutationFn: () =>
      pharmacyApi.returnSale(sale.id, {
        lines: chosen.map((p) => ({ saleItemId: p.item.id, quantity: p.quantity, restock: p.restock })),
        reason: reason.trim(),
      }),
    onSuccess: (result) => {
      if (isApprovalRequest(result)) {
        notify.success(
          'Asked for approval',
          `The return waits for someone who approves refunds. When approved: ${refundWords(paidBack)}${againstDue > 0 ? `; ${formatInr(againstDue)} off the balance due` : ''}${writesOff ? '; what is not restocked is written off' : ''}.`,
        );
      } else {
        notify.success(`Return ${result.returnNumber} recorded`, 'The refund was taken off the balance due.');
      }
      refresh();
      onClose();
    },
  });

  const setLine = (id: string, patch: Partial<ReturnLine>) =>
    setLines((prev) => ({
      ...prev,
      [id]: { quantity: prev[id]?.quantity ?? '', restock: prev[id]?.restock ?? true, ...patch },
    }));

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ lines, reason }),
    busy: submit.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 820, width: '95vw' }}>
        <DialogBody>
          <DialogTitle>Return items from {sale.invoiceNumber}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Caption1>
              Enter what is being returned. Unopened, in-date packs go back on the shelf; expired or damaged ones are
              refunded but written off. The refund first clears any balance due; the rest goes back the way the sale was
              paid. Money paid back, or anything written off, waits for someone who approves refunds.
            </Caption1>
            <div>
              <div className={styles.returnRow}>
                <Caption1 className={styles.muted}>Medicine</Caption1>
                <Caption1 className={styles.muted}>Return qty</Caption1>
                <Caption1 className={styles.muted}>Back on shelf</Caption1>
                <Caption1 className={styles.muted}>Refund</Caption1>
              </div>
              {items.map((item, index) => {
                const p = planned.find((x) => x.item.id === item.id);
                const max = toNumber(dec(item.quantity) - dec(item.returnedQuantity));
                const expired = item.expiryDate !== null && item.expiryDate < today;
                return (
                  <div key={item.id} className={styles.returnRow}>
                    <span>
                      <Text weight="semibold">{item.itemName}</Text>
                      <Caption1 block className={styles.muted}>
                        Batch {item.batchNumber} · up to {formatQty(max)}
                      </Caption1>
                    </span>
                    <Field
                      validationState={p?.invalid ? 'error' : 'none'}
                      validationMessage={p?.invalid ? `Max ${formatQty(max)}` : undefined}
                    >
                      <Input
                        type="number"
                        min={0}
                        max={max}
                        aria-label={`Quantity of ${item.itemName} returned`}
                        value={lines[item.id]?.quantity ?? ''}
                        onChange={(_, d) => setLine(item.id, { quantity: d.value })}
                        autoFocus={index === 0}
                      />
                    </Field>
                    {expired ? (
                      <Caption1>Expired: not restocked</Caption1>
                    ) : (
                      <Checkbox
                        label="Restock"
                        checked={lines[item.id]?.restock ?? true}
                        onChange={(_, d) => setLine(item.id, { restock: d.checked === true })}
                      />
                    )}
                    <Text>{p && p.quantity > 0 && !p.invalid ? formatInr(p.refund) : '—'}</Text>
                  </div>
                );
              })}
            </div>
            <Field label="Reason" required>
              <Input
                value={reason}
                maxLength={300}
                onChange={(_, d) => setReason(d.value)}
                placeholder="e.g. Doctor changed the prescription, patient discharged"
              />
            </Field>
            {chosen.length > 0 && (
              <MessageBar intent={waits ? 'warning' : 'info'} layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>{waits ? 'Waits for approval' : 'Made at once'}</MessageBarTitle>
                  Refund {formatInr(totalRefund)}
                  {againstDue > 0 ? ` · ${formatInr(againstDue)} clears the balance due` : ''}. How it goes back:{' '}
                  {refundWords(paidBack)}.{writesOff ? ' What is not restocked is written off.' : ''}
                  {waits ? ' Nothing is paid back or written off until someone who approves refunds approves it.' : ''}
                </MessageBarBody>
              </MessageBar>
            )}
            {submit.error && (
              <MessageBar intent="error">
                <MessageBarBody>
                  {isApiError(submit.error) ? submit.error.userMessage : 'Could not record the return.'}
                </MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={guard.requestClose} disabled={submit.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!valid || submit.isPending}
              icon={submit.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              onClick={() => submit.mutate()}
            >
              {waits ? 'Ask for approval' : 'Record return'}
            </Button>
          </DialogActions>
        </DialogBody>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

function CancelSale({ sale, onClose }: { sale: PharmacySaleDetail; onClose: () => void }) {
  const notify = useNotify();
  const refresh = useSaleRefresh(sale.id);
  // Every payment goes back whole, the way it was paid: a card payment to the card, UPI by UPI, cash as cash.
  const paidBack = refundSplit(sale.payments, toNumber(dec(sale.paidAmount) - dec(sale.refundedAmount)));
  const cancel = useMutation({
    mutationFn: (reason: string) => pharmacyApi.cancelSale(sale.id, reason),
    onSuccess: () => {
      notify.success(
        'Asked for approval',
        `Cancelling invoice ${sale.invoiceNumber} waits for someone who approves refunds. When approved: ${refundWords(paidBack)}.`,
      );
      refresh();
      onClose();
    },
  });
  return (
    <ReasonDialog
      title={`Ask to cancel invoice ${sale.invoiceNumber}?`}
      confirmLabel="Ask for approval"
      destructive
      busy={cancel.isPending}
      error={cancel.error}
      placeholder="e.g. Billed to the wrong patient"
      onSubmit={(reason) => cancel.mutate(reason)}
      onClose={onClose}
    >
      {`Someone who approves refunds decides; until then nothing changes. When approved: ${refundWords(paidBack)}; the stock goes back to the original batches${sale.type === 'Prescription' ? ' and the prescription returns to the queue' : ''}. It must be approved today: after midnight, take a return instead.`}
    </ReasonDialog>
  );
}
