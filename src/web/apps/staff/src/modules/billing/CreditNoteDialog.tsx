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
import { billingApi, type BillDetail, type CreditNoteLineRequest } from './api';
import { useRefreshBilling } from './hooks';
import { CREDIT_NOTE_KIND_LABEL, CREDIT_NOTE_KINDS, type CreditNoteKind } from './labels';
import { creditableAmount, creditableLines, payerCreditableAmount } from './billTotals';
import { fromPaise, parseRupees, toPaise } from './money';
import { ActionError, Amount, AmountField } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  lines: { display: 'flex', flexDirection: 'column', gap: '8px' },
});

/**
 * Asks for a credit note (wrong rate, service not given) or write-off (unrecoverable due) on a finalized bill. An
 * approver, someone other than the person asking, issues it from the approvals inbox, on the bill as it is then; it is
 * numbered and audited, reduces what the patient owes and, if the bill was paid, makes the difference refundable.
 * On a bill with GST a credit note names the services it corrects, with the amount on each: the GST it takes back is
 * each service's own (CGST Act s.34), so the books and the e-invoice portal lower the right tax. A write-off is of the
 * due and reduces no GST. A payer credit note credits the GST of what the payer's settlement disallowed, up to that
 * amount; it does not change what the patient owes.
 */
export function CreditNoteDialog({ bill, open, onClose }: { bill: BillDetail; open: boolean; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const refresh = useRefreshBilling();
  const [kind, setKind] = useState<CreditNoteKind>('CreditNote');
  const payerMax = payerCreditableAmount(bill);
  const kinds = CREDIT_NOTE_KINDS.filter((k) => k !== 'PayerShare' || payerMax > 0);
  const max = kind === 'PayerShare' ? payerMax : creditableAmount(bill);
  const services = creditableLines(bill);
  const [amount, setAmount] = useState('');
  const [lineAmounts, setLineAmounts] = useState<Record<string, string>>({});
  const [reason, setReason] = useState('');
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setKind('CreditNote');
      setAmount('');
      setLineAmounts({});
      setReason('');
    }
  }

  // A bill with GST: the credit note names its services; the amount is their sum.
  const byService = kind !== 'WriteOff' && bill.taxAmount !== 0;
  const lineEntries = services
    .filter((service) => (lineAmounts[service.id] ?? '').trim() !== '')
    .map((service) => ({ service, parsed: parseRupees(lineAmounts[service.id] ?? '', { max: service.left }) }));
  const lineErrors = new Map<string, string>();
  for (const { service, parsed: entry } of lineEntries) {
    if (!entry.ok) {
      lineErrors.set(
        service.id,
        entry.error === 'The amount is too large.'
          ? `At most ${formatInr(service.left)} can be credited on this service.`
          : entry.error,
      );
    }
  }
  const lines: CreditNoteLineRequest[] = lineEntries.flatMap((e) =>
    e.parsed.ok ? [{ billLineId: e.service.id, amount: e.parsed.rupees }] : [],
  );
  const lineTotal = fromPaise(lines.reduce((sum, l) => sum + toPaise(l.amount), 0n));

  const parsed = parseRupees(amount, { max });
  const amountError =
    amount && !parsed.ok
      ? parsed.error === 'The amount is too large.'
        ? `At most ${formatInr(max)} can be credited on this bill.`
        : parsed.error
      : null;
  const totalError =
    byService && lineTotal > max
      ? `The services come to ${formatInr(lineTotal)}; at most ${formatInr(max)} can be credited on this bill.`
      : null;
  const valid =
    (byService ? lines.length > 0 && lineErrors.size === 0 && totalError === null : parsed.ok) &&
    reason.trim().length >= 5;

  const issue = useMutation({
    mutationFn: () =>
      byService
        ? billingApi.requestCreditNote(bill.id, kind, lineTotal, reason.trim(), lines)
        : billingApi.requestCreditNote(bill.id, kind, parsed.ok ? parsed.rupees : 0, reason.trim()),
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
    dirty: useValuesChanged({ kind, amount, lineAmounts, reason }, open),
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
                  {kinds.map((k) => (
                    <Radio
                      key={k}
                      value={k}
                      label={
                        k === 'CreditNote'
                          ? `${CREDIT_NOTE_KIND_LABEL[k]} — wrong rate or service not given`
                          : k === 'PayerShare'
                            ? `${CREDIT_NOTE_KIND_LABEL[k]} — up to ${formatInr(payerMax)}`
                            : `${CREDIT_NOTE_KIND_LABEL[k]} — the due cannot be recovered`
                      }
                    />
                  ))}
                </RadioGroup>
              </Field>
              {byService ? (
                <div className={styles.lines} role="group" aria-label="Services this credit note corrects">
                  <Text>
                    The bill carries GST, so name the services this credit note corrects and what it takes off each: the
                    GST it takes back is each service&apos;s own.
                  </Text>
                  {services.length === 0 && <Text>Every service on this bill is already credited in full.</Text>}
                  {services.map((service) => (
                    <AmountField
                      key={service.id}
                      label={`${service.description} · GST ${service.taxRate}%`}
                      hint={`Up to ${formatInr(service.left)}`}
                      value={lineAmounts[service.id] ?? ''}
                      onChange={(value) => setLineAmounts((current) => ({ ...current, [service.id]: value }))}
                      error={lineErrors.get(service.id) ?? null}
                    />
                  ))}
                  <Text>
                    Credit note: <Amount value={lineTotal} strong />
                  </Text>
                  {totalError && <Text role="alert">{totalError}</Text>}
                </div>
              ) : (
                <AmountField
                  label="Amount"
                  required
                  value={amount}
                  onChange={setAmount}
                  error={amountError}
                  autoFocus
                />
              )}
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
