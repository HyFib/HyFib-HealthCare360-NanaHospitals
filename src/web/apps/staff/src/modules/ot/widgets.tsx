import { Button, Caption1, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { formatTime, todayIso } from '@hc360/core';
import { ErrorState, Section } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { otKeys, otQueries } from './api';
import { procedureWithSide } from './logic';
import { PriorityBadge, SurgeryStatusBadge, WhoProgress } from './OtBadges';
import { useFacilityId, useLiveInvalidation } from './shared/hooks';

const useStyles = makeStyles({
  counts: { display: 'flex', gap: '16px', flexWrap: 'wrap', marginBottom: '10px' },
  count: { display: 'flex', flexDirection: 'column' },
  number: { fontSize: tokens.fontSizeBase600, lineHeight: tokens.lineHeightBase600, fontWeight: tokens.fontWeightSemibold },
  urgent: { color: tokens.colorPaletteRedForeground1 },
  list: { display: 'flex', flexDirection: 'column', gap: '4px' },
  row: {
    display: 'grid',
    gridTemplateColumns: '70px minmax(0, 1fr) auto',
    gap: '2px 10px',
    alignItems: 'center',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    border: 'none',
    backgroundColor: 'transparent',
    textAlign: 'start',
    color: tokens.colorNeutralForeground1,
    cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
  muted: { color: tokens.colorNeutralForeground3 },
  truncate: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
});

/** Home-screen list of today's cases across theatres, live from the OT board. */
export function TodaysSurgeriesWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const facilityId = useFacilityId();
  const today = todayIso();
  const calendar = useQuery(otQueries.calendar({ date: today, view: 'day' }));
  useLiveInvalidation(facilityId ? `ot:${facilityId}` : null, 'ot.booking.changed', [otKeys.calendars]);

  if (calendar.error) {
    return <ErrorState error={calendar.error} onRetry={() => void calendar.refetch()} />;
  }

  const cases = (calendar.data?.theatres ?? [])
    .flatMap((t) => t.bookings.map((b) => ({ ...b, theatreCode: t.code })))
    .sort((a, b) => Date.parse(a.start ?? '') - Date.parse(b.start ?? ''));
  const inSurgery = cases.filter((c) => c.status === 'InProgress').length;
  const done = cases.filter((c) => c.status === 'Completed').length;
  const emergencies = cases.filter((c) => c.priority === 'Emergency' && c.status !== 'Completed').length;
  const upcoming = cases.filter((c) => c.status !== 'Completed').slice(0, 6);
  const waiting = calendar.data?.awaitingSlot.length ?? 0;

  return (
    <Section
      title="Today's surgeries"
      actions={
        <Button size="small" appearance="subtle" onClick={() => void navigate({ to: '/ot' })}>
          OT calendar
        </Button>
      }
    >
      {!calendar.data ? (
        <Caption1>Loading…</Caption1>
      ) : (
        <>
          <div className={styles.counts}>
            <span className={styles.count}>
              <span className={styles.number}>{cases.length}</span>
              <Caption1 className={styles.muted}>Booked today</Caption1>
            </span>
            <span className={styles.count}>
              <span className={styles.number}>{inSurgery}</span>
              <Caption1 className={styles.muted}>In surgery now</Caption1>
            </span>
            <span className={styles.count}>
              <span className={styles.number}>{done}</span>
              <Caption1 className={styles.muted}>Completed</Caption1>
            </span>
            {emergencies > 0 && (
              <span className={styles.count}>
                <span className={mergeClasses(styles.number, styles.urgent)}>{emergencies}</span>
                <Caption1 className={styles.urgent}>Emergency</Caption1>
              </span>
            )}
            {waiting > 0 && (
              <span className={styles.count}>
                <span className={styles.number}>{waiting}</span>
                <Caption1 className={styles.muted}>Waiting for a slot</Caption1>
              </span>
            )}
          </div>
          {upcoming.length === 0 ? (
            <Text className={styles.muted}>{cases.length ? 'All of today’s surgeries are complete.' : 'No surgeries are booked for today.'}</Text>
          ) : (
            <div className={styles.list}>
              {upcoming.map((c) => (
                <button key={c.id} type="button" className={styles.row} onClick={() => void navigate({ to: '/ot/bookings/$id', params: { id: c.id } })}>
                  <Text weight="semibold">{formatTime(c.start)}</Text>
                  <span className={styles.truncate}>
                    <Text weight="semibold">{c.patientName}</Text>{' '}
                    <Caption1 className={styles.muted}>
                      · {c.theatreCode} · {procedureWithSide(c.procedureName, c.laterality)}
                    </Caption1>
                  </span>
                  <span style={{ display: 'inline-flex', gap: 6, alignItems: 'center' }}>
                    <PriorityBadge priority={c.priority} />
                    <SurgeryStatusBadge status={c.status} />
                  </span>
                  <span />
                  <WhoProgress signInDone={c.signInDone} timeOutDone={c.timeOutDone} signOutDone={c.signOutDone} compact />
                </button>
              ))}
            </div>
          )}
        </>
      )}
    </Section>
  );
}
