import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  makeStyles,
  Spinner,
  Text,
  Textarea,
  tokens,
} from '@fluentui/react-components';
import { AlertUrgentRegular, CheckmarkCircleRegular, EyeRegular, PlugDisconnectedRegular } from '@fluentui/react-icons';
import { formatDateTime, formatRelative } from '@hc360/core';
import { DataTable, StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { rpmApi, rpmKeys, type RpmAlertDto } from './api';
import { alertSeverityText, alertStatusText } from './labels';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  critical: { color: tokens.colorPaletteRedForeground1 },
  actions: { display: 'flex', gap: '4px', justifyContent: 'flex-end' },
});

type AlertAction = 'acknowledge' | 'resolve';

export interface AlertsTableProps {
  alerts: RpmAlertDto[] | undefined;
  loading: boolean;
  error: unknown;
  onRetry: () => void;
  showPatient?: boolean;
  onOpen?: (alert: RpmAlertDto) => void;
  emptyTitle?: string;
  emptyDescription?: string;
  emptyAction?: ReactNode;
  page?: number;
  pageSize?: number;
  totalCount?: number;
  onPageChange?: (page: number) => void;
}

/** The care team's alert queue (RPM-03): critical first, with "seen" and "resolved" tracked by name and time. */
export function AlertsTable(props: AlertsTableProps) {
  const styles = useStyles();
  const canManage = usePermission('rpm.alerts.manage');
  const [acting, setActing] = useState<{ alert: RpmAlertDto; action: AlertAction } | null>(null);

  return (
    <>
      <DataTable
        ariaLabel="Remote monitoring alerts"
        rows={props.alerts}
        loading={props.loading}
        error={props.error}
        onRetry={props.onRetry}
        getRowId={(a) => a.id}
        onRowClick={props.onOpen}
        columns={[
          {
            key: 'severity',
            header: 'Severity',
            width: 110,
            render: (a) => (
              <StatusBadge
                status={a.severity}
                tone={alertSeverityText[a.severity].tone}
                label={alertSeverityText[a.severity].label}
              />
            ),
          },
          ...(props.showPatient === false
            ? []
            : [
                {
                  key: 'patient',
                  header: 'Patient',
                  render: (a: RpmAlertDto) => (
                    <span className={styles.stack}>
                      <Text weight="semibold" truncate>
                        {a.patientName}
                      </Text>
                      <Caption1 className={styles.muted}>{a.uhid}</Caption1>
                    </span>
                  ),
                },
              ]),
          {
            key: 'what',
            header: 'Alert',
            render: (a) => (
              <span className={styles.stack}>
                <Text className={a.severity === 'Critical' ? styles.critical : undefined} weight="semibold">
                  {a.kind === 'DeviceSilence' ? (
                    <PlugDisconnectedRegular aria-hidden style={{ verticalAlign: 'middle', marginRight: 4 }} />
                  ) : null}
                  {a.message}
                </Text>
                {a.occurrences > 1 && (
                  <Caption1 className={styles.muted}>
                    Happened {a.occurrences} times, last {formatRelative(a.lastOccurredAt)}
                  </Caption1>
                )}
              </span>
            ),
          },
          {
            key: 'raised',
            header: 'Raised',
            width: 120,
            priority: 'low',
            render: (a) => <span title={formatDateTime(a.raisedAt)}>{formatRelative(a.raisedAt)}</span>,
          },
          {
            key: 'status',
            header: 'Status',
            width: 140,
            render: (a) => (
              <span className={styles.stack}>
                <StatusBadge
                  status={a.status}
                  tone={alertStatusText[a.status].tone}
                  label={alertStatusText[a.status].label}
                />
                {a.status === 'Acknowledged' && a.acknowledgedAt && (
                  <Caption1 className={styles.muted}>{formatRelative(a.acknowledgedAt)}</Caption1>
                )}
                {a.status === 'Resolved' && a.resolvedAt && (
                  <Caption1 className={styles.muted}>{formatRelative(a.resolvedAt)}</Caption1>
                )}
              </span>
            ),
          },
        ]}
        rowActions={
          canManage
            ? (a) =>
                a.status === 'Resolved' ? null : (
                  <div className={styles.actions}>
                    {a.status === 'Open' && (
                      <Button
                        size="small"
                        icon={<EyeRegular />}
                        onClick={() => setActing({ alert: a, action: 'acknowledge' })}
                      >
                        Seen
                      </Button>
                    )}
                    <Button
                      size="small"
                      appearance={a.status === 'Acknowledged' ? 'primary' : 'subtle'}
                      icon={<CheckmarkCircleRegular />}
                      onClick={() => setActing({ alert: a, action: 'resolve' })}
                    >
                      Resolve
                    </Button>
                  </div>
                )
            : undefined
        }
        emptyTitle={props.emptyTitle ?? 'No alerts'}
        emptyDescription={props.emptyDescription}
        emptyAction={props.emptyAction}
        page={props.page}
        pageSize={props.pageSize}
        totalCount={props.totalCount}
        onPageChange={props.onPageChange}
        size="small"
      />
      <AlertActionDialog acting={acting} onClose={() => setActing(null)} />
    </>
  );
}

function AlertActionDialog({
  acting,
  onClose,
}: {
  acting: { alert: RpmAlertDto; action: AlertAction } | null;
  onClose: () => void;
}) {
  return (
    <Dialog open={acting !== null} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        {acting && <AlertActionContent key={`${acting.alert.id}-${acting.action}`} {...acting} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function AlertActionContent({
  alert,
  action,
  onClose,
}: {
  alert: RpmAlertDto;
  action: AlertAction;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [notes, setNotes] = useState('');
  const act = useMutation({
    mutationFn: () =>
      action === 'acknowledge'
        ? rpmApi.acknowledgeAlert(alert.id, notes.trim() || null)
        : rpmApi.resolveAlert(alert.id, notes.trim() || null),
    onSuccess: () => {
      notify.success(action === 'acknowledge' ? 'Marked as seen' : 'Alert resolved');
      void queryClient.invalidateQueries({ queryKey: rpmKeys.all });
      onClose();
    },
    onError: (error) => {
      notify.error(error, 'Could not update the alert');
      void queryClient.invalidateQueries({ queryKey: rpmKeys.alerts });
    },
  });

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        act.mutate();
      }}
    >
      <DialogBody>
        <DialogTitle>
          {action === 'acknowledge' ? 'Mark this alert as seen?' : 'Resolve this alert?'}
          <Caption1 block className={styles.muted}>
            {alert.patientName} · {alert.uhid}
          </Caption1>
        </DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <Text className={alert.severity === 'Critical' ? styles.critical : undefined} weight="semibold">
            {alert.severity === 'Critical' && (
              <AlertUrgentRegular aria-hidden style={{ verticalAlign: 'middle', marginRight: 4 }} />
            )}
            {alert.message}
          </Text>
          <Caption1 className={styles.muted}>Raised {formatDateTime(alert.raisedAt)}</Caption1>
          <Field
            label={action === 'acknowledge' ? 'Note (optional)' : 'What was done (optional)'}
            hint={
              action === 'resolve'
                ? 'For example: called the patient, medicine adjusted, repeat reading normal.'
                : undefined
            }
          >
            <Textarea
              value={notes}
              onChange={(_, d) => setNotes(d.value.slice(0, 500))}
              rows={3}
              resize="vertical"
              autoFocus
            />
          </Field>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={act.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={act.isPending}
            icon={act.isPending ? <Spinner size="tiny" /> : undefined}
          >
            {action === 'acknowledge' ? 'Mark as seen' : 'Resolve'}
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
