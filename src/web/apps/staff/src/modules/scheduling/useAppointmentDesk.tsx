import { formatTime, useSession } from '@hc360/core';
import { ConfirmDialog, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { usePermission } from '@/lib/permissions';
import { schedulingApi, schedulingKeys, type AppointmentRef } from './api';
import type { ActionContext, AppointmentAction } from './appointmentRules';
import { CancelAppointmentDialog } from './CancelAppointmentDialog';
import { CheckInAppointmentDialog } from './CheckInAppointmentDialog';
import { printAppointmentSlip } from './print';
import { RescheduleDialog } from './RescheduleDialog';
import { useNow, useToday } from './useNow';

export type DeskAction = AppointmentAction | 'print';
export type RunAction = (action: DeskAction, appointment: AppointmentRef) => void;

type DialogKind = 'checkIn' | 'cancel' | 'reschedule' | 'noShow';

export interface AppointmentDesk {
  run: RunAction;
  /** Dialogs for check-in, cancel, reschedule and no-show; render once on the screen. */
  dialogs: ReactNode;
  context: ActionContext;
  canManage: boolean;
}

/**
 * Appointment actions shared by the desk list, the calendar, the booking screens and the detail drawer, so every
 * screen checks in, confirms, reschedules and cancels the same way (and with the same confirmations).
 */
export function useAppointmentDesk(): AppointmentDesk {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const canManage = usePermission('scheduling.appointments.manage');
  const now = useNow();
  const today = useToday();
  const [dialog, setDialog] = useState<{ kind: DialogKind; appointment: AppointmentRef } | null>(null);
  const close = () => setDialog(null);

  const refresh = () => void queryClient.invalidateQueries({ queryKey: schedulingKeys.all });

  const confirm = useMutation({
    mutationFn: (appointment: AppointmentRef) => schedulingApi.confirm(appointment.id),
    onSuccess: (confirmed) => {
      notify.success('Appointment confirmed', `${confirmed.patientName} · ${formatTime(confirmed.startsAt)}`);
      refresh();
    },
    onError: (error) => {
      notify.error(error, 'Could not confirm the appointment');
      refresh();
    },
  });

  const noShow = useMutation({
    mutationFn: (appointment: AppointmentRef) => schedulingApi.noShow(appointment.id),
    onSuccess: () => {
      notify.success('Marked as no-show');
      setDialog(null);
      refresh();
    },
    onError: (error) => {
      notify.error(error, 'Could not record the no-show');
      refresh();
    },
  });

  const run: RunAction = (action, appointment) => {
    switch (action) {
      case 'confirm':
        confirm.mutate(appointment);
        break;
      case 'print':
        void printAppointmentSlip(facilityName, appointment).catch((error: unknown) => notify.error(error, 'Printing failed'));
        break;
      default:
        setDialog({ kind: action, appointment });
    }
  };

  const target = dialog?.appointment ?? null;
  const dialogs = (
    <>
      <CheckInAppointmentDialog appointment={dialog?.kind === 'checkIn' ? target : null} onClose={close} />
      <CancelAppointmentDialog appointment={dialog?.kind === 'cancel' ? target : null} onClose={close} />
      <RescheduleDialog appointment={dialog?.kind === 'reschedule' ? target : null} onClose={close} />
      <ConfirmDialog
        open={dialog?.kind === 'noShow'}
        title="Mark as no-show?"
        confirmLabel="Mark as no-show"
        destructive
        busy={noShow.isPending}
        onConfirm={() => target && noShow.mutate(target)}
        onCancel={close}
      >
        {target
          ? `${target.patientName} did not come for the ${formatTime(target.startsAt)} appointment with ${target.practitionerName}. It will be recorded as a no-show.`
          : null}
      </ConfirmDialog>
    </>
  );

  return { run, dialogs, context: { today, now, canManage }, canManage };
}
