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
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, DismissCircleRegular, PaymentRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { billingApi, billingQueries, type PosSaleDto } from './api';
import { saveCounterPrefs, useCounterPrefs } from './counterPrefs';
import { useAttemptKey, useRefreshBilling } from './hooks';
import { POS_STATUS_LABEL } from './labels';
import { parseRupees } from './money';
import { ActionError, AmountField } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  status: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '8px', padding: '12px 0', textAlign: 'center' },
  big: { fontSize: '28px', fontWeight: tokens.fontWeightBold },
  muted: { color: tokens.colorNeutralForeground3 },
});

export type PosTarget = { kind: 'bill'; billId: string } | { kind: 'ip'; ipAccountId: string };

const PENDING = new Set(['Initiated', 'AwaitingCustomer']);

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
  const amountError = amount && !parsed.ok ? (parsed.error === 'The amount is too large.' && maxAmount ? `The balance is ${formatInr(maxAmount)}.` : parsed.error) : null;

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
    refetchInterval: (query) => (query.state.data && PENDING.has(query.state.data.status) ? 2_000 : false),
  });
  const current = sale.data;
  const pending = current ? PENDING.has(current.status) : false;

  const cancel = useMutation({
    mutationFn: () => billingApi.cancelPosSale(saleId!),
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
    <Dialog open={open} onOpenChange={(_, data) => !data.open && !pending && !start.isPending && finish()} modalType="alert">
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
                  <RadioGroup layout="horizontal" value={mode} onChange={(_, data) => setMode(data.value as 'Card' | 'Upi')}>
                    <Radio value="Card" label="Card (tap, insert or swipe)" />
                    <Radio value="Upi" label="UPI QR on the terminal" />
                  </RadioGroup>
                </Field>
                <AmountField label="Amount" required value={amount} onChange={setAmount} error={amountError} autoFocus />
                <Field label="Terminal" required hint="The terminal id on its label. Remembered on this computer.">
                  <Input value={terminal} onChange={(_, data) => setTerminal(data.value)} maxLength={64} />
                </Field>
                <ActionError error={start.error} />
              </form>
            ) : (
              <div className={styles.status} role="status" aria-live="polite">
                {pending && <Spinner size="large" />}
                {current.status === 'Approved' && <CheckmarkCircleRegular fontSize={48} color={tokens.colorPaletteGreenForeground1} />}
                {!pending && current.status !== 'Approved' && <DismissCircleRegular fontSize={48} color={tokens.colorPaletteRedForeground1} />}
                <span className={styles.big}>{formatInr(current.amount)}</span>
                <Text weight="semibold">{POS_STATUS_LABEL[current.status]}</Text>
                {current.message && <Caption1 className={styles.muted}>{current.message}</Caption1>}
                {current.status === 'Approved' && (
                  <Caption1>
                    {[current.cardLast4 ? `Card ending ${current.cardLast4}` : null, current.approvalCode ? `Approval ${current.approvalCode}` : null, current.rrn ? `RRN ${current.rrn}` : null]
                      .filter(Boolean)
                      .join(' · ')}
                  </Caption1>
                )}
                {current.status === 'Approved' && <Caption1 className={styles.muted}>The receipt has been recorded.</Caption1>}
                {sale.error && (
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      <MessageBarTitle>Lost contact with the server</MessageBarTitle>
                      Still checking the terminal. Do not collect the money again until this shows the result.
                    </MessageBarBody>
                  </MessageBar>
                )}
                <ActionError error={cancel.error} />
              </div>
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
                  icon={start.isPending ? <Spinner size="tiny" /> : <PaymentRegular />}
                  disabled={!parsed.ok || !terminal.trim() || start.isPending}
                >
                  Send to terminal
                </Button>
              </>
            )}
            {current && pending && (
              <Button onClick={() => cancel.mutate()} disabled={cancel.isPending}>
                Cancel on the terminal
              </Button>
            )}
            {current && !pending && current.status !== 'Approved' && <Button onClick={tryAgain}>Try again</Button>}
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
