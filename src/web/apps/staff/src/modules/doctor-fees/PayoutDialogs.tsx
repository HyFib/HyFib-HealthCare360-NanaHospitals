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
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, CheckmarkRegular, DeleteRegular, MoneyHandRegular, PrintRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatInr, todayIso } from '@hc360/core';
import {
  ConfirmDialog,
  ErrorState,
  LoadingState,
  StatusBadge,
  useNotify,
  useUnsavedChanges,
  useValuesChanged,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { useAttemptKey } from '@/modules/billing/hooks';
import { PAYMENT_MODE_LABEL, type PaymentMode } from '@/modules/billing/labels';
import { parsePercent, parseRupees } from '@/modules/billing/money';
import { sendToPrinter } from '@/modules/billing/print';
import { ActionError, Amount, AmountField, SummaryList, SummaryRow } from '@/modules/billing/ui';
import {
  feeApi,
  feeKeys,
  feeQueries,
  PAYOUT_MODE_LABEL,
  PAYOUT_MODES,
  PAYOUT_STATUS_LABEL,
  type PayoutDto,
} from './api';
import { PractitionerPicker, type PickedDoctor } from './PractitionerPicker';
import { buildPayoutAdviceHtml } from './print';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    gap: '12px',
    '@media (max-width: 600px)': { gridTemplateColumns: '1fr' },
  },
  deduction: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) 150px auto', gap: '8px', alignItems: 'end' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** First and last day of the previous calendar month (the usual payout period). */
function previousMonth(): { from: string; to: string } {
  const [year, month] = todayIso().split('-').map(Number) as [number, number];
  const start = new Date(Date.UTC(year, month - 2, 1));
  const end = new Date(Date.UTC(year, month - 1, 0));
  const iso = (d: Date) => d.toISOString().slice(0, 10);
  return { from: iso(start), to: iso(end) };
}

interface DeductionDraft {
  key: number;
  description: string;
  amount: string;
}

/**
 * Prepares a doctor's payout for a period (DFA-03): all unpaid fees in the period, less deductions and TDS. It is a
 * draft until someone other than the preparer approves it.
 */
export function CreatePayoutDialog({
  doctor: presetDoctor,
  onClose,
  onCreated,
}: {
  doctor?: PickedDoctor | null;
  onClose: () => void;
  onCreated: (id: string) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [doctor, setDoctor] = useState<PickedDoctor | null>(presetDoctor ?? null);
  const [period, setPeriod] = useState(previousMonth);
  const [tds, setTds] = useState('');
  const [notes, setNotes] = useState('');
  const [deductions, setDeductions] = useState<DeductionDraft[]>([]);

  const parsedTds = tds.trim() ? parsePercent(tds, { allowZero: true }) : null;
  const parsedDeductions = deductions.map((d) => ({ draft: d, amount: parseRupees(d.amount) }));
  const deductionProblem = parsedDeductions.some((d) => !d.amount.ok || !d.draft.description.trim());
  const periodOk = Boolean(period.from) && Boolean(period.to) && period.to >= period.from;
  const valid = doctor !== null && periodOk && (parsedTds === null || parsedTds.ok) && !deductionProblem;

  const create = useMutation({
    mutationFn: () =>
      feeApi.createPayout({
        practitionerId: doctor!.id,
        periodFrom: period.from,
        periodTo: period.to,
        deductions: parsedDeductions.length
          ? parsedDeductions.map((d) => ({
              description: d.draft.description.trim(),
              amount: d.amount.ok ? d.amount.rupees : 0,
            }))
          : null,
        tdsPercent: parsedTds?.ok ? parsedTds.percent : null,
        notes: notes.trim() || null,
      }),
    onSuccess: (created) => {
      notify.success('Payout prepared', 'Another person must approve it before it is paid.');
      void queryClient.invalidateQueries({ queryKey: feeKeys.all });
      onCreated(created.id);
    },
  });

  const guard = useUnsavedChanges({
    dirty: useValuesChanged({ doctor, period, tds, notes, deductions }),
    busy: create.isPending,
    onClose,
  });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 640, width: '100%' }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !create.isPending) {
              create.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Prepare a doctor payout</DialogTitle>
            <DialogContent className={styles.body}>
              <PractitionerPicker value={doctor} onChange={setDoctor} required autoFocus={!presetDoctor} />
              <div className={styles.grid}>
                <Field label="From" required>
                  <Input
                    type="date"
                    value={period.from}
                    max={todayIso()}
                    onChange={(_, d) => setPeriod((p) => ({ ...p, from: d.value }))}
                  />
                </Field>
                <Field
                  label="To"
                  required
                  validationMessage={periodOk ? undefined : 'The period must end on or after its start.'}
                  validationState={periodOk ? 'none' : 'error'}
                >
                  <Input
                    type="date"
                    value={period.to}
                    max={todayIso()}
                    onChange={(_, d) => setPeriod((p) => ({ ...p, to: d.value }))}
                  />
                </Field>
                <Field
                  label="TDS (%)"
                  hint="Empty uses the hospital setting (usually 10 % under section 194J)"
                  validationMessage={parsedTds && !parsedTds.ok ? parsedTds.error : undefined}
                  validationState={parsedTds && !parsedTds.ok ? 'error' : 'none'}
                >
                  <Input
                    value={tds}
                    onChange={(_, d) => setTds(d.value)}
                    inputMode="decimal"
                    contentAfter={<Text>%</Text>}
                  />
                </Field>
              </div>
              <Text weight="semibold">Deductions</Text>
              {deductions.map((d) => (
                <div key={d.key} className={styles.deduction}>
                  <Field label="Description">
                    <Input
                      value={d.description}
                      onChange={(_, e) =>
                        setDeductions((list) => list.map((x) => (x.key === d.key ? { ...x, description: e.value } : x)))
                      }
                      maxLength={200}
                      placeholder="e.g. Advance paid on 5 Sep, consumables recovery"
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
              {deductions.length < 20 && (
                <div>
                  <Button
                    icon={<AddRegular />}
                    onClick={() =>
                      setDeductions((list) => [
                        ...list,
                        { key: (list.at(-1)?.key ?? 0) + 1, description: '', amount: '' },
                      ])
                    }
                  >
                    Add a deduction
                  </Button>
                </div>
              )}
              {deductionProblem && (
                <Caption1 style={{ color: tokens.colorPaletteRedForeground1 }}>
                  Every deduction needs a description and an amount.
                </Caption1>
              )}
              <Field label="Notes">
                <Textarea value={notes} onChange={(_, d) => setNotes(d.value)} maxLength={500} rows={2} />
              </Field>
              <Caption1 className={styles.muted}>
                All unpaid fees of the doctor in the period are included; they cannot go into another payout while this
                one exists.
              </Caption1>
              <ActionError error={create.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={create.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || create.isPending}
                icon={create.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Prepare payout
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

type PayoutStep = 'approve' | 'pay' | 'discard' | null;

/** One payout: amounts, deductions and TDS; approve (not by its preparer), pay with a reference, or discard the draft. */
export function PayoutDialog({ payoutId, onClose }: { payoutId: string; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const [attemptKey] = useAttemptKey();
  const [step, setStep] = useState<PayoutStep>(null);
  const [mode, setMode] = useState<PaymentMode>('NetBanking');
  const [reference, setReference] = useState('');
  const payout = useQuery(feeQueries.payout(payoutId));

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: feeKeys.all });
  };

  const act = useMutation({
    mutationFn: (p: PayoutDto) => {
      if (step === 'approve') {
        return feeApi.approvePayout(p.id, p.version);
      }
      if (step === 'pay') {
        return feeApi.payPayout(p.id, mode, reference.trim() || null, p.version, attemptKey);
      }
      return feeApi.discardPayout(p.id, p.version);
    },
    onSuccess: () => {
      notify.success(
        step === 'approve' ? 'Payout approved' : step === 'pay' ? 'Payout marked as paid' : 'Draft payout discarded',
      );
      const discarded = step === 'discard';
      setStep(null);
      refresh();
      if (discarded) {
        onClose();
      }
    },
  });

  const p = payout.data;
  const print = () =>
    p &&
    void sendToPrinter(buildPayoutAdviceHtml(p, letterhead), 'A4').catch((error: unknown) =>
      notify.error(error, 'Printing failed'),
    );
  const needsReference = mode !== 'Cash' && !reference.trim();

  return (
    <>
      <Dialog open onOpenChange={(_, d) => !d.open && !act.isPending && onClose()}>
        <DialogSurface style={{ maxWidth: 600, width: '100%' }}>
          <DialogBody>
            <DialogTitle>{p ? `Payout ${p.payoutNumber}` : 'Payout'}</DialogTitle>
            <DialogContent className={styles.body}>
              {payout.isLoading ? (
                <LoadingState />
              ) : payout.error || !p ? (
                <ErrorState error={payout.error} onRetry={() => void payout.refetch()} />
              ) : (
                <>
                  <Text>
                    <Text weight="semibold">{p.practitionerName}</Text> · {formatDateOnly(p.periodFrom)} to{' '}
                    {formatDateOnly(p.periodTo)} ·{' '}
                    <StatusBadge status={p.status} label={PAYOUT_STATUS_LABEL[p.status]} />
                  </Text>
                  <SummaryList>
                    <SummaryRow label={`Fees (${p.accrualCount} lines)`}>
                      <Amount value={p.grossAmount} />
                    </SummaryRow>
                    {p.deductions.map((d) => (
                      <SummaryRow key={d.id} label={`Less: ${d.description}`}>
                        <Amount value={-d.amount} />
                      </SummaryRow>
                    ))}
                    <SummaryRow label={`Less: TDS ${p.tdsPercent}%`}>
                      <Amount value={-p.tdsAmount} />
                    </SummaryRow>
                    <SummaryRow label="Net to pay" strong>
                      <Amount value={p.netAmount} size="large" />
                    </SummaryRow>
                  </SummaryList>
                  {p.approvedAt && (
                    <Caption1 className={styles.muted}>Approved {formatDateTime(p.approvedAt)}</Caption1>
                  )}
                  {p.paidAt && (
                    <Caption1 className={styles.muted}>
                      Paid {formatDateTime(p.paidAt)}
                      {p.paymentMode ? ` by ${PAYMENT_MODE_LABEL[p.paymentMode]}` : ''}
                      {p.paidReference ? ` · ref ${p.paidReference}` : ''}
                    </Caption1>
                  )}
                  {p.notes && <Text>{p.notes}</Text>}
                  {step === 'pay' && (
                    <div className={styles.grid}>
                      <Field label="Paid by" required>
                        <Select value={mode} onChange={(_, d) => setMode(d.value as PaymentMode)}>
                          {PAYOUT_MODES.map((m) => (
                            <option key={m} value={m}>
                              {PAYOUT_MODE_LABEL[m]}
                            </option>
                          ))}
                        </Select>
                      </Field>
                      <Field label="Transfer or cheque reference" required={mode !== 'Cash'}>
                        <Input value={reference} onChange={(_, d) => setReference(d.value)} maxLength={64} autoFocus />
                      </Field>
                    </div>
                  )}
                  {p.status === 'Draft' && (
                    <MessageBar intent="info">
                      <MessageBarBody>The person who prepared this payout cannot approve it.</MessageBarBody>
                    </MessageBar>
                  )}
                  <ActionError error={act.error} onReload={() => void payout.refetch()} />
                </>
              )}
            </DialogContent>
            <DialogActions>
              {p && (
                <Button icon={<PrintRegular />} onClick={print}>
                  Payout advice
                </Button>
              )}
              {p?.status === 'Draft' && (
                <>
                  <Button icon={<DeleteRegular />} onClick={() => setStep('discard')} disabled={act.isPending}>
                    Discard
                  </Button>
                  <Button
                    appearance="primary"
                    icon={<CheckmarkRegular />}
                    onClick={() => setStep('approve')}
                    disabled={act.isPending}
                  >
                    Approve
                  </Button>
                </>
              )}
              {p?.status === 'Approved' &&
                (step === 'pay' ? (
                  <Button
                    appearance="primary"
                    icon={act.isPending ? <Spinner size="tiny" aria-hidden /> : <MoneyHandRegular />}
                    disabled={needsReference || act.isPending}
                    onClick={() => act.mutate(p)}
                  >
                    Confirm paid · {formatInr(p.netAmount)}
                  </Button>
                ) : (
                  <Button appearance="primary" icon={<MoneyHandRegular />} onClick={() => setStep('pay')}>
                    Record payment
                  </Button>
                ))}
              <Button onClick={onClose} disabled={act.isPending}>
                Close
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      {p && (
        <ConfirmDialog
          open={step === 'approve' || step === 'discard'}
          title={step === 'approve' ? `Approve the payout of ${formatInr(p.netAmount)}?` : 'Discard this draft payout?'}
          confirmLabel={step === 'approve' ? 'Approve' : 'Discard'}
          destructive={step === 'discard'}
          busy={act.isPending}
          onConfirm={() => act.mutate(p)}
          onCancel={() => {
            setStep(null);
            act.reset();
          }}
        >
          {step === 'approve'
            ? `${p.practitionerName ?? 'The doctor'} will be paid ${formatInr(p.netAmount)} for ${formatDateOnly(p.periodFrom)} to ${formatDateOnly(p.periodTo)}.`
            : 'Its fees become unpaid again and can go into a new payout.'}
          <ActionError error={act.error} />
        </ConfirmDialog>
      )}
    </>
  );
}
