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
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { PaymentRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { isTerminalBusy, isTerminalSalePending, isTerminalSaleUnconfirmed } from '@/lib/terminal/labels';
import { TerminalSaleStatus } from '@/lib/terminal/TerminalSaleStatus';
import { ClearUnconfirmedSale } from '@/lib/terminal/UnconfirmedSale';
import { useNow } from '@/lib/terminal/useNow';
import { billingApi, billingQueries, type PosSaleDto } from './api';
import { saveCounterPrefs, useCounterPrefs } from './counterPrefs';
import { useAttemptKey, useRefreshBilling } from './hooks';
import { parseRupees } from './money';
import { ActionError, AmountField } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

export type PosTarget = { kind: 'bill'; billId: string } | { kind: 'ip'; ipAccountId: string };

/**
 * Card/UPI terminal sale (INT-POS): the amount is pushed to the terminal wired to this counter, the screen follows the
 * terminal until the patient pays, and the server records the receipt once when the terminal approves.
 */
export function PosSaleDialog({
  open,
  target,
  defaultAmount,
  maxAmount,
  onClose,
  onApproved,
}: {
  open: boolean;
  target: PosTarget;
  defaultAmount: number | null;
  /** Bills cannot take more than their balance. */
  maxAmount?: number | null;
  onClose: () => void;
  onApproved: (sale: PosSaleDto) => void;
}) {
  const styles = useStyles();
  const prefs = useCounterPrefs();
  const refresh = useRefreshBilling();
  const queryClient = useQueryClient();
  const [attemptKey, renewAttempt] = useAttemptKey();
  const [terminal, setTerminal] = useState(prefs.terminalId);
  const [amount, setAmount] = useState(defaultAmount ? String(defaultAmount) : '');
  const [mode, setMode] = useState<'Card' | 'Upi'>('Card');
  const [saleId, setSaleId] = useState<string | null>(null);
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setTerminal(prefs.terminalId);
      setAmount(defaultAmount ? String(defaultAmount) : '');
      setSaleId(null);
    }
  }

  const parsed = parseRupees(amount, { max: maxAmount ?? undefined });
  const amountError =
    amount && !parsed.ok
      ? parsed.error === 'The amount is too large.' && maxAmount
        ? `The balance is ${formatInr(maxAmount)}.`
        : parsed.error
      : null;

  const start = useMutation({
    mutationFn: () =>
      billingApi.startPosSale(
        {
          billId: target.kind === 'bill' ? target.billId : null,
          ipAccountId: target.kind === 'ip' ? target.ipAccountId : null,
          amount: parsed.ok ? parsed.rupees : 0,
          terminalId: terminal.trim(),
          mode,
        },
        { idempotencyKey: attemptKey },
      ),
    onSuccess: (sale) => {
      saveCounterPrefs({ terminalId: terminal.trim() });
      queryClient.setQueryData(billingQueries.posSale(sale.id).queryKey, sale);
      setSaleId(sale.id);
    },
  });

  const sale = useQuery({
    ...billingQueries.posSale(saleId ?? ''),
    enabled: saleId !== null,
    // An unconfirmed sale cannot be asked of the terminal; the server settles it once its wait is over.
    refetchInterval: (query) =>
      query.state.data && isTerminalSalePending(query.state.data.status)
        ? 2_000
        : query.state.data && isTerminalSaleUnconfirmed(query.state.data.status)
          ? 30_000
          : false,
  });
  const current = sale.data;
  const pending = current ? isTerminalSalePending(current.status) : false;
  const now = useNow();
  const busy = current ? isTerminalBusy(current, now) : false;

  const cancel = useMutation({
    mutationFn: (note?: string) => billingApi.cancelPosSale(saleId!, note),
    onSuccess: (cancelled) => queryClient.setQueryData(billingQueries.posSale(cancelled.id).queryKey, cancelled),
  });

  const finish = () => {
    if (current?.status === 'Approved') {
      void refresh();
      onApproved(current);
    } else {
      onClose();
    }
  };

  const tryAgain = () => {
    renewAttempt();
    setSaleId(null);
    start.reset();
  };

  return (
    <Dialog
      open={open}
      onOpenChange={(_, data) => !data.open && !pending && !start.isPending && finish()}
      modalType="alert"
    >
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>Card / UPI terminal</DialogTitle>
          <DialogContent className={styles.body}>
            {!current ? (
              <form
                id="pos-start"
                className={styles.body}
                onSubmit={(event) => {
                  event.preventDefault();
                  if (parsed.ok && terminal.trim() && !start.isPending) {
                    start.mutate();
                  }
                }}
              >
                <Field label="Paid by">
                  <RadioGroup
                    layout="horizontal"
                    value={mode}
                    onChange={(_, data) => setMode(data.value as 'Card' | 'Upi')}
                  >
                    <Radio value="Card" label="Card (tap, insert or swipe)" />
                    <Radio value="Upi" label="UPI QR on the terminal" />
                  </RadioGroup>
                </Field>
                <AmountField
                  label="Amount"
                  required
                  value={amount}
                  onChange={setAmount}
                  error={amountError}
                  autoFocus
                />
                <Field label="Terminal" required hint="The terminal id on its label. Remembered on this computer.">
                  <Input value={terminal} onChange={(_, data) => setTerminal(data.value)} maxLength={32} />
                </Field>
                <ActionError error={start.error} />
              </form>
            ) : (
              <TerminalSaleStatus
                sale={current}
                lostContact={!!sale.error}
                footer={<ActionError error={cancel.error} />}
              >
                {isTerminalSaleUnconfirmed(current.status) && (
                  <ClearUnconfirmedSale onClear={(note) => cancel.mutate(note)} pending={cancel.isPending} />
                )}
                {current.status === 'Approved' && current.receiptId && (
                  <Caption1 className={styles.muted}>The receipt has been recorded.</Caption1>
                )}
                {current.status === 'Approved' && !current.receiptId && (
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      <MessageBarTitle>Paid, but no receipt yet</MessageBarTitle>
                      The terminal took the payment, but HC360 has not recorded its receipt. Do not take the money
                      again: HC360 keeps trying, and the accountants are told if it cannot be recorded.
                    </MessageBarBody>
                  </MessageBar>
                )}
              </TerminalSaleStatus>
            )}
          </DialogContent>
          <DialogActions>
            {!current && (
              <>
                <Button onClick={onClose} disabled={start.isPending}>
                  Cancel
                </Button>
                <Button
                  type="submit"
                  form="pos-start"
                  appearance="primary"
                  icon={start.isPending ? <Spinner size="tiny" aria-hidden /> : <PaymentRegular />}
                  disabled={!parsed.ok || !terminal.trim() || start.isPending}
                >
                  Send to terminal
                </Button>
              </>
            )}
            {current && pending && (
              <Button onClick={() => cancel.mutate(undefined)} disabled={cancel.isPending}>
                Cancel on the terminal
              </Button>
            )}
            {current && !pending && current.status !== 'Approved' && (
              <Button onClick={tryAgain} disabled={busy}>
                Try again
              </Button>
            )}
            {current && !pending && (
              <Button appearance="primary" onClick={finish}>
                {current.status === 'Approved' ? 'Done' : 'Close'}
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
