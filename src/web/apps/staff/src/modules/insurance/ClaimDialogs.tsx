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
  Radio,
  RadioGroup,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr, todayIso } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { billingQueries } from '@/modules/billing/api';
import { useAttemptKey, useRefreshBilling } from '@/modules/billing/hooks';
import { amountInputText, fromPaise, maxBig, parseRupees, toPaise } from '@/modules/billing/money';
import { ActionError, Amount, AmountField, SummaryList, SummaryRow } from '@/modules/billing/ui';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import { insuranceApi, insuranceKeys, type ClaimDetail } from './api';
import { Attachments } from './Attachments';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '12px',
    '@media (max-width: 640px)': { gridTemplateColumns: '1fr' },
  },
  deduction: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) 150px auto', gap: '8px', alignItems: 'end' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Raises a claim on the payer share of a finalised bill (INS-05); codes default to the pre-authorisation's. */
export function CreateClaimDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (id: string) => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [billId, setBillId] = useState('');
  const [amount, setAmount] = useState('');
  const [reference, setReference] = useState('');
  const [diagnosis, setDiagnosis] = useState('');
  const [procedures, setProcedures] = useState('');
  const [documents, setDocuments] = useState<string[]>([]);
  const bills = useQuery({
    ...billingQueries.bills({ patientId: patient?.id, pageSize: 50 }),
    enabled: patient !== null,
  });
  const claimable = (bills.data?.items ?? []).filter(
    (b) => b.payerAmount > 0 && (b.status === 'Finalized' || b.status === 'PartiallyPaid' || b.status === 'Paid'),
  );
  const bill = claimable.find((b) => b.id === billId);
  const parsed = amount.trim() ? parseRupees(amount, { max: bill?.payerAmount }) : null;
  const valid = bill !== undefined && (parsed === null || parsed.ok);

  const create = useMutation({
    mutationFn: () =>
      insuranceApi.createClaim({
        billId,
        claimedAmount: parsed?.ok ? parsed.rupees : null,
        diagnosisCodes: diagnosis.trim() || null,
        procedureCodes: procedures.trim() || null,
        documentFileIds: documents.length ? documents : null,
        payerClaimReference: reference.trim() || null,
      }),
    onSuccess: (created) => {
      notify.success('Claim submitted', 'Track the insurer’s queries and payments on the claim.');
      void queryClient.invalidateQueries({ queryKey: insuranceKeys.claims });
      onCreated(created.id);
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !create.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 720, width: '100%' }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !create.isPending) {
              create.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>New claim</DialogTitle>
            <DialogContent className={styles.body}>
              <PatientPicker
                value={patient}
                onChange={(p) => {
                  setPatient(p);
                  setBillId('');
                }}
                required
              />
              {patient && (
                <Field
                  label="Bill"
                  required
                  hint={
                    bills.data && claimable.length === 0
                      ? 'This patient has no finalised bill with an insurer share.'
                      : undefined
                  }
                >
                  <RadioGroup value={billId} onChange={(_, d) => setBillId(d.value)}>
                    {claimable.map((b) => (
                      <Radio
                        key={b.id}
                        value={b.id}
                        label={`${b.billNumber} · ${formatDateTime(b.finalizedAt)} · ${b.payerName ?? 'payer'} pays ${formatInr(b.payerAmount)}`}
                      />
                    ))}
                  </RadioGroup>
                </Field>
              )}
              {bill && (
                <>
                  <div className={styles.grid}>
                    <AmountField
                      label="Amount claimed"
                      value={amount}
                      onChange={setAmount}
                      error={
                        parsed && !parsed.ok
                          ? parsed.error === 'The amount is too large.'
                            ? `At most ${formatInr(bill.payerAmount)}`
                            : parsed.error
                          : null
                      }
                      hint="Empty claims the whole outstanding insurer share"
                    />
                    <Field label="Insurer / TPA claim number" hint="Optional, from their portal">
                      <Input value={reference} onChange={(_, d) => setReference(d.value)} maxLength={64} />
                    </Field>
                  </div>
                  <Field label="Diagnosis (ICD-10)" hint="Empty uses the codes of the pre-authorisation">
                    <Textarea value={diagnosis} onChange={(_, d) => setDiagnosis(d.value)} maxLength={1000} rows={2} />
                  </Field>
                  <Field label="Procedures" hint="Empty uses the codes of the pre-authorisation">
                    <Textarea
                      value={procedures}
                      onChange={(_, d) => setProcedures(d.value)}
                      maxLength={1000}
                      rows={2}
                    />
                  </Field>
                  <Field label="Claim documents" hint="Final bill, discharge summary, reports">
                    <Attachments fileIds={documents} patientId={bill.patientId} onChange={setDocuments} />
                  </Field>
                </>
              )}
              <ActionError error={create.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={create.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || create.isPending}
                icon={create.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Submit claim
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/** Records the insurer's approved amount (at most the claimed amount). */
export function ApproveClaimDialog({ claim, onClose }: { claim: ClaimDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [amount, setAmount] = useState(amountInputText(claim.claimedAmount));
  const parsed = parseRupees(amount, { max: claim.claimedAmount });
  const approve = useMutation({
    mutationFn: () => insuranceApi.approveClaim(claim.id, parsed.ok ? parsed.rupees : 0),
    onSuccess: () => {
      notify.success('Approval recorded', parsed.ok ? formatInr(parsed.rupees) : undefined);
      void queryClient.invalidateQueries({ queryKey: insuranceKeys.claims });
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !approve.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 460 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (parsed.ok && !approve.isPending) {
              approve.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Insurer approved the claim</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>
                Claimed <Amount value={claim.claimedAmount} strong />. Enter the amount the insurer approved.
              </Text>
              <AmountField
                label="Approved amount"
                required
                value={amount}
                onChange={setAmount}
                error={
                  amount && !parsed.ok
                    ? parsed.error === 'The amount is too large.'
                      ? `At most the claimed ${formatInr(claim.claimedAmount)}`
                      : parsed.error
                    : null
                }
                autoFocus
              />
              <ActionError error={approve.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={approve.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!parsed.ok || approve.isPending}
                icon={approve.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Record approval
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

interface DeductionDraft {
  key: number;
  reason: string;
  amount: string;
}

/**
 * Records a settlement tranche (INS-06): money received (with the bank UTR) and itemised deductions with reasons.
 * Received plus deducted can never exceed what is outstanding; the claim closes when nothing is left.
 */
export function SettleClaimDialog({ claim, onClose }: { claim: ClaimDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const refresh = useRefreshBilling();
  const [attemptKey] = useAttemptKey();
  const [amount, setAmount] = useState(amountInputText(claim.outstanding));
  const [utr, setUtr] = useState('');
  const [reference, setReference] = useState('');
  const [settledOn, setSettledOn] = useState(todayIso());
  const [deductions, setDeductions] = useState<DeductionDraft[]>([]);

  const received = parseRupees(amount, { allowZero: true });
  const parsedDeductions = deductions.map((d) => ({ draft: d, amount: parseRupees(d.amount) }));
  const deductionProblem = parsedDeductions.find((d) => !d.amount.ok || !d.draft.reason.trim());
  const deducted = parsedDeductions.reduce((sum, d) => sum + (d.amount.ok ? d.amount.paise : 0n), 0n);
  const total = (received.ok ? received.paise : 0n) + deducted;
  const outstanding = toPaise(claim.outstanding);
  const left = fromPaise(maxBig(outstanding - total, 0n));
  const exceeds = total > outstanding;
  const needsUtr = received.ok && received.paise > 0n && !utr.trim();
  const valid = received.ok && total > 0n && !exceeds && !deductionProblem && !needsUtr && Boolean(settledOn);

  const settle = useMutation({
    mutationFn: () =>
      insuranceApi.settleClaim(
        claim.id,
        {
          amount: received.ok ? received.rupees : 0,
          deductions: parsedDeductions.length
            ? parsedDeductions.map((d) => ({
                reason: d.draft.reason.trim(),
                amount: d.amount.ok ? d.amount.rupees : 0,
              }))
            : null,
          utrNumber: utr.trim() || null,
          reference: reference.trim() || null,
          settledOn,
        },
        attemptKey,
      ),
    onSuccess: () => {
      notify.success(
        'Settlement recorded',
        left > 0 ? `${formatInr(left)} is still outstanding.` : 'The claim is fully settled.',
      );
      void queryClient.invalidateQueries({ queryKey: insuranceKeys.all });
      void refresh();
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !settle.isPending && onClose()} modalType="alert">
      <DialogSurface style={{ maxWidth: 680, width: '100%' }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !settle.isPending) {
              settle.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Record settlement · {claim.claimNumber}</DialogTitle>
            <DialogContent className={styles.body}>
              <div className={styles.grid}>
                <AmountField
                  label="Amount received"
                  value={amount}
                  onChange={setAmount}
                  error={amount && !received.ok ? received.error : null}
                  hint="Enter 0 if this tranche only has deductions"
                  autoFocus
                />
                <Field
                  label="Bank UTR"
                  required={received.ok && received.paise > 0n}
                  validationMessage={needsUtr ? 'Enter the UTR of the bank transfer.' : undefined}
                  validationState={needsUtr ? 'error' : 'none'}
                >
                  <Input value={utr} onChange={(_, d) => setUtr(d.value)} maxLength={64} />
                </Field>
                <Field label="Settlement letter reference">
                  <Input value={reference} onChange={(_, d) => setReference(d.value)} maxLength={64} />
                </Field>
                <Field label="Settled on" required>
                  <Input type="date" value={settledOn} max={todayIso()} onChange={(_, d) => setSettledOn(d.value)} />
                </Field>
              </div>

              <Text weight="semibold">Deductions by the insurer</Text>
              {deductions.map((d) => (
                <div key={d.key} className={styles.deduction}>
                  <Field label="Reason">
                    <Input
                      value={d.reason}
                      onChange={(_, e) =>
                        setDeductions((list) => list.map((x) => (x.key === d.key ? { ...x, reason: e.value } : x)))
                      }
                      placeholder="e.g. Non-payable consumables, room rent above limit"
                      maxLength={300}
                    />
                  </Field>
                  <AmountField
                    label="Amount"
                    value={d.amount}
                    onChange={(value) =>
                      setDeductions((list) => list.map((x) => (x.key === d.key ? { ...x, amount: value } : x)))
                    }
                  />
                  <Button
                    appearance="subtle"
                    icon={<DeleteRegular />}
                    aria-label="Remove deduction"
                    onClick={() => setDeductions((list) => list.filter((x) => x.key !== d.key))}
                  />
                </div>
              ))}
              {deductions.length < 50 && (
                <div>
                  <Button
                    icon={<AddRegular />}
                    onClick={() =>
                      setDeductions((list) => [...list, { key: (list.at(-1)?.key ?? 0) + 1, reason: '', amount: '' }])
                    }
                  >
                    Add a deduction
                  </Button>
                </div>
              )}
              {deductionProblem && (
                <Caption1 style={{ color: tokens.colorPaletteRedForeground1 }}>
                  Every deduction needs a reason and an amount.
                </Caption1>
              )}

              <SummaryList>
                <SummaryRow label="Outstanding on the claim">
                  <Amount value={claim.outstanding} />
                </SummaryRow>
                <SummaryRow label="This settlement (received + deducted)">
                  <Amount value={fromPaise(total)} strong tone={exceeds ? 'negative' : undefined} />
                </SummaryRow>
                <SummaryRow label="Left outstanding" strong>
                  <Amount value={left} strong />
                </SummaryRow>
              </SummaryList>
              {exceeds && (
                <MessageBar intent="error">
                  <MessageBarBody>
                    Received plus deductions cannot be more than the {formatInr(claim.outstanding)} outstanding.
                  </MessageBarBody>
                </MessageBar>
              )}
              <Caption1 className={styles.muted}>
                The money is recorded as an insurer settlement receipt against the bill; deductions are written off in
                the day book.
              </Caption1>
              <ActionError error={settle.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={settle.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || settle.isPending}
                icon={settle.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Record settlement
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
