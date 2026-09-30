import {
  Button,
  Caption1,
  Checkbox,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { BoxArrowLeftRegular, DeleteRegular, DocumentArrowLeftRegular, SaveRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatInr, formatRelative, isApiError, todayIso, useDraft } from '@hc360/core';
import { ConfirmDialog, EmptyState, ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import type { ItemLookup } from '@/modules/inventory/api';
import { dec, toNumber } from '@/modules/inventory/decimal';
import { formatPercent, formatQty, parseNumber } from '@/modules/inventory/format';
import { GST_RATES } from '@/modules/inventory/gst';
import { ItemPicker } from '@/modules/inventory/ItemPicker';
import type { ItemType } from '@/modules/inventory/labels';
import { LineTable } from '@/modules/inventory/LineTable';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { useWorkingStore } from '@/modules/inventory/stores';
import { useItemMaster } from '@/modules/pharmacy/counter';
import {
  orderStatusLabel,
  purchaseApi,
  purchaseKeys,
  purchaseQueries,
  type PurchaseOrderDetail,
  type VendorListItem,
} from '@/modules/purchase/api';
import {
  explainGstSplit,
  grnLineProblem,
  grnUnitEconomics,
  isShortExpiry,
  previewLine,
  previewTotals,
  tracksExpiry,
} from '@/modules/purchase/gstPreview';
import { GstSplitNote, PurchaseTotals } from '@/modules/purchase/GstSplitNote';
import { useFacilityGstin } from '@/modules/purchase/hooks';
import { VendorPicker } from '@/modules/purchase/VendorPicker';

export const Route = createFileRoute('/_app/purchase/grns/new')({
  validateSearch: (s: Record<string, unknown>): { orderId?: string } => ({
    orderId: typeof s.orderId === 'string' && s.orderId ? s.orderId : undefined,
  }),
  component: ReceiveGoodsPage,
});

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 360px',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1280px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  aside: { display: 'flex', flexDirection: 'column', gap: '16px', position: 'sticky', top: '16px' },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(210px, 1fr))', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  problem: { color: tokens.colorPaletteRedForeground1 },
  warn: { color: tokens.colorPaletteMarigoldForeground1 },
  small: { width: '84px' },
  medium: { width: '130px' },
  money: { fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' },
});

/** Receive goods: against a purchase order (lines prefilled) or straight from a vendor invoice. */
function ReceiveGoodsPage() {
  const { orderId } = Route.useSearch();
  const canReceive = usePermission(S.grnReceive);
  const order = useQuery({ ...purchaseQueries.order(orderId ?? ''), enabled: Boolean(orderId) });
  if (!canReceive) {
    return (
      <Page maxWidth={800}>
        <EmptyState
          title="You cannot receive goods"
          description="Ask the store in charge or an administrator for access."
        />
      </Page>
    );
  }
  if (orderId && order.isLoading) {
    return <LoadingState label="Loading the purchase order…" />;
  }
  if (orderId && order.error) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={order.error} onRetry={() => void order.refetch()} />
      </Page>
    );
  }
  return <ReceiveForm key={orderId ?? 'direct'} order={orderId ? (order.data ?? null) : null} />;
}

interface GrnLine {
  key: string;
  itemId: string;
  itemName: string;
  itemType: ItemType | null;
  purchaseUnit: string | null;
  factor: number | null;
  poLineId: string | null;
  pending: number | null;
  batchNumber: string;
  manufactureDate: string;
  expiryDate: string;
  quantity: string;
  freeQuantity: string;
  rate: string;
  mrp: string;
  discountPercent: string;
  /** '' = the item master's rate. */
  gstRate: string;
  acceptShortExpiry: boolean;
}

interface GrnDraft {
  invoiceNumber: string;
  invoiceDate: string;
  receivedOn: string;
  remarks: string;
  lines: GrnLine[];
}

interface PickedVendor {
  id: string;
  name: string;
  code?: string;
  gstin: string | null;
  licenceExpired: boolean;
  hasLicence: boolean;
}

function linesFromOrder(order: PurchaseOrderDetail): GrnLine[] {
  return order.lines
    .filter((l) => l.pendingQuantity > 0)
    .map((l) => ({
      key: l.id,
      itemId: l.itemId,
      itemName: l.itemName,
      itemType: null,
      purchaseUnit: l.purchaseUnit,
      factor: null,
      poLineId: l.id,
      pending: l.pendingQuantity,
      batchNumber: '',
      manufactureDate: '',
      expiryDate: '',
      quantity: String(l.pendingQuantity),
      freeQuantity: l.freeQuantity ? String(l.freeQuantity) : '',
      rate: String(l.rate),
      mrp: '',
      discountPercent: l.discountPercent ? String(l.discountPercent) : '',
      gstRate: String(l.gstRate),
      acceptShortExpiry: false,
    }));
}

function ReceiveForm({ order }: { order: PurchaseOrderDetail | null }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const working = useWorkingStore('purchase');
  const facilityGstin = useFacilityGstin();
  const today = todayIso();
  const [vendor, setVendor] = useState<PickedVendor | null>(
    order
      ? {
          id: order.vendorId,
          name: order.vendorName,
          gstin: order.vendorGstin,
          licenceExpired: false,
          hasLicence: true,
        }
      : null,
  );
  const [storeId, setStoreId] = useState<string | null>(order?.storeId ?? null);
  const [invoiceNumber, setInvoiceNumber] = useState('');
  const [invoiceDate, setInvoiceDate] = useState('');
  const [receivedOn, setReceivedOn] = useState(today);
  const [invoiceTotal, setInvoiceTotal] = useState('');
  const [remarks, setRemarks] = useState('');
  const [lines, setLines] = useState<GrnLine[]>(() => (order ? linesFromOrder(order) : []));
  const [confirmPost, setConfirmPost] = useState(false);
  const effectiveStore = storeId ?? working.storeId;
  const master = useItemMaster(lines.map((l) => l.itemId));
  const openOrders = useQuery({
    ...purchaseQueries.orders({ vendorId: vendor?.id, storeId: effectiveStore ?? undefined }),
    enabled: Boolean(vendor && effectiveStore) && !order,
  });
  const receivableOrders = (openOrders.data?.items ?? []).filter(
    (o) => o.status === 'Approved' || o.status === 'Sent' || o.status === 'PartiallyReceived',
  );
  const split = explainGstSplit(vendor?.gstin, facilityGstin ?? null);

  const draftValue: GrnDraft = { invoiceNumber, invoiceDate, receivedOn, remarks, lines };
  const draft = useDraft<GrnDraft>(`grn-new:${order?.id ?? 'direct'}`, draftValue, {
    enabled: lines.some((l) => l.batchNumber || l.mrp) || invoiceNumber !== '',
  });
  const [savedDraft, setSavedDraft] = useState(() => draft.restore());

  const typeOf = (line: GrnLine): ItemType => line.itemType ?? master.items.get(line.itemId)?.type ?? 'General';
  const factorOf = (line: GrnLine) => line.factor ?? master.items.get(line.itemId)?.purchaseUnitFactor ?? 1;
  const gstOf = (line: GrnLine) => parseNumber(line.gstRate) ?? master.items.get(line.itemId)?.gstRate ?? 0;

  const planned = lines.map((line) => {
    const quantity = parseNumber(line.quantity) ?? 0;
    const free = parseNumber(line.freeQuantity) ?? 0;
    const rate = parseNumber(line.rate) ?? 0;
    const mrp = parseNumber(line.mrp) ?? 0;
    const discount = parseNumber(line.discountPercent) ?? 0;
    const type = typeOf(line);
    const priced = previewLine({ quantity, rate, discountPercent: discount, gstRate: gstOf(line) }, split.interState);
    const problem =
      grnLineProblem(
        {
          itemName: line.itemName,
          itemType: type,
          unitsPerPurchaseUnit: factorOf(line),
          batchNumber: line.batchNumber,
          manufactureDate: line.manufactureDate || null,
          expiryDate: line.expiryDate || null,
          quantity,
          freeQuantity: free,
          rate,
          mrp,
          discountPercent: discount,
          gstRate: gstOf(line),
          acceptShortExpiry: line.acceptShortExpiry,
        },
        receivedOn || today,
      ) ??
      (line.pending !== null && quantity > line.pending
        ? `Only ${formatQty(line.pending)} is pending on the order.`
        : null);
    const economics = grnUnitEconomics(quantity, free, factorOf(line), priced.tax.taxable, mrp);
    return {
      line,
      quantity,
      free,
      rate,
      mrp,
      discount,
      type,
      priced,
      problem,
      economics,
      shortExpiry: isShortExpiry(line.expiryDate || null, receivedOn || today),
    };
  });
  const totals = previewTotals(
    planned.map((p) => ({ quantity: p.quantity, rate: p.rate, discountPercent: p.discount, gstRate: gstOf(p.line) })),
    split.interState,
  );
  const printedTotal = parseNumber(invoiceTotal);
  const difference = printedTotal === null ? null : toNumber(dec(totals.net) - dec(printedTotal));
  const hasDrugs = planned.some((p) => p.type === 'Drug');
  const licenceProblem = vendor && hasDrugs && (vendor.licenceExpired || !vendor.hasLicence);
  const blockers = [
    ...(vendor ? [] : ['Choose the vendor.']),
    ...(effectiveStore ? [] : ['Choose the store.']),
    ...(invoiceNumber.trim() ? [] : ["Enter the vendor's invoice number."]),
    ...(receivedOn && receivedOn <= today ? [] : ['The receipt date cannot be in the future.']),
    ...(lines.length === 0 ? ['Add the items received.'] : []),
    ...planned.filter((p) => p.problem).map((p) => `${p.line.itemName}: ${p.problem}`),
  ];

  const update = (key: string, patch: Partial<GrnLine>) =>
    setLines(lines.map((l) => (l.key === key ? { ...l, ...patch } : l)));
  const addItem = (item: ItemLookup) =>
    setLines([
      ...lines,
      {
        key: `${item.id}-${lines.length}`,
        itemId: item.id,
        itemName: item.name,
        itemType: item.type,
        purchaseUnit: item.purchaseUnit,
        factor: item.purchaseUnitFactor,
        poLineId: null,
        pending: null,
        batchNumber: '',
        manufactureDate: '',
        expiryDate: '',
        quantity: '',
        freeQuantity: '',
        rate: '',
        mrp: '',
        discountPercent: '',
        gstRate: '',
        acceptShortExpiry: false,
      },
    ]);

  const create = useMutation({
    mutationFn: (post: boolean) =>
      purchaseApi.createGrn({
        vendorId: vendor!.id,
        storeId: effectiveStore!,
        purchaseOrderId: order?.id ?? null,
        vendorInvoiceNumber: invoiceNumber.trim(),
        vendorInvoiceDate: invoiceDate || null,
        receivedOn: receivedOn || null,
        remarks: remarks.trim() || null,
        post,
        lines: planned.map((p) => ({
          itemId: p.line.itemId,
          purchaseOrderLineId: p.line.poLineId,
          batchNumber: p.line.batchNumber.trim() || null,
          manufactureDate: p.line.manufactureDate || null,
          expiryDate: p.line.expiryDate || null,
          quantity: p.quantity,
          freeQuantity: p.free,
          rate: p.rate,
          mrp: p.mrp,
          discountPercent: p.discount,
          gstRate: parseNumber(p.line.gstRate),
          acceptShortExpiry: p.line.acceptShortExpiry,
        })),
      }),
    onSuccess: (result) => {
      setConfirmPost(false);
      draft.clear();
      void queryClient.invalidateQueries({ queryKey: purchaseKeys.all });
      void queryClient.invalidateQueries({ queryKey: ['inventory'] });
      notify.success(
        result.status === 'Posted' ? `GRN ${result.number} posted` : `GRN ${result.number} saved as draft`,
        result.status === 'Posted'
          ? `${formatInr(result.netAmount)} of goods added to stock.`
          : 'Post it after checking the goods to add them to stock.',
      );
      void navigate({ to: '/purchase/grns/$grnId', params: { grnId: result.id }, replace: true });
    },
    onError: () => setConfirmPost(false),
  });
  const serverError = create.error;

  return (
    <Page>
      <PageHeader
        title="Receive goods"
        subtitle={
          order
            ? `Against purchase order ${order.number} (${orderStatusLabel[order.status]})`
            : 'Enter the vendor invoice line by line: batch, expiry, MRP and free goods'
        }
        breadcrumbs={[
          { label: 'Goods receipts', onClick: () => void navigate({ to: '/purchase/grns' }) },
          { label: 'Receive' },
        ]}
      />
      {savedDraft && (
        <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
          <MessageBarBody>
            An unfinished receipt from {formatRelative(new Date(savedDraft.savedAt).toISOString())} was found on this
            computer.
          </MessageBarBody>
          <MessageBarActions>
            <Button
              onClick={() => {
                setInvoiceNumber(savedDraft.value.invoiceNumber);
                setInvoiceDate(savedDraft.value.invoiceDate);
                setReceivedOn(savedDraft.value.receivedOn);
                setRemarks(savedDraft.value.remarks);
                setLines(savedDraft.value.lines);
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
          <Section title="Vendor invoice">
            <div className={styles.grid}>
              {order ? (
                <Field label="Vendor">
                  <Text weight="semibold">{order.vendorName}</Text>
                </Field>
              ) : (
                <VendorPicker
                  value={vendor}
                  required
                  autoFocus
                  onPick={(v: VendorListItem) =>
                    setVendor({
                      id: v.id,
                      name: v.name,
                      code: v.code,
                      gstin: v.gstin,
                      licenceExpired: v.drugLicenseExpired,
                      hasLicence: Boolean(v.drugLicenseNumber),
                    })
                  }
                />
              )}
              {order ? (
                <Field label="Store">
                  <Text weight="semibold">{order.storeName}</Text>
                </Field>
              ) : (
                <StorePicker
                  stores={working.stores}
                  value={effectiveStore}
                  onChange={setStoreId}
                  label="Receive into"
                  required
                  loading={working.loading}
                />
              )}
              <Field
                label="Invoice number"
                required
                validationMessage={
                  isApiError(serverError) && serverError.code === 'grn.duplicate_invoice'
                    ? serverError.userMessage
                    : undefined
                }
              >
                <Input value={invoiceNumber} maxLength={40} onChange={(_, d) => setInvoiceNumber(d.value)} />
              </Field>
              <Field label="Invoice date">
                <Input type="date" max={today} value={invoiceDate} onChange={(_, d) => setInvoiceDate(d.value)} />
              </Field>
              <Field label="Received on" required>
                <Input type="date" max={today} value={receivedOn} onChange={(_, d) => setReceivedOn(d.value)} />
              </Field>
              <Field label="Invoice total (as printed)" hint="To check the entry against the paper invoice">
                <Input
                  type="number"
                  contentBefore="₹"
                  value={invoiceTotal}
                  onChange={(_, d) => setInvoiceTotal(d.value)}
                />
              </Field>
            </div>
            {!order && vendor && receivableOrders.length > 0 && (
              <div style={{ marginTop: 12 }}>
                <Field label="Against a purchase order" hint="Choosing an order fills in its pending items">
                  <Select
                    value=""
                    onChange={(_, d) =>
                      d.value &&
                      void navigate({ to: '/purchase/grns/new', search: { orderId: d.value }, replace: true })
                    }
                  >
                    <option value="">Direct purchase (no order)</option>
                    {receivableOrders.map((o) => (
                      <option key={o.id} value={o.id}>
                        {o.number} · {formatDateOnly(o.orderDate)} · {formatInr(o.netAmount)} ·{' '}
                        {orderStatusLabel[o.status]}
                      </option>
                    ))}
                  </Select>
                </Field>
              </div>
            )}
            {licenceProblem && (
              <MessageBar intent="error" style={{ marginTop: 12 }}>
                <MessageBarBody>
                  <MessageBarTitle>Medicines cannot be received from this vendor</MessageBarTitle>
                  {vendor.licenceExpired ? 'Their drug licence has expired.' : 'No drug licence is on record.'} Update
                  the vendor first, or remove the medicines from this receipt.
                </MessageBarBody>
              </MessageBar>
            )}
          </Section>

          <Section
            title="Items received"
            description="Quantities, free goods and rates per purchase unit. MRP as printed on one purchase unit (GST included)."
            flush
          >
            {!order && (
              <div style={{ padding: '0 16px 12px' }}>
                <ItemPicker storeId={effectiveStore} onPick={addItem} label="Add item" showStock={false} />
              </div>
            )}
            <LineTable
              ariaLabel="Items received"
              rows={planned}
              getRowId={(p) => p.line.key}
              isFlagged={(p) =>
                p.problem !== null && (p.line.batchNumber !== '' || p.line.mrp !== '' || p.line.expiryDate !== '')
              }
              empty={
                <EmptyState
                  title="No items yet"
                  description={
                    order
                      ? 'Everything on this order has been received.'
                      : 'Search above to add each line of the invoice.'
                  }
                />
              }
              columns={[
                {
                  key: 'item',
                  header: 'Item',
                  render: (p) => (
                    <span>
                      <Text weight="semibold">{p.line.itemName}</Text>
                      <Caption1 block className={styles.muted}>
                        {p.line.purchaseUnit ?? master.items.get(p.line.itemId)?.purchaseUnit ?? ''}
                        {factorOf(p.line) !== 1
                          ? ` of ${formatQty(factorOf(p.line), master.items.get(p.line.itemId)?.baseUnit)}`
                          : ''}
                        {p.line.pending !== null ? ` · ${formatQty(p.line.pending)} pending on the order` : ''}
                      </Caption1>
                      {p.problem && (p.line.batchNumber !== '' || p.line.mrp !== '' || p.line.expiryDate !== '') && (
                        <Caption1 block className={styles.problem}>
                          {p.problem}
                        </Caption1>
                      )}
                    </span>
                  ),
                },
                {
                  key: 'batch',
                  header: 'Batch and dates',
                  render: (p) => (
                    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                      <Input
                        className={styles.medium}
                        placeholder={tracksExpiry(p.type) ? 'Batch number' : 'Batch (optional)'}
                        aria-label={`Batch of ${p.line.itemName}`}
                        value={p.line.batchNumber}
                        maxLength={40}
                        onChange={(_, d) => update(p.line.key, { batchNumber: d.value.toUpperCase() })}
                      />
                      <Tooltip content="Expiry date" relationship="label">
                        <Input
                          className={styles.medium}
                          type="date"
                          aria-label={`Expiry of ${p.line.itemName}`}
                          value={p.line.expiryDate}
                          onChange={(_, d) => update(p.line.key, { expiryDate: d.value })}
                        />
                      </Tooltip>
                      <Tooltip content="Manufacturing date (optional)" relationship="label">
                        <Input
                          className={styles.medium}
                          type="date"
                          max={today}
                          aria-label={`Manufacturing date of ${p.line.itemName}`}
                          value={p.line.manufactureDate}
                          onChange={(_, d) => update(p.line.key, { manufactureDate: d.value })}
                        />
                      </Tooltip>
                      {p.shortExpiry && (
                        <Checkbox
                          checked={p.line.acceptShortExpiry}
                          onChange={(_, d) => update(p.line.key, { acceptShortExpiry: d.checked === true })}
                          label={<Caption1 className={styles.warn}>Accept short expiry</Caption1>}
                        />
                      )}
                    </div>
                  ),
                },
                {
                  key: 'qty',
                  header: 'Qty + free',
                  render: (p) => (
                    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                      <Input
                        className={styles.small}
                        type="number"
                        min={0}
                        aria-label={`Quantity of ${p.line.itemName}`}
                        value={p.line.quantity}
                        onChange={(_, d) => update(p.line.key, { quantity: d.value })}
                      />
                      <Input
                        className={styles.small}
                        type="number"
                        min={0}
                        placeholder="Free"
                        aria-label={`Free quantity of ${p.line.itemName}`}
                        value={p.line.freeQuantity}
                        onChange={(_, d) => update(p.line.key, { freeQuantity: d.value })}
                      />
                    </div>
                  ),
                },
                {
                  key: 'price',
                  header: 'Rate, MRP, disc.',
                  render: (p) => (
                    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                      <Input
                        className={styles.medium}
                        type="number"
                        min={0}
                        contentBefore="₹"
                        placeholder="Rate"
                        aria-label={`Rate of ${p.line.itemName}`}
                        value={p.line.rate}
                        onChange={(_, d) => update(p.line.key, { rate: d.value })}
                      />
                      <Input
                        className={styles.medium}
                        type="number"
                        min={0}
                        contentBefore="MRP"
                        aria-label={`MRP of ${p.line.itemName}`}
                        value={p.line.mrp}
                        onChange={(_, d) => update(p.line.key, { mrp: d.value })}
                      />
                      <Input
                        className={styles.medium}
                        type="number"
                        min={0}
                        max={100}
                        contentAfter="%"
                        placeholder="Discount"
                        aria-label={`Discount on ${p.line.itemName}`}
                        value={p.line.discountPercent}
                        onChange={(_, d) => update(p.line.key, { discountPercent: d.value })}
                      />
                    </div>
                  ),
                },
                {
                  key: 'gst',
                  header: 'GST',
                  render: (p) => {
                    const itemRate = master.items.get(p.line.itemId)?.gstRate;
                    return (
                      <Select
                        aria-label={`GST rate of ${p.line.itemName}`}
                        value={p.line.gstRate}
                        onChange={(_, d) => update(p.line.key, { gstRate: d.value })}
                      >
                        <option value="">
                          {itemRate === undefined ? 'Item rate' : `Item (${formatPercent(itemRate)})`}
                        </option>
                        {GST_RATES.map((r) => (
                          <option key={r} value={String(r)}>
                            {formatPercent(r)}
                          </option>
                        ))}
                      </Select>
                    );
                  },
                },
                {
                  key: 'amount',
                  header: 'Amount',
                  align: 'end',
                  render: (p) => (
                    <span className={styles.money}>
                      <Text weight="semibold">{formatInr(p.priced.net)}</Text>
                      <Caption1 block className={styles.muted}>
                        Taxable {formatInr(p.priced.tax.taxable)}
                      </Caption1>
                      <Caption1 block className={styles.muted}>
                        {split.interState
                          ? `IGST ${formatInr(p.priced.tax.igst)}`
                          : `CGST ${formatInr(p.priced.tax.cgst)} · SGST ${formatInr(p.priced.tax.sgst)}`}
                      </Caption1>
                      {p.economics.baseQuantity > 0 && (
                        <Caption1 block className={styles.muted}>
                          {formatQty(p.economics.baseQuantity)} into stock at {formatInr(p.economics.unitCost)} each
                        </Caption1>
                      )}
                    </span>
                  ),
                },
                {
                  key: 'remove',
                  header: '',
                  width: 44,
                  render: (p) => (
                    <Button
                      appearance="subtle"
                      icon={<DeleteRegular />}
                      aria-label={`Remove ${p.line.itemName}`}
                      onClick={() => setLines(lines.filter((l) => l.key !== p.line.key))}
                    />
                  ),
                },
              ]}
            />
          </Section>

          <Section title="Remarks">
            <Textarea
              value={remarks}
              rows={2}
              maxLength={1000}
              onChange={(_, d) => setRemarks(d.value)}
              style={{ width: '100%' }}
              aria-label="Remarks"
            />
          </Section>
        </div>

        <aside className={styles.aside}>
          {vendor && <GstSplitNote split={split} />}
          <Section title="Invoice value">
            <PurchaseTotals totals={totals} interState={split.interState} preview />
            {difference !== null && Math.abs(difference) >= 1 && (
              <MessageBar intent="warning" style={{ marginTop: 8 }}>
                <MessageBarBody>
                  Differs from the printed total by {formatInr(Math.abs(difference))}. Check the rates, discounts, free
                  goods and GST.
                </MessageBarBody>
              </MessageBar>
            )}
            {difference !== null && Math.abs(difference) < 1 && (
              <Caption1 block style={{ marginTop: 8, color: tokens.colorPaletteGreenForeground1 }}>
                Matches the printed invoice total.
              </Caption1>
            )}
          </Section>
          <Section>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {serverError && !(isApiError(serverError) && serverError.code === 'grn.duplicate_invoice') && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Not saved</MessageBarTitle>
                    {isApiError(serverError) ? serverError.userMessage : 'Try again.'}
                  </MessageBarBody>
                </MessageBar>
              )}
              {blockers.length > 0 && lines.length > 0 && <Caption1 className={styles.problem}>{blockers[0]}</Caption1>}
              <Button
                appearance="primary"
                size="large"
                icon={create.isPending ? <Spinner size="tiny" /> : <BoxArrowLeftRegular />}
                disabled={blockers.length > 0 || Boolean(licenceProblem) || create.isPending}
                onClick={() => setConfirmPost(true)}
              >
                Receive into stock
              </Button>
              <Button
                icon={<SaveRegular />}
                disabled={blockers.length > 0 || create.isPending}
                onClick={() => create.mutate(false)}
              >
                Save as draft
              </Button>
              <Caption1 className={styles.muted}>
                A draft keeps the entry for checking; stock changes only when it is posted.
              </Caption1>
            </div>
          </Section>
        </aside>
      </div>
      <ConfirmDialog
        open={confirmPost}
        title="Receive these goods into stock?"
        confirmLabel="Receive and post"
        busy={create.isPending}
        onConfirm={() => create.mutate(true)}
        onCancel={() => setConfirmPost(false)}
      >
        {lines.length} line{lines.length === 1 ? '' : 's'} worth {formatInr(totals.net)} are added to{' '}
        {order?.storeName ?? working.stores.find((s) => s.id === effectiveStore)?.name ?? 'the store'} with their batch,
        expiry and MRP. A posted receipt cannot be edited; mistakes are corrected with a stock adjustment.
      </ConfirmDialog>
    </Page>
  );
}
