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
  tokens,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, todayIso } from '@hc360/core';
import {
  applyServerErrors,
  CloseRequestScope,
  DateField,
  FormGrid,
  LookupField,
  SelectField,
  SwitchField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from '@tanstack/react-router';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { searchPractitioners } from '@/lib/lookups';
import { emrQueries } from '@/modules/emr/api';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';
import { mrdApi, mrdKeys } from './api';
import { chartableEncounters, encounterOptionLabel } from './newChart';

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const chartSchema = z.object({
  patient: z.custom<PickedPatient | null>((patient) => patient !== null, 'Find the patient.'),
  encounterId: z.custom<string | null>(
    (id) => typeof id === 'string' && id.length > 0,
    'Choose the encounter the chart is for.',
  ),
  assignedPractitionerId: z.string().nullable(),
  dueDate: z.string().nullable(),
  isMlc: z.boolean(),
});

type ChartValues = z.infer<typeof chartSchema>;

/**
 * Opens a chart for an out-patient encounter (MRD-03): admissions get theirs automatically, but MRD also tracks the
 * file of an OP, emergency or day-care visit it keeps (a medico-legal case, a procedure done as an OP). The server
 * refuses a second chart for the same encounter and says so.
 */
export function NewChartDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const closing = useCloseRequests({ onClose });
  return (
    <Dialog open={open} onOpenChange={closing.onOpenChange}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <CloseRequestScope value={closing}>{open && <NewChartForm onClose={onClose} />}</CloseRequestScope>
      </DialogSurface>
    </Dialog>
  );
}

function NewChartForm({ onClose }: { onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const form = useForm<ChartValues>({
    resolver: zodResolver(chartSchema),
    defaultValues: { patient: null, encounterId: null, assignedPractitionerId: null, dueDate: null, isMlc: false },
  });
  const patient = useWatch({ control: form.control, name: 'patient' });
  const encounters = useQuery({ ...emrQueries.encounters(patient?.id ?? ''), enabled: Boolean(patient) });
  const choices = chartableEncounters(encounters.data?.items ?? []);

  const save = useMutation({
    mutationFn: (v: ChartValues) => {
      const encounter = choices.find((e) => e.id === v.encounterId);
      return mrdApi.createRecord({
        patientId: v.patient!.id,
        encounterId: v.encounterId!,
        admissionId: null,
        visitId: null,
        assignedPractitionerId: v.assignedPractitionerId ?? encounter?.practitionerId ?? null,
        dueDate: v.dueDate,
        isMlc: v.isMlc,
      });
    },
    onSuccess: (chart) => {
      void queryClient.invalidateQueries({ queryKey: mrdKeys.all });
      notify.success('Chart opened', 'Add its deficiencies, or scan it to find them.');
      // Saved: the chart is opened without asking about leaving unsaved changes.
      guard.close();
      void navigate({ to: '/mrd/records/$recordId', params: { recordId: chart.id } });
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'chart.exists') {
        form.setError('encounterId', { message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not open the chart');
      }
    },
  });
  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate>
      <DialogBody>
        <DialogTitle>New chart</DialogTitle>
        <DialogContent className={styles.form}>
          <Caption1 className={styles.muted}>
            In-patient charts are opened automatically at admission. Open one here for an out-patient, emergency or
            check-up encounter whose file MRD keeps.
          </Caption1>
          <Controller
            control={form.control}
            name="patient"
            render={({ field, fieldState }) => (
              <PatientPicker
                value={field.value}
                onChange={(picked) => {
                  field.onChange(picked);
                  form.setValue('encounterId', null);
                }}
                required
                autoFocus
                validationMessage={fieldState.error?.message}
              />
            )}
          />
          {patient &&
            (encounters.isLoading ? (
              <Spinner size="tiny" label="Finding the patient's encounters…" />
            ) : encounters.error ? (
              <MessageBar intent="error">
                <MessageBarBody>
                  {isApiError(encounters.error) ? encounters.error.userMessage : 'Could not list the encounters.'}
                </MessageBarBody>
              </MessageBar>
            ) : choices.length === 0 ? (
              <MessageBar intent="info">
                <MessageBarBody>
                  This patient has no out-patient encounter. A chart is opened for an encounter or an admission.
                </MessageBarBody>
              </MessageBar>
            ) : (
              <SelectField
                control={form.control}
                name="encounterId"
                label="Encounter"
                required
                placeholder="Choose the encounter"
                options={choices.map((encounter) => ({
                  value: encounter.id,
                  label: encounterOptionLabel(encounter),
                }))}
              />
            ))}
          <FormGrid columns={2}>
            <LookupField
              control={form.control}
              name="assignedPractitionerId"
              label="Responsible doctor"
              hint="The encounter's doctor when left empty"
              load={searchPractitioners}
            />
            <DateField control={form.control} name="dueDate" label="Complete by" min={todayIso()} />
          </FormGrid>
          <SwitchField control={form.control} name="isMlc" label="Medico-legal case (MLC)" />
        </DialogContent>
        <DialogActions>
          <Button onClick={guard.requestClose} disabled={save.isPending}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            disabled={save.isPending}
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
          >
            Open chart
          </Button>
        </DialogActions>
      </DialogBody>
      {guard.dialogs}
    </form>
  );
}
