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
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatDateTime } from '@hc360/core';
import { applyServerErrors, SelectField, SwitchField, TextAreaField, useNotify } from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { CANCELLATION_REASONS, schedulingApi, schedulingKeys, type AppointmentRef } from './api';
import { cancellationReasonLabel } from './labels';

const schema = z
  .object({
    reasonCode: z.enum(CANCELLATION_REASONS),
    reason: z.string().trim().max(300, 'Use at most 300 characters.'),
    notifyPatient: z.boolean(),
  })
  .superRefine((values, ctx) => {
    if (values.reasonCode === 'Other' && !values.reason) {
      ctx.addIssue({ code: 'custom', path: ['reason'], message: 'Describe why the appointment is cancelled.' });
    }
  });

type CancelValues = z.infer<typeof schema>;

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  destructive: {
    backgroundColor: tokens.colorPaletteRedBackground3,
    ':hover': { backgroundColor: tokens.colorPaletteRedForeground1 },
  },
});

/** Cancels an open appointment with a coded reason (reported in MIS); the freed slot is offered to the waitlist. */
export function CancelAppointmentDialog({
  appointment,
  onClose,
}: {
  appointment: AppointmentRef | null;
  onClose: () => void;
}) {
  return (
    <Dialog open={appointment !== null} onOpenChange={(_, data) => !data.open && onClose()} modalType="alert">
      <DialogSurface style={{ maxWidth: 520 }}>
        {appointment && <CancelContent key={appointment.id} appointment={appointment} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function CancelContent({ appointment, onClose }: { appointment: AppointmentRef; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<CancelValues>({
    resolver: zodResolver(schema),
    defaultValues: { reasonCode: 'PatientRequest', reason: '', notifyPatient: true },
  });
  const reasonCode = useWatch({ control: form.control, name: 'reasonCode' });

  const cancel = useMutation({
    mutationFn: (values: CancelValues) =>
      schedulingApi.cancel(appointment.id, {
        reasonCode: values.reasonCode,
        reason: values.reason || null,
        notifyPatient: values.notifyPatient,
      }),
    onSuccess: () => {
      notify.success('Appointment cancelled', 'The freed slot is offered to patients on the waitlist.');
      void queryClient.invalidateQueries({ queryKey: schedulingKeys.all });
      onClose();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not cancel the appointment');
        void queryClient.invalidateQueries({ queryKey: schedulingKeys.all });
      }
    },
  });

  return (
    <form onSubmit={form.handleSubmit((values) => cancel.mutate(values))} noValidate>
      <DialogBody>
        <DialogTitle>
          Cancel this appointment?
          <Caption1 block className={styles.muted}>
            {appointment.patientName} · {formatDateTime(appointment.startsAt)} with {appointment.practitionerName}
          </Caption1>
        </DialogTitle>
        <DialogContent>
          <div className={styles.form}>
            <SelectField
              control={form.control}
              name="reasonCode"
              label="Reason"
              required
              options={CANCELLATION_REASONS.map((code) => ({ value: code, label: cancellationReasonLabel[code] }))}
            />
            <TextAreaField
              control={form.control}
              name="reason"
              label={reasonCode === 'Other' ? 'Describe the reason' : 'Details (optional)'}
              required={reasonCode === 'Other'}
              rows={2}
              maxLength={300}
            />
            <SwitchField control={form.control} name="notifyPatient" label="Tell the patient by SMS/WhatsApp" />
          </div>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={cancel.isPending}>
            Keep appointment
          </Button>
          <Button
            type="submit"
            appearance="primary"
            className={styles.destructive}
            disabled={cancel.isPending}
            icon={cancel.isPending ? <Spinner size="tiny" /> : undefined}
          >
            Cancel appointment
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
