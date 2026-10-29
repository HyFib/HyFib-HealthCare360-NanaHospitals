import { Button, Caption1, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { AlertUrgentRegular, CheckmarkCircleRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { ErrorState, LoadingState, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { usePermission } from '@/lib/permissions';
import {
  acknowledgeAlertDrill,
  alertDrillQueries,
  drillSteps,
  startAlertDrill,
  type AlertDrill,
} from './alertDrillsApi';
import { adminQueries } from './api';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '12px' },
  drill: { display: 'flex', flexDirection: 'column', gap: '4px' },
  steps: { display: 'grid', gridTemplateColumns: 'minmax(120px, auto) 1fr', gap: '2px 12px', margin: 0 },
  step: { display: 'contents' },
  when: { margin: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  done: { color: tokens.colorPaletteGreenForeground1 },
  waiting: { color: tokens.colorNeutralForeground2 },
  missed: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
});

const STATE: Record<AlertDrill['state'], string> = {
  open: 'waiting to be acknowledged',
  acknowledged: 'acknowledged',
  expired: 'not acknowledged within an hour',
};

/**
 * Alert drills (audit P2): a test critical warning, told as any is, in the app and to IT on-call by SMS, and how long
 * each step took. Run one at go-live and every quarter, and whenever the on-call number or the SMS provider changes.
 */
export function AlertDrills() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canStart = usePermission('admin.system.manage');
  const drills = useQuery(alertDrillQueries.list());
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: alertDrillQueries.list().queryKey });
    void queryClient.invalidateQueries({ queryKey: adminQueries.health().queryKey });
  };

  const start = useMutation({
    mutationFn: startAlertDrill,
    onSuccess: (drill) => {
      notify.info(
        `Drill ${drill.code} started`,
        'System health shows its test warning. The alerts tell it within five minutes.',
      );
      refresh();
    },
    onError: (error) => notify.error(error, 'The drill did not start'),
  });

  const acknowledge = useMutation({
    mutationFn: acknowledgeAlertDrill,
    onSuccess: (drill) => {
      notify.success(`Drill ${drill.code} acknowledged`, 'Its timings are kept below.');
      refresh();
    },
    onError: (error) => notify.error(error, 'Not acknowledged'),
  });

  if (drills.isLoading) {
    return <LoadingState label="Loading the alert drills" />;
  }
  if (drills.error) {
    return <ErrorState error={drills.error} onRetry={() => void drills.refetch()} />;
  }

  const list = drills.data ?? [];
  const open = list.find((d) => d.state === 'open');
  return (
    <div className={styles.root}>
      <Caption1 className={styles.muted}>
        A drill raises a test critical warning. The alerts tell it as they tell any: in the app to everyone who sees
        System health, and to IT on-call by SMS. Acknowledge it when it reaches you.
      </Caption1>
      {open ? (
        <Button
          appearance="primary"
          icon={<CheckmarkCircleRegular />}
          disabled={acknowledge.isPending}
          onClick={() => acknowledge.mutate(open.id)}
        >
          Acknowledge drill {open.code}
        </Button>
      ) : (
        canStart && (
          <Button icon={<AlertUrgentRegular />} disabled={start.isPending} onClick={() => start.mutate()}>
            {start.isPending ? 'Starting…' : 'Start a drill'}
          </Button>
        )
      )}
      {list.length === 0 && <Text>No drill yet.</Text>}
      {list.map((drill) => (
        <div key={drill.id} className={styles.drill}>
          <Text weight="semibold">
            Drill {drill.code}, {formatDateTime(drill.startedAt)}
            {drill.startedBy ? ` by ${drill.startedBy}` : ''}: {STATE[drill.state]}
          </Text>
          <dl className={styles.steps}>
            {drillSteps(drill).map((step) => (
              <div key={step.label} className={styles.step}>
                <dt className={styles.muted}>{step.label}</dt>
                <dd className={mergeClasses(styles.when, styles[step.tone])}>{step.when}</dd>
              </div>
            ))}
          </dl>
        </div>
      ))}
    </div>
  );
}
