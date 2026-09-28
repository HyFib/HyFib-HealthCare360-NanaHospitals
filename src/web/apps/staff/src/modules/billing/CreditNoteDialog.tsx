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
import { useNotify } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
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
 * Credit note (wrong rate, service not given) or write-off (unrecoverable due) on a finalized bill. Issued only by
 * approvers, numbered and audited; it reduces what the patient owes and, if the bill was paid, makes the difference
 * refundable.
 */
export function CreditNoteDialog({ bill, open, onClose }: { bill: BillDetail; open: boolean; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
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
    mutationFn: () => billingApi.issueCreditNote(bill.id, kind, parsed.ok ? parsed.rupees : 0, reason.trim()),
    onSuccess: (updated) => {
      const note = updated.creditNotes.at(-1);
      notify.success(
        `${CREDIT_NOTE_KIND_LABEL[kind]} issued`,
        note ? `${note.number} · ${formatInr(note.amount)}` : undefined,
      );
      void refresh();
      onClose();
    },
  });

  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && !issue.isPending && onClose()} modalType="alert">
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
                Bill {bill.billNumber} · {bill.patientName}. Up to <Amount value={max} strong /> can be credited. This
                cannot be undone.
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
              <Button onClick={onClose} disabled={issue.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || issue.isPending}
                icon={issue.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Issue {CREDIT_NOTE_KIND_LABEL[kind].toLowerCase()}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
