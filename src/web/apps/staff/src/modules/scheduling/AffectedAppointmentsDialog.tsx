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
import { CalendarArrowRightRegular, DismissCircleRegular } from '@fluentui/react-icons';
import { formatDateTime } from '@hc360/core';
import { StatusBadge, useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { usePermission } from '@/lib/permissions';
import { schedulingQueries, type AffectedAppointment, type AppointmentDetail, type AppointmentStatus } from './api';
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
 * session), as returned by the server. The desk moves or cancels each one here; nothing is changed automatically.
 */
export function AffectedAppointmentsDialog({ affected, onClose }: { affected: AffectedAppointment[] | null; onClose: () => void }) {
  const open = affected !== null && affected.length > 0;
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 820, width: '100%' }}>
        {open && <AffectedContent key={affected.map((a) => a.id).join(',')} affected={affected} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function AffectedContent({ affected, onClose }: { affected: AffectedAppointment[]; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const canManage = usePermission('scheduling.appointments.manage');
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
        notify.info('Already handled', `This appointment is now ${appointmentStatusText[detail.status].label.toLowerCase()}.`);
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
            These bookings fall outside the doctor's sessions after this change. Call the patients, then move or cancel each one; the patient gets an SMS/WhatsApp.
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
                      {canManage && (
                        <TableCell>
                          {isOpen(status) && (
                            <div className={styles.actions}>
                              {loadingId === a.id ? (
                                <Spinner size="tiny" />
                              ) : (
                                <>
                                  <Button size="small" icon={<CalendarArrowRightRegular />} onClick={() => void start('reschedule', a.id)}>
                                    Reschedule
                                  </Button>
                                  <Button size="small" appearance="subtle" icon={<DismissCircleRegular />} onClick={() => void start('cancel', a.id)}>
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
          {!canManage && (
            <MessageBar intent="warning" style={{ marginTop: 8 }}>
              <MessageBarBody>Ask the front office to move these appointments.</MessageBarBody>
            </MessageBar>
          )}
        </DialogContent>
        <DialogActions>
          <Caption1 className={styles.muted}>{pending === 0 ? 'All handled.' : `${pending} still to handle`}</Caption1>
          <Button appearance="primary" onClick={onClose}>
            {pending === 0 ? 'Done' : 'Handle later'}
          </Button>
        </DialogActions>
      </DialogBody>
      <RescheduleDialog appointment={dialog?.kind === 'reschedule' ? dialog.appointment : null} onClose={() => void finish()} />
      <CancelAppointmentDialog appointment={dialog?.kind === 'cancel' ? dialog.appointment : null} onClose={() => void finish()} />
    </>
  );
}
