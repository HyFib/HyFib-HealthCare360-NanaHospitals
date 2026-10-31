import {
  Button,
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
import { formatInr } from '@hc360/core';
import { useNotify, useUnsavedChanges, useValuesChanged } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { approvalKeys } from '@/modules/approvals/api';
import { billingApi, type BillDetail } from './api';
import { useRefreshBilling } from './hooks';
import { CREDIT_NOTE_KIND_LABEL, CREDIT_NOTE_KINDS, type CreditNoteKind } from './labels';
import { creditableAmount } from './billTotals';
import { parseRupees } from './money';
import { ActionError, Amount, AmountField } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/**
 * Asks for a credit note (wrong rate, service not given) or write-off (unrecoverable due) on a finalized bill. An
 * approver, someone other than the person asking, issues it from the approvals inbox, on the bill as it is then; it is
 * numbered and audited, reduces what the patient owes and, if the bill was paid, makes the difference refundable.
 */
export function CreditNoteDialog({ bill, open, onClose }: { bill: BillDetail; open: boolean; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const refresh = useRefreshBilling();
  const max = creditableAmount(bill);
  const [kind, setKind] = useState<CreditNoteKind>('CreditNote');
  const [amount, setAmount] = useState('');
  const [reason, setReason] = useState('');
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setKind('CreditNote');
      setAmount('');
      setReason('');
    }
  }

  const parsed = parseRupees(amount, { max });
  const amountError =
    amount && !parsed.ok
      ? parsed.error === 'The amount is too large.'
        ? `At most ${formatInr(max)} can be credited on this bill.`
        : parsed.error
      : null;
  const valid = parsed.ok && reason.trim().length >= 5;

  const issue = useMutation({
    mutationFn: () => billingApi.requestCreditNote(bill.id, kind, parsed.ok ? parsed.rupees : 0, reason.trim()),
    onSuccess: (asked) => {
      notify.success(
        `${CREDIT_NOTE_KIND_LABEL[kind]} sent for approval`,
        `${asked.summary}. An approver, not you, issues it.`,
      );
      void queryClient.invalidateQueries({ queryKey: approvalKeys.all });
      void refresh();
      onClose();
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ kind, amount, reason }, open),
    busy: issue.isPending,
    open,
    onClose,
  });

  return (
    <Dialog open={open} onOpenChange={guard.onOpenChange} modalType="alert">
      <DialogSurface style={{ maxWidth: 540 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !issue.isPending) {
              issue.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Credit note or write-off</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>
                Bill {bill.billNumber} · {bill.patientName}. Up to <Amount value={max} strong /> can be credited. An
                approver, someone other than you, issues it; once issued it cannot be undone.
              </Text>
              <Field label="Type" required>
                <RadioGroup value={kind} onChange={(_, data) => setKind(data.value as CreditNoteKind)}>
                  {CREDIT_NOTE_KINDS.map((k) => (
                    <Radio
                      key={k}
                      value={k}
                      label={
                        k === 'CreditNote'
                          ? `${CREDIT_NOTE_KIND_LABEL[k]} — wrong rate or service not given`
                          : `${CREDIT_NOTE_KIND_LABEL[k]} — the due cannot be recovered`
                      }
                    />
                  ))}
                </RadioGroup>
              </Field>
              <AmountField label="Amount" required value={amount} onChange={setAmount} error={amountError} autoFocus />
              <Field
                label="Reason"
                required
                hint="At least 5 characters. Printed on the credit note and kept in the audit trail."
              >
                <Textarea value={reason} onChange={(_, data) => setReason(data.value)} maxLength={300} rows={2} />
              </Field>
              {bill.paidAmount > 0 && (
                <Text>
                  If the patient has already paid, the credited amount becomes refundable and can then be requested as a
                  refund.
                </Text>
              )}
              <ActionError error={issue.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={issue.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || issue.isPending}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={issue.isPending}
                icon={issue.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Ask for {CREDIT_NOTE_KIND_LABEL[kind].toLowerCase()}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
