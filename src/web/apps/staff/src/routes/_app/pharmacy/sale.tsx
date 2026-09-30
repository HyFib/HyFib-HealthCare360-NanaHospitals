import {
  Button,
  Caption1,
  Checkbox,
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
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Spinner,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  CartRegular,
  CheckmarkCircleRegular,
  DeleteRegular,
  DocumentArrowLeftRegular,
  PrintRegular,
  ReceiptRegular,
  ShieldKeyholeRegular,
} from '@fluentui/react-icons';
import { formatInr, formatRelative, isApiError, isIndianMobile, todayIso, useDraft, useHotkey } from '@hc360/core';
import { EmptyState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useRef, useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { sumNumbers } from '@/modules/inventory/decimal';
import { formatQty, parseNumber } from '@/modules/inventory/format';
import { requiresRegister } from '@/modules/inventory/labels';
import { LineTable } from '@/modules/inventory/LineTable';
import { WorkingStorePicker, StoreProblem } from '@/modules/inventory/StorePicker';
import { ItemFlags } from '@/modules/inventory/StockBits';
import { useWorkingStore } from '@/modules/inventory/stores';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import {
  pharmacyApi,
  pharmacyKeys,
  pharmacyQueries,
  type PharmacySaleCreated,
  type PharmacyStockRow,
} from '@/modules/pharmacy/api';
import { BatchPick } from '@/modules/pharmacy/BatchPick';
import { BillSummary } from '@/modules/pharmacy/BillSummary';
import {
  DEFAULT_MAX_DISCOUNT_PERCENT,
  useCounterPrefs,
  useItemMaster,
  useLetterhead,
  usePharmacyBatches,
} from '@/modules/pharmacy/counter';
import { controlledItems, itemsFromErrorData, planBatchIssue } from '@/modules/pharmacy/dispense';
import { MedicineSearch } from '@/modules/pharmacy/MedicineSearch';
import { PaymentPanel } from '@/modules/pharmacy/PaymentPanel';
import { checkPayments, defaultPayments, resolvePayments, type PaymentDraft } from '@/modules/pharmacy/payments';
import { PHARMACY_PERMISSIONS as P } from '@/modules/pharmacy/permissions';
import { priceSaleLine, saleTotals } from '@/modules/pharmacy/pricing';
import { printReceipt } from '@/modules/pharmacy/print';

export const Route = createFileRoute('/_app/pharmacy/sale')({
  component: CounterSalePage,
});

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 400px',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1180px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  aside: { display: 'flex', flexDirection: 'column', gap: '16px', position: 'sticky', top: '16px' },
  stack: { display: 'flex', flexDirection: 'column', gap: '10px' },
  grid2: { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '10px' },
  muted: { color: tokens.colorNeutralForeground3 },
  problem: { color: tokens.colorPaletteRedForeground1 },
  qty: { width: '100px' },
  money: { fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' },
  done: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '6px', textAlign: 'center' },
  big: {
    fontSize: tokens.fontSizeHero800,
    lineHeight: tokens.lineHeightHero800,
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorBrandForeground1,
  },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  controlled: { borderLeft: `4px solid ${tokens.colorPaletteRedBorder2}` },
});

interface CartLine {
  item: PharmacyStockRow;
  quantity: string;
  batchId: string | null;
}

interface Rx {
  prescriberName: string;
  registration: string;
  date: string;
  reference: string;
}

interface CounterDraft {
  cart: CartLine[];
}

const EMPTY_RX: Rx = { prescriberName: '', registration: '', date: '', reference: '' };

/** Counter (OTC) sale POS (PH-02): scan or search, pick batches FEFO, take the payment and print the GST invoice. */
function CounterSalePage() {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const [prefs, setPrefs] = useCounterPrefs();
  const canSell = usePermission(P.sales);
  const canReturn = usePermission([P.sales, P.returns]);
  const working = useWorkingStore('pharmacy-op', { type: 'Pharmacy' });
  const storeId = working.storeId;
  const searchRef = useRef<HTMLInputElement>(null);

  const [cart, setCart] = useState<CartLine[]>([]);
  const [customerKind, setCustomerKind] = useState<'walk-in' | 'patient'>('walk-in');
  const [patient, setPatient] = useState<PickedPatient | null>(null);
  const [customerName, setCustomerName] = useState('');
  const [customerPhone, setCustomerPhone] = useState('');
  const [withRx, setWithRx] = useState(false);
  const [rx, setRx] = useState<Rx>(EMPTY_RX);
  const [discount, setDiscount] = useState('');
  const [confirmControlled, setConfirmControlled] = useState(false);
  const [payments, setPayments] = useState<PaymentDraft[]>(defaultPayments);
  const [sessionKey, setSessionKey] = useState(0);
  const [done, setDone] = useState<PharmacySaleCreated | null>(null);

  const draft = useDraft<CounterDraft>('pharmacy-counter-sale', { cart }, { enabled: cart.length > 0 });
  const [savedDraft, setSavedDraft] = useState(() => draft.restore());

  const itemIds = cart.map((l) => l.item.itemId);
  const stock = usePharmacyBatches(storeId, itemIds);
  const master = useItemMaster(itemIds);
  const discountPercent = parseNumber(discount) ?? 0;
  const discountInvalid = discountPercent < 0 || discountPercent > 100;

  const lines = cart.map((line) => {
    const quantity = parseNumber(line.quantity) ?? 0;
    const batches = stock.batches.get(line.item.itemId);
    const plan = planBatchIssue(
      { quantity, remaining: null, batchId: line.batchId, minimumShelfLifeDays: 0 },
      batches,
      line.item.baseUnit,
    );
    const gstRate = master.items.get(line.item.itemId)?.gstRate ?? null;
    const priced = plan.allocation.allocations.map((a) =>
      priceSaleLine(batches?.find((b) => b.batchId === a.batchId)?.mrp ?? 0, a.quantity, gstRate, discountPercent),
    );
    return { line, quantity, batches, plan, priced, amount: sumNumbers(priced.map((p) => p.net)) };
  });

  const totals = saleTotals(lines.flatMap((l) => l.priced));
  const rxItems = cart.filter((l) => l.item.requiresPrescription).map((l) => l.item.name);
  const registerItems = controlledItems(
    cart.map((l) => ({ name: l.item.name, schedule: l.item.schedule, isControlled: l.item.isControlled })),
  );
  const rxNeeded = rxItems.length > 0;
  const rxShown = rxNeeded || withRx;
  const buyerName = customerKind === 'patient' ? (patient?.fullName ?? '') : customerName.trim();
  const buyerNeeded = cart.some((l) => requiresRegister(l.item.schedule, l.item.isControlled));
  const creditAllowed = customerKind === 'patient' && patient !== null;
  const resolved = resolvePayments(totals.net, payments);
  const paymentCheck = checkPayments(totals.net, resolved, creditAllowed);
  const phoneInvalid = customerKind === 'walk-in' && customerPhone.trim() !== '' && !isIndianMobile(customerPhone);

  const blockers: string[] = [];
  if (cart.length === 0) {
    blockers.push('Add at least one medicine.');
  }
  for (const l of lines) {
    for (const problem of l.plan.problems) {
      blockers.push(`${l.line.item.name}: ${problem}`);
    }
  }
  if (rxShown && !rx.prescriberName.trim()) {
    blockers.push(
      rxNeeded
        ? `A prescription is required for ${rxItems.join(', ')}: enter the prescriber's name.`
        : "Enter the prescriber's name or switch off the prescription.",
    );
  }
  if (buyerNeeded && !buyerName) {
    blockers.push(
      'Schedule H1/X medicines are recorded in the register: choose the patient or enter the buyer’s name.',
    );
  }
  if (customerKind === 'patient' && !patient) {
    blockers.push('Choose the registered patient, or switch to walk-in.');
  }
  if (registerItems.length > 0 && !confirmControlled) {
    blockers.push('Confirm the controlled medicines.');
  }
  if (phoneInvalid) {
    blockers.push('Enter a valid 10-digit mobile number, or leave it blank.');
  }
  if (discountInvalid) {
    blockers.push('The discount must be between 0 and 100%.');
  }
  const pending = lines.some((l) => l.plan.pending);
  const ready = canSell && Boolean(storeId) && blockers.length === 0 && !pending && paymentCheck.ok;

  const addItem = (row: PharmacyStockRow) => {
    if (row.available <= 0) {
      notify.warning(`${row.name} is out of stock here`, 'Check another store or order it through an indent.');
      return;
    }
    const existing = cart.find((l) => l.item.itemId === row.itemId);
    if (existing) {
      const next = (parseNumber(existing.quantity) ?? 0) + 1;
      setCart(cart.map((l) => (l.item.itemId === row.itemId ? { ...l, quantity: String(next) } : l)));
    } else {
      setCart([...cart, { item: row, quantity: '1', batchId: null }]);
    }
  };
  const updateLine = (itemId: string, patch: Partial<CartLine>) =>
    setCart(cart.map((l) => (l.item.itemId === itemId ? { ...l, ...patch } : l)));

  const reset = () => {
    setCart([]);
    setPatient(null);
    setCustomerKind('walk-in');
    setCustomerName('');
    setCustomerPhone('');
    setWithRx(false);
    setRx(EMPTY_RX);
    setDiscount('');
    setConfirmControlled(false);
    setPayments(defaultPayments());
    setSessionKey((k) => k + 1);
    setDone(null);
    draft.clear();
    setTimeout(() => searchRef.current?.focus(), 50);
  };

  const sell = useMutation({
    mutationFn: () =>
      pharmacyApi.counterSale({
        storeId: storeId!,
        patientId: customerKind === 'patient' ? (patient?.id ?? null) : null,
        customerName: customerKind === 'walk-in' ? customerName.trim() || null : null,
        customerPhone: customerKind === 'walk-in' ? customerPhone.trim() || null : null,
        prescription: rxShown
          ? {
              prescriberName: rx.prescriberName.trim(),
              prescriberRegistrationNumber: rx.registration.trim() || null,
              prescriptionDate: rx.date || null,
              reference: rx.reference.trim() || null,
            }
          : null,
        lines: lines.flatMap((l) =>
          l.plan.allocation.allocations.map((a) => ({
            itemId: l.line.item.itemId,
            quantity: a.quantity,
            batchId: a.batchId,
          })),
        ),
        discountPercent,
        payments: resolved
          .filter((p) => p.amount > 0)
          .map((p) => ({ mode: p.mode, amount: p.amount, reference: p.reference })),
        confirmControlledDrugs: registerItems.length > 0 && confirmControlled,
      }),
    onSuccess: (sale) => {
      setDone(sale);
      draft.clear();
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.stock });
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.sales });
      void queryClient.invalidateQueries({ queryKey: pharmacyKeys.dashboard });
      if (prefs.autoPrintReceipt) {
        void queryClient
          .fetchQuery(pharmacyQueries.sale(sale.id))
          .then((detail) => printReceipt(detail, letterhead))
          .catch((error: unknown) => notify.error(error, 'The receipt did not print'));
      }
    },
    onError: (error) => {
      if (
        isApiError(error) &&
        (error.code?.startsWith('stock.') || error.code === 'payment.mismatch' || error.isConflict)
      ) {
        stock.refetch();
      }
      if (isApiError(error) && error.code === 'pharmacy.prescription_required') {
        setWithRx(true);
      }
    },
  });

  const complete = () => {
    if (ready && !sell.isPending) {
      sell.mutate();
    }
  };
  useHotkey('f9', complete, { allowInInputs: true, enabled: ready });
  useHotkey('f2', () => searchRef.current?.focus(), { allowInInputs: true });

  const printAgain = () => {
    if (done) {
      void queryClient
        .fetchQuery(pharmacyQueries.sale(done.id))
        .then((detail) => printReceipt(detail, letterhead))
        .catch((error: unknown) => notify.error(error, 'The receipt did not print'));
    }
  };

  const errorItems = isApiError(sell.error) ? itemsFromErrorData(sell.error.data) : [];

  return (
    <Page>
      <PageHeader
        title="Counter sale"
        subtitle="F2 search or scan · F9 complete the sale. Schedule H, H1 and X medicines need a prescription."
        actions={
          <>
            {working.stores.length > 1 && <WorkingStorePicker working={working} label="Selling from" />}
            {canReturn && (
              <Button icon={<ReceiptRegular />} onClick={() => void navigate({ to: '/pharmacy/sales' })}>
                Returns and reprints
              </Button>
            )}
          </>
        }
      />

      <StoreProblem working={working} kind="pharmacy store" />

      {savedDraft && cart.length === 0 && savedDraft.value.cart.length > 0 && (
        <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
          <MessageBarBody>
            An unfinished sale of {savedDraft.value.cart.length} item{savedDraft.value.cart.length === 1 ? '' : 's'}{' '}
            from {formatRelative(new Date(savedDraft.savedAt).toISOString())} was found on this computer.
          </MessageBarBody>
          <MessageBarActions>
            <Button
              onClick={() => {
                setCart(savedDraft.value.cart);
                setSavedDraft(null);
              }}
            >
              Restore it
            </Button>
            <Button
              appearance="subtle"
              onClick={() => {
                draft.clear();
                setSavedDraft(null);
              }}
            >
              Discard
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      <div className={styles.layout}>
        <div className={styles.main}>
          <Section>
            {storeId ? (
              <MedicineSearch
                key={sessionKey}
                storeId={storeId}
                onPick={addItem}
                autoFocus
                inputRef={searchRef}
                disabled={!canSell}
                onNoMatch={(typed) => notify.warning('No medicine found', `Nothing matches "${typed}" in this store.`)}
              />
            ) : (
              <Caption1>Choose the pharmacy store to start selling.</Caption1>
            )}
          </Section>

          <Section title={`Cart${cart.length ? ` (${cart.length})` : ''}`} flush>
            <LineTable
              ariaLabel="Cart"
              rows={lines}
              getRowId={(l) => l.line.item.itemId}
              isFlagged={(l) => l.plan.problems.length > 0}
              empty={
                <EmptyState
                  icon={<CartRegular />}
                  title="The cart is empty"
                  description="Scan a barcode or search by name above. Scanning the same item again adds one more."
                />
              }
              columns={[
                {
                  key: 'item',
                  header: 'Medicine',
                  render: (l) => (
                    <span>
                      <Text weight="semibold">{l.line.item.name}</Text>{' '}
                      <ItemFlags
                        schedule={l.line.item.schedule}
                        isControlled={l.line.item.isControlled}
                        isColdChain={l.line.item.isColdChain}
                      />
                      <Caption1 block className={styles.muted}>
                        {[l.line.item.genericName, l.line.item.strength].filter(Boolean).join(' · ')}
                        {l.line.item.available > 0
                          ? ` · ${formatQty(l.line.item.available, l.line.item.baseUnit)} in stock`
                          : ''}
                      </Caption1>
                      {l.plan.problems.map((problem) => (
                        <Caption1 key={problem} block className={styles.problem}>
                          {problem}
                        </Caption1>
                      ))}
                    </span>
                  ),
                },
                {
                  key: 'qty',
                  header: 'Quantity',
                  width: 120,
                  render: (l) => (
                    <Input
                      className={styles.qty}
                      type="number"
                      min={0}
                      aria-label={`Quantity of ${l.line.item.name}`}
                      value={l.line.quantity}
                      contentAfter={<Caption1>{l.line.item.baseUnit}</Caption1>}
                      onChange={(_, d) => updateLine(l.line.item.itemId, { quantity: d.value })}
                    />
                  ),
                },
                {
                  key: 'batch',
                  header: 'Batch',
                  render: (l) => (
                    <BatchPick
                      batches={l.batches}
                      allocations={l.plan.allocation.allocations}
                      pickedBatchId={l.line.batchId}
                      onPick={(batchId) => updateLine(l.line.item.itemId, { batchId })}
                      unit={l.line.item.baseUnit}
                    />
                  ),
                },
                {
                  key: 'amount',
                  header: 'Amount',
                  align: 'end',
                  width: 110,
                  render: (l) => <span className={styles.money}>{formatInr(l.amount)}</span>,
                },
                {
                  key: 'remove',
                  header: '',
                  width: 44,
                  render: (l) => (
                    <Button
                      appearance="subtle"
                      icon={<DeleteRegular />}
                      aria-label={`Remove ${l.line.item.name}`}
                      onClick={() => setCart(cart.filter((x) => x.item.itemId !== l.line.item.itemId))}
                    />
                  ),
                },
              ]}
            />
          </Section>
        </div>

        <aside className={styles.aside}>
          <Section title="Customer">
            <div className={styles.stack}>
              <RadioGroup
                layout="horizontal"
                value={customerKind}
                onChange={(_, d) => setCustomerKind(d.value as typeof customerKind)}
              >
                <Radio value="walk-in" label="Walk-in" />
                <Radio value="patient" label="Registered patient" />
              </RadioGroup>
              {customerKind === 'patient' ? (
                <PatientPicker
                  value={patient}
                  onChange={setPatient}
                  hint="Registered patients can pay later (credit) and appear in their medication history."
                />
              ) : (
                <div className={styles.grid2}>
                  <Field
                    label="Name"
                    required={buyerNeeded}
                    hint={buyerNeeded ? 'Needed for the H1/X register' : undefined}
                  >
                    <Input value={customerName} maxLength={200} onChange={(_, d) => setCustomerName(d.value)} />
                  </Field>
                  <Field
                    label="Mobile"
                    validationState={phoneInvalid ? 'error' : 'none'}
                    validationMessage={phoneInvalid ? '10-digit mobile' : undefined}
                  >
                    <Input
                      type="tel"
                      value={customerPhone}
                      maxLength={14}
                      onChange={(_, d) => setCustomerPhone(d.value)}
                    />
                  </Field>
                </div>
              )}
            </div>
          </Section>

          <Section
            title="Prescription"
            description={rxNeeded ? `Required for ${rxItems.join(', ')}` : 'Only for prescription-only medicines'}
          >
            <div className={styles.stack}>
              {!rxNeeded && (
                <Switch
                  checked={withRx}
                  onChange={(_, d) => setWithRx(d.checked)}
                  label="Sold against a prescription"
                />
              )}
              {rxShown && (
                <>
                  <Field label="Prescriber" required>
                    <Input
                      value={rx.prescriberName}
                      maxLength={200}
                      placeholder="Dr. name"
                      onChange={(_, d) => setRx({ ...rx, prescriberName: d.value })}
                    />
                  </Field>
                  <div className={styles.grid2}>
                    <Field label="Registration no.">
                      <Input
                        value={rx.registration}
                        maxLength={64}
                        onChange={(_, d) => setRx({ ...rx, registration: d.value })}
                      />
                    </Field>
                    <Field label="Prescription date">
                      <Input
                        type="date"
                        max={todayIso()}
                        value={rx.date}
                        onChange={(_, d) => setRx({ ...rx, date: d.value })}
                      />
                    </Field>
                  </div>
                  <Field label="Prescription reference">
                    <Input
                      value={rx.reference}
                      maxLength={64}
                      placeholder="Rx number on the paper, if any"
                      onChange={(_, d) => setRx({ ...rx, reference: d.value })}
                    />
                  </Field>
                </>
              )}
            </div>
          </Section>

          {registerItems.length > 0 && (
            <Section className={styles.controlled}>
              <MessageBar intent="warning" layout="multiline" icon={<ShieldKeyholeRegular />}>
                <MessageBarBody>
                  <MessageBarTitle>Controlled medicines</MessageBarTitle>
                  {registerItems.join(', ')} go into the register with the buyer and the prescriber.
                </MessageBarBody>
              </MessageBar>
              <Checkbox
                checked={confirmControlled}
                onChange={(_, d) => setConfirmControlled(d.checked === true)}
                label="I have seen the original prescription and checked the buyer"
              />
            </Section>
          )}

          <Section title="Bill">
            <div className={styles.stack}>
              <Field
                label="Discount %"
                hint={`Up to the hospital limit (usually ${DEFAULT_MAX_DISCOUNT_PERCENT}%)`}
                validationState={discountInvalid ? 'error' : 'none'}
                validationMessage={discountInvalid ? 'Enter 0 to 100' : undefined}
              >
                <Input
                  type="number"
                  min={0}
                  max={100}
                  value={discount}
                  placeholder="0"
                  onChange={(_, d) => setDiscount(d.value)}
                />
              </Field>
              <BillSummary totals={totals} discountPercent={discountPercent} />
              <PaymentPanel
                key={sessionKey}
                bill={totals.net}
                drafts={payments}
                onChange={setPayments}
                resolved={resolved}
                check={paymentCheck}
                creditAllowed={creditAllowed}
                disabled={!canSell}
              />
            </div>
          </Section>

          <Section>
            <div className={styles.stack}>
              {sell.error && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>{isApiError(sell.error) ? sell.error.title : 'Not saved'}</MessageBarTitle>
                    {isApiError(sell.error) && sell.error.code === 'payment.mismatch'
                      ? `${sell.error.userMessage} The bill has been refreshed: check it and the payment, then try again.`
                      : isApiError(sell.error)
                        ? sell.error.userMessage
                        : 'Nothing was saved. Try again.'}
                    {errorItems.length > 0 && <div>Items: {errorItems.join(', ')}</div>}
                  </MessageBarBody>
                </MessageBar>
              )}
              {cart.length > 0 && blockers.length > 0 && (
                <div>
                  {blockers.slice(0, 4).map((b) => (
                    <Caption1 key={b} block className={styles.problem}>
                      {b}
                    </Caption1>
                  ))}
                </div>
              )}
              <Button
                appearance="primary"
                size="large"
                icon={sell.isPending ? <Spinner size="tiny" /> : <CheckmarkCircleRegular />}
                disabled={!ready || sell.isPending}
                onClick={complete}
                aria-keyshortcuts="F9"
              >
                Complete sale {formatInr(totals.net)} (F9)
              </Button>
              <div className={styles.row}>
                <Switch
                  checked={prefs.autoPrintReceipt}
                  onChange={(_, d) => setPrefs({ autoPrintReceipt: d.checked })}
                  label="Print receipt automatically"
                />
                {cart.length > 0 && (
                  <Button appearance="subtle" onClick={reset}>
                    Clear
                  </Button>
                )}
              </div>
            </div>
          </Section>
        </aside>
      </div>

      <Dialog open={done !== null} onOpenChange={(_, d) => !d.open && reset()}>
        <DialogSurface style={{ maxWidth: 460 }}>
          <DialogBody>
            <DialogTitle>Sale complete</DialogTitle>
            <DialogContent>
              <div className={styles.done} role="status">
                <CheckmarkCircleRegular fontSize={40} color={tokens.colorPaletteGreenForeground1} />
                <Caption1>Invoice {done?.invoiceNumber}</Caption1>
                <span className={styles.big}>{formatInr(done?.netAmount)}</span>
                <Caption1>
                  Paid {formatInr(done?.paidAmount)}
                  {done && done.dueAmount > 0 ? ` · ${formatInr(done.dueAmount)} due` : ''}
                </Caption1>
              </div>
            </DialogContent>
            <DialogActions fluid>
              <Button icon={<PrintRegular />} onClick={printAgain}>
                Print receipt
              </Button>
              <Button appearance="primary" onClick={reset} autoFocus>
                New sale
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </Page>
  );
}
