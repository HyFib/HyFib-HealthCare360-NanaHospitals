import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  Menu,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  SplitButton,
  Text,
  Textarea,
  tokens,
  type MenuButtonProps,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, DoorArrowRightRegular, LinkRegular, PaymentRegular, PrintRegular, WalletRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr, todayIso, useHotkey } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { billingApi, billingQueries, type BillDetail, type PatientAdvanceDto, type ReceiptDto } from './api';
import { useAttemptKey, useBillingPrint, useRefreshBilling } from './hooks';
import { fromPaise, minBig, toPaise } from './money';
import { PaymentLinkDialog } from './PaymentLinkDialog';
import { PaymentPanel } from './PaymentPanel';
import { checkPaymentLine, newPaymentLine, summariseSplit, type PaymentLineDraft } from './paymentRules';
import { PosSaleDialog } from './PosSaleDialog';
import { OpenShiftDialog } from './ShiftControls';
import { ActionError, KeyHint } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '14px' },
  head: { display: 'flex', justifyContent: 'space-between', alignItems: 'flex-end', gap: '16px', flexWrap: 'wrap' },
  dueLabel: { color: tokens.colorNeutralForeground3, textTransform: 'uppercase', letterSpacing: '0.5px' },
  due: { fontSize: '40px', lineHeight: '46px', fontWeight: tokens.fontWeightBold, color: tokens.colorBrandForeground1 },
  advance: { display: 'flex', alignItems: 'center', gap: '12px', flexWrap: 'wrap' },
  done: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '6px', padding: '8px 0', textAlign: 'center' },
  change: { fontSize: '44px', lineHeight: '52px', fontWeight: tokens.fontWeightBold, color: tokens.colorPaletteGreenForeground1 },
  muted: { color: tokens.colorNeutralForeground3 },
  alternatives: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
});

/** Patient advances still available (balance less refunds awaiting payout). */
function usableAdvances(advances: PatientAdvanceDto[] | undefined): { advance: PatientAdvanceDto; available: number }[] {
  return (advances ?? [])
    .map((advance) => ({ advance, available: fromPaise(toPaise(advance.balance) - toPaise(advance.refundPending)) }))
    .filter((a) => a.available > 0);
}

/**
 * Collects the balance of a finalized bill (OPB-05/06): patient advances first, then cash / card / UPI / cheque in any
 * split, a card terminal or a payment link. Shows the change to return, records one receipt and prints it on the
 * counter printer.
 */
export function CollectPaymentDialog({
  bill,
  open,
  onClose,
  onCollected,
}: {
  bill: BillDetail;
  open: boolean;
  onClose: () => void;
  onCollected?: (receipt: ReceiptDto) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const refresh = useRefreshBilling();
  const { prefs, printReceipt, printReceiptById, printBill } = useBillingPrint();
  const canShift = usePermission('billing.shifts');
  const [attemptKey, renewAttempt] = useAttemptKey();
  const [adjustKey, renewAdjustKey] = useAttemptKey();
  const [lines, setLines] = useState<PaymentLineDraft[]>(() => [newPaymentLine('Cash', bill.dueAmount)]);
  const [cashTendered, setCashTendered] = useState('');
  const [notes, setNotes] = useState('');
  const [showErrors, setShowErrors] = useState(false);
  const [done, setDone] = useState<{ receipt: ReceiptDto; change: number | null } | null>(null);
  const [side, setSide] = useState<'pos' | 'link' | 'shift' | null>(null);
  const today = todayIso();

  // Start again with the full balance whenever the dialog opens or the balance changes (an advance was applied).
  const [seen, setSeen] = useState({ open, due: bill.dueAmount });
  if (seen.open !== open || seen.due !== bill.dueAmount) {
    setSeen({ open, due: bill.dueAmount });
    if (open) {
      setLines([newPaymentLine('Cash', bill.dueAmount)]);
      setCashTendered('');
      setShowErrors(false);
      if (seen.open !== open) {
        setNotes('');
        setDone(null);
      }
    }
  }

  const shift = useQuery({ ...billingQueries.currentShift(), enabled: open && canShift });
  const dues = useQuery({ ...billingQueries.dues(bill.patientId), enabled: open && bill.type === 'OutPatient' });
  const advances = usableAdvances(dues.data?.advances);
  const summary = summariseSplit(lines, bill.dueAmount, cashTendered);

  const editLines = (next: PaymentLineDraft[]) => {
    setLines(next);
    renewAttempt();
  };

  const adjust = useMutation({
    mutationFn: (advance: { id: string; amount: number }) => billingApi.adjustAdvance(bill.id, advance.id, advance.amount, bill.version, { idempotencyKey: adjustKey }),
    onSuccess: (receipt) => {
      renewAdjustKey();
      notify.success('Advance applied', `${formatInr(receipt.amount)} adjusted · receipt ${receipt.receiptNumber}`);
      void refresh();
    },
  });

  const collect = useMutation({
    mutationFn: () => {
      const checked = lines.map((line) => checkPaymentLine(line, today));
      const payments = checked.flatMap((c) => (c.ok ? [c.input] : []));
      return billingApi.collectPayment({ billId: bill.id, payments, notes: notes.trim() || null }, { idempotencyKey: attemptKey });
    },
    onSuccess: (receipt) => {
      renewAttempt();
      setDone({ receipt, change: summary.change });
      void refresh();
      onCollected?.(receipt);
      if (prefs.autoPrint) {
        void printReceipt(receipt);
      }
    },
  });

  const problems = lines.map((line) => checkPaymentLine(line, today)).filter((c) => !c.ok);
  const canCollect = !collect.isPending && summary.total > 0 && summary.remaining >= 0 && !summary.shortTendered;

  const submit = () => {
    if (problems.length > 0) {
      setShowErrors(true);
      return;
    }
    if (canCollect) {
      collect.mutate();
    }
  };

  useHotkey('mod+enter', submit, { enabled: open && !done && side === null, allowInInputs: true });

  const shiftMissing = canShift && shift.data === null;
  const shiftRequired = collect.error && typeof collect.error === 'object' && 'code' in collect.error && collect.error.code === 'billing.shift_required';

  return (
    <>
      <Dialog open={open && side === null} onOpenChange={(_, data) => !data.open && !collect.isPending && onClose()}>
        <DialogSurface style={{ maxWidth: 820, width: '100%' }}>
          <DialogBody>
            <DialogTitle>{done ? 'Payment recorded' : 'Collect payment'}</DialogTitle>
            <DialogContent className={styles.body}>
              {done ? (
                <div className={styles.done} role="status">
                  <CheckmarkCircleRegular fontSize={44} color={tokens.colorPaletteGreenForeground1} />
                  <Text size={500} weight="semibold">
                    {formatInr(done.receipt.amount)} received
                  </Text>
                  <Text>
                    Receipt {done.receipt.receiptNumber} · {formatDateTime(done.receipt.receivedAt)}
                  </Text>
                  {done.change !== null && done.change > 0 && (
                    <>
                      <Caption1 className={styles.muted}>Give back as change</Caption1>
                      <span className={styles.change}>{formatInr(done.change)}</span>
                    </>
                  )}
                  <Caption1 className={styles.muted}>
                    {bill.dueAmount > 0 ? `Balance still due on the bill: ${formatInr(bill.dueAmount)}` : 'The bill is fully paid.'}
                    {prefs.autoPrint ? ' The receipt was sent to the printer.' : ''}
                  </Caption1>
                </div>
              ) : (
                <>
                  <div className={styles.head}>
                    <div>
                      <Text weight="semibold">{bill.patientName}</Text>
                      <Caption1 block className={styles.muted}>
                        {bill.uhid} · Bill {bill.billNumber}
                      </Caption1>
                    </div>
                    <div style={{ textAlign: 'end' }}>
                      <Caption1 block className={styles.dueLabel}>
                        Amount due
                      </Caption1>
                      <span className={styles.due}>{formatInr(bill.dueAmount)}</span>
                    </div>
                  </div>

                  {shiftMissing && (
                    <MessageBar intent="warning">
                      <MessageBarBody>
                        <MessageBarTitle>No open shift</MessageBarTitle>
                        Open your cash counter shift so this collection is counted in your drawer.
                      </MessageBarBody>
                      <MessageBarActions>
                        <Button icon={<DoorArrowRightRegular />} onClick={() => setSide('shift')}>
                          Open shift
                        </Button>
                      </MessageBarActions>
                    </MessageBar>
                  )}

                  {advances.length > 0 && bill.dueAmount > 0 && (
                    <MessageBar intent="info" layout="multiline">
                      <MessageBarBody>
                        <MessageBarTitle>Advance available</MessageBarTitle>
                        {advances.map(({ advance, available }) => {
                          const amount = fromPaise(minBig(toPaise(available), toPaise(bill.dueAmount)));
                          return (
                            <div key={advance.id} className={styles.advance}>
                              <Text>
                                {formatInr(available)} from {advance.appointmentNumber ? `the online payment for appointment ${advance.appointmentNumber}` : `receipt ${advance.receiptNumber}`}
                              </Text>
                              <Button
                                size="small"
                                icon={adjust.isPending ? <Spinner size="tiny" /> : <WalletRegular />}
                                disabled={adjust.isPending}
                                onClick={() => adjust.mutate({ id: advance.id, amount })}
                              >
                                Use {formatInr(amount)}
                              </Button>
                            </div>
                          );
                        })}
                      </MessageBarBody>
                    </MessageBar>
                  )}
                  <ActionError error={adjust.error} />

                  <PaymentPanel
                    due={bill.dueAmount}
                    lines={lines}
                    onLinesChange={editLines}
                    cashTendered={cashTendered}
                    onCashTenderedChange={setCashTendered}
                    showErrors={showErrors}
                    today={today}
                    disabled={collect.isPending}
                  />

                  <div className={styles.alternatives}>
                    <Button icon={<PaymentRegular />} onClick={() => setSide('pos')} disabled={collect.isPending}>
                      Card / UPI terminal
                    </Button>
                    <Button icon={<LinkRegular />} onClick={() => setSide('link')} disabled={collect.isPending}>
                      Send payment link
                    </Button>
                  </div>

                  <Field label="Note on the receipt" hint="Optional">
                    <Textarea value={notes} onChange={(_, data) => setNotes(data.value)} maxLength={500} rows={1} resize="vertical" />
                  </Field>

                  <ActionError error={collect.error} onReload={() => void refresh()} />
                  {shiftRequired && (
                    <Button icon={<DoorArrowRightRegular />} onClick={() => setSide('shift')}>
                      Open my shift now
                    </Button>
                  )}
                </>
              )}
            </DialogContent>
            <DialogActions>
              {done ? (
                <>
                  <Menu>
                    <MenuTrigger disableButtonEnhancement>
                      {(triggerProps: MenuButtonProps) => (
                        <SplitButton menuButton={triggerProps} icon={<PrintRegular />} primaryActionButton={{ onClick: () => void printReceipt(done.receipt) }}>
                          Print receipt
                        </SplitButton>
                      )}
                    </MenuTrigger>
                    <MenuPopover>
                      <MenuList>
                        <MenuItem onClick={() => void printReceipt(done.receipt, 'Receipt80')}>Receipt printer (80 mm)</MenuItem>
                        <MenuItem onClick={() => void printReceipt(done.receipt, 'A4')}>A4 receipt</MenuItem>
                        <MenuItem onClick={() => void printBill(bill, 'A4')}>A4 bill with GST details</MenuItem>
                        <MenuItem onClick={() => void printBill(bill, 'Receipt80')}>Bill on the receipt printer</MenuItem>
                      </MenuList>
                    </MenuPopover>
                  </Menu>
                  <Button appearance="primary" onClick={onClose} autoFocus>
                    Done
                  </Button>
                </>
              ) : (
                <>
                  <Button onClick={onClose} disabled={collect.isPending}>
                    Not now
                  </Button>
                  <Button
                    appearance="primary"
                    size="large"
                    onClick={submit}
                    disabled={!canCollect}
                    icon={collect.isPending ? <Spinner size="tiny" /> : undefined}
                  >
                    Collect {formatInr(summary.total)}
                    <KeyHint>Ctrl+Enter</KeyHint>
                  </Button>
                </>
              )}
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      {side === 'pos' && (
        <PosSaleDialog
          open
          target={{ kind: 'bill', billId: bill.id }}
          defaultAmount={bill.dueAmount}
          maxAmount={bill.dueAmount}
          onClose={() => setSide(null)}
          onApproved={(sale) => {
            setSide(null);
            if (sale.receiptId) {
              void printReceiptById(sale.receiptId);
            }
            notify.success('Card/UPI payment recorded', formatInr(sale.amount));
          }}
        />
      )}
      {side === 'link' && <PaymentLinkDialog open target={{ kind: 'bill', billId: bill.id, due: bill.dueAmount }} onClose={() => setSide(null)} />}
      <OpenShiftDialog open={side === 'shift'} onClose={() => setSide(null)} onOpened={() => setSide(null)} />
    </>
  );
}
