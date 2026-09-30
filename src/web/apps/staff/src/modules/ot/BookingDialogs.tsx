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
  MessageBarTitle,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { todayIso } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { otApi, otKeys, type SurgeryDetail } from './api';
import { planGate, procedureWithSide, slotConflicts } from './logic';
import { ConflictAlert } from './OtBadges';
import { applyFormErrors } from './shared/errors';
import { ActionError } from './shared/feedback';
import { PlanFields, SlotFields } from './SurgeryFormFields';
import {
  slotOf,
  surgeryFormFromDetail,
  surgeryFormSchema,
  toUpdateRequest,
  type SurgeryFormValues,
} from './surgeryForm';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

/**
 * Gives a requested or postponed surgery its theatre and slot, or moves a scheduled one. The server re-checks the
 * theatre, surgeon, anaesthetist and patient for double booking under a lock; clashes come back listed.
 */
export function ScheduleDialog({
  booking,
  onClose,
  prefill,
}: {
  booking: SurgeryDetail;
  onClose: () => void;
  prefill?: Partial<SurgeryFormValues>;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const defaults = surgeryFormFromDetail(booking);
  const form = useForm<SurgeryFormValues>({
    resolver: zodResolver(surgeryFormSchema('schedule')),
    defaultValues: { ...defaults, date: defaults.date ?? todayIso(), ...prefill },
  });

  const schedule = useMutation({
    mutationFn: (values: SurgeryFormValues) => {
      const slot = slotOf(values);
      return otApi.schedule(booking.id, {
        theatreId: values.theatreId ?? '',
        scheduledStart: slot?.start ?? '',
        scheduledEnd: slot?.end ?? '',
        version: booking.version,
      });
    },
    onSuccess: () => {
      notify.success(booking.scheduledStart && booking.status === 'Scheduled' ? 'Surgery moved' : 'Surgery scheduled');
      void queryClient.invalidateQueries({ queryKey: otKeys.all });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  const conflicts = slotConflicts(schedule.error);
  const moving = booking.status === 'Scheduled';
  const teamMissing = !booking.surgeonId
    ? 'No operating surgeon is chosen.'
    : booking.anaesthesiaType !== 'Local' && !booking.anaesthetistId
      ? 'No anaesthetist is chosen (needed for every technique except local anaesthesia).'
      : null;

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <form onSubmit={form.handleSubmit((values) => schedule.mutate(values))} noValidate>
          <DialogBody>
            <DialogTitle>
              {moving ? 'Move to another slot' : 'Schedule surgery'}
              <Caption1 block className={styles.muted}>
                {booking.patient?.fullName ?? 'Patient'} ·{' '}
                {procedureWithSide(booking.procedureName, booking.laterality)} · {booking.bookingNumber}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.body}>
              {teamMissing && (
                <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Complete the surgical team first</MessageBarTitle>
                    {teamMissing} A slot is only given once the team is known. Close this and use Edit plan.
                  </MessageBarBody>
                </MessageBar>
              )}
              <SlotFields form={form} bookingId={booking.id} />
              {conflicts ? (
                <ConflictAlert conflicts={conflicts} />
              ) : (
                <ActionError
                  error={schedule.error}
                  onReload={() => void queryClient.invalidateQueries({ queryKey: otKeys.booking(booking.id) })}
                />
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={schedule.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={schedule.isPending}
                icon={schedule.isPending ? <Spinner size="tiny" /> : undefined}
              >
                {moving ? 'Move surgery' : 'Schedule'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}

/** Procedure, side, team, anaesthesia and priority of an existing booking. A booked slot is re-checked for clashes. */
export function PlanDialog({ booking, onClose }: { booking: SurgeryDetail; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const holdsSlot = booking.status === 'Scheduled' || booking.status === 'InPreOp';
  const form = useForm<SurgeryFormValues>({
    resolver: zodResolver(surgeryFormSchema('plan', { requireTeam: holdsSlot })),
    defaultValues: surgeryFormFromDetail(booking),
  });

  const save = useMutation({
    mutationFn: (values: SurgeryFormValues) => otApi.updatePlan(booking.id, toUpdateRequest(values, booking.version)),
    onSuccess: () => {
      notify.success('Surgical plan saved');
      void queryClient.invalidateQueries({ queryKey: otKeys.all });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  const conflicts = slotConflicts(save.error);
  const gate = planGate(booking);

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 820 }}>
        <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate>
          <DialogBody>
            <DialogTitle>
              Edit surgical plan
              <Caption1 block className={styles.muted}>
                {booking.patient?.fullName ?? 'Patient'} · {booking.bookingNumber}
                {holdsSlot ? ' · the booked slot is re-checked for the new team' : ''}
              </Caption1>
            </DialogTitle>
            <DialogContent className={styles.body}>
              <PlanFields
                form={form}
                surgeonName={booking.surgeonName}
                anaesthetistName={booking.anaesthetistName}
                showPreferredDate={!holdsSlot}
              />
              {conflicts ? (
                <ConflictAlert conflicts={conflicts} />
              ) : (
                <ActionError
                  error={save.error}
                  onReload={() => void queryClient.invalidateQueries({ queryKey: otKeys.booking(booking.id) })}
                />
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={save.isPending || !gate.allowed}
                icon={save.isPending ? <Spinner size="tiny" /> : undefined}
              >
                Save plan
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
