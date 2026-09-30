import { Badge, Caption1, makeStyles, Spinner, Text, tokens } from '@fluentui/react-components';
import { ChevronRightRegular, WarningRegular } from '@fluentui/react-icons';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { privacyQueries } from './api';
import { PRIVACY_PERMISSIONS as P } from './labels';

const useStyles = makeStyles({
  head: { display: 'flex', alignItems: 'baseline', gap: '10px', flexWrap: 'wrap' },
  big: {
    fontSize: tokens.fontSizeHero800,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightHero800,
  },
  rows: { display: 'flex', flexDirection: 'column', gap: '2px', marginTop: '8px' },
  row: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorNeutralForeground1,
    textDecoration: 'none',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  grow: { flex: 1 },
  danger: { color: tokens.colorPaletteRedForeground1 },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** "Privacy requests": open requests, what is overdue or due soon, and breach reports late to the Board. */
export function PrivacyOverviewWidget() {
  const styles = useStyles();
  const canBreaches = usePermission(P.breachesManage);
  const overview = useQuery(privacyQueries.overview());

  if (overview.error) {
    return <ErrorState error={overview.error} onRetry={() => void overview.refetch()} />;
  }

  const o = overview.data;
  return (
    <Section title="Privacy requests" description="Requests about personal data under the DPDP Act">
      {!o ? (
        <Spinner size="tiny" label="Loading…" labelPosition="after" />
      ) : (
        <>
          <div className={styles.head}>
            <span className={styles.big}>{o.open.toLocaleString('en-IN')}</span>
            <Text className={styles.muted}>open</Text>
            {o.overdue > 0 && (
              <Badge color="danger" icon={<WarningRegular />}>
                {o.overdue.toLocaleString('en-IN')} overdue
              </Badge>
            )}
          </div>
          <div className={styles.rows}>
            <Link to="/privacy/requests" search={{ view: 'overdue' }} className={styles.row}>
              <Text className={styles.grow}>Past the reply-by date</Text>
              <Text weight="semibold" className={o.overdue > 0 ? styles.danger : undefined}>
                {o.overdue.toLocaleString('en-IN')}
              </Text>
              <ChevronRightRegular aria-hidden />
            </Link>
            <Link to="/privacy/requests" className={styles.row}>
              <Text className={styles.grow}>Due in the next 7 days</Text>
              <Text weight="semibold">{o.dueThisWeek.toLocaleString('en-IN')}</Text>
              <ChevronRightRegular aria-hidden />
            </Link>
            <Link to="/privacy/requests" className={styles.row}>
              <Text className={styles.grow}>Waiting for an identity check</Text>
              <Text weight="semibold">{o.awaitingIdentityCheck.toLocaleString('en-IN')}</Text>
              <ChevronRightRegular aria-hidden />
            </Link>
            {o.breachReportsOverdue > 0 &&
              (canBreaches ? (
                <Link to="/privacy/breaches" className={styles.row}>
                  <WarningRegular className={styles.danger} aria-hidden />
                  <Text className={styles.grow} weight="semibold">
                    Breach reports to the Board overdue
                  </Text>
                  <Text weight="semibold" className={styles.danger}>
                    {o.breachReportsOverdue.toLocaleString('en-IN')}
                  </Text>
                  <ChevronRightRegular aria-hidden />
                </Link>
              ) : (
                <div className={styles.row}>
                  <WarningRegular className={styles.danger} aria-hidden />
                  <Text className={styles.grow} weight="semibold">
                    Breach reports to the Board overdue
                  </Text>
                  <Text weight="semibold" className={styles.danger}>
                    {o.breachReportsOverdue.toLocaleString('en-IN')}
                  </Text>
                </div>
              ))}
          </div>
          <Caption1 className={styles.muted}>Every request must be answered by its reply-by date.</Caption1>
        </>
      )}
    </Section>
  );
}
