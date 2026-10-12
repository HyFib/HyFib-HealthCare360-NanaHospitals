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
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, PersonRegular, PrintRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatTime, isApiError, useSession } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  FormGrid,
  SelectField,
  SwitchField,
  TextAreaField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { ignoreEscapeWhile } from '@/lib/dialogFocus';
import { enumOptions } from '@/lib/lookups';
import { printTokenSlip } from '@/lib/print';
import { patientKeys } from '@/modules/patients/api';
import { schedulingApi, schedulingKeys, type AppointmentRef, type CheckInResult } from './api';

const PAYER_TYPES = ['Self', 'Insurance', 'Corporate', 'GovernmentScheme', 'Staff', 'Charity'] as const;

const schema = z.object({
  payerType: z.enum(PAYER_TYPES),
  priority: z.enum(['Normal', 'Priority', 'Emergency']),
  chiefComplaint: z.string().trim().max(500, 'Use at most 500 characters.'),
  issueToken: z.boolean(),
});

type CheckInValues = z.infer<typeof schema>;

const useStyles = makeStyles({
  done: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '8px',
    padding: '12px 0',
    textAlign: 'center',
  },
  token: {
    fontSize: '56px',
    lineHeight: '64px',
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorBrandForeground1,
    letterSpacing: '2px',
  },
  muted: { color: tokens.colorNeutralForeground3 },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
});

/**
 * Checks a booked patient in on the day (PMS-03): creates the OP visit and queue token through the Patients module.
 * The defaults suit most patients, so the usual path is "Check in" → Enter → print the token.
 */
export function CheckInAppointmentDialog({
  appointment,
  onClose,
}: {
  appointment: AppointmentRef | null;
  onClose: () => void;
}) {
  return (
    <Dialog open={appointment !== null} onOpenChange={(_, data) => !data.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        {appointment && <CheckInContent key={appointment.id} appointment={appointment} onClose={onClose} />}
      </DialogSurface>
    </Dialog>
  );
}

function CheckInContent({ appointment, onClose }: { appointment: AppointmentRef; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const [result, setResult] = useState<{ checkIn: CheckInResult; at: string } | null>(null);

  const form = useForm<CheckInValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      payerType: 'Self',
      priority: 'Normal',
      chiefComplaint: appointment.reason ?? '',
      issueToken: !appointment.isTelehealth,
    },
  });

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: schedulingKeys.all });
    void queryClient.invalidateQueries({ queryKey: ['visits'] });
    void queryClient.invalidateQueries({ queryKey: ['front-office'] });
    void queryClient.invalidateQueries({ queryKey: patientKeys.all });
  };

  const checkIn = useMutation({
    mutationFn: (values: CheckInValues) =>
      schedulingApi.checkIn(appointment.id, {
        payerType: values.payerType,
        payerId: null,
        priority: values.priority,
        chiefComplaint: values.chiefComplaint || null,
        issueToken: values.issueToken && !appointment.isTelehealth,
      }),
    onSuccess: (checkedIn) => {
      setResult({ checkIn: checkedIn, at: new Date().toISOString() });
      refresh();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'appointment.already_checked_in') {
        notify.info('Already checked in', 'This patient was checked in from another desk.');
        refresh();
        onClose();
        return;
      }
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not check the patient in');
      }
    },
  });

  if (result) {
    const { checkIn: done, at } = result;
    const print = () => {
      if (!done.tokenLabel) {
        return;
      }
      void printTokenSlip({
        facilityName,
        tokenLabel: done.tokenLabel,
        department: appointment.departmentName ?? '',
        doctor: appointment.practitionerName,
        patientName: appointment.patientName,
        uhid: appointment.uhid,
        visitNumber: done.visitNumber,
        issuedAt: at,
      }).catch((error: unknown) => notify.error(error, 'Printing failed'));
    };

    return (
      <DialogBody>
        <DialogTitle>Checked in</DialogTitle>
        <DialogContent>
          <div className={styles.done} role="status">
            <CheckmarkCircleRegular fontSize={40} color={tokens.colorPaletteGreenForeground1} />
            <Text>
              {appointment.patientName} · {appointment.uhid}
            </Text>
            {done.tokenLabel ? (
              <>
                <Caption1 className={styles.muted}>Token number</Caption1>
                <span className={styles.token}>{done.tokenLabel}</span>
                <Text>{[appointment.departmentName, appointment.practitionerName].filter(Boolean).join(' · ')}</Text>
              </>
            ) : (
              <Text>
                {appointment.isTelehealth
                  ? 'Visit created for the video consultation. The doctor admits the patient from the waiting room.'
                  : 'Visit created.'}
              </Text>
            )}
            <Caption1 className={styles.muted}>Visit number {done.visitNumber}</Caption1>
            {done.linkedExistingVisit && (
              <MessageBar intent="info">
                <MessageBarBody>
                  The patient was already registered as a walk-in today, so the appointment was linked to that visit.
                </MessageBarBody>
              </MessageBar>
            )}
          </div>
        </DialogContent>
        <DialogActions>
          <Button
            icon={<PersonRegular />}
            onClick={() => void navigate({ to: '/patients/$patientId', params: { patientId: appointment.patientId } })}
          >
            Open patient
          </Button>
          {done.tokenLabel && (
            <Button icon={<PrintRegular />} onClick={print}>
              Print token slip
            </Button>
          )}
          <Button appearance="primary" onClick={onClose} autoFocus>
            Done
          </Button>
        </DialogActions>
      </DialogBody>
    );
  }

  return (
    <form
      onSubmit={form.handleSubmit((values) => !checkIn.isPending && checkIn.mutate(values))}
      onKeyDown={ignoreEscapeWhile(checkIn.isPending)}
      noValidate
    >
      <DialogBody>
        <DialogTitle>
          Check in {appointment.patientName}
          <Caption1 block className={styles.muted}>
            {formatTime(appointment.startsAt)} with {appointment.practitionerName} · UHID {appointment.uhid}
          </Caption1>
        </DialogTitle>
        <DialogContent>
          <div className={styles.form}>
            <FormGrid columns={2}>
              <SelectField
                control={form.control}
                name="payerType"
                label="Payment by"
                options={enumOptions(PAYER_TYPES, {
                  Self: 'Self (cash / card / UPI)',
                  GovernmentScheme: 'Government scheme',
                })}
              />
            </FormGrid>
            <ChoiceField
              control={form.control}
              name="priority"
              label="Queue priority"
              hint="Priority: senior citizens, pregnant women, differently-abled"
              options={[
                { value: 'Normal', label: 'Normal' },
                { value: 'Priority', label: 'Priority' },
                { value: 'Emergency', label: 'Emergency' },
              ]}
            />
            <TextAreaField
              control={form.control}
              name="chiefComplaint"
              label="Reason for visit"
              rows={2}
              maxLength={500}
            />
            {!appointment.isTelehealth && (
              <SwitchField control={form.control} name="issueToken" label="Issue a queue token" />
            )}
          </div>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={checkIn.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            autoFocus
            disabled={checkIn.isPending}
            // Keeps focus while checking in (a disabled button drops it); Enter can still submit, which is ignored.
            disabledFocusable={checkIn.isPending}
            icon={checkIn.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Check in
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
