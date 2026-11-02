import {
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  Radio,
  RadioGroup,
  Spinner,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { formatDateTime } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { approvalKeys } from '@/modules/approvals/api';
import { billingApi, billingQueries, type ReceiptPaymentDto, type RefundDto } from './api';
import { useRefreshBilling } from './hooks';
import {
  PAYMENT_MODE_LABEL,
  RECEIPT_REVERSAL_REASON_LABEL,
  REFUND_REVERSAL_REASON_LABEL,
  REVERSAL_REASONS,
  type ReversalReason,
} from './labels';
import { remaining, reversibleLines } from './reversals';
import { ActionError, Amount } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/** The note a reversal needs: how it was found that the money did not move (as the server requires). */
const NOTE_MIN = 10;
const NOTE_MAX = 500;

const lineLabel = (line: ReceiptPaymentDto) => {
  const evidence =
    line.chequeNumber != null
      ? `cheque ${line.chequeNumber}${line.chequeBank ? `, ${line.chequeBank}` : ''}`
      : line.cardLast4 != null
        ? `card ending ${line.cardLast4}`
        : line.reference;
  return `${PAYMENT_MODE_LABEL[line.mode]}${evidence ? ` (${evidence})` : ''}`;
};

/**
 * Asks for money a receipt recorded to be reversed as never received (a cheque the bank returned, a card, UPI or online
 * payment the bank never settled, or one a training simulator made up). An approver, someone other than the person
 * asking, makes it from the approvals inbox. The bill then owes the money again, or the advance holds less, and bills an
 * advance paid owe again too. Cash is never reversed: it was counted when the shift closed.
 */
export function ReceiptReversalDialog({
  receiptId,
  open,
  onClose,
}: {
  receiptId: string;
  open: boolean;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const refresh = useRefreshBilling();
  const receipt = useQuery({ ...billingQueries.receipt(receiptId), enabled: open });
  const lines = reversibleLines(receipt.data?.payments ?? []);
  const [chosen, setChosen] = useState<string[] | null>(null);
  const [reason, setReason] = useState<ReversalReason | ''>('');
  const [note, setNote] = useState('');
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setChosen(null);
      setReason('');
      setNote('');
    }
  }

  // Every line that can be taken back, until the user unticks one.
  const picked = chosen ?? lines.map((l) => l.id!);
  const total = lines.filter((l) => picked.includes(l.id!)).reduce((sum, l) => sum + remaining(l), 0);
  const valid = picked.length > 0 && reason !== '' && note.trim().length >= NOTE_MIN;

  const ask = useMutation({
    mutationFn: () => billingApi.requestReceiptReversal(receiptId, picked, reason as ReversalReason, note.trim()),
    onSuccess: (asked) => {
      notify.success('Sent for approval', `${asked.summary}. An approver, not you, makes it.`);
      void queryClient.invalidateQueries({ queryKey: approvalKeys.all });
      void refresh();
      onClose();
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ chosen, reason, note }, open),
    busy: ask.isPending,
    open,
    onClose,
  });

  return (
    <Dialog open={open} onOpenChange={guard.onOpenChange} modalType="alert">
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !ask.isPending) {
              ask.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Money not received</DialogTitle>
            <DialogContent className={styles.body}>
              {receipt.isLoading && <Spinner size="small" label="Opening the receipt…" />}
              <ActionError error={receipt.error} onReload={() => void receipt.refetch()} />
              {receipt.data && (
                <>
                  <Text>
                    Receipt {receipt.data.receiptNumber} of {formatDateTime(receipt.data.receivedAt)} ·{' '}
                    {receipt.data.patientName}. An approver, someone other than you, makes the reversal. The receipt
                    stays as it was; the money comes off what was paid, so the bill owes it again.
                  </Text>
                  {lines.length === 0 ? (
                    <Text>
                      Nothing on this receipt can be reversed. Cash is counted when the shift closes: cash recorded and
                      not received is the shift&apos;s shortage. An advance adjusted or an insurer&apos;s settlement
                      took no money from the patient.
                    </Text>
                  ) : (
                    <Field label="Payments whose money did not arrive" required>
                      {lines.map((line) => (
                        <Checkbox
                          key={line.id}
                          checked={picked.includes(line.id!)}
                          onChange={(_, data) =>
                            setChosen(data.checked ? [...picked, line.id!] : picked.filter((id) => id !== line.id))
                          }
                          label={
                            <>
                              {lineLabel(line)} · <Amount value={remaining(line)} />
                            </>
                          }
                        />
                      ))}
                    </Field>
                  )}
                  {lines.length > 0 && (
                    <>
                      <Field label="Why" required>
                        <RadioGroup value={reason} onChange={(_, data) => setReason(data.value as ReversalReason)}>
                          {REVERSAL_REASONS.map((r) => (
                            <Radio key={r} value={r} label={RECEIPT_REVERSAL_REASON_LABEL[r]} />
                          ))}
                        </RadioGroup>
                      </Field>
                      <Field
                        label="How it was found"
                        hint="The bank's return memo, the gateway's report, the training server…"
                        required
                        validationMessage={
                          note && note.trim().length < NOTE_MIN ? 'Say in a sentence how it was found.' : undefined
                        }
                      >
                        <Textarea
                          value={note}
                          onChange={(_, data) => setNote(data.value)}
                          maxLength={NOTE_MAX}
                          required
                        />
                      </Field>
                      <Text>
                        Not received: <Amount value={total} strong />
                      </Text>
                    </>
                  )}
                </>
              )}
              <ActionError error={ask.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={ask.isPending}>
                {lines.length > 0 ? 'Cancel' : 'Close'}
              </Button>
              {lines.length > 0 && (
                <Button
                  type="submit"
                  appearance="primary"
                  disabled={!valid || ask.isPending}
                  disabledFocusable={ask.isPending}
                  icon={ask.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
                >
                  Ask for the reversal
                </Button>
              )}
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

/**
 * Asks for a paid refund to be recorded as not paid out (a cheque stopped or never presented, a transfer, card reversal
 * or online refund that failed, or one a training simulator made up). An approver makes it; the money is then back on
 * the bill or advance it came from, and a new refund pays the patient another way.
 */
export function RefundNotPaidDialog({
  refund,
  open,
  onClose,
}: {
  refund: RefundDto;
  open: boolean;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const refresh = useRefreshBilling();
  const [reason, setReason] = useState<ReversalReason | ''>('');
  const [note, setNote] = useState('');
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setReason('');
      setNote('');
    }
  }

  const valid = reason !== '' && note.trim().length >= NOTE_MIN;
  const ask = useMutation({
    mutationFn: () => billingApi.requestRefundNotPaid(refund.id, reason as ReversalReason, note.trim()),
    onSuccess: (asked) => {
      notify.success('Sent for approval', `${asked.summary}. An approver, not you, makes it.`);
      void queryClient.invalidateQueries({ queryKey: approvalKeys.all });
      void refresh();
      onClose();
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ reason, note }, open),
    busy: ask.isPending,
    open,
    onClose,
  });

  return (
    <Dialog open={open} onOpenChange={guard.onOpenChange} modalType="alert">
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !ask.isPending) {
              ask.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Refund not paid out</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>
                Refund {refund.refundNumber} of <Amount value={refund.amount} strong />, recorded as paid by{' '}
                {refund.mode ? PAYMENT_MODE_LABEL[refund.mode] : '—'}
                {refund.paidAt ? ` on ${formatDateTime(refund.paidAt)}` : ''}. An approver, someone other than you,
                records it. The refund stays as it was; the money is back where it came from, and a new refund pays the
                patient.
              </Text>
              <Field label="Why" required>
                <RadioGroup value={reason} onChange={(_, data) => setReason(data.value as ReversalReason)}>
                  {REVERSAL_REASONS.map((r) => (
                    <Radio key={r} value={r} label={REFUND_REVERSAL_REASON_LABEL[r]} />
                  ))}
                </RadioGroup>
              </Field>
              <Field
                label="How it was found"
                hint="The bank's return of the transfer, the gateway's report, the patient…"
                required
                validationMessage={
                  note && note.trim().length < NOTE_MIN ? 'Say in a sentence how it was found.' : undefined
                }
              >
                <Textarea value={note} onChange={(_, data) => setNote(data.value)} maxLength={NOTE_MAX} required />
              </Field>
              <ActionError error={ask.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={ask.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || ask.isPending}
                disabledFocusable={ask.isPending}
                icon={ask.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Ask to record it
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
