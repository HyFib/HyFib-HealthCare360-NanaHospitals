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
  ChoiceField,
  CloseRequestScope,
  DateField,
  FormGrid,
  LookupField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
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

const chartSchema = z
  .object({
    patient: z.custom<PickedPatient | null>((patient) => patient !== null, 'Find the patient.'),
    /** A chart of an encounter in the record, or of a file that exists only on paper. */
    kind: z.enum(['encounter', 'paper']),
    encounterId: z.string().nullable(),
    fileNumber: z.string().trim().max(40, 'Use at most 40 characters.'),
    episode: z.enum(['OutPatient', 'InPatient']),
    episodeFrom: z.string().nullable(),
    episodeTo: z.string().nullable(),
    note: z.string().trim().max(300, 'Use at most 300 characters.'),
    assignedPractitionerId: z.string().nullable(),
    dueDate: z.string().nullable(),
    isMlc: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (v.kind === 'encounter') {
      if (!v.encounterId) {
        ctx.addIssue({ code: 'custom', path: ['encounterId'], message: 'Choose the encounter the chart is for.' });
      }
      return;
    }
    if (!v.fileNumber) {
      ctx.addIssue({ code: 'custom', path: ['fileNumber'], message: 'Enter the number on the file.' });
    } else if (!/^[A-Za-z0-9][A-Za-z0-9/.-]*$/.test(v.fileNumber)) {
      ctx.addIssue({ code: 'custom', path: ['fileNumber'], message: 'Use letters, digits, / . and -.' });
    }
    if (!v.episodeFrom) {
      ctx.addIssue({
        code: 'custom',
        path: ['episodeFrom'],
        message: v.episode === 'InPatient' ? 'Enter the admission day.' : 'Enter the visit day.',
      });
    } else if (v.episode === 'InPatient' && v.episodeTo && v.episodeTo < v.episodeFrom) {
      ctx.addIssue({ code: 'custom', path: ['episodeTo'], message: 'The discharge day is on or after admission.' });
    }
    // A stay's records are kept from discharge.
    if (v.episode === 'InPatient' && !v.episodeTo) {
      ctx.addIssue({ code: 'custom', path: ['episodeTo'], message: 'Enter the discharge day.' });
    }
  });

type ChartValues = z.infer<typeof chartSchema>;

/**
 * Opens a chart for an out-patient encounter (MRD-03): admissions get theirs automatically, but MRD also tracks the
 * file of an OP, emergency or day-care visit it keeps (a medico-legal case, a procedure done as an OP). Or a chart of
 * a file that exists only on paper (an episode from before the system, or written by hand while it was down): its
 * number, its episode's days and what it holds; it is issued, returned and archived like any other. The server refuses
 * a second chart for the same encounter or paper file and says so.
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
    defaultValues: {
      patient: null,
      kind: 'encounter',
      encounterId: null,
      fileNumber: '',
      episode: 'OutPatient',
      episodeFrom: null,
      episodeTo: null,
      note: '',
      assignedPractitionerId: null,
      dueDate: null,
      isMlc: false,
    },
  });
  const patient = useWatch({ control: form.control, name: 'patient' });
  const kind = useWatch({ control: form.control, name: 'kind' });
  const episode = useWatch({ control: form.control, name: 'episode' });
  const encounters = useQuery({ ...emrQueries.encounters(patient?.id ?? ''), enabled: Boolean(patient) });
  const choices = chartableEncounters(encounters.data?.items ?? []);

  const save = useMutation({
    mutationFn: (v: ChartValues) => {
      if (v.kind === 'paper') {
        return mrdApi.createRecord({
          patientId: v.patient!.id,
          encounterId: null,
          admissionId: null,
          visitId: null,
          assignedPractitionerId: v.assignedPractitionerId,
          dueDate: v.dueDate,
          isMlc: v.isMlc,
          paper: {
            fileNumber: v.fileNumber,
            isInpatient: v.episode === 'InPatient',
            episodeFrom: v.episodeFrom!,
            episodeTo: v.episode === 'InPatient' ? v.episodeTo : null,
            note: v.note || null,
          },
        });
      }
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
      notify.success(
        'Chart opened',
        chart.paper ? 'Add the deficiencies you find in the file.' : 'Add its deficiencies, or scan it to find them.',
      );
      // Saved: the chart is opened without asking about leaving unsaved changes.
      guard.close();
      void navigate({ to: '/mrd/records/$recordId', params: { recordId: chart.id } });
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'chart.exists') {
        form.setError('encounterId', { message: error.userMessage });
      } else if (isApiError(error) && error.code === 'chart.paper_exists') {
        form.setError('fileNumber', { message: error.userMessage });
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
            check-up encounter whose file MRD keeps, or for a file that exists only on paper.
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
          <ChoiceField
            control={form.control}
            name="kind"
            label="Chart of"
            options={[
              { value: 'encounter', label: 'An encounter in the record' },
              { value: 'paper', label: 'A paper file' },
            ]}
          />
          {kind === 'paper' && (
            <>
              <FormGrid columns={2}>
                <TextField
                  control={form.control}
                  name="fileNumber"
                  label="File number"
                  hint="As written on the file (old IP or MRD number)"
                  required
                />
                <ChoiceField
                  control={form.control}
                  name="episode"
                  label="Episode"
                  options={[
                    { value: 'OutPatient', label: 'Out-patient' },
                    { value: 'InPatient', label: 'In-patient stay' },
                  ]}
                />
                <DateField
                  control={form.control}
                  name="episodeFrom"
                  label={episode === 'InPatient' ? 'Admitted on' : 'Visit day'}
                  required
                  max={todayIso()}
                />
                {episode === 'InPatient' && (
                  <DateField control={form.control} name="episodeTo" label="Discharged on" required max={todayIso()} />
                )}
              </FormGrid>
              <TextAreaField
                control={form.control}
                name="note"
                label="What the file holds"
                hint="Volumes, department, condition of the file"
              />
              <Caption1 className={styles.muted}>
                The retention period counts from the episode. A paper chart has no record to scan: raise its
                deficiencies by hand.
              </Caption1>
            </>
          )}
          {kind === 'encounter' &&
            patient &&
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
              hint={kind === 'encounter' ? "The encounter's doctor when left empty" : undefined}
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
