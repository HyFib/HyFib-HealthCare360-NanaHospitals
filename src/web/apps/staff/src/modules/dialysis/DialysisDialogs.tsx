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
  Input,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { PersonAddRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { todayIso } from '@hc360/core';
import { ChoiceField, DateField, FormGrid, LoadingState, useNotify, useUnsavedChanges } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { applyFormErrors, isConcurrencyConflict } from '@/modules/ot/shared/errors';
import { ActionError } from '@/modules/ot/shared/feedback';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import { dialysisApi, dialysisKeys, dialysisQueries, DIALYSIS_SHIFTS, type DialysisShift } from './api';
import { MachinePicker, SeroBadge } from './DialysisBadges';
import { DISINFECTION_METHODS, seroLabel, shiftLabel } from './labels';
import { machineChoices, suggestMachine } from './logic';

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  methods: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
});

const scheduleSchema = z
  .object({
    patient: z.object({ id: z.string(), uhid: z.string(), fullName: z.string() }).nullable(),
    date: z.string().nullable(),
    shift: z.enum(DIALYSIS_SHIFTS),
    machineId: z.string().nullable(),
  })
  .superRefine((v, ctx) => {
    if (!v.patient) {
      ctx.addIssue({ code: 'custom', path: ['patient'], message: 'Choose the patient.' });
    }
    if (!v.date) {
      ctx.addIssue({ code: 'custom', path: ['date'], message: 'Choose the date.' });
    } else if (v.date < todayIso()) {
      ctx.addIssue({ code: 'custom', path: ['date'], message: 'Sessions cannot be scheduled in the past.' });
    }
  });

type ScheduleValues = z.infer<typeof scheduleSchema>;

/**
 * Books a dialysis session for an enrolled patient. The machine is optional here; when chosen, only machines of
 * the patient's sero category can be picked — the others are shown disabled with the reason.
 */
export function ScheduleSessionDialog({
  onClose,
  preset,
}: {
  onClose: () => void;
  preset?: { patient?: PickedPatient; date?: string; shift?: DialysisShift };
}) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const form = useForm<ScheduleValues>({
    resolver: zodResolver(scheduleSchema),
    defaultValues: {
      patient: preset?.patient ?? null,
      date: preset?.date ?? todayIso(),
      shift: preset?.shift ?? 'Morning',
      machineId: null,
    },
  });
  const [patient, date, shift, machineId] = useWatch({
    control: form.control,
    name: ['patient', 'date', 'shift', 'machineId'],
  });
  const profile = useQuery({ ...dialysisQueries.profile(patient?.id ?? ''), enabled: Boolean(patient) });
  const board = useQuery({ ...dialysisQueries.board(date ?? todayIso()), enabled: Boolean(date) });

  const sero = profile.data?.seroStatus ?? null;
  const choices = machineChoices(board.data, sero, shift, 'schedule');
  const suggestion = sero
    ? suggestMachine(board.data?.machines.map((m) => ({ ...m, isActive: true })) ?? [], sero, shift, board.data)
    : null;
  const chosen = choices.find((c) => c.id === machineId);

  const schedule = useMutation({
    mutationFn: (values: ScheduleValues) =>
      dialysisApi.schedule({
        patientId: values.patient?.id ?? '',
        scheduledDate: values.date ?? todayIso(),
        shift: values.shift,
        machineId: values.machineId,
        visitId: null,
        admissionId: null,
      }),
    onSuccess: (created) => {
      notify.success('Dialysis session booked');
      void queryClient.invalidateQueries({ queryKey: dialysisKeys.all });
      onClose();
      void navigate({ to: '/dialysis/sessions/$id', params: { id: created.id } });
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  const notEnrolled = patient && profile.isSuccess && profile.data === null;

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: schedule.isPending, onClose });

  return (
    <Dialog open onOpenChange={guard.onOpenChange}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <form onSubmit={form.handleSubmit((values) => schedule.mutate(values))} noValidate>
          <DialogBody>
            <DialogTitle>Book a dialysis session</DialogTitle>
            <DialogContent className={styles.body}>
              <Controller
                control={form.control}
                name="patient"
                render={({ field, fieldState }) => (
                  <PatientPicker
                    value={field.value}
                    onChange={field.onChange}
                    required
                    validationMessage={fieldState.error?.message}
                    hint="Only patients enrolled in the dialysis unit can be booked."
                  />
                )}
              />
              {patient && profile.isLoading && (
                <Spinner size="tiny" label="Checking enrolment…" labelPosition="after" />
              )}
              {notEnrolled && (
                <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>
                    <MessageBarTitle>Not enrolled in the dialysis unit</MessageBarTitle>
                    Enrol the patient with their dry weight, access and sero status first. The sero status decides which
                    machines they may use.
                  </MessageBarBody>
                  <MessageBarActions>
                    <Button
                      icon={<PersonAddRegular />}
                      onClick={() =>
                        patient &&
                        void navigate({ to: '/dialysis/patients/$patientId', params: { patientId: patient.id } })
                      }
                    >
                      Enrol patient
                    </Button>
                  </MessageBarActions>
                </MessageBar>
              )}
              {profile.data && (
                <div className={styles.row}>
                  <SeroBadge sero={profile.data.seroStatus} long />
                  <Caption1 className={styles.muted}>
                    Dry weight {profile.data.dryWeightKg} kg · usual shift {shiftLabel[profile.data.shift]}
                    {profile.data.seroRescreeningDue ? ' · sero re-screening is due' : ''}
                  </Caption1>
                </div>
              )}
              <FormGrid columns={2}>
                <DateField control={form.control} name="date" label="Date" required min={todayIso()} />
                <div>
                  <ChoiceField
                    control={form.control}
                    name="shift"
                    label="Shift"
                    required
                    options={DIALYSIS_SHIFTS.map((s) => ({ value: s, label: shiftLabel[s] }))}
                  />
                  {profile.data && profile.data.shift !== shift && (
                    <Button
                      size="small"
                      appearance="subtle"
                      onClick={() => profile.data && form.setValue('shift', profile.data.shift, { shouldDirty: true })}
                    >
                      Use the usual shift ({shiftLabel[profile.data.shift]})
                    </Button>
                  )}
                </div>
              </FormGrid>
              {profile.data && (
                <Field
                  label="Machine (optional now)"
                  hint={
                    suggestion
                      ? `Suggested: ${suggestion.code}. Only ${seroLabel[profile.data.seroStatus].toLowerCase()} machines can be chosen.`
                      : `No free ${seroLabel[profile.data.seroStatus].toLowerCase()} machine in this shift. Book without a machine and assign one later.`
                  }
                >
                  {board.isLoading ? (
                    <LoadingState label="Loading machines…" />
                  ) : (
                    <Controller
                      control={form.control}
                      name="machineId"
                      render={({ field }) => (
                        <MachinePicker choices={choices} value={field.value} onChange={field.onChange} />
                      )}
                    />
                  )}
                </Field>
              )}
              {machineId && chosen && (
                <div className={styles.row}>
                  <Text>
                    Machine <Text weight="semibold">{chosen.code}</Text> chosen.
                  </Text>
                  <Button size="small" appearance="subtle" onClick={() => form.setValue('machineId', null)}>
                    Clear
                  </Button>
                </div>
              )}
              <ActionError error={schedule.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={guard.requestClose} disabled={schedule.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={schedule.isPending || Boolean(notEnrolled)}
                icon={schedule.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Book session
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
        {guard.dialogs}
      </DialogSurface>
    </Dialog>
  );
}

/** Gives a scheduled session its machine; sero-incompatible or booked machines are disabled with the reason. */
export function AssignMachineDialog({
  session,
  onClose,
}: {
  session: {
    id: string;
    patientId: string;
    patientName: string;
    scheduledDate: string;
    shift: DialysisShift;
    machineId: string | null;
    version: number;
  };
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const profile = useQuery(dialysisQueries.profile(session.patientId));
  const board = useQuery(dialysisQueries.board(session.scheduledDate));
  const [machineId, setMachineId] = useState<string | null>(session.machineId);
  // The session as the dialog opened: the session page reads it again in the background, and a session changed
  // meanwhile must be refused, not overwritten (REL-04).
  const [loadedVersion] = useState(session.version);
  const sero = profile.data?.seroStatus ?? null;
  const choices = machineChoices(board.data, sero, session.shift, 'assign', session.id);

  const assign = useMutation({
    mutationFn: () => dialysisApi.assignMachine(session.id, machineId ?? '', loadedVersion),
    onSuccess: () => {
      notify.success('Machine assigned');
      void queryClient.invalidateQueries({ queryKey: dialysisKeys.all });
      onClose();
    },
    onError: (error) => {
      if (isConcurrencyConflict(error)) {
        // Opened again, the dialog shows the session as it is now.
        void queryClient.invalidateQueries({ queryKey: dialysisKeys.all });
      }
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <DialogBody>
          <DialogTitle>
            Assign a machine
            <Caption1 block className={styles.muted}>
              {session.patientName} · {shiftLabel[session.shift]} shift
            </Caption1>
          </DialogTitle>
          <DialogContent className={styles.body}>
            {profile.isLoading || board.isLoading ? (
              <LoadingState label="Checking machines…" />
            ) : (
              <>
                {sero ? (
                  <div className={styles.row}>
                    <Text>Patient:</Text>
                    <SeroBadge sero={sero} long />
                    <Caption1 className={styles.muted}>Only machines dedicated to this category can be used.</Caption1>
                  </div>
                ) : (
                  <MessageBar intent="warning">
                    <MessageBarBody>
                      The patient&apos;s dialysis enrolment (with sero status) was not found.
                    </MessageBarBody>
                  </MessageBar>
                )}
                <MachinePicker choices={choices} value={machineId} onChange={setMachineId} />
              </>
            )}
            <ActionError error={assign.error} />
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose} disabled={assign.isPending}>
              Cancel
            </Button>
            <Button
              appearance="primary"
              disabled={!machineId || machineId === session.machineId || assign.isPending}
              icon={assign.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              onClick={() => assign.mutate()}
            >
              Assign machine
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** Records the disinfection that must follow every session before the machine takes the next patient. */
export function DisinfectionDialog({
  machine,
  onClose,
}: {
  machine: { id: string; code: string };
  onClose: () => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [method, setMethod] = useState<string>(DISINFECTION_METHODS[0]);
  const record = useMutation({
    mutationFn: () => dialysisApi.recordDisinfection(machine.id, method.trim()),
    onSuccess: () => {
      notify.success(`Disinfection of ${machine.code} recorded`);
      void queryClient.invalidateQueries({ queryKey: dialysisKeys.all });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (method.trim()) {
              record.mutate();
            }
          }}
          noValidate
        >
          <DialogBody>
            <DialogTitle>Record disinfection of {machine.code}</DialogTitle>
            <DialogContent className={styles.body}>
              <Text>
                Disinfect after every session, as per the machine manual. The machine cannot take the next patient until
                this is recorded.
              </Text>
              <div className={styles.methods} role="group" aria-label="Common methods">
                {DISINFECTION_METHODS.map((m) => (
                  <Button
                    key={m}
                    size="small"
                    appearance={method === m ? 'primary' : 'secondary'}
                    onClick={() => setMethod(m)}
                  >
                    {m}
                  </Button>
                ))}
              </div>
              <Field label="Method" required>
                <Input value={method} onChange={(_, d) => setMethod(d.value)} maxLength={100} />
              </Field>
              <ActionError error={record.error} />
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={record.isPending}>
                Cancel
              </Button>
              <Button
                type="submit"
                appearance="primary"
                disabled={!method.trim() || record.isPending}
                icon={record.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
              >
                Record disinfection
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
