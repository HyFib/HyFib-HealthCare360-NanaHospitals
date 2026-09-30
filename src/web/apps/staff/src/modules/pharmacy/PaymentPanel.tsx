import { Button, Caption1, Field, Input, makeStyles, Text, ToggleButton, tokens } from '@fluentui/react-components';
import { AddRegular, DeleteRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { useState } from 'react';
import { parseNumber } from '@/modules/inventory/format';
import { PAYMENT_MODES, paymentModeLabel, type PaymentMode } from './api';
import {
  changeDue,
  MAX_PAYMENTS,
  newPaymentKey,
  paymentsTotal,
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
}: {
  bill: number;
  drafts: PaymentDraft[];
  onChange: (drafts: PaymentDraft[]) => void;
  resolved: ResolvedPayment[];
  check: PaymentCheck;
  creditAllowed: boolean;
  disabled?: boolean;
}) {
  const styles = useStyles();
  const [cashReceived, setCashReceived] = useState('');
  const modes = PAYMENT_MODES.filter((m) => m !== 'Credit' || creditAllowed);
  const cashTotal = paymentsTotal(resolved.filter((p) => p.mode === 'Cash'));
  const change = changeDue(parseNumber(cashReceived), cashTotal);
  const balanceIndex = drafts.map((d) => d.amount === null).lastIndexOf(true);

  const update = (key: string, patch: Partial<PaymentDraft>) =>
    onChange(drafts.map((d) => (d.key === key ? { ...d, ...patch } : d)));
  const remove = (key: string) => onChange(drafts.filter((d) => d.key !== key));
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
        const needsReference = draft.mode === 'Card' || draft.mode === 'Upi';
        const missingAmount = draft.amount === null && !isBalance;
        return (
          <div key={draft.key} className={styles.payment}>
            <div className={styles.modes} role="group" aria-label={`Payment ${index + 1} mode`}>
              {modes.map((mode) => (
                <ToggleButton
                  key={mode}
                  size="small"
                  checked={draft.mode === mode}
                  disabled={disabled}
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
                  disabled={disabled}
                  value={draft.amount === null ? '' : String(draft.amount)}
                  placeholder={
                    isBalance ? (current ? `${current.amount.toFixed(2)} (balance)` : 'Nothing left') : 'Amount'
                  }
                  onChange={(_, d) => update(draft.key, { amount: parseNumber(d.value) })}
                />
              </Field>
              {needsReference && (
                <Field
                  label={draft.mode === 'Card' ? 'Card slip number' : 'UPI transaction ID'}
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
              {drafts.length > 1 && (
                <Button
                  appearance="subtle"
                  icon={<DeleteRegular />}
                  aria-label={`Remove payment ${index + 1}`}
                  disabled={disabled}
                  onClick={() => remove(draft.key)}
                />
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
