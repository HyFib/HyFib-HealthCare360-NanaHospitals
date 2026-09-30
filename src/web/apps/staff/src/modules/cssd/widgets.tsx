import {
  Button,
  Caption1,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Text,
  tokens,
} from '@fluentui/react-components';
import { BoxCheckmarkRegular } from '@fluentui/react-icons';
import { todayIso } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { addDays } from '@/modules/inpatient/time';
import { cssdQueries } from './api';

const useStyles = makeStyles({
  grid: { display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: '12px' },
  cell: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    textDecoration: 'none',
    color: tokens.colorNeutralForeground1,
    ':hover': { backgroundColor: tokens.colorNeutralBackground2Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  value: { fontSize: tokens.fontSizeBase600, fontWeight: tokens.fontWeightSemibold },
  alert: { color: tokens.colorPaletteRedForeground1 },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** CSSD at a glance: open requests, indicators to read, recalls and packs about to expire. */
export function CssdWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const canSeeRecalls = usePermission(['cssd.recalls', 'cssd.read']);
  const today = todayIso();
  const summary = useQuery(cssdQueries.summary(addDays(today, -29), today));
  const s = summary.data;

  return (
    <Section
      title="Sterile supply (CSSD)"
      description="Last 30 days"
      actions={
        <Button
          size="small"
          appearance="subtle"
          icon={<BoxCheckmarkRegular />}
          onClick={() => void navigate({ to: '/cssd' })}
        >
          CSSD
        </Button>
      }
    >
      {summary.error ? (
        <ErrorState error={summary.error} onRetry={() => void summary.refetch()} />
      ) : !s ? (
        <Caption1 className={styles.muted}>Loading…</Caption1>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          {s.openRecalls > 0 && canSeeRecalls && (
            <MessageBar intent="error" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>{s.openRecalls} recalled packs not yet acknowledged</MessageBarTitle>
                Withdraw them and confirm the action taken.
              </MessageBarBody>
              <MessageBarActions>
                <Button size="small" onClick={() => void navigate({ to: '/cssd/recalls' })}>
                  Open recalls
                </Button>
              </MessageBarActions>
            </MessageBar>
          )}
          <div className={styles.grid}>
            <Link to="/cssd/requests" className={styles.cell}>
              <span className={styles.value}>{s.openRequests}</span>
              <Text size={200}>Open requests</Text>
            </Link>
            <Link to="/cssd/loads" search={{ biologicalPending: true }} className={styles.cell}>
              <span className={s.biologicalPending ? `${styles.value} ${styles.alert}` : styles.value}>
                {s.biologicalPending}
              </span>
              <Text size={200}>Biological indicators to read</Text>
            </Link>
            <Link to="/cssd/packs" search={{ expiring: true }} className={styles.cell}>
              <span className={s.packsExpiringSoon ? `${styles.value} ${styles.alert}` : styles.value}>
                {s.packsExpiringSoon}
              </span>
              <Text size={200}>Packs expiring in 2 days</Text>
            </Link>
            <Link to="/cssd/loads" className={styles.cell}>
              <span className={styles.value}>{s.failureRatePercent}%</span>
              <Text size={200}>
                Load failures ({s.loadsFailed} of {s.loads})
              </Text>
            </Link>
          </div>
        </div>
      )}
    </Section>
  );
}
