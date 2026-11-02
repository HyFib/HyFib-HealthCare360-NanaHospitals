import { Button, Caption1, makeStyles, mergeClasses, Text, tokens } from '@fluentui/react-components';
import { CheckmarkCircleRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { DataTable, ErrorState, LoadingState, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { alertDrillQueries } from './alertDrillsApi';
import {
  acknowledgeAlert,
  alertQueries,
  alertStatus,
  REPORT_DAYS,
  reportSummary,
  type AlertIncident,
} from './alertIncidentsApi';
import { adminQueries } from './api';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '12px' },
  alert: { display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: '4px' },
  muted: { color: tokens.colorNeutralForeground3 },
  danger: { color: tokens.colorPaletteRedForeground1, fontWeight: tokens.fontWeightSemibold },
  warning: { color: tokens.colorPaletteDarkOrangeForeground1 },
  success: { color: tokens.colorPaletteGreenForeground1 },
  informative: { color: tokens.colorNeutralForeground2 },
});

/**
 * Critical alerts (audit P2): each critical warning stays an alert until someone acknowledges it. It is told at once in
 * the app and to IT on-call, and while nobody acknowledges it, to the second contact and then HyFib support. Below the
 * open ones, how long the alerts of the last month took to be acknowledged.
 */
export function AlertIncidents() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const report = useQuery(alertQueries.report());

  const acknowledge = useMutation({
    mutationFn: acknowledgeAlert,
    onSuccess: () => {
      notify.success('Alert acknowledged', 'Nobody else is paged for it. It stays on System health until it clears.');
      void queryClient.invalidateQueries({ queryKey: ['admin', 'alerts'] });
      void queryClient.invalidateQueries({ queryKey: alertDrillQueries.list().queryKey });
      void queryClient.invalidateQueries({ queryKey: adminQueries.health().queryKey });
    },
    onError: (error) => notify.error(error, 'Not acknowledged'),
  });

  if (report.isLoading) {
    return <LoadingState label="Loading the alerts" />;
  }
  if (report.error || !report.data) {
    return <ErrorState error={report.error} onRetry={() => void report.refetch()} />;
  }

  const open = report.data.incidents.filter((i) => !i.clearedAt);
  const past = report.data.incidents.filter((i) => i.clearedAt);
  const cleared = report.data.total - open.length;
  return (
    <div className={styles.root}>
      <Caption1 className={styles.muted}>
        Each critical warning is an alert until someone acknowledges it. It is told at once, in the app and to IT
        on-call; while nobody acknowledges it, to the second contact and then HyFib support. Acknowledge it when you are
        on it.
      </Caption1>
      {open.length === 0 ? (
        <Text>No open alert.</Text>
      ) : (
        open.map((alert) => (
          <div key={alert.id} className={styles.alert}>
            <Text weight="semibold">{alert.message}</Text>
            <Caption1 className={styles.muted}>
              {alert.code}
              {alert.server ? `, server ${alert.server}` : ''}, since {formatDateTime(alert.raisedAt)}
            </Caption1>
            <Status alert={alert} />
            {!alert.acknowledgedAt && (
              <Button
                appearance="primary"
                icon={<CheckmarkCircleRegular />}
                disabled={acknowledge.isPending}
                aria-label={`Acknowledge the alert: ${alert.message}`}
                onClick={() => acknowledge.mutate(alert.id)}
              >
                Acknowledge
              </Button>
            )}
          </div>
        ))
      )}
      <Text>{reportSummary(report.data)}</Text>
      {past.length > 0 && (
        <DataTable
          ariaLabel={`Alerts of the last ${REPORT_DAYS} days`}
          rows={past}
          getRowId={(a) => a.id}
          columns={[
            {
              key: 'alert',
              header: 'Alert',
              width: 360,
              render: (a) => (
                <span>
                  <Text>{a.message}</Text>
                  <Caption1 block className={styles.muted}>
                    {a.code}
                  </Caption1>
                </span>
              ),
            },
            { key: 'raised', header: 'Raised', width: 180, render: (a) => formatDateTime(a.raisedAt) },
            { key: 'outcome', header: 'Outcome', width: 300, render: (a) => <Status alert={a} /> },
            {
              key: 'cleared',
              header: 'Cleared',
              width: 180,
              priority: 'low',
              render: (a) => (a.clearedAt ? formatDateTime(a.clearedAt) : '—'),
            },
          ]}
          emptyTitle="No alert cleared"
          emptyDescription={`None in the last ${REPORT_DAYS} days.`}
          size="small"
        />
      )}
      {cleared > past.length && (
        <Caption1 className={styles.muted}>
          The latest {past.length} of the {cleared} that cleared are listed; the figures above count them all.
        </Caption1>
      )}
    </div>
  );
}

function Status({ alert }: { alert: AlertIncident }) {
  const styles = useStyles();
  const status = alertStatus(alert);
  return <Text className={mergeClasses(styles[status.tone])}>{status.text}</Text>;
}
