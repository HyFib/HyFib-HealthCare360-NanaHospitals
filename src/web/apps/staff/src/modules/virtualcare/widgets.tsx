import { Badge, Button, Caption1, makeStyles, Text, tokens } from '@fluentui/react-components';
import { AlertUrgentRegular, PlugDisconnectedRegular, VideoRegular, WarningRegular } from '@fluentui/react-icons';
import { formatRelative, formatTime, useRealtimeEvent, useSession } from '@hc360/core';
import { ErrorState, Section, StatusBadge } from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { usePermission } from '@/lib/permissions';
import { formatDuration, minutesSince } from '@/modules/scheduling/dates';
import { useNow, useToday } from '@/modules/scheduling/useNow';
import { rpmQueries } from './rpm/api';
import { alertSeverityText } from './rpm/labels';
import { useRpmLive } from './rpm/useRpmLive';
import { telehealthKeys, telehealthQueries } from './telehealth/api';
import { isSessionOpen, sessionStatusText } from './telehealth/labels';

const useStyles = makeStyles({
  counts: { display: 'flex', gap: '8px', flexWrap: 'wrap', marginBottom: '12px' },
  rows: { display: 'flex', flexDirection: 'column', gap: '8px' },
  row: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px' },
  text: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  waiting: { color: tokens.colorPaletteRedForeground1 },
  alertRow: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '2px',
    padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium,
    border: 'none',
    background: 'transparent',
    textAlign: 'start',
    cursor: 'pointer',
    fontFamily: 'inherit',
    width: '100%',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}` },
  },
});

/** Today's video consultations (the doctor's own when signed in as a doctor), with patients waiting on top. */
export function TelehealthTodayWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const facilityId = useSession((s) => s.user?.facilityId);
  const mine = Boolean(useSession((s) => s.user?.practitionerId));
  const canConsult = usePermission('telehealth.consult');
  const today = useToday();
  const now = useNow();
  const sessions = useQuery(telehealthQueries.sessions({ date: today, mine: mine || undefined }));
  useRealtimeEvent(facilityId ? `facility:${facilityId}` : null, 'telehealth.changed', () => {
    void queryClient.invalidateQueries({ queryKey: telehealthKeys.sessions });
  });

  const rows = sessions.data ?? [];
  const waiting = rows.filter((s) => s.status === 'WaitingRoom');
  const upcoming = rows.filter((s) => s.status === 'Scheduled' || s.status === 'InProgress');
  const shown = [...waiting, ...upcoming].slice(0, 5);

  return (
    <Section
      title={mine ? 'My video consultations today' : 'Video consultations today'}
      actions={
        <Button appearance="transparent" onClick={() => void navigate({ to: '/telehealth' })}>
          Open
        </Button>
      }
    >
      {sessions.error ? (
        <ErrorState error={sessions.error} onRetry={() => void sessions.refetch()} />
      ) : (
        <>
          <div className={styles.counts}>
            <Badge
              appearance={waiting.length > 0 ? 'filled' : 'tint'}
              color={waiting.length > 0 ? 'danger' : 'informative'}
            >
              {waiting.length} waiting
            </Badge>
            <Badge appearance="tint" color="brand">
              {rows.filter((s) => s.status === 'InProgress').length} in progress
            </Badge>
            <Badge appearance="tint">{rows.filter((s) => s.status === 'Scheduled').length} to come</Badge>
            <Badge appearance="tint" color="success">
              {rows.filter((s) => s.status === 'Completed').length} done
            </Badge>
          </div>
          {shown.length === 0 ? (
            <Text className={styles.muted}>
              {sessions.isLoading ? 'Loading…' : 'No more video consultations today.'}
            </Text>
          ) : (
            <div className={styles.rows}>
              {shown.map((s) => (
                <div key={s.id} className={styles.row}>
                  <span className={styles.text}>
                    <Text weight="semibold" truncate>
                      {s.patientName}
                    </Text>
                    <Caption1 className={s.status === 'WaitingRoom' ? styles.waiting : styles.muted}>
                      {formatTime(s.scheduledStartAt)}
                      {mine ? '' : ` · ${s.practitionerName}`}
                      {s.status === 'WaitingRoom' && s.patientWaitingSince
                        ? ` · waiting ${formatDuration(minutesSince(s.patientWaitingSince, now))}`
                        : ''}
                    </Caption1>
                  </span>
                  {canConsult && isSessionOpen(s.status) ? (
                    <Button
                      size="small"
                      appearance={s.status === 'WaitingRoom' ? 'primary' : 'secondary'}
                      icon={<VideoRegular />}
                      onClick={() => void navigate({ to: '/telehealth/$sessionId', params: { sessionId: s.id } })}
                    >
                      Join
                    </Button>
                  ) : (
                    <StatusBadge
                      status={s.status}
                      tone={sessionStatusText[s.status].tone}
                      label={sessionStatusText[s.status].label}
                    />
                  )}
                </div>
              ))}
            </div>
          )}
        </>
      )}
    </Section>
  );
}

/** Remote monitoring alerts for the care team, critical first. */
export function RpmAlertsWidget() {
  const styles = useStyles();
  const navigate = useNavigate();
  useRpmLive();
  const dashboard = useQuery(rpmQueries.dashboard());
  const alerts = useQuery(rpmQueries.alerts({ pageSize: 5 }));
  const d = dashboard.data;

  return (
    <Section
      title="Remote monitoring alerts"
      actions={
        <Button appearance="transparent" onClick={() => void navigate({ to: '/rpm' })}>
          Open
        </Button>
      }
    >
      {dashboard.error || alerts.error ? (
        <ErrorState
          error={dashboard.error ?? alerts.error}
          onRetry={() => void Promise.all([dashboard.refetch(), alerts.refetch()])}
        />
      ) : (
        <>
          <div className={styles.counts}>
            <Badge
              appearance={d && d.unacknowledgedCritical > 0 ? 'filled' : 'tint'}
              color="danger"
              icon={<AlertUrgentRegular />}
            >
              {d ? d.criticalAlerts : '…'} critical
              {d && d.unacknowledgedCritical > 0 ? ` (${d.unacknowledgedCritical} unseen)` : ''}
            </Badge>
            <Badge appearance="tint" color="warning" icon={<WarningRegular />}>
              {d ? d.openAlerts : '…'} open
            </Badge>
            <Badge appearance="tint" icon={<PlugDisconnectedRegular />}>
              {d ? d.silentDevices : '…'} silent devices
            </Badge>
          </div>
          {(alerts.data?.items ?? []).length === 0 ? (
            <Text className={styles.muted}>{alerts.isLoading ? 'Loading…' : 'No open alerts.'}</Text>
          ) : (
            <div className={styles.rows}>
              {(alerts.data?.items ?? []).map((a) => (
                <button
                  key={a.id}
                  type="button"
                  className={styles.alertRow}
                  onClick={() =>
                    void navigate({
                      to: '/rpm/enrollments/$enrollmentId',
                      params: { enrollmentId: a.enrollmentId },
                      search: { alert: a.id },
                    })
                  }
                >
                  <span>
                    <StatusBadge
                      status={a.severity}
                      tone={alertSeverityText[a.severity].tone}
                      label={alertSeverityText[a.severity].label}
                    />{' '}
                    <Text weight="semibold">{a.patientName}</Text>
                  </span>
                  <Caption1>{a.message}</Caption1>
                  <Caption1 className={styles.muted}>{formatRelative(a.raisedAt)}</Caption1>
                </button>
              ))}
            </div>
          )}
        </>
      )}
    </Section>
  );
}
