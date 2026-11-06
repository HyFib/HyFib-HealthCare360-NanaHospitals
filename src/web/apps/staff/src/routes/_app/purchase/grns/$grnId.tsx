import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { DismissCircleRegular, SendRegular, OpenRegular, PrintRegular, TagRegular } from '@fluentui/react-icons';
import { formatDateOnly, formatDateTime, formatInr, isApiError } from '@hc360/core';
import { ConfirmDialog, DataTable, ErrorState, LoadingState, Page, PageHeader, Section, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useLetterhead } from '@/lib/letterhead';
import { usePermission } from '@/lib/permissions';
import { PendingApprovals } from '@/modules/approvals/PendingApprovals';
import { formatPercent, formatQty } from '@/modules/inventory/format';
import { SUPPLY_PERMISSIONS as S } from '@/modules/inventory/permissions';
import { ReasonDialog } from '@/modules/inventory/ReasonDialog';
import { Detail, DetailGrid, DocStatus, ExpiryText } from '@/modules/inventory/StockBits';
import { PrintTagsDialog } from '@/modules/pharmacy/PrintTagsDialog';
import type { PharmacyTag } from '@/modules/pharmacy/tags';
import { purchaseApi, purchaseKeys, purchaseQueries, type GoodsReceiptLineDto } from '@/modules/purchase/api';
import { GstSplitNote, PurchaseTotals } from '@/modules/purchase/GstSplitNote';
import { explainGstSplit, type GstSplit } from '@/modules/purchase/gstPreview';
import { useFacilityGstin } from '@/modules/purchase/hooks';
import { printGoodsReceipt } from '@/modules/purchase/print';

export const Route = createFileRoute('/_app/purchase/grns/$grnId')({
  component: GrnPage,
});

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 340px',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1180px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  aside: { display: 'flex', flexDirection: 'column', gap: '16px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** One goods receipt: what arrived, at what cost and MRP, and the GST split exactly as saved. */
function GrnPage() {
  const styles = useStyles();
  const { grnId } = Route.useParams();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const letterhead = useLetterhead();
  const facilityGstin = useFacilityGstin();
  const canReceive = usePermission(S.grnReceive);
  const grn = useQuery(purchaseQueries.grn(grnId));
  const [dialog, setDialog] = useState<'ask' | 'cancel' | null>(null);
  const [tagging, setTagging] = useState(false);
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: purchaseKeys.all });
    void queryClient.invalidateQueries({ queryKey: ['inventory'] });
  };
  const ask = useMutation({
    mutationFn: () => purchaseApi.askGrnApproval(grnId),
    onSuccess: () => {
      setDialog(null);
      notify.success('Sent for approval', 'The batches go into stock when someone other than you approves it.');
      refresh();
      void queryClient.invalidateQueries({ queryKey: ['approvals'] });
    },
    onError: () => setDialog(null),
  });
  const cancel = useMutation({
    mutationFn: (reason: string) => purchaseApi.cancelGrn(grnId, reason),
    onSuccess: () => {
      setDialog(null);
      notify.success('Goods receipt cancelled');
      refresh();
    },
  });

  if (grn.isLoading) {
    return <LoadingState />;
  }
  if (!grn.data) {
    return (
      <Page maxWidth={900}>
        <ErrorState error={grn.error} onRetry={() => void grn.refetch()} />
      </Page>
    );
  }
  const g = grn.data;
  const split = explainGstSplit(g.vendorGstin, facilityGstin ?? null);
  const savedSplit: GstSplit = g.isInterState
    ? {
        interState: true,
        known: true,
        headline: 'IGST (inter-state supply)',
        detail: split.interState ? split.detail : 'The vendor is registered in another state, so the full GST is IGST.',
      }
    : {
        interState: false,
        known: true,
        headline: 'CGST + SGST (same state)',
        detail: split.interState ? 'Saved as a supply within the state.' : split.detail,
      };

  const tags: PharmacyTag[] = g.lines
    .filter((l) => l.itemCode)
    .map((l) => ({
      itemCode: l.itemCode!,
      medicine: l.itemName,
      batchNumber: l.batchNumber,
      expiryDate: l.expiryDate,
      mrp: l.unitMrp,
      unit: l.baseUnit,
      copies: 1,
    }));

  return (
    <Page>
      <PageHeader
        title={`Goods receipt ${g.number}`}
        subtitle={`${g.vendorName} · invoice ${g.vendorInvoiceNumber} · into ${g.storeName}`}
        breadcrumbs={[
          { label: 'Goods receipts', onClick: () => void navigate({ to: '/purchase/grns' }) },
          { label: g.number },
        ]}
        actions={
          <>
            <Button
              icon={<PrintRegular />}
              onClick={() =>
                void printGoodsReceipt(g, letterhead).catch((error: unknown) => notify.error(error, 'Printing failed'))
              }
            >
              Print
            </Button>
            {g.status === 'Posted' && tags.length > 0 && (
              <Button icon={<TagRegular />} onClick={() => setTagging(true)}>
                Print tags
              </Button>
            )}
            {g.purchaseOrderId && (
              <Button
                icon={<OpenRegular />}
                onClick={() =>
                  void navigate({ to: '/purchase/orders/$orderId', params: { orderId: g.purchaseOrderId! } })
                }
              >
                Purchase order
              </Button>
            )}
            {canReceive && g.status === 'Draft' && (
              <>
                <Button icon={<DismissCircleRegular />} onClick={() => setDialog('cancel')}>
                  Cancel draft
                </Button>
                {!g.approvalWaitingSince && (
                  <Button appearance="primary" icon={<SendRegular />} onClick={() => setDialog('ask')}>
                    Send for approval
                  </Button>
                )}
              </>
            )}
          </>
        }
      />
      {g.status === 'Draft' && g.approvalWaitingSince && (
        <PendingApprovals subjectType="GoodsReceiptNote" subjectId={g.id} />
      )}
      {g.status === 'Draft' && !g.approvalWaitingSince && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <MessageBarTitle>Not in stock yet</MessageBarTitle>
            This receipt is a draft. Send it for approval: someone other than whoever entered it checks it against the
            supplier&apos;s invoice and posts it into stock.
          </MessageBarBody>
        </MessageBar>
      )}
      {ask.error && (
        <MessageBar intent="error">
          <MessageBarBody>
            <MessageBarTitle>Not sent for approval</MessageBarTitle>
            {isApiError(ask.error) ? ask.error.userMessage : 'Try again.'}
          </MessageBarBody>
        </MessageBar>
      )}
      <div className={styles.layout}>
        <div className={styles.main}>
          <Section>
            <DetailGrid>
              <Detail label="Status">
                <DocStatus status={g.status} />
              </Detail>
              <Detail label="Vendor">
                {g.vendorName}
                {g.vendorGstin ? ` · ${g.vendorGstin}` : ''}
              </Detail>
              <Detail label="Invoice">
                {g.vendorInvoiceNumber}
                {g.vendorInvoiceDate ? ` dated ${formatDateOnly(g.vendorInvoiceDate)}` : ''}
              </Detail>
              <Detail label="Invoice total as printed">
                {g.vendorInvoiceAmount === null
                  ? 'Not entered'
                  : `${formatInr(g.vendorInvoiceAmount)} · ${
                      g.vendorInvoiceAmount === g.netAmount
                        ? 'matches the lines'
                        : `the lines come to ${formatInr(g.netAmount)}`
                    }`}
              </Detail>
              <Detail label="Received on">{formatDateOnly(g.receivedOn)}</Detail>
              <Detail label="Store">{g.storeName}</Detail>
              {g.postedAt && <Detail label="Posted">{formatDateTime(g.postedAt)}</Detail>}
              {g.remarks && <Detail label="Remarks">{g.remarks}</Detail>}
            </DetailGrid>
          </Section>
          <Section title="Items" flush>
            <DataTable<GoodsReceiptLineDto>
              ariaLabel="Received items"
              rows={g.lines}
              getRowId={(l) => l.id}
              size="small"
              columns={[
                {
                  key: 'item',
                  header: 'Item',
                  render: (l) => (
                    <span>
                      <Text weight="semibold">{l.itemName}</Text>
                      <Caption1 block className={styles.muted}>
                        Batch {l.batchNumber} · MRP {formatInr(l.mrp)}
                      </Caption1>
                    </span>
                  ),
                },
                { key: 'exp', header: 'Expiry', render: (l) => <ExpiryText date={l.expiryDate} /> },
                {
                  key: 'qty',
                  header: 'Qty',
                  align: 'end',
                  render: (l) => (
                    <span>
                      {formatQty(l.quantity)}
                      {l.freeQuantity ? <Caption1 block>+ {formatQty(l.freeQuantity)} free</Caption1> : null}
                    </span>
                  ),
                },
                {
                  key: 'rate',
                  header: 'Rate',
                  align: 'end',
                  priority: 'low',
                  render: (l) =>
                    `${formatInr(l.rate)}${l.discountPercent ? ` − ${formatPercent(l.discountPercent)}` : ''}`,
                },
                { key: 'gst', header: 'GST', align: 'end', width: 70, render: (l) => formatPercent(l.gstRate) },
                {
                  key: 'tax',
                  header: g.isInterState ? 'IGST' : 'CGST + SGST',
                  align: 'end',
                  priority: 'low',
                  render: (l) =>
                    g.isInterState
                      ? formatInr(l.igstAmount)
                      : `${formatInr(l.cgstAmount)} + ${formatInr(l.sgstAmount)}`,
                },
                {
                  key: 'total',
                  header: 'Amount',
                  align: 'end',
                  render: (l) => <Text weight="semibold">{formatInr(l.lineTotal)}</Text>,
                },
                {
                  key: 'stock',
                  header: 'Into stock',
                  align: 'end',
                  priority: 'low',
                  render: (l) => (
                    <span>
                      {formatQty(l.baseQuantity)}
                      <Caption1 block>at {formatInr(l.unitCost)} each</Caption1>
                    </span>
                  ),
                },
              ]}
            />
          </Section>
        </div>
        <aside className={styles.aside}>
          <GstSplitNote split={savedSplit} />
          <Section title="Invoice value">
            <PurchaseTotals
              totals={{
                gross: g.grossAmount,
                discount: g.discountAmount,
                taxable: g.taxableAmount,
                cgst: g.cgstAmount,
                sgst: g.sgstAmount,
                igst: g.igstAmount,
                roundOff: g.roundOff,
                net: g.netAmount,
              }}
              interState={g.isInterState}
            />
          </Section>
        </aside>
      </div>
      <ConfirmDialog
        open={dialog === 'ask'}
        title="Send this receipt for approval?"
        confirmLabel="Send for approval"
        busy={ask.isPending}
        onConfirm={() => ask.mutate()}
        onCancel={() => setDialog(null)}
      >
        Someone other than whoever entered it checks it against the supplier&apos;s invoice and approves it; the batches
        then go into {g.storeName} with their expiry and MRP, and the purchase order is updated.
      </ConfirmDialog>
      {dialog === 'cancel' && (
        <ReasonDialog
          title="Cancel this draft receipt?"
          confirmLabel="Cancel draft"
          destructive
          busy={cancel.isPending}
          error={cancel.error}
          onSubmit={(reason) => cancel.mutate(reason)}
          onClose={() => setDialog(null)}
        >
          Nothing was added to stock. The vendor invoice number can be used again.
        </ReasonDialog>
      )}
      {tagging && (
        <PrintTagsDialog title={`Pharmacy tags for ${g.number}`} tags={tags} onClose={() => setTagging(false)} />
      )}
    </Page>
  );
}
