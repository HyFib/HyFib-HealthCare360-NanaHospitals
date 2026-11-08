import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Spinner,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CalendarArrowRightRegular, DismissCircleRegular, SendRegular } from '@fluentui/react-icons';
import { formatDateTime, formatTime } from '@hc360/core';
import { StatusBadge, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import {
  schedulingApi,
  schedulingKeys,
  schedulingQueries,
  type AffectedAppointment,
  type AppointmentDetail,
  type AppointmentStatus,
  type StrandedPatient,
} from './api';
import { isOpen } from './appointmentRules';
import { CancelAppointmentDialog } from './CancelAppointmentDialog';
import { appointmentStatusText } from './labels';
import { RescheduleDialog } from './RescheduleDialog';

const useStyles = makeStyles({
  list: { maxHeight: '50vh', overflowY: 'auto' },
  muted: { color: tokens.colorNeutralForeground3 },
  actions: { display: 'flex', gap: '4px', justifyContent: 'flex-end' },
});

/**
 * Appointments a schedule change leaves without a doctor (inside new leave/blocked time, or outside an edited
 * session), as returned by the server. The desk moves or cancels each one here; nothing is changed automatically. For
 * a leave or block (<paramref name="exceptionId"/>) the desk can also tell every patient at once, by SMS and WhatsApp,
 * that the doctor is not available, with a link to choose another time; the list then says who was told and how.
 */
export function AffectedAppointmentsDialog({
  affected,
  exceptionId = null,
  onClose,
}: {
  affected: AffectedAppointment[] | null;
  exceptionId?: string | null;
  onClose: () => void;
}) {
  const open = affected !== null && affected.length > 0;
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 820, width: '100%' }}>
        {open && (
          <AffectedContent
            key={affected.map((a) => a.id).join(',')}
            affected={affected}
            exceptionId={exceptionId}
            onClose={onClose}
          />
        )}
      </DialogSurface>
    </Dialog>
  );
}

function AffectedContent({
  affected,
  exceptionId,
  onClose,
}: {
  affected: AffectedAppointment[];
  exceptionId: string | null;
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('scheduling.appointments.manage');
  const canTell = canManage && exceptionId !== null;
  const stranded = useQuery({ ...schedulingQueries.stranded(exceptionId ?? ''), enabled: canTell });
  const told = new Map((stranded.data?.patients ?? []).map((p) => [p.appointmentId, p]));
  const untold = (stranded.data?.patients ?? []).filter((p) => !p.toldAt && !p.notToldReason).length;
  const tell = useMutation({
    mutationFn: () => schedulingApi.notifyStranded(exceptionId ?? ''),
    onSuccess: (result) => {
      queryClient.setQueryData(schedulingKeys.stranded(result.exceptionId), result);
      const count = result.patients.filter((p) => p.toldAt).length;
      notify.success(
        'Patients told',
        `${count === 1 ? '1 patient was' : `${count} patients were`} told the doctor is not available, with a link to choose another time.`,
      );
    },
    onError: (error) => notify.error(error, 'Could not tell the patients'),
  });
  const [statuses, setStatuses] = useState<Record<string, AppointmentStatus>>({});
  const [loadingId, setLoadingId] = useState<string | null>(null);
  const [dialog, setDialog] = useState<{ kind: 'reschedule' | 'cancel'; appointment: AppointmentDetail } | null>(null);

  const start = async (kind: 'reschedule' | 'cancel', id: string) => {
    setLoadingId(id);
    try {
      const detail = await queryClient.fetchQuery({ ...schedulingQueries.appointment(id), staleTime: 0 });
      setStatuses((s) => ({ ...s, [id]: detail.status }));
      if (isOpen(detail.status)) {
        setDialog({ kind, appointment: detail });
      } else {
        notify.info(
          'Already handled',
          `This appointment is now ${appointmentStatusText[detail.status].label.toLowerCase()}.`,
        );
      }
    } catch (error) {
      notify.error(error, 'Could not open the appointment');
    } finally {
      setLoadingId(null);
    }
  };

  const finish = async () => {
    const id = dialog?.appointment.id;
    setDialog(null);
    if (id) {
      try {
        const detail = await queryClient.fetchQuery({ ...schedulingQueries.appointment(id), staleTime: 0 });
        setStatuses((s) => ({ ...s, [id]: detail.status }));
      } catch {
        // The row keeps its previous status; the desk can refresh the appointment list.
      }
    }
  };

  const pending = affected.filter((a) => isOpen(statuses[a.id] ?? a.status)).length;

  return (
    <>
      <DialogBody>
        <DialogTitle>
          {affected.length === 1 ? '1 appointment needs a new time' : `${affected.length} appointments need a new time`}
          <Caption1 block className={styles.muted}>
            These bookings fall outside the doctor's sessions after this change. Call the patients, then move or cancel
            each one; the patient gets an SMS/WhatsApp.
          </Caption1>
        </DialogTitle>
        <DialogContent>
          <div className={styles.list}>
            <Table size="small" aria-label="Appointments affected by the change">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>When</TableHeaderCell>
                  <TableHeaderCell>Patient</TableHeaderCell>
                  <TableHeaderCell>Status</TableHeaderCell>
                  {canTell && <TableHeaderCell>Told</TableHeaderCell>}
                  {canManage && <TableHeaderCell aria-label="Actions" />}
                </TableRow>
              </TableHeader>
              <TableBody>
                {affected.map((a) => {
                  const status = statuses[a.id] ?? a.status;
                  const text = appointmentStatusText[status];
                  return (
                    <TableRow key={a.id}>
                      <TableCell>
                        <Text>{formatDateTime(a.startsAt)}</Text>
                        <Caption1 block className={styles.muted}>
                          {a.number}
                        </Caption1>
                      </TableCell>
                      <TableCell>
                        <Text>{a.patientName}</Text>
                        {a.phoneMasked && (
                          <Caption1 block className={styles.muted}>
                            {a.phoneMasked}
                          </Caption1>
                        )}
                      </TableCell>
                      <TableCell>
                        <StatusBadge status={status} tone={text.tone} label={text.label} />
                      </TableCell>
                      {canTell && (
                        <TableCell>
                          <ToldCell row={told.get(a.id)} />
                        </TableCell>
                      )}
                      {canManage && (
                        <TableCell>
                          {isOpen(status) && (
                            <div className={styles.actions}>
                              {loadingId === a.id ? (
                                <Spinner size="tiny" aria-label="Loading…" />
                              ) : (
                                <>
                                  <Button
                                    size="small"
                                    icon={<CalendarArrowRightRegular />}
                                    onClick={() => void start('reschedule', a.id)}
                                  >
                                    Reschedule
                                  </Button>
                                  <Button
                                    size="small"
                                    appearance="subtle"
                                    icon={<DismissCircleRegular />}
                                    onClick={() => void start('cancel', a.id)}
                                  >
                                    Cancel
                                  </Button>
                                </>
                              )}
                            </div>
                          )}
                        </TableCell>
                      )}
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
          {canTell && stranded.data && !stranded.data.smsReady && stranded.data.smsNote && (
            <MessageBar intent="warning" layout="multiline" style={{ marginTop: 8 }}>
              <MessageBarBody>{stranded.data.smsNote}</MessageBarBody>
            </MessageBar>
          )}
          {!canManage && (
            <MessageBar intent="warning" style={{ marginTop: 8 }}>
              <MessageBarBody>Ask the front office to move these appointments.</MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Caption1 className={styles.muted}>{pending === 0 ? 'All handled.' : `${pending} still to handle`}</Caption1>
          {canTell && (
            <Button
              icon={<SendRegular />}
              disabled={tell.isPending || stranded.isLoading || untold === 0}
              onClick={() => tell.mutate()}
            >
              {untold === 0 && stranded.data ? 'Patients told' : 'Tell the patients by SMS/WhatsApp'}
            </Button>
          )}
          <Button appearance="primary" onClick={onClose}>
            {pending === 0 ? 'Done' : 'Handle later'}
          </Button>
        </DialogActions>
      </DialogBody>
      <RescheduleDialog
        appointment={dialog?.kind === 'reschedule' ? dialog.appointment : null}
        onClose={() => void finish()}
      />
      <CancelAppointmentDialog
        appointment={dialog?.kind === 'cancel' ? dialog.appointment : null}
        onClose={() => void finish()}
      />
    </>
  );
}

const CHANNEL: Record<string, string> = { Sms: 'SMS', WhatsApp: 'WhatsApp', Email: 'e-mail' };

/** "Told 10:42 · SMS, WhatsApp", what went wrong on a channel, or why the patient cannot be told by message. */
function ToldCell({ row }: { row: StrandedPatient | undefined }) {
  const styles = useStyles();
  if (!row) {
    return <Caption1 className={styles.muted}>—</Caption1>;
  }
  if (row.notToldReason) {
    return <Caption1>{row.notToldReason}</Caption1>;
  }
  if (!row.toldAt) {
    return <Caption1 className={styles.muted}>Not told yet</Caption1>;
  }
  const problems = row.notices.filter(
    (n) => n.status === 'Failed' || n.status === 'Skipped' || n.status === 'Suppressed',
  );
  return (
    <>
      <Text>
        Told {formatTime(row.toldAt)} · {row.notices.map((n) => CHANNEL[n.channel] ?? n.channel).join(', ')}
      </Text>
      {problems.map((n) => (
        <Caption1 key={n.channel} block className={styles.muted}>
          {CHANNEL[n.channel] ?? n.channel}: {n.status.toLowerCase()}
          {n.error ? ` (${n.error})` : ''}
        </Caption1>
      ))}
    </>
  );
}
