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
import {
  careChangeAfterSignIn,
  keepsAnaesthesiaConsent,
  moveEndsWaiver,
  onCaseTeam,
  planGate,
  procedureWithSide,
  siteChangeVoidsChecklist,
  slotConflicts,
  surgeonChangeEndsWaiver,
} from './logic';
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
              {moveEndsWaiver(booking) && (
                <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Consent was waived in an emergency</MessageBarTitle>
                    Moving the operation to another theatre or start ends the waiver: the patient&apos;s consent is
                    taken for the new slot, or the operating surgeon waives it again with a second doctor.
                  </MessageBarBody>
                </MessageBar>
              )}
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
  const [procedureServiceItemId, procedureName, laterality, siteLevel, anaesthesiaType, priority, surgeonId] = useWatch(
    {
      control: form.control,
      name: [
        'procedureServiceItemId',
        'procedureName',
        'laterality',
        'siteLevel',
        'anaesthesiaType',
        'priority',
        'surgeonId',
      ],
    },
  );
  // After the WHO Sign-in a change of side, level or procedure voids it (and the Time-out): only the operating surgeon or
  // the anaesthetist saves it, with a reason (server SiteChangeRule).
  const voidsChecklist = siteChangeVoidsChecklist(booking, {
    procedureServiceItemId,
    procedureName,
    laterality,
    siteLevel,
  });
  // So is a change of anaesthesia or priority, which bears on the patient's consent; the checklist stands
  // (server TechniqueOrPriorityChangeRule).
  const careChange = careChangeAfterSignIn(booking, { anaesthesiaType, priority });
  const asksReason = voidsChecklist || careChange;
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
            if (asksReason && !values.changeReason.trim()) {
              form.setError('changeReason', {
                message: voidsChecklist
                  ? 'Give the reason for changing the side or procedure after the Sign-in.'
                  : 'Give the reason for changing the anaesthesia or the priority after the Sign-in.',
              });
              return;
            }
            save.mutate(asksReason ? values : { ...values, changeReason: '' });
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
              {careChange && (
                <MessageBar intent={mayChangeSite ? 'warning' : 'error'} layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Anaesthesia or priority changed after the Sign-in</MessageBarTitle>
                    Both bear on the patient&apos;s consent: local anaesthesia needs no consent to anaesthesia, and only
                    an emergency goes ahead without consent.
                    {keepsAnaesthesiaConsent(booking, { anaesthesiaType })
                      ? ' The consent to anaesthesia, needed at the Sign-in, stays needed.'
                      : ''}
                    {mayChangeSite
                      ? ' Give the reason; it is recorded with your name.'
                      : ' Only the operating surgeon or the anaesthetist of this case changes them now, from their own sign-in.'}
                  </MessageBarBody>
                </MessageBar>
              )}
              {surgeonChangeEndsWaiver(booking, surgeonId) && (
                <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Consent was waived in an emergency</MessageBarTitle>
                    Another operating surgeon ends the waiver: it was the first surgeon&apos;s judgement. The
                    patient&apos;s consent is taken, or the new surgeon waives it again with a second doctor.
                  </MessageBarBody>
                </MessageBar>
              )}
              {asksReason && mayChangeSite && (
                <TextField
                  control={form.control}
                  name="changeReason"
                  label="Reason for the change"
                  required
                  maxLength={500}
                  hint={
                    voidsChecklist
                      ? 'For example: consent form and imaging say the right side.'
                      : 'For example: spinal failed twice, converting to general anaesthesia.'
                  }
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
                disabled={save.isPending || !gate.allowed || (asksReason && !mayChangeSite)}
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
