import { Button, makeStyles, MessageBar, MessageBarBody, MessageBarTitle, Spinner } from '@fluentui/react-components';
import { DropRegular, WarningRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { useHotkey } from '@hc360/core';
import {
  ChoiceField,
  FormGrid,
  FullRow,
  LoadingState,
  NumberField,
  Page,
  PageHeader,
  Section,
  SelectField,
  TextAreaField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { usePermission } from '@/lib/permissions';
import { bloodApi, bloodKeys, COMPONENT_TYPES, URGENCIES } from '@/modules/blood-bank/api';
import { componentLabel, urgencyLabel } from '@/modules/blood-bank/labels';
import { fromIstInput } from '@/modules/ot/shared/datetime';
import { applyFormErrors } from '@/modules/ot/shared/errors';
import { ActionError, NoAccess } from '@/modules/ot/shared/feedback';
import { DateTimeField } from '@/modules/ot/shared/fields';
import { patientQueries } from '@/modules/patients/api';
import { PatientPicker, type PickedPatient } from '@/modules/patients/PatientPicker';

interface NewRequestSearch {
  patientId?: string;
}

export const Route = createFileRoute('/_app/blood-bank/requests/new')({
  validateSearch: (s: Record<string, unknown>): NewRequestSearch => ({
    patientId: typeof s.patientId === 'string' && s.patientId ? s.patientId : undefined,
  }),
  component: NewBloodRequestPage,
});

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  footer: { display: 'flex', justifyContent: 'flex-end', gap: '8px' },
});

const schema = z
  .object({
    patient: z.object({ id: z.string(), uhid: z.string(), fullName: z.string() }).nullable(),
    componentType: z.enum(COMPONENT_TYPES),
    units: z.number().nullable(),
    urgency: z.enum(URGENCIES),
    indication: z.string().trim().min(1, 'State the clinical indication for transfusion.').max(1000),
    requiredBy: z.string().nullable(),
    haemoglobinGdl: z.number().nullable(),
    plateletCount: z.number().nullable(),
  })
  .superRefine((v, ctx) => {
    if (!v.patient) {
      ctx.addIssue({ code: 'custom', path: ['patient'], message: 'Choose the patient.' });
    }
    if (v.units === null || v.units < 1 || v.units > 20) {
      ctx.addIssue({ code: 'custom', path: ['units'], message: 'Request 1 to 20 units.' });
    }
    if (v.requiredBy && !fromIstInput(v.requiredBy)) {
      ctx.addIssue({ code: 'custom', path: ['requiredBy'], message: 'Enter a valid date and time.' });
    }
    if (v.haemoglobinGdl !== null && (v.haemoglobinGdl < 1 || v.haemoglobinGdl > 25)) {
      ctx.addIssue({ code: 'custom', path: ['haemoglobinGdl'], message: 'Haemoglobin between 1 and 25 g/dL.' });
    }
    if (v.plateletCount !== null && (v.plateletCount < 0 || v.plateletCount > 2_000_000)) {
      ctx.addIssue({
        code: 'custom',
        path: ['plateletCount'],
        message: 'Platelet count between 0 and 20,00,000 per µL.',
      });
    }
  });

type Values = z.infer<typeof schema>;

function NewBloodRequestPage() {
  const search = Route.useSearch();
  const canRequest = usePermission('bloodbank.requests.create');
  const preset = useQuery({
    ...patientQueries.detail(search.patientId ?? ''),
    enabled: Boolean(search.patientId) && canRequest,
  });
  if (!canRequest) {
    return (
      <Page>
        <NoAccess what="blood requests" />
      </Page>
    );
  }
  if (search.patientId && preset.isLoading) {
    return <LoadingState label="Loading the patient…" />;
  }
  const patient: PickedPatient | null = preset.data
    ? { id: preset.data.id, uhid: preset.data.uhid, fullName: preset.data.fullName }
    : null;
  return <RequestForm patient={patient} />;
}

/** Blood request from the ward or OT: component, units, urgency and the indication (kept for transfusion audits). */
function RequestForm({ patient }: { patient: PickedPatient | null }) {
  const styles = useStyles();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      patient,
      componentType: 'PRBC',
      units: 1,
      urgency: 'Routine',
      indication: '',
      requiredBy: null,
      haemoglobinGdl: null,
      plateletCount: null,
    },
  });
  const [urgency, componentType] = useWatch({ control: form.control, name: ['urgency', 'componentType'] });

  const create = useMutation({
    mutationFn: (v: Values) =>
      bloodApi.createRequest({
        patientId: v.patient?.id ?? '',
        admissionId: null,
        visitId: null,
        componentType: v.componentType,
        units: v.units ?? 1,
        urgency: v.urgency,
        indication: v.indication,
        requiredBy: fromIstInput(v.requiredBy),
        haemoglobinGdl: v.haemoglobinGdl,
        plateletCount: v.plateletCount,
      }),
    onSuccess: (created, v) => {
      notify.success(
        `Blood request ${created.requestNumber} sent`,
        v.urgency === 'Emergency'
          ? 'The blood bank has been alerted.'
          : 'Send the patient’s sample for grouping and cross-match.',
      );
      void queryClient.invalidateQueries({ queryKey: bloodKeys.all });
      void navigate({ to: '/blood-bank/requests/$id', params: { id: created.id } });
    },
    onError: (error) => {
      applyFormErrors(error, form.setError);
    },
  });

  const submit = form.handleSubmit((v) => create.mutate(v));
  useHotkey('mod+s', () => void submit(), { allowInInputs: true, enabled: !create.isPending });

  return (
    <Page maxWidth={1000}>
      <PageHeader
        title="Request blood"
        subtitle="The patient’s sample is grouped and cross-matched by the blood bank. Ctrl+S sends the request."
        icon={<DropRegular />}
        breadcrumbs={[
          { label: 'Blood requests', onClick: () => void navigate({ to: '/blood-bank/requests' }) },
          { label: 'New request' },
        ]}
      />
      <form onSubmit={(event) => void submit(event)} noValidate className={styles.form}>
        <Section title="Patient">
          <Controller
            control={form.control}
            name="patient"
            render={({ field, fieldState }) => (
              <PatientPicker
                value={field.value}
                onChange={field.onChange}
                required
                validationMessage={fieldState.error?.message}
              />
            )}
          />
        </Section>
        <Section title="What is needed">
          <FormGrid columns={3}>
            <SelectField
              control={form.control}
              name="componentType"
              label="Component"
              options={COMPONENT_TYPES.map((c) => ({ value: c, label: componentLabel[c] }))}
            />
            <NumberField control={form.control} name="units" label="Units" required min={1} max={20} />
            <DateTimeField
              control={form.control}
              name="requiredBy"
              label="Needed by"
              hint="Leave blank if as soon as ready."
            />
            <FullRow>
              <ChoiceField
                control={form.control}
                name="urgency"
                label="Urgency"
                options={URGENCIES.map((u) => ({ value: u, label: urgencyLabel[u] }))}
              />
            </FullRow>
          </FormGrid>
          {urgency === 'Emergency' && (
            <MessageBar intent="error" layout="multiline" icon={<WarningRegular />} style={{ marginTop: 12 }}>
              <MessageBarBody>
                <MessageBarTitle>Emergency request</MessageBarTitle>
                The blood bank is alerted at once. If waiting for a cross-match would endanger life, a doctor can
                authorise release of uncross-matched O Rh(D)-negative red cells. Send the sample immediately anyway.
              </MessageBarBody>
            </MessageBar>
          )}
        </Section>
        <Section title="Clinical details" description="Kept with the request for transfusion audits.">
          <FormGrid columns={2}>
            <FullRow>
              <TextAreaField
                control={form.control}
                name="indication"
                label="Indication"
                required
                rows={3}
                maxLength={1000}
                placeholder="e.g. Symptomatic anaemia, Hb 6.2 g/dL, planned for surgery tomorrow"
              />
            </FullRow>
            <NumberField
              control={form.control}
              name="haemoglobinGdl"
              label="Latest haemoglobin (g/dL)"
              min={1}
              max={25}
              step={0.1}
              hint={componentType === 'PRBC' || componentType === 'WholeBlood' ? 'Before transfusion.' : undefined}
            />
            <NumberField
              control={form.control}
              name="plateletCount"
              label="Latest platelet count (per µL)"
              min={0}
              max={2000000}
              step={1000}
              hint={componentType === 'PlateletConcentrate' ? 'Before transfusion.' : undefined}
            />
          </FormGrid>
        </Section>
        <ActionError error={create.error} />
        <div className={styles.footer}>
          <Button appearance="subtle" onClick={() => void navigate({ to: '/blood-bank/requests' })}>
            Cancel
          </Button>
          <Button
            type="submit"
            appearance="primary"
            size="large"
            disabled={create.isPending}
            icon={create.isPending ? <Spinner size="tiny" /> : <DropRegular />}
          >
            Send request
          </Button>
        </div>
      </form>
    </Page>
  );
}
