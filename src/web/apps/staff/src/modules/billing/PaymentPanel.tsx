import { Button, Caption1, Field, Input, makeStyles, MessageBar, MessageBarBody, Select, Text, tokens, ToggleButton } from '@fluentui/react-components';
import {
  AddRegular,
  BuildingBankRegular,
  DeleteRegular,
  DocumentTextRegular,
  MoneyRegular,
  PaymentRegular,
  QrCodeRegular,
  WalletRegular,
} from '@fluentui/react-icons';
import type { FluentIcon } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { COUNTER_PAYMENT_MODES, PAYMENT_MODE_LABEL, type CounterPaymentMode } from './labels';
import { amountInputText, fromPaise, parseRupees, toPaise } from './money';
import { checkPaymentLine, MAX_SPLIT_MODES, newPaymentLine, summariseSplit, type PaymentLineDraft } from './paymentRules';
import { Amount, AmountField } from './ui';

const MODE_ICON: Record<CounterPaymentMode, FluentIcon> = {
  Cash: MoneyRegular,
  Card: PaymentRegular,
  Upi: QrCodeRegular,
  NetBanking: BuildingBankRegular,
  Cheque: DocumentTextRegular,
  Wallet: WalletRegular,
};

const CARD_NETWORKS = ['Visa', 'Mastercard', 'RuPay', 'American Express', 'Diners Club', 'Other'];

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '12px' },
  modes: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  line: {
    display: 'grid',
    gridTemplateColumns: 'minmax(130px, 160px) minmax(130px, 170px) 1fr auto',
    gap: '8px 12px',
    alignItems: 'end',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
    '@media (max-width: 760px)': { gridTemplateColumns: '1fr 1fr' },
  },
  evidence: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'end' },
  error: { gridColumn: '1 / -1', color: tokens.colorPaletteRedForeground1 },
  summary: {
    display: 'flex',
    gap: '24px',
    flexWrap: 'wrap',
    alignItems: 'baseline',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  cash: { display: 'flex', gap: '16px', alignItems: 'end', flexWrap: 'wrap' },
  change: { display: 'flex', flexDirection: 'column', gap: '2px' },
  changeValue: { fontSize: '28px', lineHeight: '34px', fontWeight: tokens.fontWeightBold, color: tokens.colorPaletteGreenForeground1 },
  muted: { color: tokens.colorNeutralForeground3 },
});

export interface PaymentPanelProps {
  /** Balance being collected; null when the cashier decides the amount (IP advance). */
  due: number | null;
  lines: PaymentLineDraft[];
  onLinesChange: (lines: PaymentLineDraft[]) => void;
  cashTendered: string;
  onCashTenderedChange: (value: string) => void;
  /** Show every problem (after the first attempt to collect). */
  showErrors: boolean;
  /** Hospital date (yyyy-MM-dd) for cheque validity. */
  today: string;
  disabled?: boolean;
}

/**
 * Split payment at the counter (OPB-05): one line per mode with the evidence the bank reconciliation needs (card last
 * 4 digits, UPI/transfer reference, cheque details), the running balance and — for cash — the change to hand back.
 */
export function PaymentPanel({ due, lines, onLinesChange, cashTendered, onCashTenderedChange, showErrors, today, disabled }: PaymentPanelProps) {
  const styles = useStyles();
  const summary = summariseSplit(lines, due, cashTendered);
  const hasCash = lines.some((line) => line.mode === 'Cash');

  const update = (key: string, patch: Partial<PaymentLineDraft>) => onLinesChange(lines.map((line) => (line.key === key ? { ...line, ...patch } : line)));

  const pickMode = (mode: CounterPaymentMode) => {
    const only = lines.length === 1 ? lines[0] : undefined;
    const untouched = only && !only.reference && !only.cardLast4 && !only.chequeNumber && !only.chequeBank;
    if (only && untouched) {
      onLinesChange([{ ...only, mode }]);
      return;
    }
    if (lines.length >= MAX_SPLIT_MODES) {
      return;
    }
    const remaining = due === null ? null : Math.max(summary.remaining, 0);
    onLinesChange([...lines, newPaymentLine(mode, remaining)]);
  };

  /** The amount this line needs so that nothing is left to collect (null when that is not a positive amount). */
  const balanceFor = (line: PaymentLineDraft): number | null => {
    const parsed = parseRupees(line.amount);
    const current = parsed.ok ? parsed.paise : 0n;
    const target = toPaise(summary.remaining) + current;
    return target > 0n && target !== current ? fromPaise(target) : null;
  };

  return (
    <div className={styles.root}>
      <div className={styles.modes} role="group" aria-label="Add a payment mode">
        {COUNTER_PAYMENT_MODES.map((mode) => {
          const Icon = MODE_ICON[mode];
          const active = lines.some((line) => line.mode === mode);
          return (
            <ToggleButton key={mode} checked={active} icon={<Icon />} onClick={() => pickMode(mode)} disabled={disabled} appearance={active ? 'primary' : 'secondary'}>
              {PAYMENT_MODE_LABEL[mode]}
            </ToggleButton>
          );
        })}
      </div>

      {lines.map((line, index) => {
        const check = showErrors ? checkPaymentLine(line, today) : null;
        return (
          <div key={line.key} className={styles.line}>
            <Field label={`Mode ${lines.length > 1 ? index + 1 : ''}`.trim()}>
              <Select value={line.mode} onChange={(_, data) => update(line.key, { mode: data.value as CounterPaymentMode })} disabled={disabled}>
                {COUNTER_PAYMENT_MODES.map((mode) => (
                  <option key={mode} value={mode}>
                    {PAYMENT_MODE_LABEL[mode]}
                  </option>
                ))}
              </Select>
            </Field>
            <AmountField
              label="Amount"
              value={line.amount}
              onChange={(amount) => update(line.key, { amount })}
              disabled={disabled}
              autoFocus={index === lines.length - 1 && lines.length > 1}
            />
            <div className={styles.evidence}>
              {line.mode === 'Card' && (
                <>
                  <Field label="Card last 4 digits" required>
                    <Input
                      value={line.cardLast4}
                      onChange={(_, data) => update(line.key, { cardLast4: data.value.replace(/\D/g, '').slice(0, 4) })}
                      inputMode="numeric"
                      maxLength={4}
                      style={{ width: 110 }}
                      disabled={disabled}
                    />
                  </Field>
                  <Field label="Network">
                    <Select value={line.cardNetwork} onChange={(_, data) => update(line.key, { cardNetwork: data.value })} disabled={disabled}>
                      <option value="">Not noted</option>
                      {CARD_NETWORKS.map((network) => (
                        <option key={network} value={network}>
                          {network}
                        </option>
                      ))}
                    </Select>
                  </Field>
                </>
              )}
              {(line.mode === 'Upi' || line.mode === 'NetBanking' || line.mode === 'Wallet') && (
                <Field label={line.mode === 'Upi' ? 'UPI reference (UTR)' : 'Transaction reference'} required style={{ minWidth: 220 }}>
                  <Input value={line.reference} onChange={(_, data) => update(line.key, { reference: data.value })} maxLength={64} disabled={disabled} />
                </Field>
              )}
              {line.mode === 'Cheque' && (
                <>
                  <Field label="Cheque no." required>
                    <Input
                      value={line.chequeNumber}
                      onChange={(_, data) => update(line.key, { chequeNumber: data.value.replace(/\D/g, '').slice(0, 6) })}
                      inputMode="numeric"
                      maxLength={6}
                      style={{ width: 110 }}
                      disabled={disabled}
                    />
                  </Field>
                  <Field label="Bank" required>
                    <Input value={line.chequeBank} onChange={(_, data) => update(line.key, { chequeBank: data.value })} maxLength={100} disabled={disabled} />
                  </Field>
                  <Field label="Cheque date" required>
                    <Input type="date" value={line.chequeDate} max={today} onChange={(_, data) => update(line.key, { chequeDate: data.value })} disabled={disabled} />
                  </Field>
                </>
              )}
              {due !== null && balanceFor(line) !== null && (
                <Button appearance="subtle" onClick={() => update(line.key, { amount: amountInputText(balanceFor(line)) })} disabled={disabled}>
                  Make it {formatInr(balanceFor(line))}
                </Button>
              )}
            </div>
            <Button
              appearance="subtle"
              icon={<DeleteRegular />}
              aria-label={`Remove ${PAYMENT_MODE_LABEL[line.mode]} payment`}
              onClick={() => onLinesChange(lines.filter((l) => l.key !== line.key))}
              disabled={disabled || lines.length === 1}
            />
            {check && !check.ok && (
              <Caption1 className={styles.error} role="alert">
                {check.error}
              </Caption1>
            )}
          </div>
        );
      })}

      {lines.length < MAX_SPLIT_MODES && (
        <div>
          <Button appearance="transparent" icon={<AddRegular />} onClick={() => pickMode('Cash')} disabled={disabled}>
            Split with another mode
          </Button>
        </div>
      )}

      {hasCash && (
        <div className={styles.cash}>
          <AmountField
            label="Cash handed over by the patient"
            hint="Optional. Shows the change to give back."
            value={cashTendered}
            onChange={onCashTenderedChange}
            disabled={disabled}
            placeholder={amountInputText(summary.cash)}
          />
          {summary.change !== null && summary.change > 0 && (
            <div className={styles.change} aria-live="polite">
              <Caption1 className={styles.muted}>Change to return</Caption1>
              <span className={styles.changeValue}>{formatInr(summary.change)}</span>
            </div>
          )}
          {summary.shortTendered && (
            <MessageBar intent="warning">
              <MessageBarBody>The cash handed over is less than the cash amount of {formatInr(summary.cash)}.</MessageBarBody>
            </MessageBar>
          )}
        </div>
      )}

      <div className={styles.summary} aria-live="polite">
        <span>
          <Caption1 className={styles.muted}>Entered</Caption1>
          <Text block>
            <Amount value={summary.total} strong />
          </Text>
        </span>
        {due !== null && (
          <>
            <span>
              <Caption1 className={styles.muted}>Due</Caption1>
              <Text block>
                <Amount value={due} />
              </Text>
            </span>
            <span>
              <Caption1 className={styles.muted}>{summary.remaining < 0 ? 'More than the balance by' : 'Left to collect'}</Caption1>
              <Text block>
                <Amount value={Math.abs(summary.remaining)} strong tone={summary.remaining < 0 ? 'negative' : summary.remaining === 0 ? 'positive' : 'neutral'} />
              </Text>
            </span>
          </>
        )}
      </div>
      {due !== null && summary.remaining < 0 && (
        <MessageBar intent="error">
          <MessageBarBody>The counter cannot collect more than the balance. Reduce an amount, or use the change field for cash.</MessageBarBody>
        </MessageBar>
      )}
    </div>
  );
}
