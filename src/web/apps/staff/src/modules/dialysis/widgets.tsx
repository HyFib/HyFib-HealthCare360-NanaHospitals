import { Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { BroomRegular, PersonClockRegular } from '@fluentui/react-icons';
import { todayIso } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useFacilityId, useLiveInvalidation } from '@/modules/ot/shared/hooks';
import { dialysisKeys, dialysisQueries } from './api';

const useStyles = makeStyles({
  counts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(90px, 1fr))', gap: '8px', marginBottom: '8px' },
  count: { display: 'flex', flexDirection: 'column' },
  number: { fontSize: tokens.fontSizeBase600, lineHeight: tokens.lineHeightBase600, fontWeight: tokens.fontWeightSemibold },
  muted: { color: tokens.colorNeutralForeground3 },
  alert: { display: 'flex', gap: '6px', alignItems: 'center', color: tokens.colorPaletteDarkOrangeForeground1 },
});

/** Today's dialysis unit at a glance: sessions by stage, machines awaiting disinfection, patients due but not booked. */
export function DialysisTodayWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const facilityId = useFacilityId();
  const board = useQuery(dialysisQueries.board(todayIso()));
  useLiveInvalidation(facilityId ? `facility:${facilityId}` : null, 'dialysis.board.changed', [dialysisKeys.boards]);

  if (board.error) {
    return <ErrorState error={board.error} onRetry={() => void board.refetch()} />;
  }
  const sessions = [...(board.data?.machines.flatMap((m) => m.sessions) ?? []), ...(board.data?.unassigned ?? [])];
  const count = (status: string) => sessions.filter((s) => s.status === status).length;
  const dirty = board.data?.machines.filter((m) => m.needsDisinfection).length ?? 0;
  const due = board.data?.dueNotScheduled.length ?? 0;

  return (
    <Section
      title="Dialysis today"
      actions={
        <Button size="small" appearance="subtle" onClick={() => void navigate({ to: '/dialysis' })}>
          Board
        </Button>
      }
    >
      {!board.data ? (
        <Caption1>Loading…</Caption1>
      ) : (
        <>
          <div className={styles.counts}>
            {(
              [
                ['Scheduled', 'Scheduled'],
                ['InProgress', 'On dialysis'],
                ['Completed', 'Completed'],
                ['Missed', 'Missed'],
              ] as const
            ).map(([status, label]) => (
              <span key={status} className={styles.count}>
                <span className={styles.number}>{count(status)}</span>
                <Caption1 className={styles.muted}>{label}</Caption1>
              </span>
            ))}
          </div>
          {dirty > 0 && (
            <Text className={styles.alert}>
              <BroomRegular aria-hidden /> {dirty} machine{dirty === 1 ? '' : 's'} need{dirty === 1 ? 's' : ''} disinfection
            </Text>
          )}
          {due > 0 && (
            <Text className={styles.alert}>
              <PersonClockRegular aria-hidden /> {due} patient{due === 1 ? ' is' : 's are'} due today but not booked
            </Text>
          )}
          {dirty === 0 && due === 0 && <Caption1 className={styles.muted}>All machines are disinfected and every due patient is booked.</Caption1>}
        </>
      )}
    </Section>
  );
}
