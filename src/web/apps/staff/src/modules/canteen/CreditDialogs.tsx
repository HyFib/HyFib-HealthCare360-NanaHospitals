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
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, MoneyRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatInr, isApiError } from '@hc360/core';
import { DataTable, ErrorState, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { previousMonth } from '@/modules/inpatient/time';
import { canteenApi, canteenKeys, canteenQueries, CREDIT_KIND_LABEL, type SettlementResult, type StaffBalanceDto } from './api';
import { isValidEmployeeCode, monthLabel, recentMonths } from './rules';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  summary: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(140px, 1fr))', gap: '8px' },
  cell: { display: 'flex', flexDirection: 'column', padding: '8px 10px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground2 },
  value: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase400, fontFamily: tokens.fontFamilyNumeric },
  muted: { color: tokens.colorNeutralForeground3 },
  plus: { color: tokens.colorPaletteRedForeground1 },
  minus: { color: tokens.colorPaletteGreenForeground1 },
});

/** Month statement of one employee: opening balance, purchases, credits and a running balance. */
export function StatementPanel({ employeeCode, currentMonth }: { employeeCode: string; currentMonth: string }) {
  const styles = useStyles();
  const [period, setPeriod] = useState(currentMonth);
  const statement = useQuery(canteenQueries.statement(employeeCode, period));
  const s = statement.data;

  return (
    <div className={styles.stack}>
      <Field label="Month">
        <Select value={period} onChange={(_, d) => setPeriod(d.value)}>
          {recentMonths(currentMonth, 12).map((m) => (
            <option key={m} value={m}>
              {monthLabel(m)}
            </option>
          ))}
        </Select>
      </Field>
      {statement.error ? (
        <ErrorState error={statement.error} onRetry={() => void statement.refetch()} />
      ) : !s ? (
        <Spinner size="small" label="Loading the statement…" />
      ) : (
        <>
          <div className={styles.summary}>
            <div className={styles.cell}>
              <Caption1 className={styles.muted}>Opening</Caption1>
              <span className={styles.value}>{formatInr(s.openingBalance)}</span>
            </div>
            <div className={styles.cell}>
              <Caption1 className={styles.muted}>Purchases</Caption1>
              <span className={styles.value}>{formatInr(s.purchases)}</span>
            </div>
            <div className={styles.cell}>
              <Caption1 className={styles.muted}>Paid / deducted</Caption1>
              <span className={styles.value}>{formatInr(s.credits)}</span>
            </div>
            <div className={styles.cell}>
              <Caption1 className={styles.muted}>Closing</Caption1>
              <span className={styles.value}>{formatInr(s.closingBalance)}</span>
            </div>
          </div>
          <DataTable
            ariaLabel={`Statement for ${monthLabel(period)}`}
            rows={s.lines}
            getRowId={(l) => `${l.date}-${l.kind}-${l.reference ?? ''}-${l.balance}`}
            size="small"
            columns={[
              { key: 'date', header: 'Date', width: 120, render: (l) => formatDateOnly(l.date) },
              {
                key: 'what',
                header: 'Entry',
                render: (l) => (
                  <span>
                    {CREDIT_KIND_LABEL[l.kind]}
                    {l.reference && <Caption1 block>{l.reference}</Caption1>}
                  </span>
                ),
              },
              {
                key: 'amount',
                header: 'Amount',
                width: 110,
                align: 'end',
                render: (l) => <span className={l.amount > 0 ? styles.plus : styles.minus}>{l.amount > 0 ? `+${formatInr(l.amount)}` : formatInr(l.amount)}</span>,
              },
              { key: 'balance', header: 'Balance', width: 110, align: 'end', render: (l) => formatInr(l.balance) },
            ]}
            emptyTitle="No entries this month"
          />
        </>
      )}
    </div>
  );
}

/** Cash or UPI repayment of a staff balance at the counter. */
export function PaymentDialog({ balance, onClose }: { balance: StaffBalanceDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [code, setCode] = useState(balance?.employeeCode ?? '');
  const [amount, setAmount] = useState(balance && balance.balance > 0 ? String(balance.balance) : '');
  const [reference, setReference] = useState('');
  const value = Number(amount);
  const amountOk = amount !== '' && value > 0 && value <= 100_000 && Math.round(value * 100) === value * 100;
  const valid = isValidEmployeeCode(code) && amountOk;
  const pay = useMutation({
    mutationFn: () => canteenApi.recordPayment({ employeeCode: code.trim(), amount: value, reference: reference.trim() || null }),
    onSuccess: (updated) => {
      notify.success(`Payment recorded for ${updated.employeeName ?? updated.employeeCode}`, `Balance now ${formatInr(updated.balance)}.`);
      void queryClient.invalidateQueries({ queryKey: canteenKeys.all });
      onClose();
    },
  });
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !pay.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 460 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !pay.isPending) {
              pay.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Record staff payment</DialogTitle>
            <DialogContent className={styles.stack}>
              {balance && (
                <Text>
                  {balance.employeeName ?? balance.employeeCode} owes <strong>{formatInr(balance.balance)}</strong>.
                </Text>
              )}
              <Field label="Employee code" required validationMessage={code && !isValidEmployeeCode(code) ? 'Check the code on the staff ID card.' : undefined}>
                <Input value={code} onChange={(_, d) => setCode(d.value)} maxLength={20} disabled={Boolean(balance)} />
              </Field>
              <Field label="Amount paid (₹)" required validationMessage={amount && !amountOk ? 'Enter an amount up to ₹1,00,000 with at most two decimals.' : undefined}>
                <Input type="number" inputMode="decimal" min={0} value={amount} onChange={(_, d) => setAmount(d.value)} autoFocus />
              </Field>
              <Field label="Reference" hint="Receipt or UPI transaction number">
                <Input value={reference} onChange={(_, d) => setReference(d.value)} maxLength={64} />
              </Field>
              {pay.error && (
                <MessageBar intent="error">
                  <MessageBarBody>{isApiError(pay.error) ? pay.error.userMessage : 'The payment could not be recorded.'}</MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={pay.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={!valid || pay.isPending} icon={pay.isPending ? <Spinner size="tiny" /> : <MoneyRegular />}>
                Record payment
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/**
 * Month-end settlement: posts each employee's balance to payroll as a CANTEEN deduction (once per employee and month).
 * Balances that could not be deducted come back as plain-language lines to collect at the counter.
 */
export function SettleDialog({ currentMonth, onClose }: { currentMonth: string; onClose: () => void }) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const months = recentMonths(previousMonth(currentMonth), 6);
  const [period, setPeriod] = useState(months[0] ?? '');
  const [reference, setReference] = useState('');
  const [result, setResult] = useState<SettlementResult | null>(null);
  const settle = useMutation({
    mutationFn: () => canteenApi.settle({ period, reference: reference.trim(), employeeCodes: null }),
    onSuccess: (r) => {
      setResult(r);
      void queryClient.invalidateQueries({ queryKey: canteenKeys.all });
    },
  });
  const valid = Boolean(period) && reference.trim().length > 0;
  const skipped = result?.skipped ?? [];

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !settle.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        {result ? (
          <DialogBody>
            <DialogTitle>Settled {monthLabel(result.period)}</DialogTitle>
            <DialogContent className={styles.stack}>
              <div style={{ display: 'flex', gap: 10, alignItems: 'center' }} role="status">
                <CheckmarkCircleRegular fontSize={32} color={tokens.colorPaletteGreenForeground1} />
                <Text>
                  {result.employees} employees, <strong>{formatInr(result.amount)}</strong> sent to payroll as canteen deductions.
                </Text>
              </div>
              {skipped.length > 0 && (
                <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>{skipped.length} balances were not deducted</MessageBarTitle>
                    Collect these at the canteen counter:
                    <ul style={{ margin: '6px 0 0', paddingInlineStart: 20 }}>
                      {skipped.map((line) => (
                        <li key={line}>{line}</li>
                      ))}
                    </ul>
                  </MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="primary" onClick={onClose}>
                Done
              </Button>
            </DialogActions>
          </DialogBody>
        ) : (
          <form
            onSubmit={(event) => {
              event.preventDefault();
              if (valid && !settle.isPending) {
                settle.mutate();
              }
            }}
          >
            <DialogBody>
              <DialogTitle>Settle staff credit with payroll</DialogTitle>
              <DialogContent className={styles.stack}>
                <Text>
                  Each employee's balance up to the end of the month is deducted from salary. A month is settled once per employee, so running it again does not
                  deduct twice.
                </Text>
                <Field label="Month" required hint="Only months that have ended can be settled">
                  <Select value={period} onChange={(_, d) => setPeriod(d.value)}>
                    {months.map((m) => (
                      <option key={m} value={m}>
                        {monthLabel(m)}
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Payroll run reference" required>
                  <Input value={reference} onChange={(_, d) => setReference(d.value)} maxLength={64} placeholder="e.g. PAYROLL-2026-09" autoFocus />
                </Field>
                {settle.error && (
                  <MessageBar intent="error">
                    <MessageBarBody>{isApiError(settle.error) ? settle.error.userMessage : 'The settlement could not be completed.'}</MessageBarBody>
                  </MessageBar>
                )}
              </DialogContent>
              <DialogActions>
                <Button onClick={onClose} disabled={settle.isPending}>
                  Cancel
                </Button>
                <Button type="submit" appearance="primary" disabled={!valid || settle.isPending} icon={settle.isPending ? <Spinner size="tiny" /> : undefined}>
                  Settle {period ? monthLabel(period) : ''}
                </Button>
              </DialogActions>
            </DialogBody>
          </form>
        )}
      </DialogSurface>
    </Dialog>
  );
}
