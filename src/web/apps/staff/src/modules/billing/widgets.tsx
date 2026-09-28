import { Button, Caption1, makeStyles, ProgressBar, Text, tokens } from '@fluentui/react-components';
import { ArrowRightRegular } from '@fluentui/react-icons';
import { formatInr, formatRelative, todayIso } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { billingQueries } from './api';
import { useBillingLive } from './hooks';
import { PAYMENT_MODE_LABEL } from './labels';
import { sumRupees } from './money';
import { Amount } from './ui';

const useStyles = makeStyles({
  figures: { display: 'flex', gap: '24px', flexWrap: 'wrap', alignItems: 'baseline', marginBottom: '12px' },
  figure: { display: 'flex', flexDirection: 'column', gap: '2px' },
  bars: { display: 'flex', flexDirection: 'column', gap: '8px' },
  bar: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: '2px 12px', alignItems: 'center' },
  list: { display: 'flex', flexDirection: 'column', gap: '6px' },
  item: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto',
    gap: '2px 12px',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    cursor: 'pointer',
    border: 'none',
    background: 'transparent',
    textAlign: 'start',
    width: '100%',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** "Today's collections" on the home dashboard: net collection by payment mode (day-end figures, live). */
export function TodaysCollectionsWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  useBillingLive();
  const canShifts = usePermission('billing.shifts');
  const today = todayIso();
  const report = useQuery({ ...billingQueries.dayEnd(today), enabled: canShifts, refetchInterval: 60_000 });
  const receipts = useQuery({ ...billingQueries.receipts({ from: today, to: today, pageSize: 200 }), enabled: !canShifts, refetchInterval: 60_000 });

  const error = canShifts ? report.error : receipts.error;
  if (error) {
    return (
      <Section title="Today's collections">
        <ErrorState error={error} onRetry={() => void (canShifts ? report.refetch() : receipts.refetch())} />
      </Section>
    );
  }

  const byMode = report.data?.byMode.filter((m) => m.collected !== 0 || m.refunded !== 0) ?? [];
  const max = Math.max(1, ...byMode.map((m) => Math.abs(m.net)));
  const receiptTotal = receipts.data ? sumRupees(receipts.data.items.map((r) => r.amount)) : null;

  return (
    <Section
      title="Today's collections"
      description={canShifts ? 'All counters, net of refunds' : 'Receipts recorded today'}
      actions={
        <Button appearance="transparent" icon={<ArrowRightRegular />} iconPosition="after" onClick={() => void navigate({ to: canShifts ? '/billing/day-end' : '/billing/receipts' })}>
          {canShifts ? 'Day-end report' : 'Receipts'}
        </Button>
      }
    >
      {canShifts ? (
        <>
          <div className={styles.figures}>
            <span className={styles.figure}>
              <Caption1 className={styles.muted}>Net collection</Caption1>
              <Amount value={report.data?.netCollection ?? null} size="large" />
            </span>
            <span className={styles.figure}>
              <Caption1 className={styles.muted}>IP advances</Caption1>
              <Amount value={report.data?.depositsCollected ?? null} />
            </span>
            <span className={styles.figure}>
              <Caption1 className={styles.muted}>Bills finalised</Caption1>
              <Text>{report.data ? report.data.billsFinalized.toLocaleString('en-IN') : '…'}</Text>
            </span>
          </div>
          {byMode.length === 0 ? (
            <Text className={styles.muted}>{report.isLoading ? 'Loading…' : 'Nothing collected yet today.'}</Text>
          ) : (
            <div className={styles.bars}>
              {byMode.map((mode) => (
                <div key={mode.mode} className={styles.bar}>
                  <Text>{PAYMENT_MODE_LABEL[mode.mode]}</Text>
                  <Amount value={mode.net} strong />
                  <div style={{ gridColumn: '1 / -1' }}>
                    <ProgressBar value={Math.abs(mode.net) / max} thickness="large" aria-label={`${PAYMENT_MODE_LABEL[mode.mode]}: ${formatInr(mode.net)}`} />
                  </div>
                </div>
              ))}
            </div>
          )}
          {report.data && report.data.openShifts > 0 && (
            <Caption1 className={styles.muted}>
              {report.data.openShifts} shift{report.data.openShifts === 1 ? '' : 's'} still open
            </Caption1>
          )}
        </>
      ) : (
        <div className={styles.figures}>
          <span className={styles.figure}>
            <Caption1 className={styles.muted}>Received</Caption1>
            <Amount value={receiptTotal} size="large" />
          </span>
          <span className={styles.figure}>
            <Caption1 className={styles.muted}>Receipts</Caption1>
            <Text>{receipts.data ? receipts.data.totalCount.toLocaleString('en-IN') : '…'}</Text>
          </span>
          {receipts.data && receipts.data.totalCount > receipts.data.items.length && (
            <Caption1 className={styles.muted}>Total of the latest {receipts.data.items.length} receipts.</Caption1>
          )}
        </div>
      )}
    </Section>
  );
}

/** "Discounts awaiting approval" for approvers: the oldest first, one click to decide. */
export function DiscountApprovalsWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const pending = useQuery({ ...billingQueries.discountApprovals('Pending', 1, 5), refetchInterval: 60_000 });

  return (
    <Section
      title="Discounts awaiting approval"
      description={pending.data ? `${pending.data.totalCount} waiting` : undefined}
      actions={
        <Button appearance="transparent" icon={<ArrowRightRegular />} iconPosition="after" onClick={() => void navigate({ to: '/billing/discount-approvals' })}>
          All requests
        </Button>
      }
    >
      {pending.error ? (
        <ErrorState error={pending.error} onRetry={() => void pending.refetch()} />
      ) : pending.data && pending.data.items.length === 0 ? (
        <Text className={styles.muted}>No discount is waiting for a decision.</Text>
      ) : (
        <div className={styles.list}>
          {(pending.data?.items ?? []).map((request) => (
            <button
              key={request.id}
              type="button"
              className={styles.item}
              onClick={() => void navigate({ to: '/billing/discount-approvals/$approvalId', params: { approvalId: request.id } })}
            >
              <Text weight="semibold">{request.patientName ?? request.uhid ?? 'Patient'}</Text>
              <Amount value={request.requestedAmount} strong />
              <Caption1 className={styles.muted}>
                {request.reason} · {request.requestedByName ?? 'cashier'} · {formatRelative(request.requestedAt)}
              </Caption1>
            </button>
          ))}
        </div>
      )}
    </Section>
  );
}
