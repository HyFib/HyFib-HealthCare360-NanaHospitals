import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { ArrowRightRegular } from '@fluentui/react-icons';
import { formatInr } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { insuranceQueries } from './api';
import { CLAIM_STATUS_LABEL, OPEN_CLAIM_STATUSES, type ClaimStatus } from './labels';

const useStyles = makeStyles({
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(140px, 1fr))', gap: '8px' },
  tile: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    border: 'none',
    backgroundColor: tokens.colorNeutralBackground2,
    textAlign: 'start',
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground2Hover },
  },
  count: {
    fontSize: tokens.fontSizeHero700,
    lineHeight: tokens.lineHeightHero700,
    fontWeight: tokens.fontWeightSemibold,
  },
  muted: { color: tokens.colorNeutralForeground3 },
});

function StatusTile({ status }: { status: ClaimStatus }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const count = useQuery(insuranceQueries.claimCount(status));
  return (
    <button
      type="button"
      className={styles.tile}
      onClick={() => void navigate({ to: '/insurance/claims', search: { status } })}
    >
      <span className={styles.count}>{count.data === undefined ? '…' : count.data.toLocaleString('en-IN')}</span>
      <Caption1 className={styles.muted}>{CLAIM_STATUS_LABEL[status]}</Caption1>
    </button>
  );
}

/** "Claims pending" on the home dashboard: open claims by stage and the money the insurers still owe. */
export function ClaimsPendingWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const ageing = useQuery(insuranceQueries.ageing());

  return (
    <Section
      title="Claims pending"
      description={
        ageing.data
          ? `Insurers owe ${formatInr(ageing.data.total)}${ageing.data.over90 > 0 ? ` · ${formatInr(ageing.data.over90)} over 90 days` : ''}`
          : undefined
      }
      actions={
        <Button
          appearance="transparent"
          icon={<ArrowRightRegular />}
          iconPosition="after"
          onClick={() => void navigate({ to: '/insurance/ageing' })}
        >
          Ageing
        </Button>
      }
    >
      {ageing.error ? (
        <ErrorState error={ageing.error} onRetry={() => void ageing.refetch()} />
      ) : (
        <div className={styles.grid}>
          {OPEN_CLAIM_STATUSES.map((status) => (
            <StatusTile key={status} status={status} />
          ))}
        </div>
      )}
      {ageing.data && ageing.data.payers.length === 0 && (
        <Text className={styles.muted}>No money is outstanding from insurers.</Text>
      )}
    </Section>
  );
}
