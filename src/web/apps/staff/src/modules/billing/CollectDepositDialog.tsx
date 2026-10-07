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
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  CheckmarkCircleRegular,
  DoorArrowRightRegular,
  LinkRegular,
  PaymentRegular,
  PrintRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatInr, todayIso, useHotkey } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { billingApi, billingQueries, type ReceiptDto } from './api';
import { useAttemptKey, useBillingPrint, useRefreshBilling } from './hooks';
import { PaymentLinkDialog } from './PaymentLinkDialog';
import { PaymentPanel } from './PaymentPanel';
import { checkPaymentLine, newPaymentLine, summariseSplit, type PaymentLineDraft } from './paymentRules';
import { PosSaleDialog } from './PosSaleDialog';
import { OpenShiftDialog } from './ShiftControls';
import { ActionError, Amount, KeyHint } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '14px' },
  head: { display: 'flex', justifyContent: 'space-between', alignItems: 'flex-end', gap: '16px', flexWrap: 'wrap' },
  label: { color: tokens.colorNeutralForeground3, textTransform: 'uppercase', letterSpacing: '0.5px' },
  total: {
    fontSize: '36px',
    lineHeight: '42px',
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorBrandForeground1,
  },
  done: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '6px',
    padding: '8px 0',
    textAlign: 'center',
  },
  change: {
    fontSize: '40px',
    lineHeight: '48px',
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorPaletteGreenForeground1,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  row: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
});

export interface DepositAccount {
  id: string;
  admissionNumber: string;
  patientName: string | null;
  uhid: string | null;
  depositBalance: number;
  estimatedAmount?: number;
}

/**
 * In-patient advance (IPB-01) collected at the counter: any split of cash, card, UPI, cheque; or a card terminal, or a
 * payment link sent to the family. Prints the advance receipt on the counter printer.
 */
export function CollectDepositDialog({
  account,
  open,
  onClose,
  onCollected,
}: {
  account: DepositAccount;
  open: boolean;
  onClose: () => void;
  onCollected?: (receipt: ReceiptDto) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const refresh = useRefreshBilling();
  const { prefs, printReceipt, printReceiptById } = useBillingPrint();
  const canShift = usePermission('billing.shifts');
  const [attemptKey, renewAttempt] = useAttemptKey();
  const [lines, setLines] = useState<PaymentLineDraft[]>(() => [newPaymentLine('Cash', null)]);
  const [cashTendered, setCashTendered] = useState('');
  const [notes, setNotes] = useState('');
  const [showErrors, setShowErrors] = useState(false);
  const [done, setDone] = useState<{ receipt: ReceiptDto; change: number | null } | null>(null);
  const [side, setSide] = useState<'pos' | 'link' | 'shift' | null>(null);
  const [wasOpen, setWasOpen] = useState(open);
  const today = todayIso();
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setLines([newPaymentLine('Cash', null)]);
      setCashTendered('');
      setNotes('');
      setShowErrors(false);
      setDone(null);
    }
  }

  const shift = useQuery({ ...billingQueries.currentShift(), enabled: open && canShift });
  const summary = summariseSplit(lines, null, cashTendered);

  const editLines = (next: PaymentLineDraft[]) => {
    setLines(next);
    renewAttempt();
  };

  const collect = useMutation({
    mutationFn: () => {
      const payments = lines.map((line) => checkPaymentLine(line, today)).flatMap((c) => (c.ok ? [c.input] : []));
      return billingApi.collectDeposit(
        account.id,
        { payments, notes: notes.trim() || null },
        { idempotencyKey: attemptKey },
      );
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
  const canCollect = !collect.isPending && summary.total > 0 && !summary.shortTendered;
  const submit = () => {
    if (problems.length > 0) {
      setShowErrors(true);
      return;
    }
    if (canCollect) {
      collect.mutate();
    }
  };
  const surface = useRef<HTMLDivElement>(null);
  useHotkey('mod+enter', submit, { enabled: open && !done && side === null, allowInInputs: true, within: surface });

  return (
    <>
      <Dialog open={open && side === null} onOpenChange={(_, data) => !data.open && !collect.isPending && onClose()}>
        <DialogSurface ref={surface} style={{ maxWidth: 820, width: '100%' }}>
          <DialogBody>
            <DialogTitle>{done ? 'Advance received' : 'Collect IP advance'}</DialogTitle>
            <DialogContent className={styles.body}>
              {done ? (
                <div className={styles.done} role="status">
                  <CheckmarkCircleRegular fontSize={44} color={tokens.colorPaletteGreenForeground1} />
                  <Text size={500} weight="semibold">
                    {formatInr(done.receipt.amount)} advance received
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
                </div>
              ) : (
                <>
                  <div className={styles.head}>
                    <div>
                      <Text weight="semibold">{account.patientName}</Text>
                      <Caption1 block className={styles.muted}>
                        {account.uhid} · Admission {account.admissionNumber} · advance held{' '}
                        <Amount value={account.depositBalance} />
                        {account.estimatedAmount ? ` · estimate ${formatInr(account.estimatedAmount)}` : ''}
                      </Caption1>
                    </div>
                    <div style={{ textAlign: 'end' }}>
                      <Caption1 block className={styles.label}>
                        Advance amount
                      </Caption1>
                      <span className={styles.total}>{formatInr(summary.total)}</span>
                    </div>
                  </div>
                  {canShift && shift.data === null && (
                    <MessageBar intent="warning">
                      <MessageBarBody>
                        <MessageBarTitle>No open shift</MessageBarTitle>
                        Open your cash counter shift so this advance is counted in your drawer.
                      </MessageBarBody>
                      <MessageBarActions>
                        <Button icon={<DoorArrowRightRegular />} onClick={() => setSide('shift')}>
                          Open shift
                        </Button>
                      </MessageBarActions>
                    </MessageBar>
                  )}
                  <PaymentPanel
                    due={null}
                    lines={lines}
                    onLinesChange={editLines}
                    cashTendered={cashTendered}
                    onCashTenderedChange={setCashTendered}
                    showErrors={showErrors}
                    today={today}
                    disabled={collect.isPending}
                  />
                  <div className={styles.row}>
                    <Button icon={<PaymentRegular />} onClick={() => setSide('pos')} disabled={collect.isPending}>
                      Card / UPI terminal
                    </Button>
                    <Button icon={<LinkRegular />} onClick={() => setSide('link')} disabled={collect.isPending}>
                      Send payment link
                    </Button>
                  </div>
                  <Field label="Note on the receipt" hint="Optional">
                    <Textarea
                      value={notes}
                      onChange={(_, data) => setNotes(data.value)}
                      maxLength={500}
                      rows={1}
                      resize="vertical"
                    />
                  </Field>
                  <ActionError error={collect.error} />
                </>
              )}
            </DialogContent>
            <DialogActions>
              {done ? (
                <>
                  <Button icon={<PrintRegular />} onClick={() => void printReceipt(done.receipt, 'Receipt80')}>
                    Receipt printer
                  </Button>
                  <Button icon={<PrintRegular />} onClick={() => void printReceipt(done.receipt, 'A4')}>
                    A4 receipt
                  </Button>
                  <Button appearance="primary" onClick={onClose} autoFocus>
                    Done
                  </Button>
                </>
              ) : (
                <>
                  <Button onClick={onClose} disabled={collect.isPending}>
                    Cancel
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
          target={{ kind: 'ip', ipAccountId: account.id }}
          defaultAmount={summary.total > 0 ? summary.total : null}
          onClose={() => setSide(null)}
          onApproved={(sale) => {
            setSide(null);
            if (sale.receiptId) {
              void printReceiptById(sale.receiptId);
            }
            notify.success('Advance received on the terminal', formatInr(sale.amount));
            onClose();
          }}
        />
      )}
      {side === 'link' && (
        <PaymentLinkDialog open target={{ kind: 'ip', ipAccountId: account.id }} onClose={() => setSide(null)} />
      )}
      <OpenShiftDialog open={side === 'shift'} onClose={() => setSide(null)} onOpened={() => setSide(null)} />
    </>
  );
}
