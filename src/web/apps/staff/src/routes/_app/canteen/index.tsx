import {
  Badge,
  Button,
  Caption1,
  Checkbox,
  Field,
  Input,
  makeStyles,
  mergeClasses,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  SearchBox,
  Skeleton,
  SkeletonItem,
  Spinner,
  Text,
  ToggleButton,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  CartRegular,
  DeleteRegular,
  OpenRegular,
  PrintRegular,
  ReceiptRegular,
  SubtractRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatInr, isApiError, useHotkey } from '@hc360/core';
import {
  applyServerErrors,
  EmptyState,
  ErrorState,
  Page,
  PageHeader,
  Section,
  TextField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { useLetterhead } from '@/lib/letterhead';
import { usePermission } from '@/lib/permissions';
import {
  canteenApi,
  canteenKeys,
  canteenQueries,
  CUSTOMER_LABEL,
  CUSTOMER_TYPES,
  MENU_CATEGORIES,
  PAYMENT_LABEL,
  PAYMENT_MODES,
  type CanteenSaleDto,
  type MenuCategory,
  type MenuItemDto,
} from '@/modules/canteen/api';
import { calculateBill, changeDue, type BillLineInput } from '@/modules/canteen/billCalculator';
import { printCanteenReceipt } from '@/modules/canteen/print';
import { isValidEmployeeCode } from '@/modules/canteen/rules';
import { SaleDrawer } from '@/modules/canteen/SaleDrawer';
import { useStoredState } from '@/modules/inpatient/hooks';
import { PatientPicker } from '@/modules/patients/PatientPicker';

export const Route = createFileRoute('/_app/canteen/')({
  component: CanteenPosPage,
});

const SEARCH_ID = 'canteen-pos-search';

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(340px, 400px)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1000px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  menuTools: { display: 'flex', flexDirection: 'column', gap: '10px', marginBottom: '12px' },
  categories: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  grid: { display: 'grid', gap: '8px', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))' },
  tile: {
    appearance: 'none',
    margin: 0,
    textAlign: 'start',
    fontFamily: 'inherit',
    color: tokens.colorNeutralForeground1,
    position: 'relative',
    display: 'flex',
    flexDirection: 'column',
    justifyContent: 'space-between',
    gap: '6px',
    minHeight: '84px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover, boxShadow: tokens.shadow4 },
    ':active': { transform: 'scale(0.98)' },
    ':focus-visible': { outline: `3px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '1px' },
    ':disabled': { cursor: 'not-allowed', opacity: 0.5 },
    '@media (prefers-reduced-motion: reduce)': { ':active': { transform: 'none' } },
  },
  inCart: { border: `2px solid ${tokens.colorBrandStroke1}` },
  tileName: {
    display: 'flex',
    gap: '6px',
    alignItems: 'flex-start',
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightBase300,
  },
  tileFoot: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: '4px' },
  price: { fontWeight: tokens.fontWeightSemibold, fontFamily: tokens.fontFamilyNumeric },
  count: { position: 'absolute', top: '-8px', right: '-6px' },
  veg: {
    flexShrink: 0,
    marginTop: '3px',
    width: '12px',
    height: '12px',
    border: `1.5px solid ${tokens.colorPaletteGreenForeground1}`,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    '::after': {
      content: '""',
      width: '6px',
      height: '6px',
      borderRadius: '50%',
      backgroundColor: tokens.colorPaletteGreenForeground1,
    },
  },
  nonVeg: {
    border: `1.5px solid ${tokens.colorPaletteRedForeground1}`,
    '::after': {
      content: '""',
      width: '6px',
      height: '6px',
      borderRadius: '50%',
      backgroundColor: tokens.colorPaletteRedForeground1,
    },
  },
  cart: { position: 'sticky', top: '12px' },
  cartBody: { display: 'flex', flexDirection: 'column', gap: '12px' },
  lines: { display: 'flex', flexDirection: 'column', gap: '6px', maxHeight: '38vh', overflowY: 'auto' },
  line: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto auto', gap: '4px 8px', alignItems: 'center' },
  stepper: { display: 'inline-flex', alignItems: 'center', gap: '2px' },
  qty: { minWidth: '28px', textAlign: 'center', fontWeight: tokens.fontWeightSemibold },
  totals: {
    display: 'grid',
    gridTemplateColumns: '1fr auto',
    gap: '2px 12px',
    padding: '8px 0',
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  right: { textAlign: 'end', fontFamily: tokens.fontFamilyNumeric },
  net: {
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightBold,
    fontFamily: tokens.fontFamilyNumeric,
    textAlign: 'end',
  },
  segments: { display: 'grid', gridTemplateColumns: 'repeat(4, minmax(0, 1fr))', gap: '4px' },
  segment: { minWidth: 0, paddingLeft: '4px', paddingRight: '4px' },
  charge: { minHeight: '52px', fontSize: tokens.fontSizeBase500 },
  muted: { color: tokens.colorNeutralForeground3 },
  last: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap', justifyContent: 'space-between' },
});

const checkoutSchema = z
  .object({
    customerType: z.enum(CUSTOMER_TYPES),
    paymentMode: z.enum(PAYMENT_MODES),
    paymentReference: z.string().trim().max(64),
    employeeCode: z.string().trim().max(20),
    employeeName: z.string().trim().max(200),
    customerName: z.string().trim().max(200),
    patient: z.object({ id: z.string(), uhid: z.string(), fullName: z.string() }).nullable(),
  })
  .superRefine((v, ctx) => {
    if (v.paymentMode === 'StaffCredit' && v.customerType !== 'Staff') {
      ctx.addIssue({ code: 'custom', path: ['customerType'], message: 'Only staff can buy on credit.' });
    }
    if (v.paymentMode === 'StaffCredit' && !v.employeeCode) {
      ctx.addIssue({
        code: 'custom',
        path: ['employeeCode'],
        message: 'Enter the employee code from the staff ID card.',
      });
    } else if (v.employeeCode && !isValidEmployeeCode(v.employeeCode)) {
      ctx.addIssue({
        code: 'custom',
        path: ['employeeCode'],
        message: 'Use the code on the staff ID card (letters, digits, "/" or "-").',
      });
    }
  });

type CheckoutValues = z.infer<typeof checkoutSchema>;

interface CartLine {
  menuItemId: string;
  quantity: number;
}

const RESET_FIELDS = {
  paymentMode: 'Cash',
  paymentReference: '',
  employeeCode: '',
  employeeName: '',
  customerName: '',
  patient: null,
} as const;

/**
 * Canteen POS (CAN-02): tap items, check the GST bill, choose how the customer pays, charge (F9). The receipt prints
 * silently on the counter printer. Staff credit is checked against HR and the monthly credit limit by the server.
 */
function CanteenPosPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const canSell = usePermission('canteen.sales.create');
  const canReport = usePermission('canteen.reports.read');
  const menu = useQuery({ ...canteenQueries.menu(true), enabled: canSell });
  const [category, setCategory] = useState<MenuCategory | 'All'>('All');
  const [term, setTerm] = useState('');
  const [cart, setCart] = useState<CartLine[]>([]);
  const [tendered, setTendered] = useState('');
  const [lastSale, setLastSale] = useState<CanteenSaleDto | null>(null);
  const [openSale, setOpenSale] = useState<string | null>(null);
  const [autoPrint, setAutoPrint] = useStoredState('hc360:canteen:autoprint', '1');
  const form = useForm<CheckoutValues>({
    resolver: zodResolver(checkoutSchema),
    defaultValues: { customerType: 'Visitor', ...RESET_FIELDS },
  });
  const [customerType, paymentMode] = useWatch({ control: form.control, name: ['customerType', 'paymentMode'] });

  const items = menu.data ?? [];
  const byId = new Map(items.map((m) => [m.id, m]));
  const categories = MENU_CATEGORIES.filter((c) => items.some((m) => m.category === c));
  const needle = term.trim().toLowerCase();
  const shown = items.filter(
    (m) =>
      (category === 'All' || m.category === category) &&
      (!needle || m.name.toLowerCase().includes(needle) || m.code.toLowerCase().includes(needle)),
  );
  const quantityOf = (id: string) => cart.find((l) => l.menuItemId === id)?.quantity ?? 0;

  const priced: BillLineInput[] = cart.flatMap((l) => {
    const m = byId.get(l.menuItemId);
    return m
      ? [
          {
            menuItemId: m.id,
            itemName: m.name,
            quantity: l.quantity,
            unitPrice: m.price,
            gstRate: m.gstRate,
            priceIncludesGst: m.priceIncludesGst,
          },
        ]
      : [];
  });
  const bill = calculateBill(priced);
  const dropped = cart.length - priced.length;
  const itemCount = priced.reduce((sum, l) => sum + l.quantity, 0);

  const focusSearch = () => document.getElementById(SEARCH_ID)?.focus();

  const add = (item: MenuItemDto) => {
    const current = quantityOf(item.id);
    if (item.trackStock && current >= item.stockOnHand) {
      notify.warning(`Only ${item.stockOnHand} ${item.name} left`);
      return;
    }
    if (current >= 100) {
      return;
    }
    if (current === 0 && cart.length >= 50) {
      notify.warning('A bill can have at most 50 items', 'Charge this bill and start another.');
      return;
    }
    setCart((lines) =>
      current
        ? lines.map((l) => (l.menuItemId === item.id ? { ...l, quantity: l.quantity + 1 } : l))
        : [...lines, { menuItemId: item.id, quantity: 1 }],
    );
  };
  const decrease = (id: string) =>
    setCart((lines) =>
      lines.flatMap((l) => (l.menuItemId !== id ? [l] : l.quantity > 1 ? [{ ...l, quantity: l.quantity - 1 }] : [])),
    );
  const remove = (id: string) => setCart((lines) => lines.filter((l) => l.menuItemId !== id));

  const sale = useMutation({
    mutationFn: (v: CheckoutValues) =>
      canteenApi.createSale({
        customerType: v.customerType,
        customerName: v.customerType === 'Visitor' || v.customerType === 'Attendant' ? v.customerName || null : null,
        employeeCode: v.customerType === 'Staff' ? v.employeeCode || null : null,
        // For staff credit the server prints the name from HR; a typed name is sent only for cash staff bills.
        employeeName: v.customerType === 'Staff' && v.paymentMode !== 'StaffCredit' ? v.employeeName || null : null,
        patientId: v.customerType === 'Patient' ? (v.patient?.id ?? null) : null,
        paymentMode: v.paymentMode,
        paymentReference: v.paymentMode === 'Upi' || v.paymentMode === 'Card' ? v.paymentReference || null : null,
        lines: priced.map((l) => ({ menuItemId: l.menuItemId, quantity: l.quantity })),
      }),
    onSuccess: (result) => {
      setLastSale(result);
      setCart([]);
      setTendered('');
      form.reset({ ...form.getValues(), ...RESET_FIELDS });
      void queryClient.invalidateQueries({ queryKey: canteenKeys.all });
      if (autoPrint === '1') {
        void printCanteenReceipt(result, letterhead).catch((error: unknown) =>
          notify.error(error, 'The receipt did not print. Use Reprint.'),
        );
      }
      notify.success(`Bill ${result.billNumber}: ${formatInr(result.netAmount)}`, PAYMENT_LABEL[result.paymentMode]);
      focusSearch();
    },
    onError: (error) => {
      if (
        isApiError(error) &&
        ['canteen.item_unavailable', 'canteen.out_of_stock', 'canteen.stock_changed'].includes(error.code ?? '')
      ) {
        void queryClient.invalidateQueries({ queryKey: canteenKeys.menu(true) });
      }
      if (isApiError(error) && error.code === 'canteen.employee_inactive') {
        form.setError('employeeCode', { type: 'server', message: error.userMessage });
      }
      applyServerErrors(error, form.setError);
    },
  });

  const charge = form.handleSubmit((values) => {
    if (priced.length > 0 && !sale.isPending) {
      sale.mutate(values);
    }
  });
  useHotkey('f9', () => void charge(), { allowInInputs: true, enabled: canSell });
  useHotkey('f2', focusSearch, { allowInInputs: true, enabled: canSell });

  const serverError =
    sale.error &&
    !(isApiError(sale.error) && (sale.error.isValidation || sale.error.code === 'canteen.employee_inactive'))
      ? sale.error
      : null;
  const tenderedNumber = Number(tendered);
  const change =
    paymentMode === 'Cash' && tendered !== '' && Number.isFinite(tenderedNumber)
      ? changeDue(bill.netAmount, tenderedNumber)
      : null;
  const short = paymentMode === 'Cash' && tendered !== '' && tenderedNumber < bill.netAmount;

  // Leaving the screen or closing the window with unsaved changes asks first (SRS UX-08).
  useLeaveGuard({ dirty: cart.length > 0, busy: sale.isPending });

  if (!canSell) {
    return (
      <Page maxWidth={720}>
        <EmptyState
          icon={<CartRegular />}
          title="Canteen billing is for canteen staff"
          description="Ask the canteen manager for access, or open the canteen reports."
        />
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="Canteen billing"
        icon={<CartRegular />}
        subtitle="F2 finds an item · Enter adds the first match · F9 charges the bill"
        actions={
          <>
            <Button icon={<ReceiptRegular />} onClick={() => void navigate({ to: '/canteen/sales' })}>
              Today’s bills
            </Button>
            {canReport && <Button onClick={() => void navigate({ to: '/canteen/report' })}>Day report</Button>}
          </>
        }
      />
      <div className={styles.layout}>
        <Section>
          <div className={styles.menuTools}>
            <SearchBox
              id={SEARCH_ID}
              value={term}
              onChange={(_, d) => setTerm(d.value)}
              placeholder="Find an item by name or code"
              aria-label="Find a menu item"
              autoFocus
              onKeyDown={(event) => {
                if (event.key === 'Enter' && shown[0]) {
                  event.preventDefault();
                  add(shown[0]);
                  setTerm('');
                }
              }}
            />
            <div className={styles.categories} role="toolbar" aria-label="Menu categories">
              <ToggleButton shape="circular" checked={category === 'All'} onClick={() => setCategory('All')}>
                All
              </ToggleButton>
              {categories.map((c) => (
                <ToggleButton key={c} shape="circular" checked={category === c} onClick={() => setCategory(c)}>
                  {c}
                </ToggleButton>
              ))}
            </div>
          </div>
          {menu.error ? (
            <ErrorState error={menu.error} onRetry={() => void menu.refetch()} title="Could not load the menu" />
          ) : menu.isLoading ? (
            <div className={styles.grid} aria-busy="true">
              {Array.from({ length: 12 }, (_, i) => (
                <Skeleton key={i} aria-label="Loading the menu">
                  <SkeletonItem style={{ height: 84 }} />
                </Skeleton>
              ))}
            </div>
          ) : shown.length === 0 ? (
            <EmptyState
              title={items.length ? 'No item matches' : 'The menu is empty'}
              description={
                items.length
                  ? 'Clear the search or pick another category.'
                  : 'Add items on the Menu page and mark them available.'
              }
            />
          ) : (
            <div className={styles.grid}>
              {shown.map((item) => {
                const inCart = quantityOf(item.id);
                const soldOut = item.trackStock && item.stockOnHand <= 0;
                return (
                  <button
                    key={item.id}
                    type="button"
                    className={mergeClasses(styles.tile, inCart > 0 && styles.inCart)}
                    disabled={soldOut}
                    onClick={() => add(item)}
                    aria-label={`${item.name}, ${formatInr(item.price)}, ${item.isVeg ? 'vegetarian' : 'non-vegetarian'}${soldOut ? ', sold out' : ''}${inCart ? `, ${inCart} in the bill` : ''}`}
                  >
                    <span className={styles.tileName}>
                      <span
                        className={mergeClasses(styles.veg, !item.isVeg && styles.nonVeg)}
                        title={item.isVeg ? 'Vegetarian' : 'Non-vegetarian'}
                        aria-hidden
                      />
                      {item.name}
                    </span>
                    <span className={styles.tileFoot}>
                      <span className={styles.price}>{formatInr(item.price)}</span>
                      {item.trackStock && (
                        <Caption1 className={styles.muted}>
                          {soldOut ? 'Sold out' : `${item.stockOnHand - inCart} left`}
                        </Caption1>
                      )}
                    </span>
                    {inCart > 0 && (
                      <Badge className={styles.count} appearance="filled" color="brand" aria-hidden>
                        {inCart}
                      </Badge>
                    )}
                  </button>
                );
              })}
            </div>
          )}
        </Section>

        <div className={styles.cart}>
          <Section
            title={`Bill${itemCount ? ` · ${itemCount} items` : ''}`}
            actions={
              cart.length > 0 ? (
                <Button size="small" appearance="subtle" onClick={() => setCart([])}>
                  Clear
                </Button>
              ) : undefined
            }
          >
            <form onSubmit={charge} noValidate className={styles.cartBody}>
              {priced.length === 0 ? (
                <Caption1 className={styles.muted}>Tap items on the left to add them to the bill.</Caption1>
              ) : (
                <div className={styles.lines} aria-label="Bill items">
                  {bill.lines.map((l) => (
                    <div key={l.menuItemId} className={styles.line}>
                      <span>
                        <Text weight="semibold">{l.itemName}</Text>
                        <Caption1 block className={styles.muted}>
                          {formatInr(l.unitPrice)} · GST {l.gstRate}%
                        </Caption1>
                      </span>
                      <span className={styles.stepper}>
                        <Button
                          size="small"
                          appearance="subtle"
                          icon={<SubtractRegular />}
                          aria-label={`One less ${l.itemName}`}
                          onClick={() => decrease(l.menuItemId)}
                        />
                        <span className={styles.qty} aria-label={`${l.quantity} ${l.itemName}`}>
                          {l.quantity}
                        </span>
                        <Button
                          size="small"
                          appearance="subtle"
                          icon={<AddRegular />}
                          aria-label={`One more ${l.itemName}`}
                          onClick={() => byId.get(l.menuItemId) && add(byId.get(l.menuItemId)!)}
                        />
                      </span>
                      <span style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}>
                        <Text className={styles.right}>{formatInr(l.lineTotal)}</Text>
                        <Button
                          size="small"
                          appearance="subtle"
                          icon={<DeleteRegular />}
                          aria-label={`Remove ${l.itemName}`}
                          onClick={() => remove(l.menuItemId)}
                        />
                      </span>
                    </div>
                  ))}
                </div>
              )}
              {dropped > 0 && (
                <Caption1 style={{ color: tokens.colorPaletteMarigoldForeground1 }}>
                  Items no longer on the menu were left out of the bill.
                </Caption1>
              )}

              <div className={styles.totals} aria-live="polite">
                <Caption1>Taxable value</Caption1>
                <Caption1 className={styles.right}>{formatInr(bill.taxableAmount)}</Caption1>
                <Caption1>CGST</Caption1>
                <Caption1 className={styles.right}>{formatInr(bill.cgst)}</Caption1>
                <Caption1>SGST</Caption1>
                <Caption1 className={styles.right}>{formatInr(bill.sgst)}</Caption1>
                <Caption1>Round off</Caption1>
                <Caption1 className={styles.right}>{formatInr(bill.roundOff)}</Caption1>
                <Text weight="semibold" style={{ alignSelf: 'end' }}>
                  To pay
                </Text>
                <span className={styles.net}>{formatInr(bill.netAmount)}</span>
              </div>

              <Controller
                control={form.control}
                name="customerType"
                render={({ field, fieldState }) => (
                  <Field
                    label="Customer"
                    validationMessage={fieldState.error?.message}
                    validationState={fieldState.error ? 'error' : 'none'}
                  >
                    <div className={styles.segments} role="radiogroup" aria-label="Customer">
                      {CUSTOMER_TYPES.map((t) => (
                        <ToggleButton
                          key={t}
                          role="radio"
                          aria-checked={field.value === t}
                          className={styles.segment}
                          checked={field.value === t}
                          onClick={() => {
                            field.onChange(t);
                            if (t !== 'Staff' && form.getValues('paymentMode') === 'StaffCredit') {
                              form.setValue('paymentMode', 'Cash');
                            }
                          }}
                        >
                          {CUSTOMER_LABEL[t]}
                        </ToggleButton>
                      ))}
                    </div>
                  </Field>
                )}
              />
              <Controller
                control={form.control}
                name="paymentMode"
                render={({ field }) => (
                  <Field label="Paid by">
                    <div className={styles.segments} role="radiogroup" aria-label="Paid by">
                      {PAYMENT_MODES.map((m) => (
                        <ToggleButton
                          key={m}
                          role="radio"
                          aria-checked={field.value === m}
                          className={styles.segment}
                          checked={field.value === m}
                          disabled={m === 'StaffCredit' && customerType !== 'Staff'}
                          title={
                            m === 'StaffCredit' && customerType !== 'Staff' ? 'Only staff can buy on credit' : undefined
                          }
                          onClick={() => field.onChange(m)}
                        >
                          {m === 'StaffCredit' ? 'Credit' : PAYMENT_LABEL[m]}
                        </ToggleButton>
                      ))}
                    </div>
                  </Field>
                )}
              />

              {customerType === 'Staff' && (
                <TextField
                  control={form.control}
                  name="employeeCode"
                  label={paymentMode === 'StaffCredit' ? 'Employee code (staff credit)' : 'Employee code'}
                  required={paymentMode === 'StaffCredit'}
                  maxLength={20}
                  autoComplete="off"
                  hint={
                    paymentMode === 'StaffCredit'
                      ? 'Checked with HR; the name on the bill comes from HR records.'
                      : undefined
                  }
                />
              )}
              {customerType === 'Staff' && paymentMode !== 'StaffCredit' && (
                <TextField control={form.control} name="employeeName" label="Name" maxLength={200} autoComplete="off" />
              )}
              {(customerType === 'Visitor' || customerType === 'Attendant') && (
                <TextField
                  control={form.control}
                  name="customerName"
                  label="Name (optional)"
                  maxLength={200}
                  autoComplete="off"
                />
              )}
              {customerType === 'Patient' && (
                <Controller
                  control={form.control}
                  name="patient"
                  render={({ field }) => (
                    <PatientPicker value={field.value} onChange={field.onChange} label="Patient (optional)" />
                  )}
                />
              )}
              {(paymentMode === 'Upi' || paymentMode === 'Card') && (
                <TextField
                  control={form.control}
                  name="paymentReference"
                  label={paymentMode === 'Upi' ? 'UPI transaction ID' : 'Card slip number'}
                  maxLength={64}
                  autoComplete="off"
                  hint="Optional, helps when reconciling"
                />
              )}
              {paymentMode === 'Cash' && priced.length > 0 && (
                <Field
                  label="Cash received"
                  validationMessage={
                    short ? 'Less than the bill amount' : change !== null ? `Return ${formatInr(change)}` : undefined
                  }
                  validationState={short ? 'warning' : change !== null ? 'success' : 'none'}
                >
                  <Input
                    type="number"
                    inputMode="decimal"
                    min={0}
                    value={tendered}
                    onChange={(_, d) => setTendered(d.value)}
                    contentBefore={<Text>₹</Text>}
                  />
                </Field>
              )}

              {serverError && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>{isApiError(serverError) ? serverError.title : 'Not charged'}</MessageBarTitle>
                    {isApiError(serverError) ? serverError.userMessage : 'Please try again. The bill was not saved.'}
                  </MessageBarBody>
                </MessageBar>
              )}

              <Checkbox
                checked={autoPrint === '1'}
                onChange={(_, d) => setAutoPrint(d.checked ? '1' : '0')}
                label="Print the receipt automatically"
              />
              <Button
                type="submit"
                appearance="primary"
                className={styles.charge}
                disabled={priced.length === 0 || sale.isPending}
                icon={sale.isPending ? <Spinner size="tiny" aria-hidden /> : <ReceiptRegular />}
                aria-keyshortcuts="F9"
              >
                Charge {formatInr(bill.netAmount)} (F9)
              </Button>
            </form>
          </Section>

          {lastSale && (
            <Section style={{ marginTop: 12 }}>
              <div className={styles.last}>
                <span>
                  <Text weight="semibold">Last bill {lastSale.billNumber}</Text>
                  <Caption1 block className={styles.muted}>
                    {formatInr(lastSale.netAmount)} · {PAYMENT_LABEL[lastSale.paymentMode]}
                    {lastSale.employeeName ? ` · ${lastSale.employeeName}` : ''}
                  </Caption1>
                </span>
                <span style={{ display: 'inline-flex', gap: 6 }}>
                  <Button
                    size="small"
                    icon={<PrintRegular />}
                    onClick={() =>
                      void printCanteenReceipt(lastSale, letterhead, { duplicate: true }).catch((error: unknown) =>
                        notify.error(error, 'Printing failed'),
                      )
                    }
                  >
                    Reprint
                  </Button>
                  <Button size="small" icon={<OpenRegular />} onClick={() => setOpenSale(lastSale.id)}>
                    Open
                  </Button>
                </span>
              </div>
            </Section>
          )}
        </div>
      </div>
      {openSale && <SaleDrawer saleId={openSale} onClose={() => setOpenSale(null)} />}
    </Page>
  );
}
