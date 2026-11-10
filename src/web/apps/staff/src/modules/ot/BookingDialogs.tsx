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
import { todayIso, useSession } from '@hc360/core';
import { TextField, useNotify, useUnsavedChanges } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm, useWatch } from 'react-hook-form';
import { givenBySurgeon, otApi, otKeys, type SurgeryDetail } from './api';
import { phaseLabel } from './labels';
import { onCaseTeam, planGate, procedureWithSide, siteChangeVoidsChecklist, slotConflicts } from './logic';
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
    : !givenBySurgeon(booking.anaesthesiaType) && !booking.anaesthetistId
      ? 'No anaesthetist is chosen (needed for every technique except local and topical anaesthesia).'
      : null;

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: schedule.isPending, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <form onSubmit={form.handleSubmit((values) => !schedule.isPending && schedule.mutate(values))} noValidate>
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
              <Button onClick={guard.requestClose} disabled={schedule.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={schedule.isPending}
                // Keeps focus while busy (a disabled button drops it); Enter can still submit, which is ignored.
                disabledFocusable={schedule.isPending}
                icon={schedule.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                {moving ? 'Move surgery' : 'Schedule'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
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

  const practitionerId = useSession((s) => s.user?.practitionerId ?? null);
  const [procedureServiceItemId, procedureName, laterality, siteLevel] = useWatch({
    control: form.control,
    name: ['procedureServiceItemId', 'procedureName', 'laterality', 'siteLevel'],
  });
  // After the WHO Sign-in a change of side, level or procedure voids it (and the Time-out): only the operating surgeon or
  // the anaesthetist saves it, with a reason (server SiteChangeRule).
  const voidsChecklist = siteChangeVoidsChecklist(booking, {
    procedureServiceItemId,
    procedureName,
    laterality,
    siteLevel,
  });
  const mayChangeSite = onCaseTeam(booking, practitionerId);
  const voidedPhases = booking.safetyChecklist
    .filter((p) => p.phase !== 'SignOut' && p.isComplete)
    .map((p) => phaseLabel[p.phase]);

  const save = useMutation({
    mutationFn: (values: SurgeryFormValues) => otApi.updatePlan(booking.id, toUpdateRequest(values, booking.version)),
    onSuccess: () => {
      notify.success(voidsChecklist ? 'Surgical plan saved: do the WHO checklist again' : 'Surgical plan saved');
      void queryClient.invalidateQueries({ queryKey: otKeys.all });
      onClose();
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  const conflicts = slotConflicts(save.error);
  const gate = planGate(booking);

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 820 }}>
        <form
          onSubmit={form.handleSubmit((values) => {
            if (voidsChecklist && !values.changeReason.trim()) {
              form.setError('changeReason', {
                message: 'Give the reason for changing the side or procedure after the Sign-in.',
              });
              return;
            }
            save.mutate(voidsChecklist ? values : { ...values, changeReason: '' });
          })}
          noValidate
        >
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
                nurseName={booking.nurseName}
                showPreferredDate={!holdsSlot}
              />
              {voidsChecklist && (
                <MessageBar intent={mayChangeSite ? 'warning' : 'error'} layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>The WHO checklist will be done again</MessageBarTitle>
                    The {voidedPhases.join(' and ')} {voidedPhases.length === 1 ? 'is' : 'are'} done. Changing the side,
                    level or procedure now voids {voidedPhases.length === 1 ? 'it' : 'them'}: the surgeon marks the site
                    again and the team repeats {voidedPhases.length === 1 ? 'it' : 'them'} before the incision. The
                    theatre team is told.
                    {mayChangeSite
                      ? ' Give the reason; it is recorded with your name.'
                      : ' Only the operating surgeon or the anaesthetist of this case changes it now, from their own sign-in.'}
                  </MessageBarBody>
                </MessageBar>
              )}
              {voidsChecklist && mayChangeSite && (
                <TextField
                  control={form.control}
                  name="changeReason"
                  label="Reason for the change"
                  required
                  maxLength={500}
                  hint="For example: consent form and imaging say the right side."
                />
              )}
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
              <Button onClick={guard.requestClose} disabled={save.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={save.isPending || !gate.allowed || (voidsChecklist && !mayChangeSite)}
                icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                {voidsChecklist ? 'Save and redo the checklist' : 'Save plan'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}
