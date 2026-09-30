import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { DrinkCoffeeRegular } from '@fluentui/react-icons';
import { formatInr, todayIso } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { canteenQueries, PAYMENT_LABEL, type CanteenPaymentMode } from './api';

const useStyles = makeStyles({
  big: {
    fontSize: tokens.fontSizeHero700,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightHero700,
  },
  rows: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) auto', gap: '4px 12px', marginTop: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** Today's canteen takings with the split by payment mode. */
export function CanteenTodayWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const today = todayIso();
  const report = useQuery({ ...canteenQueries.daily(today), refetchInterval: 120_000 });
  const r = report.data;

  return (
    <Section
      title="Canteen today"
      actions={
        <Button
          size="small"
          appearance="subtle"
          icon={<DrinkCoffeeRegular />}
          onClick={() => void navigate({ to: '/canteen/report' })}
        >
          Day report
        </Button>
      }
    >
      {report.error ? (
        <ErrorState error={report.error} onRetry={() => void report.refetch()} />
      ) : !r ? (
        <Caption1 className={styles.muted}>Loading…</Caption1>
      ) : (
        <>
          <span className={styles.big}>{formatInr(r.netSales)}</span>
          <Caption1 block className={styles.muted}>
            {r.bills} bills{r.cancelledBills ? ` · ${r.cancelledBills} cancelled` : ''} · GST{' '}
            {formatInr(r.cgst + r.sgst)}
          </Caption1>
          {r.byPaymentMode.length > 0 && (
            <div className={styles.rows}>
              {r.byPaymentMode.map((m) => (
                <div key={m.key} style={{ display: 'contents' }}>
                  <Text>{PAYMENT_LABEL[m.key as CanteenPaymentMode] ?? m.key}</Text>
                  <Text weight="semibold">{formatInr(m.amount)}</Text>
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </Section>
  );
}
