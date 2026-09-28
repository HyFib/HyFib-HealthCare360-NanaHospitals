import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, makeStyles, Spinner } from '@fluentui/react-components';
import { PlayRegular } from '@fluentui/react-icons';
import { useSession } from '@hc360/core';
import { FormGrid, LookupField, SelectField, TextAreaField } from '@hc360/ui';
import { useQuery } from '@tanstack/react-query';
import { useForm, useWatch } from 'react-hook-form';
import { searchPractitioners, toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { ENCOUNTER_TYPES, emrQueries, encounterTypeLabel, type EncounterType } from './api';
import { useStartEncounter } from './useStartEncounter';

const useStyles = makeStyles({ form: { display: 'flex', flexDirection: 'column', gap: '12px' } });

interface StartValues {
  type: EncounterType;
  departmentId: string | null;
  practitionerId: string | null;
  templateId: string | null;
  admissionId: string | null;
  chiefComplaint: string;
}

/** Starts an encounter from the chart (walk-in, ward round, teleconsultation). From the worklist it is one click. */
export function StartEncounterDialog({ patientId, open, onClose }: { patientId: string; open: boolean; onClose: () => void }) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>{open && <StartEncounterForm patientId={patientId} onClose={onClose} />}</DialogSurface>
    </Dialog>
  );
}

function StartEncounterForm({ patientId, onClose }: { patientId: string; onClose: () => void }) {
  const styles = useStyles();
  const hasPractitioner = useSession((s) => Boolean(s.user?.practitionerId));
  const canSeeAdmissions = usePermission('inpatient.admissions.read');
  const start = useStartEncounter();
  const form = useForm<StartValues>({
    defaultValues: { type: 'OutPatient', departmentId: null, practitionerId: null, templateId: null, admissionId: null, chiefComplaint: '' },
  });
  const type = useWatch({ control: form.control, name: 'type' });
  const departments = useDepartments('Clinical');
  const templates = useQuery(emrQueries.templates());
  const admissions = useQuery({ ...emrQueries.admissions(patientId), enabled: canSeeAdmissions && type === 'InPatient' });
  const activeAdmissions = (admissions.data ?? []).filter((a) => a.status === 'Admitted' || a.status === 'DischargeInitiated');

  const submit = form.handleSubmit((values) => {
    if (!hasPractitioner && !values.practitionerId) {
      form.setError('practitionerId', { message: 'Choose the treating doctor.' });
      return;
    }
    if (values.type === 'InPatient' && !values.admissionId) {
      form.setError('admissionId', { message: 'Choose the admission.' });
      return;
    }
    start.mutate(
      {
        patientId,
        type: values.type,
        departmentId: values.departmentId,
        practitionerId: values.practitionerId,
        templateId: values.templateId,
        admissionId: values.type === 'InPatient' ? values.admissionId : null,
        chiefComplaint: values.chiefComplaint.trim() || null,
      },
      { onSuccess: onClose },
    );
  });

  return (
    <form onSubmit={submit} noValidate>
      <DialogBody>
        <DialogTitle>Start encounter</DialogTitle>
        <DialogContent className={styles.form}>
          <FormGrid columns={2}>
            <SelectField control={form.control} name="type" label="Type" options={ENCOUNTER_TYPES.map((t) => ({ value: t, label: encounterTypeLabel[t] }))} />
            <SelectField control={form.control} name="departmentId" label="Department" placeholder="My department" options={toOptions(departments.data)} />
            {type === 'InPatient' && (
              <SelectField
                control={form.control}
                name="admissionId"
                label="Admission"
                required
                placeholder={canSeeAdmissions ? (activeAdmissions.length === 0 ? 'No current admission' : 'Choose') : 'You cannot view admissions'}
                options={activeAdmissions.map((a) => ({ value: a.id, label: `${a.admissionNumber}${a.wardName ? ` · ${a.wardName}` : ''}` }))}
              />
            )}
            {!hasPractitioner && <LookupField control={form.control} name="practitionerId" label="Treating doctor" required load={searchPractitioners} />}
            <SelectField
              control={form.control}
              name="templateId"
              label="Template"
              placeholder="No template"
              options={(templates.data ?? []).map((t) => ({ value: t.id, label: `${t.name}${t.specialtyCode ? ` (${t.specialtyCode})` : ''}` }))}
            />
          </FormGrid>
          <TextAreaField control={form.control} name="chiefComplaint" label="Chief complaint" rows={2} maxLength={1000} placeholder="Optional; the visit's reason is used when blank" />
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={start.isPending}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" icon={start.isPending ? <Spinner size="tiny" /> : <PlayRegular />} disabled={start.isPending}>
            Start
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
