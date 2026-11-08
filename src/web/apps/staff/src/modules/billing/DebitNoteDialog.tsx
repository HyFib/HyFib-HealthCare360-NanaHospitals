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
import { billingApi, type BillDetail, type DebitNoteParty } from './api';
import { useRefreshBilling } from './hooks';
import { fromPaise, parseRupees, toPaise } from './money';
import { ActionError, Amount, AmountField } from './ui';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  lines: { display: 'flex', flexDirection: 'column', gap: '8px' },
});

/** The GST a debit note adds on `taxable` at `rate`% (paise), as `DebitNotes.Gst` rounds it. */
export function debitGst(taxable: number, rate: number): number {
  return fromPaise((toPaise(taxable) * BigInt(Math.round(rate * 100)) + 5000n) / 10000n);
}

/**
 * Asks for a debit note (a supplementary invoice, CGST Act s.34(3)) on a finalized bill: the services charged short and
 * the taxable value each is raised by, GST added at each service's rate, charged to the patient or the payer. An
 * approver, someone other than the person asking, issues it; it is numbered in its own series and, to a payer registered
 * for GST on a bill with GST, is an e-invoice (DBN).
 */
export function DebitNoteDialog({ bill, open, onClose }: { bill: BillDetail; open: boolean; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const refresh = useRefreshBilling();
  const hasPayer = bill.payerId !== null && bill.payerType !== 'Self';
  const [party, setParty] = useState<DebitNoteParty>('Patient');
  const [amounts, setAmounts] = useState<Record<string, string>>({});
  const [reason, setReason] = useState('');
  const services = bill.lines.filter((l) => !l.isReturn && (party === 'Patient' || l.isPayerPayable));
  const entries = services
    .filter((s) => (amounts[s.id] ?? '').trim() !== '')
    .map((s) => ({ service: s, parsed: parseRupees(amounts[s.id] ?? '') }));
  const lines = entries.flatMap((e) => (e.parsed.ok ? [{ billLineId: e.service.id, taxableAmount: e.parsed.rupees }] : []));
  const total = entries.reduce(
    (sum, e) => (e.parsed.ok ? sum + e.parsed.rupees + debitGst(e.parsed.rupees, e.service.taxRate) : sum),
    0,
  );
  const valid = lines.length > 0 && lines.length === entries.length && reason.trim().length >= 5;

  const issue = useMutation({
    mutationFn: () => billingApi.requestDebitNote(bill.id, party, reason.trim(), lines),
    onSuccess: (asked) => {
      notify.success('Debit note sent for approval', `${asked.summary}. An approver, not you, issues it.`);
      void queryClient.invalidateQueries({ queryKey: approvalKeys.all });
      void refresh();
      onClose();
    },
  });
  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ party, amounts, reason }, open),
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
            <DialogTitle>Debit note</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>
                Bill {bill.billNumber} · {bill.patientName}. A debit note raises what the bill charged for services on it
                (a rate charged short, a quantity under-billed). An approver, someone other than you, issues it.
              </Text>
              {hasPayer && (
                <Field label="Charged to" required>
                  <RadioGroup value={party} onChange={(_, data) => setParty(data.value as DebitNoteParty)}>
                    <Radio value="Patient" label="The patient" />
                    <Radio value="Payer" label={`The payer${bill.payerName ? ` (${bill.payerName})` : ''}`} />
                  </RadioGroup>
                </Field>
              )}
              <div className={styles.lines} role="group" aria-label="Services this debit note raises">
                <Text>Enter the taxable value each service is raised by; its GST is added at the service&apos;s rate.</Text>
                {services.map((service) => {
                  const entry = entries.find((e) => e.service.id === service.id);
                  return (
                    <AmountField
                      key={service.id}
                      label={`${service.description} · GST ${service.taxRate}%`}
                      value={amounts[service.id] ?? ''}
                      onChange={(value) => setAmounts((current) => ({ ...current, [service.id]: value }))}
                      error={entry && !entry.parsed.ok ? entry.parsed.error : null}
                    />
                  );
                })}
                <Text>
                  Debit note, GST included: <Amount value={total} strong />
                </Text>
              </div>
              <Field label="Reason" required hint="At least 5 characters. Printed on the debit note and kept in the audit trail.">
                <Textarea value={reason} onChange={(_, data) => setReason(data.value)} maxLength={300} rows={2} />
              </Field>
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
                disabledFocusable={issue.isPending}
                icon={issue.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Ask for debit note of {formatInr(total)}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
