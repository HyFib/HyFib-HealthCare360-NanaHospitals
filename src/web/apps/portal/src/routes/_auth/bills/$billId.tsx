import { Button, makeStyles, Spinner, tokens } from '@fluentui/react-components';
import { PaymentRegular, PrintRegular, ReceiptRegular } from '@fluentui/react-icons';
import { isApiError } from '@hc360/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState, type ReactNode } from 'react';
import { DocumentActions } from '@/components/DocumentActions';
import { BackLink } from '@/components/links';
import { ListRow, RowCard } from '@/components/ListRow';
import { OwnerGuard } from '@/components/OwnerGuard';
import { PortalPage } from '@/components/PortalPage';
import { ErrorBlock, Notice, PageSpinner } from '@/components/States';
import { StatusPill } from '@/components/StatusPill';
import { useLanguage, useT } from '@/i18n';
import { useFormat } from '@/i18n/useFormat';
import { isGuid } from '@/lib/ids';
import { clearPendingPayment, isTrainingGateway, peekPendingPayment, runCheckout } from '@/lib/payments';
import { printPortalDocument } from '@/lib/print';
import { useToast } from '@/lib/toast';
import type { PortalBillDetail } from '@/lib/types';
import { billApi, billQueries } from '@/modules/bills/api';
import { BILL_STATUS, BILL_TYPE, isDue, RECEIPT_PURPOSE } from '@/modules/bills/labels';
import { billDocument, receiptDocument } from '@/modules/bills/print';
import { ReportCard } from '@/modules/reports/ReportParts';
import { useHospitalName } from '@/shell/hooks';
import { useActiveProfile } from '@/shell/profileContext';

export const Route = createFileRoute('/_auth/bills/$billId')({
  component: BillPage,
});

const useStyles = makeStyles({
  due: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    padding: '16px',
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorStatusWarningBackground1,
    border: `1px solid ${tokens.colorStatusWarningBorder1}`,
  },
  dueAmount: {
    margin: 0,
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
  },
  secure: { margin: 0, color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200 },
  lines: { listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column' },
  line: {
    display: 'flex',
    justifyContent: 'space-between',
    gap: '12px',
    padding: '10px 0',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
  },
  lineText: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  lineName: { fontWeight: tokens.fontWeightSemibold, overflowWrap: 'anywhere' },
  lineMeta: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  lineAmount: { fontWeight: tokens.fontWeightSemibold, whiteSpace: 'nowrap' },
  totals: { width: '100%', borderCollapse: 'collapse' },
  totalLabel: { padding: '6px 0', color: tokens.colorNeutralForeground2, textAlign: 'start', fontWeight: 'inherit' },
  totalValue: { padding: '6px 0', textAlign: 'end', fontVariantNumeric: 'tabular-nums' },
  grand: {
    fontWeight: tokens.fontWeightSemibold,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground1,
  },
  receipts: { display: 'flex', flexDirection: 'column', gap: '8px' },
  muted: { margin: 0, color: tokens.colorNeutralForeground2 },
});

function BillPage() {
  const { billId } = Route.useParams();
  const t = useT();
  const { profile } = useActiveProfile();
  const bill = useQuery({ ...billQueries.detail(profile.id, billId), enabled: isGuid(billId) });
  const back = <BackLink to="/bills">{t('bills.title')}</BackLink>;

  if (!isGuid(billId)) {
    return (
      <PortalPage title={t('bills.title')} back={back} width="narrow">
        <Notice intent="warning">{t('errors.notFound')}</Notice>
      </PortalPage>
    );
  }
  if (bill.isPending) {
    return <PageSpinner />;
  }
  if (!bill.data) {
    return (
      <PortalPage title={t('bills.title')} back={back} width="narrow">
        <ErrorBlock error={bill.error} onRetry={() => void bill.refetch()} />
      </PortalPage>
    );
  }
  return (
    <OwnerGuard ownerId={bill.data.patientId}>
      <BillDetail bill={bill.data} back={back} />
    </OwnerGuard>
  );
}

function BillDetail({ bill, back }: { bill: PortalBillDetail; back: ReactNode }) {
  const styles = useStyles();
  const t = useT();
  const f = useFormat();
  const { language } = useLanguage();
  const toast = useToast();
  const hospital = useHospitalName();
  const queryClient = useQueryClient();
  const { profile } = useActiveProfile();
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  // A payment started on the gateway's own page (and now back here) is confirmed first.
  const [verifyOrder, setVerifyOrder] = useState<string | null>(() => peekPendingPayment(bill.id)?.orderRowId ?? null);
  const number = bill.billNumber ?? '';
  const status = BILL_STATUS[bill.status];
  const printContext = () => ({ t, f, hospital, uhid: profile.uhid, printedAt: new Date().toISOString() });

  // Confirming is a server-to-server check with the gateway, so it is safe to repeat ("Check payment status").
  const verification = useQuery({
    queryKey: ['portal', profile.id, 'payment-verify', bill.id, verifyOrder],
    queryFn: async () => {
      const result = await billApi.verify(bill.id, verifyOrder ?? '');
      if (result.status !== 'Created') {
        clearPendingPayment();
      }
      if (result.status === 'Paid') {
        void queryClient.invalidateQueries({ queryKey: billQueries.all(profile.id) });
      }
      return result;
    },
    enabled: verifyOrder !== null,
    retry: 2,
    staleTime: Infinity,
  });

  const pay = async () => {
    setBusy(true);
    setNote(null);
    try {
      const order = await billApi.pay(bill.id);
      if (isTrainingGateway(order)) {
        toast.info(t('payment.training'));
      }
      const outcome = await runCheckout(order, {
        hospital,
        description: t('bills.number', { number }),
        language,
        pending: { kind: 'bill', billId: bill.id, orderRowId: order.id },
      });
      switch (outcome.kind) {
        case 'completed':
        case 'captured':
          setVerifyOrder(order.id);
          break;
        case 'redirected':
          return;
        case 'dismissed':
          setNote(t('payment.dismissed'));
          break;
        case 'failed':
          setNote(t('payment.failed'));
          break;
        default:
          setNote(t('payment.unavailable'));
      }
    } catch (error) {
      if (isApiError(error) && error.status === 422) {
        setNote(t('payment.unavailable'));
      } else {
        toast.failure(error, t);
      }
    } finally {
      setBusy(false);
    }
  };

  const paidNow = verification.data?.status === 'Paid';
  const pending = verification.data?.status === 'Created';
  const failed = verification.data && !paidNow && !pending;

  return (
    <PortalPage
      title={t('bills.number', { number })}
      subtitle={[t(BILL_TYPE[bill.type]), f.date(bill.billedAt)].filter(Boolean).join(' · ')}
      back={back}
      actions={<DocumentActions build={() => billDocument(bill, printContext())} />}
    >
      <div>
        <StatusPill tone={status.tone}>{t(status.key)}</StatusPill>
      </div>

      {verification.isFetching && (
        <Notice intent="info" action={<Spinner size="tiny" aria-hidden />}>
          {t('payment.verifying')}
        </Notice>
      )}
      {!verification.isFetching && paidNow && (
        <Notice intent="success" title={t('payment.success')}>
          {t('bills.paidNow')}
        </Notice>
      )}
      {!verification.isFetching && pending && (
        <Notice
          intent="warning"
          action={
            <Button onClick={() => void verification.refetch()} disabled={verification.isFetching}>
              {t('payment.checkAgain')}
            </Button>
          }
        >
          {t('payment.pending')}
        </Notice>
      )}
      {!verification.isFetching && failed && <Notice intent="error">{t('payment.failed')}</Notice>}
      {verification.isError && <ErrorBlock error={verification.error} onRetry={() => void verification.refetch()} />}
      {note && <Notice intent="warning">{note}</Notice>}

      {isDue(bill.dueAmount) && !paidNow && (
        <section className={styles.due} aria-label={t('bills.due')}>
          <span className={styles.muted}>{t('bills.due')}</span>
          <p className={styles.dueAmount}>{f.inr(bill.dueAmount)}</p>
          <div>
            <Button
              appearance="primary"
              size="large"
              icon={busy ? <Spinner size="tiny" /> : <PaymentRegular />}
              disabled={busy || verification.isFetching}
              onClick={() => void pay()}
            >
              {busy ? t('payment.opening') : t('bills.pay', { amount: f.inr(bill.dueAmount) })}
            </Button>
          </div>
          <p className={styles.secure}>{t('payment.secure')}</p>
        </section>
      )}

      <ReportCard title={t('bills.items')}>
        <ul className={styles.lines}>
          {bill.lines.map((line, index) => (
            <li key={`${line.description}-${index}`} className={styles.line}>
              <span className={styles.lineText}>
                <span className={styles.lineName}>
                  {line.description}
                  {line.isReturn && (
                    <>
                      {' '}
                      <StatusPill tone="neutral">{t('bills.returned')}</StatusPill>
                    </>
                  )}
                </span>
                <span className={styles.lineMeta}>
                  {[
                    f.date(line.serviceDate),
                    t('bills.qty', { count: f.number(line.quantity), price: f.inr(line.unitPrice) }),
                    line.discountAmount ? t('bills.discount', { amount: f.inr(line.discountAmount) }) : null,
                    line.taxAmount ? t('bills.tax', { amount: f.inr(line.taxAmount) }) : null,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
                </span>
              </span>
              <span className={styles.lineAmount}>{f.inr(line.netAmount)}</span>
            </li>
          ))}
        </ul>
      </ReportCard>

      <ReportCard title={t('bills.summary')}>
        <table className={styles.totals}>
          <tbody>
            <TotalRow label={t('bills.gross')} value={f.inr(bill.grossAmount)} />
            {bill.discountAmount !== 0 && <TotalRow label={t('bills.discounts')} value={f.inr(-bill.discountAmount)} />}
            {bill.taxAmount !== 0 && <TotalRow label={t('bills.gst')} value={f.inr(bill.taxAmount)} />}
            {bill.roundOffAmount !== 0 && <TotalRow label={t('bills.roundOff')} value={f.inr(bill.roundOffAmount)} />}
            <TotalRow label={t('bills.net')} value={f.inr(bill.netAmount)} grand />
            {bill.payerAmount !== 0 && <TotalRow label={t('bills.payer')} value={f.inr(bill.payerAmount)} />}
            <TotalRow label={t('bills.patientShare')} value={f.inr(bill.patientAmount)} />
            <TotalRow label={t('bills.paid')} value={f.inr(bill.paidAmount)} />
            <TotalRow label={t('bills.due')} value={f.inr(bill.dueAmount)} grand />
          </tbody>
        </table>
      </ReportCard>

      {bill.receipts.length > 0 && (
        <ReportCard title={t('bills.receipts')}>
          <div className={styles.receipts}>
            {bill.receipts.map((receipt) => (
              <RowCard key={receipt.id}>
                <ListRow
                  icon={<ReceiptRegular />}
                  tone="success"
                  title={`${f.inr(receipt.amount)} · ${t(RECEIPT_PURPOSE[receipt.purpose])}`}
                  subtitle={`${f.dateTime(receipt.receivedAt)} · ${receipt.modes}`}
                  meta={t('bills.receipt', { number: receipt.receiptNumber })}
                  chevron={false}
                  trailing={
                    <Button
                      icon={<PrintRegular />}
                      onClick={() => printPortalDocument(receiptDocument(receipt, bill, printContext()))}
                    >
                      {t('bills.receipt.print')}
                    </Button>
                  }
                />
              </RowCard>
            ))}
          </div>
        </ReportCard>
      )}

      <p className={styles.muted}>{t('bills.help')}</p>
    </PortalPage>
  );
}

function TotalRow({ label, value, grand }: { label: string; value: string; grand?: boolean }) {
  const styles = useStyles();
  return (
    <tr className={grand ? styles.grand : undefined}>
      <th scope="row" className={styles.totalLabel}>
        {label}
      </th>
      <td className={styles.totalValue}>{value}</td>
    </tr>
  );
}
