import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  Text,
  ToggleButton,
  Tooltip,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, PaymentRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { useState } from 'react';
import { parseNumber } from '@/modules/inventory/format';
import { terminalDetailsLine } from '@/lib/terminal/labels';
import { TerminalSaleDialog } from '@/lib/terminal/TerminalSaleDialog';
import { toTerminalPaid } from '@/lib/terminal/types';
import { PAYMENT_MODES, paymentModeLabel, pharmacyTerminalApi, type PaymentMode } from './api';
import {
  changeDue,
  MAX_PAYMENTS,
  newPaymentKey,
  paymentsTotal,
  roomForRow,
  type PaymentCheck,
  type PaymentDraft,
  type ResolvedPayment,
} from './payments';

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px' },
  payment: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  modes: { display: 'flex', gap: '4px', flexWrap: 'wrap' },
  fields: { display: 'flex', gap: '8px', alignItems: 'flex-end', flexWrap: 'wrap' },
  amount: { width: '150px' },
  reference: { flex: '1 1 140px' },
  footer: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  warn: { color: tokens.colorPaletteMarigoldForeground1 },
  ok: { color: tokens.colorPaletteGreenForeground1 },
  change: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
});

/**
 * How the bill is paid: one tap for the mode, the amount follows the bill; split across cash, card, UPI and credit
 * (registered patients only) when needed. Card and UPI need the slip or transaction reference for reconciliation.
 */
export function PaymentPanel({
  bill,
  drafts,
  onChange,
  resolved,
  check,
  creditAllowed,
  disabled,
  patientId,
}: {
  bill: number;
  drafts: PaymentDraft[];
  onChange: (drafts: PaymentDraft[]) => void;
  resolved: ResolvedPayment[];
  check: PaymentCheck;
  creditAllowed: boolean;
  disabled?: boolean;
  /** The registered patient the bill is for (passed to the terminal); walk-ins have none. */
  patientId?: string | null;
}) {
  const styles = useStyles();
  const [cashReceived, setCashReceived] = useState('');
  const [terminalKey, setTerminalKey] = useState<string | null>(null);
  const modes = PAYMENT_MODES.filter((m) => m !== 'Credit' || creditAllowed);
  const cashTotal = paymentsTotal(resolved.filter((p) => p.mode === 'Cash'));
  const change = changeDue(parseNumber(cashReceived), cashTotal);
  const balanceIndex = drafts.map((d) => d.amount === null).lastIndexOf(true);

  const update = (key: string, patch: Partial<PaymentDraft>) =>
    onChange(drafts.map((d) => (d.key === key ? { ...d, ...patch } : d)));
  const remove = (key: string) => onChange(drafts.filter((d) => d.key !== key));
  /** Takes the terminal payment off a row; with no other row, it goes back to a cash balance row. */
  const detach = (key: string) =>
    onChange(
      drafts.length > 1
        ? drafts.filter((d) => d.key !== key)
        : drafts.map((d) => (d.key === key ? { ...d, mode: 'Cash' as const, amount: null, terminal: null } : d)),
    );
  const terminalDraft = drafts.find((d) => d.key === terminalKey);
  const terminalCurrent = resolved.find((p) => p.key === terminalKey);
  const add = () => {
    const used = new Set(drafts.map((d) => d.mode));
    const mode: PaymentMode = (['Upi', 'Card', 'Cash'] as const).find((m) => !used.has(m)) ?? 'Cash';
    onChange([...drafts, { key: newPaymentKey(), mode, amount: null, reference: '' }]);
  };

  return (
    <div className={styles.list}>
      {drafts.map((draft, index) => {
        const current = resolved.find((p) => p.key === draft.key);
        const isBalance = index === balanceIndex;
        const paidOnTerminal = draft.terminal ?? null;
        const needsReference = (draft.mode === 'Card' || draft.mode === 'Upi') && !paidOnTerminal;
        const missingAmount = draft.amount === null && !isBalance;
        return (
          <div key={draft.key} className={styles.payment}>
            <div className={styles.modes} role="group" aria-label={`Payment ${index + 1} mode`}>
              {modes.map((mode) => (
                <ToggleButton
                  key={mode}
                  size="small"
                  checked={draft.mode === mode}
                  disabled={disabled || paidOnTerminal !== null}
                  onClick={() => update(draft.key, { mode })}
                >
                  {mode === 'Credit' ? 'Credit' : paymentModeLabel[mode]}
                </ToggleButton>
              ))}
            </div>
            <div className={styles.fields}>
              <Field
                label="Amount"
                size="small"
                className={styles.amount}
                validationState={missingAmount ? 'error' : 'none'}
                validationMessage={missingAmount ? 'Enter the amount' : undefined}
              >
                <Input
                  type="number"
                  min={0}
                  step="0.01"
                  contentBefore="₹"
                  disabled={disabled || paidOnTerminal !== null}
                  value={
                    paidOnTerminal ? String(paidOnTerminal.amount) : draft.amount === null ? '' : String(draft.amount)
                  }
                  placeholder={
                    isBalance ? (current ? `${current.amount.toFixed(2)} (balance)` : 'Nothing left') : 'Amount'
                  }
                  onChange={(_, d) => update(draft.key, { amount: parseNumber(d.value) })}
                />
              </Field>
              {needsReference && (
                <Field
                  label={draft.mode === 'Card' ? 'RRN or approval code' : 'UPI reference (UTR)'}
                  hint={draft.mode === 'Card' ? 'From the charge slip' : 'The 12-digit number on the payer’s screen'}
                  size="small"
                  required
                  className={styles.reference}
                >
                  <Input
                    value={draft.reference}
                    maxLength={64}
                    disabled={disabled}
                    onChange={(_, d) => update(draft.key, { reference: d.value })}
                  />
                </Field>
              )}
              {(draft.mode === 'Card' || draft.mode === 'Upi') && !paidOnTerminal && (
                <Button
                  size="small"
                  icon={<PaymentRegular />}
                  disabled={disabled || !current || !(current.amount > 0)}
                  onClick={() => setTerminalKey(draft.key)}
                >
                  Take on terminal
                </Button>
              )}
              {paidOnTerminal && (
                <Text className={styles.reference}>
                  {['Paid on the terminal', terminalDetailsLine(paidOnTerminal) || null].filter(Boolean).join(' · ')}
                </Text>
              )}
              {paidOnTerminal ? (
                <Tooltip
                  content="Removing this does not return the money: it stays on the terminal and can be used again from “Already paid on the terminal”."
                  relationship="description"
                >
                  <Button
                    appearance="subtle"
                    icon={<DeleteRegular />}
                    aria-label={`Remove payment ${index + 1}`}
                    disabled={disabled}
                    onClick={() => detach(draft.key)}
                  />
                </Tooltip>
              ) : (
                drafts.length > 1 && (
                  <Button
                    appearance="subtle"
                    icon={<DeleteRegular />}
                    aria-label={`Remove payment ${index + 1}`}
                    disabled={disabled}
                    onClick={() => remove(draft.key)}
                  />
                )
              )}
            </div>
            {draft.mode === 'Credit' && (
              <Caption1>Put on the patient's account and collected later from Invoices.</Caption1>
            )}
          </div>
        );
      })}

      <div className={styles.footer}>
        <Button size="small" icon={<AddRegular />} disabled={disabled || drafts.length >= MAX_PAYMENTS} onClick={add}>
          Split payment
        </Button>
        {bill > 0 &&
          (check.ok ? (
            <Caption1 className={styles.ok}>Payments match the bill</Caption1>
          ) : (
            <Caption1 className={styles.warn}>{check.message}</Caption1>
          ))}
      </div>

      {terminalDraft && terminalCurrent && (
        <TerminalSaleDialog
          open
          api={pharmacyTerminalApi}
          counter="pharmacy"
          amount={terminalCurrent.amount}
          initialMode={terminalDraft.mode === 'Upi' ? 'Upi' : 'Card'}
          patientId={patientId}
          maxAmount={roomForRow(bill, resolved, terminalDraft.key)}
          exact={false}
          approveLabel="Use this payment"
          onClose={() => setTerminalKey(null)}
          onApproved={(sale) => {
            update(terminalDraft.key, {
              terminal: toTerminalPaid(sale),
              mode: sale.mode,
              amount: sale.amount,
              reference: '',
            });
            setTerminalKey(null);
          }}
        />
      )}

      {cashTotal > 0 && (
        <div className={styles.fields}>
          <Field
            label="Cash received (optional)"
            size="small"
            hint="Works out the change to return"
            className={styles.amount}
          >
            <Input
              type="number"
              min={0}
              contentBefore="₹"
              value={cashReceived}
              onChange={(_, d) => setCashReceived(d.value)}
              disabled={disabled}
            />
          </Field>
          {change > 0 && (
            <Text className={styles.change} aria-live="polite">
              Return {formatInr(change)}
            </Text>
          )}
        </div>
      )}
      {check.ok && check.due > 0 && (
        <Caption1 className={styles.warn}>
          {formatInr(check.due)} will be left as a balance due on this invoice.
        </Caption1>
      )}
    </div>
  );
}
