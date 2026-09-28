import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Select,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { DeleteRegular, SaveRegular, SendRegular } from '@fluentui/react-icons';
import { formatInr, isApiError, todayIso, useHotkey } from '@hc360/core';
import { EmptyState, Section, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import type { ItemLookup } from '@/modules/inventory/api';
import { formatPercent, parseNumber } from '@/modules/inventory/format';
import { GST_RATES, isStandardGstRate } from '@/modules/inventory/gst';
import { ItemPicker } from '@/modules/inventory/ItemPicker';
import { LineTable } from '@/modules/inventory/LineTable';
import { StorePicker } from '@/modules/inventory/StorePicker';
import { useWorkingStore } from '@/modules/inventory/stores';
import { useItemMaster } from '@/modules/pharmacy/counter';
import { purchaseApi, purchaseKeys, type PurchaseOrderDetail, type RequisitionDetail, type SavePurchaseOrderRequest, type VendorListItem } from './api';
import { explainGstSplit, previewLine, previewTotals } from './gstPreview';
import { GstSplitNote, PurchaseTotals } from './GstSplitNote';
import { useFacilityGstin } from './hooks';
import { VendorPicker } from './VendorPicker';

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 360px',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1180px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  aside: { display: 'flex', flexDirection: 'column', gap: '16px', position: 'sticky', top: '16px' },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  problem: { color: tokens.colorPaletteRedForeground1 },
  num: { width: '96px' },
  money: { fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' },
});

interface PickedVendor {
  id: string;
  name: string;
  gstin: string | null;
  paymentTermsDays: number | null;
  licenceExpired: boolean;
}

interface OrderLine {
  key: string;
  itemId: string;
  itemName: string;
  purchaseUnit: string | null;
  quantity: string;
  freeQuantity: string;
  rate: string;
  discountPercent: string;
  /** '' = the item master's rate. */
  gstRate: string;
}

function fromOrder(order: PurchaseOrderDetail): OrderLine[] {
  return order.lines.map((l) => ({
    key: l.id,
    itemId: l.itemId,
    itemName: l.itemName,
    purchaseUnit: l.purchaseUnit,
    quantity: String(l.quantity),
    freeQuantity: l.freeQuantity ? String(l.freeQuantity) : '',
    rate: String(l.rate),
    discountPercent: l.discountPercent ? String(l.discountPercent) : '',
    gstRate: String(l.gstRate),
  }));
}

function fromRequisition(requisition: RequisitionDetail): OrderLine[] {
  return requisition.lines.map((l) => ({
    key: l.id,
    itemId: l.itemId,
    itemName: l.itemName,
    purchaseUnit: null,
    quantity: String(l.quantity),
    freeQuantity: '',
    rate: '',
    discountPercent: '',
    gstRate: '',
  }));
}

/**
 * Purchase order editor (new or draft). Rates exclude GST; the GST split (CGST + SGST or IGST) follows the vendor's
 * and the hospital's GSTIN states and is previewed live. Ctrl+S saves the draft.
 */
export function OrderForm({
  order,
  requisition,
  onSaved,
  onCancel,
}: {
  order: PurchaseOrderDetail | null;
  requisition?: RequisitionDetail | null;
  onSaved: (id: string, submitted: boolean) => void;
  onCancel: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const working = useWorkingStore('purchase');
  const facilityGstin = useFacilityGstin();
  const [vendor, setVendor] = useState<PickedVendor | null>(
    order ? { id: order.vendorId, name: order.vendorName, gstin: order.vendorGstin, paymentTermsDays: order.paymentTermsDays, licenceExpired: false } : null,
  );
  const [storeId, setStoreId] = useState<string | null>(order?.storeId ?? requisition?.storeId ?? null);
  const [expected, setExpected] = useState(order?.expectedDeliveryDate ?? requisition?.requiredBy ?? '');
  const [terms, setTerms] = useState(order ? String(order.paymentTermsDays) : '');
  const [remarks, setRemarks] = useState(order?.remarks ?? (requisition ? `Against requisition ${requisition.number}` : ''));
  const [lines, setLines] = useState<OrderLine[]>(() => (order ? fromOrder(order) : requisition ? fromRequisition(requisition) : []));
  const master = useItemMaster(lines.map((l) => l.itemId));
  const effectiveStore = storeId ?? working.storeId;
  const split = explainGstSplit(vendor?.gstin, facilityGstin ?? null);

  const effectiveGst = (line: OrderLine): number | null => {
    const typed = parseNumber(line.gstRate);
    return typed ?? master.items.get(line.itemId)?.gstRate ?? null;
  };
  const unitOf = (line: OrderLine) => line.purchaseUnit ?? master.items.get(line.itemId)?.purchaseUnit ?? '';

  const planned = lines.map((line) => {
    const quantity = parseNumber(line.quantity);
    const rate = parseNumber(line.rate);
    const discount = parseNumber(line.discountPercent) ?? 0;
    const free = parseNumber(line.freeQuantity) ?? 0;
    const gst = effectiveGst(line);
    const problems: string[] = [];
    if (!(quantity !== null && quantity > 0)) {
      problems.push('Enter the quantity.');
    }
    if (rate === null || rate < 0) {
      problems.push('Enter the rate.');
    }
    if (discount < 0 || discount > 100) {
      problems.push('Discount must be 0 to 100%.');
    }
    if (free < 0) {
      problems.push('Free quantity cannot be negative.');
    }
    if (gst !== null && !isStandardGstRate(gst)) {
      problems.push('Use a notified GST rate.');
    }
    const priced = previewLine({ quantity, rate, discountPercent: discount, gstRate: gst ?? 0 }, split.interState);
    return { line, quantity: quantity ?? 0, rate: rate ?? 0, discount, free, gst, problems, priced };
  });
  const totals = previewTotals(
    planned.map((p) => ({ quantity: p.quantity, rate: p.rate, discountPercent: p.discount, gstRate: p.gst ?? 0 })),
    split.interState,
  );
  const termsValue = terms.trim() === '' ? null : parseNumber(terms);
  const termsInvalid = termsValue !== null && (termsValue < 0 || termsValue > 365 || !Number.isInteger(termsValue));
  const blockers = [
    ...(vendor ? [] : ['Choose the vendor.']),
    ...(effectiveStore ? [] : ['Choose the store the goods are delivered to.']),
    ...(lines.length === 0 ? ['Add at least one item.'] : []),
    ...(termsInvalid ? ['Credit days must be a whole number from 0 to 365.'] : []),
    ...planned.flatMap((p) => p.problems.map((problem) => `${p.line.itemName}: ${problem}`)),
  ];

  const request = (): SavePurchaseOrderRequest => ({
    vendorId: vendor!.id,
    storeId: effectiveStore!,
    requisitionId: order?.requisitionId ?? requisition?.id ?? null,
    expectedDeliveryDate: expected || null,
    paymentTermsDays: termsValue,
    remarks: remarks.trim() || null,
    lines: planned.map((p) => ({
      itemId: p.line.itemId,
      quantity: p.quantity,
      freeQuantity: p.free,
      rate: p.rate,
      discountPercent: p.discount,
      gstRate: parseNumber(p.line.gstRate),
    })),
    version: order?.version ?? null,
  });

  const save = useMutation({
    mutationFn: async (submit: boolean) => {
      const body = request();
      let id = order?.id ?? null;
      if (id) {
        await purchaseApi.updateOrder(id, body);
      } else {
        id = (await purchaseApi.createOrder(body)).id;
      }
      if (submit) {
        await purchaseApi.submitOrder(id);
      }
      return { id, submit };
    },
    onSuccess: ({ id, submit }) => {
      void queryClient.invalidateQueries({ queryKey: purchaseKeys.all });
      notify.success(submit ? 'Order sent for approval' : 'Draft saved');
      onSaved(id, submit);
    },
  });

  useHotkey('mod+s', () => blockers.length === 0 && !save.isPending && save.mutate(false), { allowInInputs: true });

  const pickVendor = (v: VendorListItem) => {
    setVendor({ id: v.id, name: v.name, gstin: v.gstin, paymentTermsDays: v.paymentTermsDays, licenceExpired: v.drugLicenseExpired });
    if (terms.trim() === '') {
      setTerms(String(v.paymentTermsDays));
    }
  };
  const addItem = (item: ItemLookup) => {
    if (lines.some((l) => l.itemId === item.id)) {
      notify.info(`${item.name} is already on the order`);
      return;
    }
    setLines([
      ...lines,
      { key: `${item.id}-${lines.length}`, itemId: item.id, itemName: item.name, purchaseUnit: item.purchaseUnit, quantity: '', freeQuantity: '', rate: '', discountPercent: '', gstRate: '' },
    ]);
  };
  const update = (key: string, patch: Partial<OrderLine>) => setLines(lines.map((l) => (l.key === key ? { ...l, ...patch } : l)));

  return (
    <div className={styles.layout}>
      <div className={styles.main}>
        <Section title="Order">
          <div className={styles.grid}>
            {vendor && order ? (
              <Field label="Vendor">
                <Text weight="semibold">{vendor.name}</Text>
              </Field>
            ) : (
              <VendorPicker
                value={vendor}
                onPick={pickVendor}
                required
                autoFocus={!vendor}
              />
            )}
            <StorePicker stores={working.stores} value={effectiveStore} onChange={setStoreId} label="Deliver to store" required loading={working.loading} />
            <Field label="Expected delivery">
              <Input type="date" min={todayIso()} value={expected} onChange={(_, d) => setExpected(d.value)} />
            </Field>
            <Field label="Credit days" hint="Blank = the vendor's usual terms" validationState={termsInvalid ? 'error' : 'none'}>
              <Input type="number" min={0} max={365} value={terms} placeholder={vendor?.paymentTermsDays != null ? String(vendor.paymentTermsDays) : ''} onChange={(_, d) => setTerms(d.value)} />
            </Field>
          </div>
          {vendor?.licenceExpired && (
            <MessageBar intent="warning" style={{ marginTop: 12 }}>
              <MessageBarBody>This vendor's drug licence has expired. Medicines cannot be received from them until it is renewed.</MessageBarBody>
            </MessageBar>
          )}
        </Section>

        <Section title="Items" description="Quantities and rates are per purchase unit (strip, box, bottle); rates exclude GST." flush>
          <div style={{ padding: '0 16px 12px' }}>
            <ItemPicker storeId={effectiveStore} onPick={addItem} label="Add item" hint="Stock shown is what the receiving store has now" />
          </div>
          <LineTable
            ariaLabel="Order lines"
            rows={planned}
            getRowId={(p) => p.line.key}
            isFlagged={(p) => p.problems.length > 0 && (p.line.quantity !== '' || p.line.rate !== '')}
            empty={<EmptyState title="No items yet" description="Search above to add items, or create the order from an approved requisition." />}
            columns={[
              {
                key: 'item',
                header: 'Item',
                render: (p) => (
                  <span>
                    <Text weight="semibold">{p.line.itemName}</Text>
                    <Caption1 block className={styles.muted}>
                      {unitOf(p.line) ? `Per ${unitOf(p.line)}` : ''}
                    </Caption1>
                  </span>
                ),
              },
              {
                key: 'qty',
                header: 'Qty',
                width: 100,
                render: (p) => (
                  <Input className={styles.num} type="number" min={0} aria-label={`Quantity of ${p.line.itemName}`} value={p.line.quantity} onChange={(_, d) => update(p.line.key, { quantity: d.value })} />
                ),
              },
              {
                key: 'free',
                header: 'Free',
                width: 100,
                priority: 'low',
                render: (p) => (
                  <Input className={styles.num} type="number" min={0} aria-label={`Free quantity of ${p.line.itemName}`} value={p.line.freeQuantity} onChange={(_, d) => update(p.line.key, { freeQuantity: d.value })} />
                ),
              },
              {
                key: 'rate',
                header: 'Rate',
                width: 120,
                render: (p) => (
                  <Input style={{ width: 110 }} type="number" min={0} step="0.01" contentBefore="₹" aria-label={`Rate of ${p.line.itemName}`} value={p.line.rate} onChange={(_, d) => update(p.line.key, { rate: d.value })} />
                ),
              },
              {
                key: 'disc',
                header: 'Disc. %',
                width: 90,
                priority: 'low',
                render: (p) => (
                  <Input style={{ width: 80 }} type="number" min={0} max={100} aria-label={`Discount on ${p.line.itemName}`} value={p.line.discountPercent} onChange={(_, d) => update(p.line.key, { discountPercent: d.value })} />
                ),
              },
              {
                key: 'gst',
                header: 'GST',
                width: 130,
                render: (p) => {
                  const itemRate = master.items.get(p.line.itemId)?.gstRate;
                  return (
                    <Select aria-label={`GST rate of ${p.line.itemName}`} value={p.line.gstRate} onChange={(_, d) => update(p.line.key, { gstRate: d.value })}>
                      <option value="">{itemRate === undefined ? 'Item rate' : `Item (${formatPercent(itemRate)})`}</option>
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
                      {formatInr(p.priced.tax.taxable)} + {split.interState ? `IGST ${formatInr(p.priced.tax.igst)}` : `GST ${formatInr(p.priced.tax.gst)}`}
                    </Caption1>
                    {p.problems.length > 0 && (p.line.quantity !== '' || p.line.rate !== '') && (
                      <Caption1 block className={styles.problem}>
                        {p.problems[0]}
                      </Caption1>
                    )}
                  </span>
                ),
              },
              {
                key: 'remove',
                header: '',
                width: 44,
                render: (p) => <Button appearance="subtle" icon={<DeleteRegular />} aria-label={`Remove ${p.line.itemName}`} onClick={() => setLines(lines.filter((l) => l.key !== p.line.key))} />,
              },
            ]}
          />
        </Section>

        <Section title="Remarks">
          <Textarea value={remarks} rows={2} maxLength={1000} onChange={(_, d) => setRemarks(d.value)} style={{ width: '100%' }} aria-label="Remarks" />
        </Section>
      </div>

      <aside className={styles.aside}>
        {vendor && <GstSplitNote split={split} />}
        <Section title="Order value">
          <PurchaseTotals totals={totals} interState={split.interState} preview />
          <Caption1 block className={styles.muted} style={{ marginTop: 8 }}>
            {lines.length} item{lines.length === 1 ? '' : 's'}. The saved order shows the final GST worked out by the server.
          </Caption1>
        </Section>
        <Section>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            {save.error && (
              <MessageBar intent="error" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>Not saved</MessageBarTitle>
                  {isApiError(save.error) ? save.error.userMessage : 'Try again.'}
                </MessageBarBody>
              </MessageBar>
            )}
            {blockers.length > 0 && lines.length > 0 && (
              <Caption1 className={styles.problem}>{blockers[0]}</Caption1>
            )}
            <Button appearance="primary" icon={save.isPending ? <Spinner size="tiny" /> : <SendRegular />} disabled={blockers.length > 0 || save.isPending} onClick={() => save.mutate(true)}>
              Save and send for approval
            </Button>
            <Button icon={<SaveRegular />} disabled={blockers.length > 0 || save.isPending} onClick={() => save.mutate(false)}>
              Save draft (Ctrl+S)
            </Button>
            <Button appearance="subtle" onClick={onCancel} disabled={save.isPending}>
              Cancel
            </Button>
          </div>
        </Section>
      </aside>
    </div>
  );
}
