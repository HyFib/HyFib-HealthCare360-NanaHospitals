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
  tokens,
} from '@fluentui/react-components';
import { PaymentRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr, isApiError } from '@hc360/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useAttemptKey } from '@/modules/billing/hooks';
import { ActionError } from '@/modules/billing/ui';
import {
  isTerminalBusy,
  isTerminalSalePending,
  isTerminalSaleUnconfirmed,
  terminalDetailsLine,
  terminalSaleFits,
} from './labels';
import { TerminalSaleStatus } from './TerminalSaleStatus';
import { loadTerminalId, saveTerminalId } from './terminalPrefs';
import { ClearUnconfirmedSale } from './UnconfirmedSale';
import { useNow } from './useNow';
import type { CounterTerminalSale, TerminalMode, TerminalSaleApi } from './types';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  big: { fontSize: '28px', fontWeight: tokens.fontWeightBold },
  unused: { display: 'flex', flexDirection: 'column', gap: '6px' },
  unusedRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: '8px',
    padding: '6px 10px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** The terminal is not wired up for this hospital: staff type the slip reference instead. */
const NO_TERMINAL_CODES = new Set(['billing.pos_not_configured', 'pos.unavailable']);

/**
 * Card/UPI payment on the hospital's terminal from a counter screen (pharmacy, canteen). The caller fixes the amount;
 * the mode is chosen here. The terminal is pushed the amount and the dialog follows it until the customer pays. An
 * approved payment is handed to `onApproved`; one already paid but never recorded ("Already paid on the terminal")
 * can be picked before sending a new one.
 */
export function TerminalSaleDialog({
  open,
  api,
  counter,
  amount,
  initialMode,
  patientId,
  maxAmount,
  exact,
  approveLabel,
  onClose,
  onApproved,
}: {
  open: boolean;
  api: TerminalSaleApi;
  /** Names the counter for the remembered terminal id ("pharmacy", "canteen"). */
  counter: string;
  amount: number;
  initialMode: TerminalMode;
  patientId?: string | null;
  /** The most an earlier payment may be to be used here. */
  maxAmount: number;
  /** An earlier payment must equal `maxAmount` exactly. */
  exact: boolean;
  /** The primary button once approved ("Use this payment", "Finish the bill"). */
  approveLabel: string;
  onClose: () => void;
  onApproved: (sale: CounterTerminalSale) => void;
}) {
  const styles = useStyles();
  const queryClient = useQueryClient();
  const [attemptKey, renewAttempt] = useAttemptKey();
  const [terminal, setTerminal] = useState(() => loadTerminalId(counter));
  const [mode, setMode] = useState<TerminalMode>(initialMode);
  const [saleId, setSaleId] = useState<string | null>(null);
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setTerminal(loadTerminalId(counter));
      setMode(initialMode);
      setSaleId(null);
    }
  }

  const saleKey = (id: string) => [...api.queryKey, 'sale', id] as const;
  const unusedKey = [...api.queryKey, 'unused'] as const;

  const start = useMutation({
    mutationFn: () =>
      api.start(
        { amount, terminalId: terminal.trim(), mode, patientId: patientId ?? null },
        { idempotencyKey: attemptKey },
      ),
    onSuccess: (sale) => {
      saveTerminalId(counter, terminal.trim());
      queryClient.setQueryData(saleKey(sale.id), sale);
      setSaleId(sale.id);
    },
  });

  const sale = useQuery({
    queryKey: saleKey(saleId ?? ''),
    queryFn: ({ signal }) => api.get(saleId!, signal),
    enabled: saleId !== null,
    // An unconfirmed sale cannot be asked of the terminal; the server settles it once its wait is over.
    refetchInterval: (query) =>
      query.state.data && isTerminalSalePending(query.state.data.status)
        ? 2_000
        : query.state.data && isTerminalSaleUnconfirmed(query.state.data.status)
          ? 30_000
          : false,
  });
  const current = sale.data;
  const pending = current ? isTerminalSalePending(current.status) : false;
  const now = useNow();
  const busy = current ? isTerminalBusy(current, now) : false;

  const unused = useQuery({
    queryKey: unusedKey,
    queryFn: ({ signal }) => api.unused(signal),
    enabled: open && saleId === null,
    staleTime: 0,
    gcTime: 0,
  });

  const cancel = useMutation({
    mutationFn: (note?: string) => api.cancel(saleId!, note),
    onSuccess: (cancelled) => queryClient.setQueryData(saleKey(cancelled.id), cancelled),
  });

  const finish = () => {
    if (current?.status === 'Approved') {
      onApproved(current);
    } else {
      onClose();
    }
  };

  const tryAgain = () => {
    renewAttempt();
    setSaleId(null);
    start.reset();
  };

  const noTerminal = isApiError(start.error) && start.error.code && NO_TERMINAL_CODES.has(start.error.code);

  return (
    <Dialog
      open={open}
      onOpenChange={(_, data) => !data.open && !pending && !start.isPending && finish()}
      modalType="alert"
    >
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>Card / UPI terminal</DialogTitle>
          <DialogContent className={styles.body}>
            {!current ? (
              <form
                id="terminal-sale-start"
                className={styles.body}
                onSubmit={(event) => {
                  event.preventDefault();
                  // The dialog can sit inside another form (React bubbles submits through portals).
                  event.stopPropagation();
                  if (terminal.trim() && amount > 0 && !start.isPending) {
                    start.mutate();
                  }
                }}
              >
                {unused.data && unused.data.length > 0 && (
                  <div className={styles.unused}>
                    <Text weight="semibold">Already paid on the terminal</Text>
                    <Caption1 className={styles.muted}>
                      Paid by a customer but not on any invoice yet. Use one instead of charging again.
                    </Caption1>
                    {unused.data.map((paid) => {
                      const fits = terminalSaleFits(paid, maxAmount, exact);
                      return (
                        <div key={paid.id} className={styles.unusedRow}>
                          <div>
                            <Text weight="semibold">
                              {formatInr(paid.amount)} · {paid.mode === 'Upi' ? 'UPI' : 'Card'}
                            </Text>
                            <br />
                            <Caption1 className={styles.muted}>
                              {[
                                terminalDetailsLine({ cardLast4: paid.cardLast4, rrn: paid.rrn }),
                                formatDateTime(paid.completedAt ?? paid.createdAt),
                              ]
                                .filter(Boolean)
                                .join(' · ')}
                            </Caption1>
                            {!fits && (
                              <>
                                <br />
                                <Caption1 className={styles.muted}>
                                  {exact
                                    ? `Not the bill amount of ${formatInr(maxAmount)}.`
                                    : `More than the ${formatInr(maxAmount)} to pay.`}
                                </Caption1>
                              </>
                            )}
                          </div>
                          {fits && (
                            <Button size="small" appearance="primary" onClick={() => onApproved(paid)}>
                              Use this payment
                            </Button>
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}
                <Field label="Amount">
                  <span className={styles.big} aria-label={`Amount ${formatInr(amount)}`}>
                    {formatInr(amount)}
                  </span>
                </Field>
                <Field label="Paid by">
                  <RadioGroup
                    layout="horizontal"
                    value={mode}
                    onChange={(_, data) => setMode(data.value as TerminalMode)}
                  >
                    <Radio value="Card" label="Card (tap, insert or swipe)" />
                    <Radio value="Upi" label="UPI QR on the terminal" />
                  </RadioGroup>
                </Field>
                <Field label="Terminal" required hint="The terminal id on its label. Remembered on this computer.">
                  <Input value={terminal} onChange={(_, data) => setTerminal(data.value)} maxLength={32} />
                </Field>
                {noTerminal && (
                  <MessageBar intent="info">
                    <MessageBarBody>
                      No card terminal is set up for this hospital. Close this and type the slip or transaction
                      reference instead.
                    </MessageBarBody>
                  </MessageBar>
                )}
                {!noTerminal && <ActionError error={start.error} />}
              </form>
            ) : (
              <TerminalSaleStatus
                sale={current}
                payer="customer"
                lostContact={!!sale.error}
                footer={<ActionError error={cancel.error} />}
              >
                {isTerminalSaleUnconfirmed(current.status) && (
                  <ClearUnconfirmedSale onClear={(note) => cancel.mutate(note)} pending={cancel.isPending} />
                )}
              </TerminalSaleStatus>
            )}
          </DialogContent>
          <DialogActions>
            {!current && (
              <>
                <Button onClick={onClose} disabled={start.isPending}>
                  Cancel
                </Button>
                <Button
                  type="submit"
                  form="terminal-sale-start"
                  appearance="primary"
                  icon={start.isPending ? <Spinner size="tiny" aria-hidden /> : <PaymentRegular />}
                  disabled={!terminal.trim() || !(amount > 0) || start.isPending}
                >
                  Send to terminal
                </Button>
              </>
            )}
            {current && pending && (
              <Button onClick={() => cancel.mutate(undefined)} disabled={cancel.isPending}>
                Cancel on the terminal
              </Button>
            )}
            {current && !pending && current.status !== 'Approved' && (
              <Button onClick={tryAgain} disabled={busy}>
                Try again
              </Button>
            )}
            {current && !pending && (
              <Button appearance="primary" onClick={finish}>
                {current.status === 'Approved' ? approveLabel : 'Close'}
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
