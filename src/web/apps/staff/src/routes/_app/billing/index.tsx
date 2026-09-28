import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { ArrowRightRegular, ReceiptAddRegular, ReceiptMoneyRegular, WalletRegular } from '@fluentui/react-icons';
import { formatDateTime, formatInr, formatRelative, formatTime, todayIso } from '@hc360/core';
import { DataTable, KpiCard, Page, PageHeader, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { billingQueries, type RefundDto } from '@/modules/billing/api';
import { useBillingLive } from '@/modules/billing/hooks';
import { PAYMENT_MODE_LABEL, REFUND_PURPOSE_LABEL } from '@/modules/billing/labels';
import { DecideRefundDialog, PayRefundDialog } from '@/modules/billing/RefundDialogs';
import { ShiftBar } from '@/modules/billing/ShiftControls';
import { Amount, BillStatusBadge, NoAccess } from '@/modules/billing/ui';
import { DiscountApprovalsWidget, TodaysCollectionsWidget } from '@/modules/billing/widgets';

export const Route = createFileRoute('/_app/billing/')({
  component: CashierDeskPage,
});

const useStyles = makeStyles({
  kpis: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(auto-fill, minmax(190px, 1fr))' },
  grid: {
    display: 'grid',
    gap: '16px',
    gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  muted: { color: tokens.colorNeutralForeground3 },
  modes: { display: 'flex', flexDirection: 'column', gap: '6px' },
  mode: { display: 'flex', justifyContent: 'space-between', gap: '12px' },
});

/** Cashier desk: the shift, today's collections by mode, dues to chase, and the refunds and discounts waiting on someone. */
function CashierDeskPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  useBillingLive();
  const canUse = usePermission(['billing.payments.collect', 'billing.shifts', 'billing.charges.read', 'billing.op.create']);
  const canCollect = usePermission('billing.payments.collect');
  const canShifts = usePermission('billing.shifts');
  const canCreate = usePermission('billing.op.create');
  const canDeposit = usePermission('billing.deposits');
  const canApproveDiscounts = usePermission('billing.discounts.approve');
  const canApproveRefunds = usePermission('billing.refunds.approve');
  const canReadRefunds = usePermission(['billing.refunds.request', 'billing.refunds.approve', 'billing.charges.read']);
  const canReadBills = usePermission(['billing.charges.read', 'billing.op.create', 'billing.ip.manage', 'billing.payments.collect']);
  const [refundAction, setRefundAction] = useState<{ refund: RefundDto; action: 'approve' | 'reject' | 'pay' } | null>(null);

  const today = todayIso();
  const shift = useQuery({ ...billingQueries.currentShift(), enabled: canShifts });
  const dayEnd = useQuery({ ...billingQueries.dayEnd(today), enabled: canShifts, refetchInterval: 60_000 });
  const dues = useQuery({ ...billingQueries.bills({ withDueOnly: true, sort: 'dueAmount', desc: true, pageSize: 8 }), enabled: canReadBills });
  const toApprove = useQuery({ ...billingQueries.refunds({ status: 'Requested', pageSize: 8 }), enabled: canReadRefunds && canApproveRefunds });
  const toPay = useQuery({ ...billingQueries.refunds({ status: 'Approved', pageSize: 8 }), enabled: canReadRefunds && canCollect });

  if (!canUse) {
    return (
      <Page>
        <PageHeader title="Cashier desk" />
        <NoAccess what="the cashier desk" />
      </Page>
    );
  }

  const d = dayEnd.data;
  const s = shift.data;

  return (
    <Page>
      <PageHeader
        title="Cashier desk"
        subtitle="Your shift, today's collections and what needs attention"
        icon={<ReceiptMoneyRegular />}
        actions={
          <>
            {canDeposit && (
              <Button icon={<WalletRegular />} onClick={() => void navigate({ to: '/billing/deposits' })}>
                Collect IP advance
              </Button>
            )}
            {canCreate && (
              <Button appearance="primary" icon={<ReceiptAddRegular />} onClick={() => void navigate({ to: '/billing/op' })}>
                OP billing
              </Button>
            )}
          </>
        }
      />
      <ShiftBar />

      {canShifts && (
        <div className={styles.kpis}>
          <KpiCard label="Net collection today" value={<Amount value={d?.netCollection ?? null} />} hint="All counters, after refunds" onClick={() => void navigate({ to: '/billing/day-end' })} />
          <KpiCard label="My shift" value={s ? <Amount value={s.expectedCash} /> : 'Closed'} hint={s ? `Cash expected · since ${formatTime(s.openedAt)}` : 'Open a shift to collect'} />
          <KpiCard label="Bills finalised" value={d ? d.billsFinalized.toLocaleString('en-IN') : '…'} hint={d ? <Amount value={d.billedAmount} /> : undefined} onClick={() => void navigate({ to: '/billing/bills' })} />
          <KpiCard label="Discounts given" value={<Amount value={d?.discountGiven ?? null} />} hint={d && d.billsCancelled > 0 ? `${d.billsCancelled} bill(s) cancelled` : 'Today'} />
        </div>
      )}

      <div className={styles.grid}>
        {canCollect && <TodaysCollectionsWidget />}
        {s && s.totals.length > 0 && (
          <Section title="My shift by payment mode" description={`${s.counter} · ${s.receiptCount} receipts`}>
            <div className={styles.modes}>
              {s.totals.map((t) => (
                <div key={t.mode} className={styles.mode}>
                  <Text>{PAYMENT_MODE_LABEL[t.mode]}</Text>
                  <span>
                    <Amount value={t.net} strong />
                    {t.refunded > 0 && <Caption1 className={styles.muted}> (refunds {formatInr(t.refunded)})</Caption1>}
                  </span>
                </div>
              ))}
            </div>
          </Section>
        )}
        {canApproveDiscounts && <DiscountApprovalsWidget />}

        {canApproveRefunds && canReadRefunds && (
          <Section
            title="Refunds awaiting approval"
            description={toApprove.data ? `${toApprove.data.totalCount} waiting` : undefined}
            actions={
              <Button appearance="transparent" icon={<ArrowRightRegular />} iconPosition="after" onClick={() => void navigate({ to: '/billing/refunds', search: { status: 'Requested' } })}>
                All
              </Button>
            }
            flush
          >
            <DataTable
              ariaLabel="Refunds awaiting approval"
              rows={toApprove.data?.items}
              loading={toApprove.isLoading}
              error={toApprove.error}
              onRetry={() => void toApprove.refetch()}
              getRowId={(r) => r.id}
              size="small"
              columns={[
                { key: 'patient', header: 'Patient', render: (r) => r.patientName ?? r.uhid ?? '—' },
                { key: 'purpose', header: 'For', priority: 'low', render: (r) => REFUND_PURPOSE_LABEL[r.purpose] },
                { key: 'when', header: 'Requested', priority: 'low', render: (r) => formatRelative(r.requestedAt) },
                { key: 'amount', header: 'Amount', align: 'end', render: (r) => <Amount value={r.amount} strong /> },
              ]}
              rowActions={(r) => (
                <>
                  <Button size="small" onClick={() => setRefundAction({ refund: r, action: 'approve' })}>
                    Approve
                  </Button>{' '}
                  <Button size="small" appearance="subtle" onClick={() => setRefundAction({ refund: r, action: 'reject' })}>
                    Reject
                  </Button>
                </>
              )}
              emptyTitle="No refund is waiting for approval"
            />
          </Section>
        )}

        {canCollect && canReadRefunds && (
          <Section
            title="Approved refunds to pay out"
            description={toPay.data ? `${toPay.data.totalCount} ready` : undefined}
            actions={
              <Button appearance="transparent" icon={<ArrowRightRegular />} iconPosition="after" onClick={() => void navigate({ to: '/billing/refunds', search: { status: 'Approved' } })}>
                All
              </Button>
            }
            flush
          >
            <DataTable
              ariaLabel="Approved refunds"
              rows={toPay.data?.items}
              loading={toPay.isLoading}
              error={toPay.error}
              onRetry={() => void toPay.refetch()}
              getRowId={(r) => r.id}
              size="small"
              columns={[
                { key: 'patient', header: 'Patient', render: (r) => r.patientName ?? r.uhid ?? '—' },
                { key: 'no', header: 'Refund', priority: 'low', render: (r) => <Text font="monospace">{r.refundNumber}</Text> },
                { key: 'amount', header: 'Amount', align: 'end', render: (r) => <Amount value={r.amount} strong /> },
              ]}
              rowActions={(r) => (
                <Button size="small" appearance="primary" onClick={() => setRefundAction({ refund: r, action: 'pay' })}>
                  Pay out
                </Button>
              )}
              emptyTitle="No approved refund to pay"
            />
          </Section>
        )}

        {canReadBills && (
          <Section
            title="Dues"
            description="Finalised bills with a balance, largest first"
            actions={
              <Button appearance="transparent" icon={<ArrowRightRegular />} iconPosition="after" onClick={() => void navigate({ to: '/billing/bills', search: { due: true, sort: 'dueAmount' } })}>
                All dues
              </Button>
            }
            flush
          >
            <DataTable
              ariaLabel="Bills with a balance due"
              rows={dues.data?.items}
              loading={dues.isLoading}
              error={dues.error}
              onRetry={() => void dues.refetch()}
              getRowId={(b) => b.id}
              onRowClick={(b) => void navigate({ to: '/billing/bills/$billId', params: { billId: b.id } })}
              size="small"
              columns={[
                {
                  key: 'patient',
                  header: 'Patient',
                  render: (b) => (
                    <span>
                      <Text weight="semibold">{b.patientName ?? '—'}</Text>
                      <Caption1 block className={styles.muted}>
                        {b.billNumber} · {formatDateTime(b.finalizedAt)}
                      </Caption1>
                    </span>
                  ),
                },
                { key: 'status', header: 'Status', priority: 'low', render: (b) => <BillStatusBadge status={b.status} /> },
                { key: 'due', header: 'Due', align: 'end', render: (b) => <Amount value={b.dueAmount} strong tone="negative" /> },
              ]}
              emptyTitle="No dues"
              emptyDescription="Every finalised bill is paid."
            />
          </Section>
        )}
      </div>

      {refundAction?.action === 'pay' && <PayRefundDialog refund={refundAction.refund} onClose={() => setRefundAction(null)} />}
      {refundAction && refundAction.action !== 'pay' && <DecideRefundDialog refund={refundAction.refund} decision={refundAction.action} onClose={() => setRefundAction(null)} />}
    </Page>
  );
}
