import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  Select,
  Spinner,
  Text,
  Textarea,
} from '@fluentui/react-components';
import { formatInr } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { billingApi, type RefundDto } from './api';
import { useAttemptKey, useBillingPrint, useRefreshBilling } from './hooks';
import { REFUND_PAYOUT_MODE_LABEL, REFUND_PAYOUT_MODES, REFUND_PURPOSE_LABEL, type PaymentMode } from './labels';
import { amountInputText, parseRupees } from './money';
import { ActionError, AmountField, ReasonDialog, SummaryList, SummaryRow, Amount } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

export type RefundTarget =
  | { kind: 'bill'; billId: string; max: number; label: string }
  | { kind: 'deposit'; ipAccountId: string; max: number; label: string }
  | { kind: 'advance'; advanceId: string; max: number; label: string };

/**
 * Asks for a refund (MB-05). Nothing leaves the drawer yet: a second person approves the request, then the counter
 * pays it out in a stated mode.
 */
export function RequestRefundDialog({
  open,
  target,
  onClose,
  onRequested,
}: {
  open: boolean;
  target: RefundTarget;
  onClose: () => void;
  onRequested?: (id: string) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const refresh = useRefreshBilling();
  const [amount, setAmount] = useState(amountInputText(target.max));
  const [reason, setReason] = useState('');
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setAmount(amountInputText(target.max));
      setReason('');
    }
  }
  const parsed = parseRupees(amount, { max: target.max });
  const amountError =
    amount && !parsed.ok
      ? parsed.error === 'The amount is too large.'
        ? `At most ${formatInr(target.max)} can be refunded.`
        : parsed.error
      : null;
  const valid = parsed.ok && reason.trim().length >= 5;

  const request = useMutation({
    mutationFn: () =>
      billingApi.requestRefund({
        billId: target.kind === 'bill' ? target.billId : null,
        ipAccountId: target.kind === 'deposit' ? target.ipAccountId : null,
        advanceId: target.kind === 'advance' ? target.advanceId : null,
        amount: parsed.ok ? parsed.rupees : 0,
        reason: reason.trim(),
      }),
    onSuccess: (created) => {
      notify.success('Refund requested', 'It now waits for approval by accounts.');
      void refresh();
      onRequested?.(created.id);
      onClose();
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ amount, reason }, open),
    busy: request.isPending,
    open,
    onClose,
  });

  return (
    <Dialog open={open} onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !request.isPending) {
              request.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Request a refund</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>
                {target.label}. Up to <Amount value={target.max} strong /> can be refunded.
              </Text>
              <AmountField
                label="Amount to refund"
                required
                value={amount}
                onChange={setAmount}
                error={amountError}
                autoFocus
              />
              <Field label="Reason" required hint="At least 5 characters. The approver sees this.">
                <Textarea
                  value={reason}
                  onChange={(_, data) => setReason(data.value)}
                  maxLength={300}
                  rows={2}
                  placeholder="e.g. Test not done, duplicate payment, unused advance at discharge"
                />
              </Field>
              <ActionError error={request.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={request.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || request.isPending}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={request.isPending}
                icon={request.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Request refund
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

/** Approve (optional remarks) or reject (reason) a refund request; the requester cannot decide their own request. */
export function DecideRefundDialog({
  refund,
  decision,
  onClose,
}: {
  refund: RefundDto;
  decision: 'approve' | 'reject';
  onClose: () => void;
}) {
  const notify = useNotify();
  const refresh = useRefreshBilling();
  const decide = useMutation({
    mutationFn: (text: string) =>
      decision === 'approve'
        ? billingApi.approveRefund(refund.id, text || null)
        : billingApi.rejectRefund(refund.id, text),
    onSuccess: () => {
      notify.success(
        decision === 'approve' ? 'Refund approved' : 'Refund rejected',
        `${refund.refundNumber} · ${formatInr(refund.amount)}`,
      );
      void refresh();
      onClose();
    },
  });
  return (
    <ReasonDialog
      open
      title={
        decision === 'approve'
          ? `Approve refund of ${formatInr(refund.amount)}?`
          : `Reject refund of ${formatInr(refund.amount)}?`
      }
      description={`${refund.patientName ?? 'Patient'} (${refund.uhid ?? '—'}) · ${REFUND_PURPOSE_LABEL[refund.purpose]} · requested by ${refund.requestedByName ?? 'the system'}: “${refund.reason}”`}
      label={decision === 'approve' ? 'Remarks' : 'Why is it rejected?'}
      optional={decision === 'approve'}
      confirmLabel={decision === 'approve' ? 'Approve refund' : 'Reject refund'}
      destructive={decision === 'reject'}
      busy={decide.isPending}
      error={decide.error}
      onConfirm={(text) => decide.mutate(text)}
      onCancel={onClose}
    />
  );
}

/** Pays out an approved refund: cash from the drawer (needs an open shift), or a stated reversal/transfer reference. */
export function PayRefundDialog({ refund, onClose }: { refund: RefundDto; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const refresh = useRefreshBilling();
  const { printRefund } = useBillingPrint();
  const [attemptKey] = useAttemptKey();
  const [mode, setMode] = useState<PaymentMode>('Cash');
  const [reference, setReference] = useState('');
  const needsReference = mode !== 'Cash' && mode !== 'PaymentGateway';
  const valid = !needsReference || reference.trim().length > 0;

  const pay = useMutation({
    mutationFn: () => billingApi.payRefund(refund.id, mode, reference.trim() || null, { idempotencyKey: attemptKey }),
    onSuccess: () => {
      notify.success('Refund paid', `${formatInr(refund.amount)} to ${refund.patientName ?? 'the patient'}`);
      void refresh();
      void printRefund({
        ...refund,
        status: 'Paid',
        mode,
        reference: reference.trim() || null,
        paidAt: new Date().toISOString(),
      });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, data) => !data.open && !pay.isPending && onClose()} modalType="alert">
      <DialogSurface style={{ maxWidth: 520 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !pay.isPending) {
              pay.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Pay refund {refund.refundNumber}</DialogTitle>
            <DialogContent className={styles.body}>
              <SummaryList>
                <SummaryRow label="Patient">{`${refund.patientName ?? '—'} (${refund.uhid ?? '—'})`}</SummaryRow>
                <SummaryRow label="For">{REFUND_PURPOSE_LABEL[refund.purpose]}</SummaryRow>
                <SummaryRow label="Amount to hand back" strong>
                  <Amount value={refund.amount} size="large" />
                </SummaryRow>
              </SummaryList>
              <Field label="How is the money returned?" required>
                <Select value={mode} onChange={(_, data) => setMode(data.value as PaymentMode)} autoFocus>
                  {REFUND_PAYOUT_MODES.map((m) => (
                    <option key={m} value={m}>
                      {REFUND_PAYOUT_MODE_LABEL[m]}
                    </option>
                  ))}
                </Select>
              </Field>
              {mode !== 'Cash' && (
                <Field
                  label={mode === 'PaymentGateway' ? 'Reference (optional)' : 'Reversal, transfer or cheque reference'}
                  required={needsReference}
                  hint={
                    mode === 'PaymentGateway' ? 'The original online payment is reversed by the gateway.' : undefined
                  }
                >
                  <Input value={reference} onChange={(_, data) => setReference(data.value)} maxLength={64} />
                </Field>
              )}
              {mode === 'Cash' && <Text>The cash is taken from your drawer and counted in your shift.</Text>}
              <ActionError error={pay.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={pay.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || pay.isPending}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={pay.isPending}
                icon={pay.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Mark as paid · {formatInr(refund.amount)}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
