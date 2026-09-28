import {
  Badge,
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
  mergeClasses,
  MessageBar,
  MessageBarBody,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import {
  CalculatorRegular,
  DoorArrowLeftRegular,
  DoorArrowRightRegular,
  LockClosedRegular,
} from '@fluentui/react-icons';
import { formatInr, formatTime } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { billingApi, billingQueries, type ShiftDto } from './api';
import { saveCounterPrefs, useCounterPrefs } from './counterPrefs';
import { useRefreshBilling } from './hooks';
import { PAYMENT_MODE_LABEL } from './labels';
import { fromPaise, parseRupees, toPaise } from './money';
import { ActionError, Amount, AmountField, SummaryList, SummaryRow } from './ui';

const useStyles = makeStyles({
  bar: {
    display: 'flex',
    alignItems: 'center',
    gap: '10px',
    flexWrap: 'wrap',
    padding: '8px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow2,
  },
  closed: { borderLeft: `4px solid ${tokens.colorPaletteMarigoldBorderActive}` },
  open: { borderLeft: `4px solid ${tokens.colorPaletteGreenBorderActive}` },
  grow: { flex: 1, minWidth: '200px' },
  muted: { color: tokens.colorNeutralForeground3 },
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  notes: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(120px, 1fr))', gap: '8px' },
  variance: { fontSize: '22px', fontWeight: tokens.fontWeightBold },
});

const NOTES = [500, 200, 100, 50, 20, 10] as const;

/**
 * The cashier's shift at a glance, on every counter screen: open it before collecting money, close it (declare the
 * drawer) at the end. Hidden for users without cash-counter access.
 */
export function ShiftBar({ onShift }: { onShift?: (shift: ShiftDto | null) => void }) {
  const styles = useStyles();
  const canShift = usePermission('billing.shifts');
  const current = useQuery({ ...billingQueries.currentShift(), enabled: canShift });
  const [dialog, setDialog] = useState<'open' | 'close' | null>(null);

  if (!canShift) {
    return null;
  }
  const shift = current.data ?? null;

  return (
    <div className={mergeClasses(styles.bar, shift ? styles.open : styles.closed)} role="status" aria-live="polite">
      {current.isLoading ? (
        <Spinner size="tiny" label="Checking your cash shift…" />
      ) : shift ? (
        <>
          <Badge appearance="filled" color="success" icon={<LockClosedRegular />}>
            Shift open
          </Badge>
          <Text className={styles.grow}>
            <Text weight="semibold">{shift.counter}</Text> since {formatTime(shift.openedAt)} · cash expected in drawer{' '}
            <Amount value={shift.expectedCash} strong /> · {shift.receiptCount} receipt
            {shift.receiptCount === 1 ? '' : 's'}
          </Text>
          <Button icon={<DoorArrowLeftRegular />} onClick={() => setDialog('close')}>
            Close shift
          </Button>
        </>
      ) : (
        <>
          <Badge appearance="tint" color="warning">
            No open shift
          </Badge>
          <Text className={styles.grow}>Open your cash counter shift before collecting or paying out money.</Text>
          <Button appearance="primary" icon={<DoorArrowRightRegular />} onClick={() => setDialog('open')}>
            Open shift
          </Button>
        </>
      )}
      <OpenShiftDialog
        open={dialog === 'open'}
        onClose={() => setDialog(null)}
        onOpened={(opened) => {
          setDialog(null);
          onShift?.(opened);
        }}
      />
      {shift && (
        <CloseShiftDialog
          shift={shift}
          open={dialog === 'close'}
          onClose={() => setDialog(null)}
          onClosed={() => {
            setDialog(null);
            onShift?.(null);
          }}
        />
      )}
    </div>
  );
}

export function OpenShiftDialog({
  open,
  onClose,
  onOpened,
}: {
  open: boolean;
  onClose: () => void;
  onOpened: (shift: ShiftDto) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const refresh = useRefreshBilling();
  const prefs = useCounterPrefs();
  const [counter, setCounter] = useState(prefs.counter);
  const [float, setFloat] = useState('0');
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setCounter(prefs.counter);
      setFloat('0');
    }
  }
  const parsedFloat = parseRupees(float, { allowZero: true });
  const valid = counter.trim().length > 0 && counter.trim().length <= 64 && parsedFloat.ok;

  const openShift = useMutation({
    mutationFn: () => billingApi.openShift(counter.trim(), parsedFloat.ok ? parsedFloat.rupees : 0),
    onSuccess: (shift) => {
      saveCounterPrefs({ counter: shift.counter });
      notify.success('Shift opened', `${shift.counter} · opening cash ${formatInr(shift.openingFloat)}`);
      void refresh();
      onOpened(shift);
    },
  });

  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && !openShift.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 460 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !openShift.isPending) {
              openShift.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>Open cash shift</DialogTitle>
            <DialogContent className={styles.body}>
              <Field
                label="Counter"
                required
                hint="As printed on receipts, e.g. OPD Counter 1. Remembered on this computer."
              >
                <Input
                  value={counter}
                  onChange={(_, data) => setCounter(data.value)}
                  maxLength={64}
                  autoFocus={!prefs.counter}
                />
              </Field>
              <AmountField
                label="Opening cash in the drawer"
                hint="Change float handed over at the start of the shift"
                value={float}
                onChange={setFloat}
                error={parsedFloat.ok ? null : parsedFloat.error}
                autoFocus={Boolean(prefs.counter)}
              />
              <ActionError error={openShift.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={openShift.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!valid || openShift.isPending}
                icon={openShift.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Open shift
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/** Declared minus expected cash, in plain words. */
function varianceText(variance: bigint): { text: string; tone: 'positive' | 'negative' | 'neutral' } {
  if (variance === 0n) {
    return { text: 'The drawer tallies', tone: 'positive' };
  }
  return variance < 0n
    ? { text: `Short by ${formatInr(fromPaise(-variance))}`, tone: 'negative' }
    : { text: `Excess of ${formatInr(fromPaise(variance))}`, tone: 'neutral' };
}

export function CloseShiftDialog({
  shift,
  open,
  onClose,
  onClosed,
}: {
  shift: ShiftDto;
  open: boolean;
  onClose: () => void;
  onClosed: (shift: ShiftDto) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const refresh = useRefreshBilling();
  const [declared, setDeclared] = useState('');
  const [notes, setNotes] = useState('');
  const [counting, setCounting] = useState(false);
  const [counts, setCounts] = useState<Record<string, string>>({});
  const [wasOpen, setWasOpen] = useState(open);
  if (open !== wasOpen) {
    setWasOpen(open);
    if (open) {
      setDeclared('');
      setNotes('');
      setCounts({});
      setCounting(false);
    }
  }

  // Live figures while the dialog is open (a receipt may be recorded meanwhile at this counter). The shift detail
  // endpoint returns running totals for open shifts, so a supervisor closing someone else's shift sees them too.
  const live = useQuery({ ...billingQueries.shift(shift.id), enabled: open, refetchInterval: open ? 30_000 : false });
  const current = live.data ?? shift;

  const countedPaise = NOTES.reduce(
    (total, note) => total + BigInt(Math.max(0, Math.floor(Number(counts[String(note)] || 0)))) * BigInt(note) * 100n,
    0n,
  );
  const coins = parseRupees(counts.coins ?? '', { allowZero: true });
  const countTotal = countedPaise + (coins.ok ? coins.paise : 0n);

  const parsed = parseRupees(counting ? String(fromPaise(countTotal)) : declared, { allowZero: true });
  const variance = parsed.ok ? parsed.paise - toPaise(current.expectedCash) : null;
  const verdict = variance !== null ? varianceText(variance) : null;
  const needsNote = variance !== null && variance !== 0n && !notes.trim();

  const close = useMutation({
    mutationFn: () =>
      billingApi.closeShift(shift.id, parsed.ok ? parsed.rupees : 0, notes.trim() || null, current.version),
    onSuccess: (closed) => {
      notify.success(
        'Shift closed',
        closed.variance ? `Variance ${formatInr(closed.variance)} recorded` : 'The drawer tallied',
      );
      void refresh();
      onClosed(closed);
    },
  });

  return (
    <Dialog open={open} onOpenChange={(_, data) => !data.open && !close.isPending && onClose()} modalType="alert">
      <DialogSurface style={{ maxWidth: 620 }}>
        <DialogBody>
          <DialogTitle>Close shift at {shift.counter}</DialogTitle>
          <DialogContent className={styles.body}>
            <Text>
              Count the cash in the drawer and enter it. Closing is final: later collections need a new shift.
            </Text>
            <SummaryList>
              <SummaryRow label="Opening cash">
                <Amount value={current.openingFloat} />
              </SummaryRow>
              {current.totals.map((total) => (
                <SummaryRow
                  key={total.mode}
                  label={`${PAYMENT_MODE_LABEL[total.mode]} collected`}
                  hint={total.refunded ? `Refunded ${formatInr(total.refunded)}` : undefined}
                >
                  <Amount value={total.net} />
                </SummaryRow>
              ))}
              <SummaryRow label="Cash expected in the drawer" strong>
                <Amount value={current.expectedCash} strong />
              </SummaryRow>
            </SummaryList>

            {counting ? (
              <div className={styles.notes}>
                {NOTES.map((note) => (
                  <Field key={note} label={`₹${note} notes`}>
                    <Input
                      value={counts[String(note)] ?? ''}
                      onChange={(_, data) =>
                        setCounts((c) => ({ ...c, [String(note)]: data.value.replace(/\D/g, '') }))
                      }
                      inputMode="numeric"
                    />
                  </Field>
                ))}
                <AmountField
                  label="Coins"
                  value={counts.coins ?? ''}
                  onChange={(value) => setCounts((c) => ({ ...c, coins: value }))}
                />
                <Field label="Counted">
                  <Text weight="semibold">{formatInr(fromPaise(countTotal))}</Text>
                </Field>
              </div>
            ) : (
              <AmountField
                label="Cash counted in the drawer"
                required
                value={declared}
                onChange={setDeclared}
                error={declared && !parsed.ok ? parsed.error : null}
                autoFocus
              />
            )}
            <div>
              <Button appearance="transparent" icon={<CalculatorRegular />} onClick={() => setCounting((c) => !c)}>
                {counting ? 'Type the total instead' : 'Count by notes and coins'}
              </Button>
            </div>

            {verdict && (
              <MessageBar
                intent={verdict.tone === 'positive' ? 'success' : verdict.tone === 'negative' ? 'error' : 'warning'}
              >
                <MessageBarBody>
                  <span className={styles.variance}>{verdict.text}</span>
                </MessageBarBody>
              </MessageBar>
            )}
            <Field
              label="Notes"
              hint={
                variance !== null && variance !== 0n
                  ? 'Explain the difference; it is recorded against the shift.'
                  : 'Optional'
              }
              required={needsNote}
            >
              <Textarea value={notes} onChange={(_, data) => setNotes(data.value)} maxLength={500} rows={2} />
            </Field>
            <Caption1 className={styles.muted}>Receipts in this shift: {current.receiptCount}</Caption1>
            <ActionError error={close.error} onReload={() => void live.refetch()} />
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={close.isPending}>
              Keep shift open
            </Button>
            <Button
              appearance="primary"
              disabled={!parsed.ok || needsNote || close.isPending}
              icon={close.isPending ? <Spinner size="tiny" /> : undefined}
              onClick={() => close.mutate()}
            >
              Close shift
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
