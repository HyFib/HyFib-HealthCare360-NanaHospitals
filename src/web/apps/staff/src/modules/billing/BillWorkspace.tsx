import {
  Button,
  Caption1,
  makeStyles,
  Menu,
  MenuDivider,
  MenuItem,
  MenuList,
  MenuPopover,
  MenuTrigger,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
  Tooltip,
  useRestoreFocusTarget,
} from '@fluentui/react-components';
import {
  ArrowClockwiseRegular,
  ArrowDownloadRegular,
  ArrowUndoRegular,
  BedRegular,
  DeleteRegular,
  DismissCircleRegular,
  DocumentCheckmarkRegular,
  LinkRegular,
  MoneyHandRegular,
  MoreHorizontalRegular,
  PrintRegular,
  ReceiptRegular,
  TagPercentRegular,
} from '@fluentui/react-icons';
import { formatDateTime, formatInr, useHotkey } from '@hc360/core';
import { ConfirmDialog, DataTable, Section, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useServerDocuments } from '@/lib/documents';
import { usePermission } from '@/lib/permissions';
import { billDocument, billingApi, billingQueries, type BillDetail, type BillLineDto } from './api';
import { BillLinesTable } from './BillLinesTable';
import { BillPackagePanel } from './BillPackagePanel';
import { BillTotalsCard } from './BillTotalsCard';
import { creditableAmount } from './billTotals';
import { CollectPaymentDialog } from './CollectPaymentDialog';
import { CreditNoteDialog } from './CreditNoteDialog';
import { DiscountDialog } from './DiscountDialog';
import { useBillingPrint, useRefreshBilling } from './hooks';
import { CREDIT_NOTE_KIND_LABEL, RECEIPT_PURPOSE_LABEL, REFUND_STATUS_LABEL, modeSummaryLabel } from './labels';
import { fromPaise, maxBig, toPaise } from './money';
import { PaymentLinkDialog } from './PaymentLinkDialog';
import { RequestRefundDialog } from './RefundDialogs';
import { ServicePicker } from './ServicePicker';
import { ActionError, Amount, KeyHint, ReasonDialog } from './ui';

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) minmax(300px, 380px)',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  side: { display: 'flex', flexDirection: 'column', gap: '12px', position: 'sticky', top: '8px' },
  actions: { display: 'flex', flexDirection: 'column', gap: '8px' },
  row: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  big: { width: '100%', justifyContent: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
  toolbar: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap', marginBottom: '8px' },
});

type DialogKind = 'discount' | 'finalize' | 'collect' | 'discard' | 'cancel' | 'refund' | 'credit' | 'link' | null;

/** Refundable balance less refunds already requested or approved (they count against it until paid). */
function refundableNow(bill: BillDetail): number {
  const outstanding = bill.refunds
    .filter((r) => r.status === 'Requested' || r.status === 'Approved')
    .reduce((sum, r) => sum + toPaise(r.amount), 0n);
  return fromPaise(maxBig(toPaise(bill.refundableAmount) - outstanding, 0n));
}

/** What cancelling a finalised bill does to its services, its OP package line and the money already paid. */
function cancelDescription(bill: BillDetail): string {
  const effects = ['The services return to the unbilled list'];
  if (bill.package && bill.type === 'OutPatient') {
    effects.push('the package line is cancelled');
  }
  if (bill.paidAmount > 0) {
    effects.push(
      `${formatInr(bill.paidAmount)} already paid becomes a refund request that another person must approve`,
    );
  }
  const said =
    effects.length > 1 ? `${effects.slice(0, -1).join(', ')} and ${effects[effects.length - 1]}` : effects[0];
  return `${said}. This cannot be undone.`;
}

/**
 * One bill, from draft to paid (OPB, IPB): services with returns shown as returns, discounts within the user's limit
 * or sent for approval, finalisation (allocates the gap-free bill number), collection, printing and the A4 bill as a
 * PDF, and afterwards credit notes, refunds and cancellation. Keyboard: F2 add service, F4 discount, F8 finalise /
 * collect, F9 print.
 */
export function BillWorkspace({
  bill,
  onDiscarded,
  allowServiceEntry = true,
}: {
  bill: BillDetail;
  onDiscarded?: () => void;
  allowServiceEntry?: boolean;
}) {
  const styles = useStyles();
  // Closing a dialog opened from the menu returns focus to the menu button.
  const restoreFocusTarget = useRestoreFocusTarget();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const refresh = useRefreshBilling();
  const { printBill, printReceiptById, prefs } = useBillingPrint();
  const { downloadPdf } = useServerDocuments();
  const [dialog, setDialog] = useState<DialogKind>(null);
  const [removing, setRemoving] = useState<string | null>(null);

  const canEditOp = usePermission('billing.op.create');
  const canDiscount = usePermission(['billing.op.create', 'billing.ip.manage']);
  const canFinalize = usePermission('billing.bills.finalize');
  const canCollect = usePermission('billing.payments.collect');
  const canCancel = usePermission('billing.bills.cancel');
  const canRequestRefund = usePermission('billing.refunds.request');
  const canCredit = usePermission('billing.refunds.approve');

  const isDraft = bill.status === 'Draft';
  const isCancelled = bill.status === 'Cancelled' || bill.status === 'Refunded';
  const isOpDraft = isDraft && bill.type === 'OutPatient';
  const hasDue = !isDraft && !isCancelled && bill.dueAmount > 0;
  const refundable = refundableNow(bill);
  const creditable = !isDraft && !isCancelled ? creditableAmount(bill) : 0;

  const store = (detail: BillDetail) => {
    queryClient.setQueryData(billingQueries.bill(detail.id).queryKey, detail);
    void refresh();
  };

  const reload = () => void queryClient.invalidateQueries({ queryKey: billingQueries.bill(bill.id).queryKey });

  const removeLine = useMutation({
    mutationFn: (line: BillLineDto) => billingApi.removeLine(bill.id, line.id, bill.version),
    onMutate: (line) => setRemoving(line.id),
    onSettled: () => setRemoving(null),
    onSuccess: (detail, line) => {
      store(detail);
      notify.info('Service removed', line.description);
    },
  });

  const refreshServices = useMutation({
    mutationFn: () => billingApi.refresh(bill.id, bill.version),
    onSuccess: (detail) => {
      const added = detail.lines.length - bill.lines.length;
      store(detail);
      notify.info(
        added > 0 ? `${added} new service${added === 1 ? '' : 's'} added` : 'No new services',
        added > 0 ? 'Charged since the bill was started.' : undefined,
      );
    },
  });

  const finalize = useMutation({
    mutationFn: () => billingApi.finalize(bill.id, bill.version),
    onSuccess: (detail) => {
      store(detail);
      notify.success(
        `Bill ${detail.billNumber} finalised`,
        detail.dueAmount > 0 ? `${formatInr(detail.dueAmount)} to collect` : 'Nothing to collect',
      );
      setDialog(detail.dueAmount > 0 && canCollect ? 'collect' : null);
      if (detail.dueAmount === 0 && prefs.autoPrint) {
        void printBill(detail, prefs.receiptFormat);
      }
    },
  });

  const discard = useMutation({
    mutationFn: () => billingApi.discard(bill.id, bill.version),
    onSuccess: () => {
      notify.info('Draft discarded', 'Its services are back in the unbilled list.');
      queryClient.removeQueries({ queryKey: billingQueries.bill(bill.id).queryKey });
      void refresh();
      setDialog(null);
      onDiscarded?.();
    },
  });

  const cancel = useMutation({
    mutationFn: (reason: string) => billingApi.cancel(bill.id, reason, bill.version),
    onSuccess: (detail) => {
      store(detail);
      setDialog(null);
      notify.warning(
        `Bill ${detail.billNumber} cancelled`,
        detail.refundableAmount > 0
          ? `A refund request of ${formatInr(detail.refundableAmount)} awaits approval.`
          : undefined,
      );
    },
  });

  const openDialog = (kind: DialogKind) => () => setDialog(kind);
  const noDialog = dialog === null;
  useHotkey('f4', () => canDiscount && isDraft && setDialog('discount'), {
    enabled: noDialog && isDraft,
    allowInInputs: true,
  });
  useHotkey(
    'f8',
    () => (isDraft ? canFinalize && setDialog('finalize') : hasDue && canCollect && setDialog('collect')),
    { enabled: noDialog, allowInInputs: true },
  );
  useHotkey('f9', () => void printBill(bill, isDraft ? 'A4' : prefs.receiptFormat), {
    enabled: noDialog,
    allowInInputs: true,
  });

  const primary = isDraft ? (
    canFinalize && (
      <Button
        appearance="primary"
        size="large"
        className={styles.big}
        icon={<DocumentCheckmarkRegular />}
        onClick={openDialog('finalize')}
        disabled={bill.lines.length === 0 || Boolean(bill.pendingDiscount)}
      >
        {bill.type === 'OutPatient' && canCollect ? 'Finalise and collect' : 'Finalise bill'}
        <KeyHint>F8</KeyHint>
      </Button>
    )
  ) : hasDue && canCollect ? (
    <Button
      appearance="primary"
      size="large"
      className={styles.big}
      icon={<MoneyHandRegular />}
      onClick={openDialog('collect')}
    >
      Collect {formatInr(bill.dueAmount)}
      <KeyHint>F8</KeyHint>
    </Button>
  ) : null;

  return (
    <div className={styles.layout}>
      <div className={styles.main}>
        {bill.pendingDiscount && (
          <MessageBar intent="warning" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>
                Discount of {formatInr(bill.pendingDiscount.requestedAmount)} is waiting for approval
              </MessageBarTitle>
              Requested by {bill.pendingDiscount.requestedByName ?? 'a cashier'}{' '}
              {formatDateTime(bill.pendingDiscount.requestedAt)}: “{bill.pendingDiscount.reason}”. The bill can be
              finalised once accounts decide; this screen updates by itself.
            </MessageBarBody>
            <MessageBarActions>
              <Button icon={<ArrowClockwiseRegular />} onClick={reload}>
                Check now
              </Button>
            </MessageBarActions>
          </MessageBar>
        )}
        {isCancelled && (
          <MessageBar intent="error" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>Cancelled {formatDateTime(bill.cancelledAt)}</MessageBarTitle>
              {bill.cancelReason}
            </MessageBarBody>
          </MessageBar>
        )}

        <BillPackagePanel bill={bill} onChanged={store} />

        <Section
          title="Services"
          description={
            isDraft
              ? 'Draft: services can still be added, removed and discounted.'
              : `${bill.lines.length} service${bill.lines.length === 1 ? '' : 's'}`
          }
          actions={
            isOpDraft && canEditOp ? (
              <Button
                icon={refreshServices.isPending ? <Spinner size="tiny" /> : <ArrowClockwiseRegular />}
                onClick={() => refreshServices.mutate()}
                disabled={refreshServices.isPending}
              >
                Pick up new services
              </Button>
            ) : undefined
          }
          flush
        >
          {isOpDraft && canEditOp && allowServiceEntry && (
            <div style={{ padding: '0 16px 12px' }}>
              <ServicePicker bill={bill} onAdded={store} enabled={noDialog} />
            </div>
          )}
          <div style={{ padding: '0 16px' }}>
            <ActionError error={removeLine.error ?? refreshServices.error} onReload={reload} />
          </div>
          <BillLinesTable
            bill={bill}
            onRemove={isOpDraft && canEditOp ? (line) => removeLine.mutate(line) : undefined}
            removing={removing}
          />
        </Section>

        {!isDraft && (bill.receipts.length > 0 || bill.refunds.length > 0 || bill.creditNotes.length > 0) && (
          <Section title="Payments and adjustments" flush>
            {bill.receipts.length > 0 && (
              <DataTable
                ariaLabel="Receipts"
                rows={bill.receipts}
                getRowId={(r) => r.id}
                size="small"
                columns={[
                  { key: 'no', header: 'Receipt', render: (r) => <Text font="monospace">{r.receiptNumber}</Text> },
                  { key: 'at', header: 'Received', render: (r) => formatDateTime(r.receivedAt) },
                  { key: 'purpose', header: 'For', priority: 'low', render: (r) => RECEIPT_PURPOSE_LABEL[r.purpose] },
                  { key: 'mode', header: 'Mode', render: (r) => modeSummaryLabel(r.modes) },
                  { key: 'amount', header: 'Amount', align: 'end', render: (r) => <Amount value={r.amount} strong /> },
                ]}
                rowActions={(r) => (
                  <Button
                    size="small"
                    appearance="subtle"
                    icon={<PrintRegular />}
                    aria-label={`Print receipt ${r.receiptNumber}`}
                    onClick={() => void printReceiptById(r.id)}
                  />
                )}
              />
            )}
            {bill.refunds.length > 0 && (
              <DataTable
                ariaLabel="Refunds"
                rows={bill.refunds}
                getRowId={(r) => r.id}
                size="small"
                onRowClick={(r) => void navigate({ to: '/billing/refunds/$refundId', params: { refundId: r.id } })}
                columns={[
                  { key: 'no', header: 'Refund', render: (r) => <Text font="monospace">{r.refundNumber}</Text> },
                  { key: 'at', header: 'Requested', render: (r) => formatDateTime(r.requestedAt) },
                  {
                    key: 'status',
                    header: 'Status',
                    render: (r) => <StatusBadge status={r.status} label={REFUND_STATUS_LABEL[r.status]} />,
                  },
                  { key: 'amount', header: 'Amount', align: 'end', render: (r) => <Amount value={r.amount} strong /> },
                ]}
              />
            )}
            {bill.creditNotes.length > 0 && (
              <DataTable
                ariaLabel="Credit notes"
                rows={bill.creditNotes}
                getRowId={(c) => c.id}
                size="small"
                columns={[
                  { key: 'no', header: 'Credit note', render: (c) => <Text font="monospace">{c.number}</Text> },
                  { key: 'kind', header: 'Type', render: (c) => CREDIT_NOTE_KIND_LABEL[c.kind] },
                  {
                    key: 'reason',
                    header: 'Reason',
                    priority: 'low',
                    render: (c) => `${c.reason}${c.issuedByName ? ` · ${c.issuedByName}` : ''}`,
                  },
                  { key: 'amount', header: 'Amount', align: 'end', render: (c) => <Amount value={c.amount} strong /> },
                ]}
              />
            )}
          </Section>
        )}
      </div>

      <aside className={styles.side} aria-label="Bill totals and actions">
        <Section>
          <BillTotalsCard bill={bill} />
        </Section>
        <div className={styles.actions}>
          {primary}
          {isDraft && canDiscount && (
            <Button icon={<TagPercentRegular />} onClick={openDialog('discount')} disabled={bill.lines.length === 0}>
              Discount
              <KeyHint>F4</KeyHint>
            </Button>
          )}
          <div className={styles.row}>
            <Button icon={<PrintRegular />} onClick={() => void printBill(bill, 'A4')}>
              {isDraft ? 'Print estimate' : 'Print bill (A4)'}
            </Button>
            {!isDraft && (
              <Button icon={<ReceiptRegular />} onClick={() => void printBill(bill, 'Receipt80')}>
                80 mm
              </Button>
            )}
            <Tooltip
              content={isDraft ? 'Download the estimate as a PDF' : 'Download the A4 bill as a PDF'}
              relationship="label"
            >
              <Button
                icon={downloadPdf.isPending ? <Spinner size="tiny" /> : <ArrowDownloadRegular />}
                disabled={downloadPdf.isPending}
                onClick={() => downloadPdf.mutate(billDocument(bill.id))}
              >
                PDF
              </Button>
            </Tooltip>
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                <Button icon={<MoreHorizontalRegular />} aria-label="More bill actions" {...restoreFocusTarget} />
              </MenuTrigger>
              <MenuPopover>
                <MenuList>
                  {hasDue && canCollect && (
                    <MenuItem icon={<LinkRegular />} onClick={openDialog('link')}>
                      Send payment link
                    </MenuItem>
                  )}
                  {bill.ipAccountId && (
                    <MenuItem
                      icon={<BedRegular />}
                      onClick={() =>
                        void navigate({ to: '/billing/ip/$accountId', params: { accountId: bill.ipAccountId! } })
                      }
                    >
                      Open the IP account
                    </MenuItem>
                  )}
                  {!isDraft && canRequestRefund && refundable > 0 && (
                    <MenuItem icon={<ArrowUndoRegular />} onClick={openDialog('refund')}>
                      Request refund of {formatInr(refundable)}
                    </MenuItem>
                  )}
                  {canCredit && creditable > 0 && (
                    <MenuItem icon={<ReceiptRegular />} onClick={openDialog('credit')}>
                      Credit note or write-off
                    </MenuItem>
                  )}
                  {(isDraft || (!isCancelled && canCancel)) && <MenuDivider />}
                  {isDraft && (
                    <MenuItem icon={<DeleteRegular />} onClick={openDialog('discard')}>
                      Discard draft
                    </MenuItem>
                  )}
                  {!isDraft && !isCancelled && canCancel && (
                    <MenuItem icon={<DismissCircleRegular />} onClick={openDialog('cancel')}>
                      Cancel bill
                    </MenuItem>
                  )}
                </MenuList>
              </MenuPopover>
            </Menu>
          </div>
          {isDraft && bill.lines.length > 0 && !canFinalize && (
            <Caption1 className={styles.muted}>
              Ask a cashier to finalise this bill; your role can prepare it but not allocate the bill number.
            </Caption1>
          )}
          <Caption1 className={styles.muted}>
            {bill.billNumber
              ? `Bill ${bill.billNumber} · finalised ${formatDateTime(bill.finalizedAt)}`
              : `Draft started ${formatDateTime(bill.createdAt)}`}
            {bill.tariffPlanName ? ` · tariff ${bill.tariffPlanName}` : ''}
          </Caption1>
        </div>
      </aside>

      <DiscountDialog bill={bill} open={dialog === 'discount'} onClose={() => setDialog(null)} />

      <ConfirmDialog
        open={dialog === 'finalize'}
        title="Finalise this bill?"
        confirmLabel={finalize.isPending ? 'Finalising…' : 'Finalise'}
        busy={finalize.isPending}
        onConfirm={() => finalize.mutate()}
        onCancel={() => {
          finalize.reset();
          setDialog(null);
        }}
      >
        <Text block>
          The bill number is allocated now and the services can no longer be changed. Amount payable by the patient:{' '}
          <Amount value={bill.patientAmount} strong />.
        </Text>
        {bill.type === 'OutPatient' && (
          <Caption1 block>An online prepayment for this visit is adjusted automatically.</Caption1>
        )}
        <ActionError error={finalize.error} onReload={reload} />
      </ConfirmDialog>

      <ConfirmDialog
        open={dialog === 'discard'}
        title="Discard this draft bill?"
        confirmLabel="Discard draft"
        destructive
        busy={discard.isPending}
        onConfirm={() => discard.mutate()}
        onCancel={() => setDialog(null)}
      >
        <Text block>
          The services go back to the unbilled list; services added on this screen
          {bill.package && bill.type === 'OutPatient' ? ' and the package line' : ''} are cancelled. No bill number is
          used.
        </Text>
        <ActionError error={discard.error} onReload={reload} />
      </ConfirmDialog>

      <ReasonDialog
        open={dialog === 'cancel'}
        title={`Cancel bill ${bill.billNumber ?? ''}?`}
        description={cancelDescription(bill)}
        confirmLabel="Cancel bill"
        destructive
        busy={cancel.isPending}
        error={cancel.error}
        onConfirm={(reason) => cancel.mutate(reason)}
        onCancel={() => setDialog(null)}
      />

      {!isDraft && <CollectPaymentDialog bill={bill} open={dialog === 'collect'} onClose={() => setDialog(null)} />}
      {dialog === 'link' && (
        <PaymentLinkDialog
          open
          target={{ kind: 'bill', billId: bill.id, due: bill.dueAmount }}
          onClose={() => setDialog(null)}
        />
      )}
      {dialog === 'refund' && (
        <RequestRefundDialog
          open
          target={{
            kind: 'bill',
            billId: bill.id,
            max: refundable,
            label: `Bill ${bill.billNumber} · ${bill.patientName}`,
          }}
          onClose={() => setDialog(null)}
        />
      )}
      {dialog === 'credit' && <CreditNoteDialog bill={bill} open onClose={() => setDialog(null)} />}
    </div>
  );
}
