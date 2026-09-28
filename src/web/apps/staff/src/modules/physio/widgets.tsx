import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { formatTime, todayIso } from '@hc360/core';
import { ErrorState, Section, StatusBadge } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useFacilityId, useLiveInvalidation } from '@/modules/ot/shared/hooks';
import { physioKeys, physioQueries } from './api';
import { physioSessionStatusLabel, physioSessionStatusTone } from './labels';

const useStyles = makeStyles({
  summary: { marginBottom: '8px' },
  list: { display: 'flex', flexDirection: 'column', gap: '4px' },
  row: {
    display: 'grid',
    gridTemplateColumns: '64px minmax(0, 1fr) auto',
    gap: '8px',
    alignItems: 'center',
    padding: '4px 6px',
    border: 'none',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: 'transparent',
    textAlign: 'start',
    color: tokens.colorNeutralForeground1,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  truncate: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/** The physiotherapist's day: sessions still to see and those done. */
export function PhysioTodayWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const facilityId = useFacilityId();
  const worklist = useQuery(physioQueries.worklist(todayIso()));
  useLiveInvalidation(facilityId ? `facility:${facilityId}` : null, 'physio.worklist.changed', [physioKeys.worklists]);

  if (worklist.error) {
    return <ErrorState error={worklist.error} onRetry={() => void worklist.refetch()} />;
  }
  const items = worklist.data ?? [];
  const waiting = items.filter((i) => i.status === 'Scheduled');
  const done = items.filter((i) => i.status === 'Attended').length;

  return (
    <Section
      title="Physiotherapy today"
      actions={
        <Button size="small" appearance="subtle" onClick={() => void navigate({ to: '/physio' })}>
          Worklist
        </Button>
      }
    >
      {!worklist.data ? (
        <Caption1>Loading…</Caption1>
      ) : (
        <>
          <Text block className={styles.summary}>
            {waiting.length} to see · {done} done
          </Text>
          {waiting.length === 0 ? (
            <Caption1 className={styles.muted}>{items.length ? 'Everyone booked for today has been seen.' : 'No sessions booked for today.'}</Caption1>
          ) : (
            <div className={styles.list}>
              {waiting.slice(0, 6).map((item) => (
                <button key={item.sessionId} type="button" className={styles.row} onClick={() => void navigate({ to: '/physio/referrals/$id', params: { id: item.referralId } })}>
                  <Text weight="semibold">{formatTime(item.scheduledAt)}</Text>
                  <span className={styles.truncate}>
                    {item.patientName}{' '}
                    <Caption1 className={styles.muted}>
                      · session {item.sessionNumber}
                      {item.sessionsPlanned ? ` of ${item.sessionsPlanned}` : ''}
                    </Caption1>
                  </span>
                  <StatusBadge status={item.status} tone={physioSessionStatusTone[item.status]} label={physioSessionStatusLabel[item.status]} />
                </button>
              ))}
            </div>
          )}
        </>
      )}
    </Section>
  );
}
