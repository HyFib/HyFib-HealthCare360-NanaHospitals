import {
  Button,
  Caption1,
  Checkbox,
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
import {
  CheckmarkCircleRegular,
  DocumentArrowLeftRegular,
  PersonAddRegular,
  PrintRegular,
  TagRegular,
} from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import {
  formatRelative,
  humanize,
  isApiError,
  isIndianMobile,
  normalizePhone,
  todayIso,
  useHotkey,
  useSession,
  type PayerType,
} from '@hc360/core';
import {
  applyServerErrors,
  DateField,
  FormGrid,
  Page,
  PageHeader,
  Section,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { enumOptions, toOptions, useDepartments, usePractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { useFormDraft } from '@/lib/useFormDraft';
import {
  ADMISSION_SOURCE_LABEL,
  ADMISSION_SOURCES,
  ADMISSION_TYPE_LABEL,
  ADMISSION_TYPES,
  admissionApi,
  inpatientKeys,
  inpatientQueries,
  isActiveAdmission,
  PAYER_LABEL,
  PAYER_TYPES,
  type AdmissionCreated,
  type AvailableBedDto,
} from '@/modules/inpatient/api';
import { BedPicker } from '@/modules/inpatient/BedPicker';
import { useStoredState } from '@/modules/inpatient/hooks';
import { printAdmissionSlip, printWristband } from '@/modules/inpatient/print';
import { fromIstInput, toIstInput } from '@/modules/inpatient/time';
import { patientQueries } from '@/modules/patients/api';
import { PatientPicker } from '@/modules/patients/PatientPicker';

interface AdmitSearch {
  bedId?: string;
  patientId?: string;
}

export const Route = createFileRoute('/_app/inpatient/admit')({
  validateSearch: (s: Record<string, unknown>): AdmitSearch => ({
    bedId: typeof s.bedId === 'string' ? s.bedId : undefined,
    patientId: typeof s.patientId === 'string' ? s.patientId : undefined,
  }),
  component: AdmitPatientPage,
});

const POLICY_PAYERS: PayerType[] = ['Insurance', 'Corporate', 'GovernmentScheme'];

const schema = z
  .object({
    patient: z.object({ id: z.string(), uhid: z.string(), fullName: z.string() }).nullable(),
    type: z.enum(ADMISSION_TYPES),
    source: z.enum(ADMISSION_SOURCES),
    referredBy: z.string().trim().max(200),
    departmentId: z.string().nullable(),
    admittingPractitionerId: z.string().nullable(),
    consultantPractitionerId: z.string().nullable(),
    provisionalDiagnosis: z.string().trim().max(500),
    expectedDischargeDate: z.string().nullable(),
    bedId: z.string().nullable(),
    payerType: z.enum(['Self', 'Insurance', 'Corporate', 'GovernmentScheme', 'Staff', 'Charity']),
    policyNumber: z
      .string()
      .trim()
      .max(64)
      .regex(/^[A-Za-z0-9/ -]*$/, "Use letters, digits, spaces, '/' or '-'."),
    depositNote: z.string().trim().max(300),
    isMlc: z.boolean(),
    mlcNumber: z.string().trim().max(64),
    attendantName: z.string().trim().max(200),
    attendantRelation: z.string().trim().max(64),
    attendantPhone: z.string().trim(),
    earlier: z.boolean(),
    admittedAt: z.string(),
    notes: z.string().trim().max(600),
    notifyPatient: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (!v.patient) {
      ctx.addIssue({ code: 'custom', path: ['patient'], message: 'Find the patient by UHID, mobile or name.' });
    }
    if (!v.consultantPractitionerId) {
      ctx.addIssue({ code: 'custom', path: ['consultantPractitionerId'], message: 'Choose the consultant in charge.' });
    }
    if (!v.bedId) {
      ctx.addIssue({ code: 'custom', path: ['bedId'], message: 'Choose a vacant bed.' });
    }
    if (v.source === 'Referral' && !v.referredBy) {
      ctx.addIssue({ code: 'custom', path: ['referredBy'], message: 'Enter the referring doctor or hospital.' });
    }
    if (v.isMlc && !v.mlcNumber) {
      ctx.addIssue({ code: 'custom', path: ['mlcNumber'], message: 'Enter the MLC number for a medico-legal case.' });
    }
    if (v.attendantPhone && !isIndianMobile(v.attendantPhone)) {
      ctx.addIssue({ code: 'custom', path: ['attendantPhone'], message: 'Enter a 10-digit mobile number.' });
    }
    if (v.earlier && !fromIstInput(v.admittedAt)) {
      ctx.addIssue({ code: 'custom', path: ['admittedAt'], message: 'Enter when the patient was admitted.' });
    }
  });

type AdmitValues = z.infer<typeof schema>;

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 320px',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  aside: { position: 'sticky', top: '16px', display: 'flex', flexDirection: 'column', gap: '12px' },
  summary: { display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '6px 12px' },
  label: { color: tokens.colorNeutralForeground3 },
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  done: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '6px',
    textAlign: 'center',
    padding: '8px 0',
  },
  number: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: '28px',
    lineHeight: '36px',
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorBrandForeground1,
  },
  submit: { width: '100%', minHeight: '44px' },
});

function composeNotes(depositNote: string, notes: string): string | null {
  return [depositNote ? `Deposit: ${depositNote}` : null, notes || null].filter(Boolean).join('\n') || null;
}

/** Admit a patient (WB-03): patient → doctors → bed → payer and attendant; prints the slip and the wristband. */
function AdmitPatientPage() {
  const styles = useStyles();
  const search = Route.useSearch();
  const navigate = useNavigate();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const facilityName = useSession((s) => s.user?.facilityName) ?? 'Nana Hospitals';
  const ownPractitionerId = useSession((s) => s.user?.practitionerId ?? null);
  const canRegister = usePermission('patients.register');
  const [autoPrint, setAutoPrint] = useStoredState('hc360:inpatient:admit-autoprint', '1');
  const [nowInput] = useState(() => toIstInput(new Date()));
  const [created, setCreated] = useState<AdmissionCreated | null>(null);

  const defaults: AdmitValues = {
    patient: null,
    type: 'Elective',
    source: 'OutPatient',
    referredBy: '',
    departmentId: null,
    admittingPractitionerId: ownPractitionerId,
    consultantPractitionerId: ownPractitionerId,
    provisionalDiagnosis: '',
    expectedDischargeDate: null,
    bedId: search.bedId ?? null,
    payerType: 'Self',
    policyNumber: '',
    depositNote: '',
    isMlc: false,
    mlcNumber: '',
    attendantName: '',
    attendantRelation: '',
    attendantPhone: '',
    earlier: false,
    admittedAt: nowInput,
    notes: '',
    notifyPatient: true,
  };
  const form = useForm<AdmitValues>({ resolver: zodResolver(schema), defaultValues: defaults, mode: 'onTouched' });
  const draft = useFormDraft(form, 'inpatient-admit');
  const [patient, departmentId, admittingId, source, payerType, isMlc, earlier, consultantId, bedId] = useWatch({
    control: form.control,
    name: [
      'patient',
      'departmentId',
      'admittingPractitionerId',
      'source',
      'payerType',
      'isMlc',
      'earlier',
      'consultantPractitionerId',
      'bedId',
    ],
  });
  const departments = useDepartments('Clinical');
  const practitioners = usePractitioners(departmentId);

  // Arriving from a patient profile or a reserved bed: pre-select the patient.
  const linkedPatient = useQuery({
    ...patientQueries.detail(search.patientId ?? ''),
    enabled: Boolean(search.patientId),
  });
  useEffect(() => {
    const p = linkedPatient.data;
    if (p && !form.getValues('patient')) {
      form.setValue('patient', { id: p.id, uhid: p.uhid, fullName: p.fullName }, { shouldValidate: true });
    }
  }, [linkedPatient.data, form]);

  // The consultant in charge defaults to the admitting doctor.
  useEffect(() => {
    if (admittingId && !form.getValues('consultantPractitionerId')) {
      form.setValue('consultantPractitionerId', admittingId);
    }
  }, [admittingId, form]);

  const history = useQuery({ ...inpatientQueries.patientAdmissions(patient?.id ?? ''), enabled: Boolean(patient?.id) });
  // Same query (and cache) as the bed picker: gives the chosen bed's label and ward for the summary.
  const available = useQuery(inpatientQueries.available(patient?.id ?? null));
  const pickedBed: AvailableBedDto | null = available.data?.find((b) => b.bedId === bedId) ?? null;
  const activeStay = history.data?.find((a) => isActiveAdmission(a.status)) ?? null;
  const detail = useQuery({ ...inpatientQueries.admission(created?.id ?? ''), enabled: Boolean(created?.id) });

  const admit = useMutation({
    mutationFn: (v: AdmitValues) =>
      admissionApi.admit({
        patientId: v.patient!.id,
        bedId: v.bedId!,
        type: v.type,
        source: v.source,
        sourceVisitId: null,
        departmentId: v.departmentId,
        admittingPractitionerId: v.admittingPractitionerId,
        consultantPractitionerId: v.consultantPractitionerId!,
        provisionalDiagnosis: v.provisionalDiagnosis || null,
        referredBy: v.referredBy || null,
        payerType: v.payerType,
        payerId: null,
        policyNumber: POLICY_PAYERS.includes(v.payerType) ? v.policyNumber || null : null,
        isMlc: v.isMlc,
        mlcNumber: v.isMlc ? v.mlcNumber : null,
        expectedDischargeDate: v.expectedDischargeDate,
        attendantName: v.attendantName || null,
        attendantRelation: v.attendantRelation || null,
        attendantPhone: v.attendantPhone ? normalizePhone(v.attendantPhone) : null,
        admittedAt: v.earlier ? fromIstInput(v.admittedAt) : null,
        notes: composeNotes(v.depositNote, v.notes),
        notifyPatient: v.notifyPatient,
      }),
    onSuccess: async (result) => {
      draft.clear();
      setCreated(result);
      void queryClient.invalidateQueries({ queryKey: inpatientKeys.all });
      if (autoPrint === '1') {
        try {
          const admission = await queryClient.fetchQuery(inpatientQueries.admission(result.id));
          await printWristband(admission);
          await printAdmissionSlip(admission, facilityName);
        } catch (error) {
          notify.error(error, 'Printing failed. Use the print buttons to try again.');
        }
      }
    },
    onError: (error) => {
      if (
        isApiError(error) &&
        (error.code === 'bed.taken' || error.code === 'bed.unavailable' || error.code === 'bed.reserved')
      ) {
        form.setValue('bedId', null);
        void queryClient.invalidateQueries({ queryKey: ['inpatient', 'available'] });
      }
      applyServerErrors(error, form.setError);
    },
  });

  const submit = form.handleSubmit((values) => admit.mutate(values));
  useHotkey('mod+s', () => void submit(), { allowInInputs: true, enabled: !admit.isPending && !created });

  const print = async (what: 'slip' | 'wristband') => {
    const admission = detail.data;
    if (!admission) {
      return;
    }
    try {
      if (what === 'wristband') {
        await printWristband(admission);
      } else {
        await printAdmissionSlip(admission, facilityName);
      }
    } catch (error) {
      notify.error(error, 'Printing failed');
    }
  };

  const startAnother = () => {
    setCreated(null);
    admit.reset();
    form.reset({ ...defaults, bedId: null, admittedAt: toIstInput(new Date()) });
    void navigate({ to: '/inpatient/admit', search: {} });
  };

  const serverError = admit.error && !(isApiError(admit.error) && admit.error.isValidation) ? admit.error : null;
  const consultantName = practitioners.data?.find((p) => p.id === consultantId)?.name;

  return (
    <Page maxWidth={1400}>
      <PageHeader
        title="Admit patient"
        icon={<PersonAddRegular />}
        subtitle="Choose the patient, the doctors and a vacant bed. Press Ctrl+S to admit."
        breadcrumbs={[
          { label: 'In-patients', onClick: () => void navigate({ to: '/inpatient/admissions' }) },
          { label: 'Admit' },
        ]}
      />

      {draft.saved && (
        <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
          <MessageBarBody>
            An unfinished admission from {formatRelative(new Date(draft.saved.savedAt).toISOString())} was found on this
            computer.
          </MessageBarBody>
          <MessageBarActions>
            <Button onClick={draft.restore}>Restore it</Button>
            <Button appearance="subtle" onClick={draft.discard}>
              Discard
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      <form onSubmit={submit} noValidate>
        <div className={styles.layout}>
          <div className={styles.main}>
            <Section title="Patient">
              <div className={styles.stack}>
                <Controller
                  control={form.control}
                  name="patient"
                  render={({ field, fieldState }) => (
                    <PatientPicker
                      value={field.value}
                      onChange={(p) => field.onChange(p)}
                      required
                      validationMessage={fieldState.error?.message}
                    />
                  )}
                />
                {canRegister && (
                  <div>
                    <Button
                      size="small"
                      appearance="subtle"
                      icon={<PersonAddRegular />}
                      onClick={() => void navigate({ to: '/patients/register' })}
                    >
                      New patient? Register first
                    </Button>
                  </div>
                )}
                {activeStay && (
                  <MessageBar intent="warning" layout="multiline">
                    <MessageBarBody>
                      <MessageBarTitle>Already admitted</MessageBarTitle>
                      {patient?.fullName} is in {activeStay.wardName ?? 'a ward'} under admission{' '}
                      {activeStay.admissionNumber}. Transfer the patient instead of admitting again.
                    </MessageBarBody>
                    <MessageBarActions>
                      <Button
                        onClick={() =>
                          void navigate({
                            to: '/inpatient/admissions/$admissionId',
                            params: { admissionId: activeStay.id },
                          })
                        }
                      >
                        Open admission
                      </Button>
                    </MessageBarActions>
                  </MessageBar>
                )}
              </div>
            </Section>

            <Section title="Admission">
              <div className={styles.stack}>
                <FormGrid columns={3}>
                  <SelectField
                    control={form.control}
                    name="type"
                    label="Admission type"
                    options={enumOptions(ADMISSION_TYPES, ADMISSION_TYPE_LABEL)}
                  />
                  <SelectField
                    control={form.control}
                    name="source"
                    label="Came from"
                    options={enumOptions(ADMISSION_SOURCES, ADMISSION_SOURCE_LABEL)}
                  />
                  {source === 'Referral' && (
                    <TextField control={form.control} name="referredBy" label="Referred by" required maxLength={200} />
                  )}
                </FormGrid>
                <FormGrid columns={3}>
                  <SelectField
                    control={form.control}
                    name="departmentId"
                    label="Department"
                    placeholder={departments.isLoading ? 'Loading…' : 'Choose department'}
                    options={toOptions(departments.data)}
                  />
                  <SelectField
                    control={form.control}
                    name="admittingPractitionerId"
                    label="Admitting doctor"
                    placeholder={departmentId ? 'Choose doctor' : 'Any doctor'}
                    options={toOptions(practitioners.data)}
                  />
                  <SelectField
                    control={form.control}
                    name="consultantPractitionerId"
                    label="Consultant in charge"
                    required
                    placeholder="Choose consultant"
                    options={toOptions(practitioners.data)}
                  />
                </FormGrid>
                <TextAreaField
                  control={form.control}
                  name="provisionalDiagnosis"
                  label="Provisional diagnosis"
                  rows={2}
                  maxLength={500}
                />
                <FormGrid columns={3}>
                  <DateField
                    control={form.control}
                    name="expectedDischargeDate"
                    label="Expected discharge"
                    min={todayIso()}
                  />
                  <div>
                    <SwitchField control={form.control} name="isMlc" label="Medico-legal case (MLC)" />
                    {isMlc && (
                      <TextField control={form.control} name="mlcNumber" label="MLC number" required maxLength={64} />
                    )}
                  </div>
                  <div>
                    <SwitchField control={form.control} name="earlier" label="Admitted earlier (last 3 days)" />
                    {earlier && (
                      <Controller
                        control={form.control}
                        name="admittedAt"
                        render={({ field, fieldState }) => (
                          <Field
                            label="Admitted at"
                            required
                            validationMessage={fieldState.error?.message}
                            validationState={fieldState.error ? 'error' : 'none'}
                          >
                            <Input
                              type="datetime-local"
                              value={field.value}
                              max={nowInput}
                              onChange={(_, d) => field.onChange(d.value)}
                            />
                          </Field>
                        )}
                      />
                    )}
                  </div>
                </FormGrid>
              </div>
            </Section>

            <Section
              title="Bed"
              description="Vacant beds the patient may use. Ward rules (male/female, NICU, PICU, maternity) are checked for you."
            >
              <Controller
                control={form.control}
                name="bedId"
                render={({ field, fieldState }) => (
                  <BedPicker
                    label="Vacant bed"
                    patientId={patient?.id ?? null}
                    value={field.value}
                    onChange={(bed) => field.onChange(bed?.bedId ?? null)}
                    validationMessage={fieldState.error?.message}
                  />
                )}
              />
            </Section>

            <Section title="Payment and attendant">
              <div className={styles.stack}>
                <FormGrid columns={3}>
                  <SelectField
                    control={form.control}
                    name="payerType"
                    label="Payment by"
                    options={PAYER_TYPES.map((p) => ({ value: p, label: PAYER_LABEL[p] }))}
                  />
                  {POLICY_PAYERS.includes(payerType) && (
                    <TextField
                      control={form.control}
                      name="policyNumber"
                      label="Policy / TPA card number"
                      maxLength={64}
                      autoComplete="off"
                      hint="Stored encrypted"
                    />
                  )}
                  <TextField
                    control={form.control}
                    name="depositNote"
                    label="Deposit note"
                    maxLength={300}
                    placeholder="e.g. ₹20,000 advance, receipt 1234"
                    hint="Collected at the IP billing counter"
                  />
                </FormGrid>
                <FormGrid columns={3}>
                  <TextField
                    control={form.control}
                    name="attendantName"
                    label="Attendant name"
                    maxLength={200}
                    autoComplete="off"
                  />
                  <TextField
                    control={form.control}
                    name="attendantRelation"
                    label="Relation to patient"
                    maxLength={64}
                    placeholder="e.g. Son, Wife"
                  />
                  <TextField
                    control={form.control}
                    name="attendantPhone"
                    label="Attendant mobile"
                    type="tel"
                    maxLength={14}
                    autoComplete="off"
                  />
                </FormGrid>
                <TextAreaField control={form.control} name="notes" label="Other notes" rows={2} maxLength={600} />
                <SwitchField
                  control={form.control}
                  name="notifyPatient"
                  label="Send the admission SMS (ward, bed and doctor) to the patient"
                />
              </div>
            </Section>
          </div>

          <aside className={styles.aside} aria-label="Admission summary">
            <Section title="Summary">
              <div className={styles.stack}>
                <div className={styles.summary}>
                  <Caption1 className={styles.label}>Patient</Caption1>
                  <Text weight="semibold">{patient ? `${patient.fullName} (${patient.uhid})` : '—'}</Text>
                  <Caption1 className={styles.label}>Bed</Caption1>
                  <Text weight="semibold">{pickedBed ? `${pickedBed.label} · ${pickedBed.wardName}` : '—'}</Text>
                  <Caption1 className={styles.label}>Consultant</Caption1>
                  <Text>{consultantName ?? '—'}</Text>
                  <Caption1 className={styles.label}>Payment</Caption1>
                  <Text>{PAYER_LABEL[payerType]}</Text>
                </div>
                {serverError && (
                  <MessageBar intent="error" layout="multiline">
                    <MessageBarBody>
                      <MessageBarTitle>
                        {isApiError(serverError) ? serverError.title : 'Could not admit'}
                      </MessageBarTitle>
                      {isApiError(serverError) ? serverError.userMessage : 'Please try again.'}
                    </MessageBarBody>
                  </MessageBar>
                )}
                <Checkbox
                  checked={autoPrint === '1'}
                  onChange={(_, d) => setAutoPrint(d.checked ? '1' : '0')}
                  label="Print the slip and wristband after admitting"
                />
                <Button
                  type="submit"
                  appearance="primary"
                  className={styles.submit}
                  disabled={admit.isPending || Boolean(activeStay)}
                  icon={admit.isPending ? <Spinner size="tiny" /> : <PersonAddRegular />}
                  aria-keyshortcuts="Control+S"
                >
                  Admit patient
                </Button>
                <Button appearance="subtle" onClick={() => void navigate({ to: '/inpatient/beds' })}>
                  Cancel
                </Button>
              </div>
            </Section>
          </aside>
        </div>
      </form>

      <Dialog open={created !== null} onOpenChange={(_, d) => !d.open && startAnother()}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Patient admitted</DialogTitle>
            <DialogContent>
              <div className={styles.done} role="status">
                <CheckmarkCircleRegular fontSize={40} color={tokens.colorPaletteGreenForeground1} />
                <Text size={400} weight="semibold">
                  {patient?.fullName}
                </Text>
                <Caption1 className={styles.label}>Admission number</Caption1>
                <span className={styles.number}>{created?.admissionNumber}</span>
                <Text>
                  Bed {created?.bedLabel}
                  {detail.data?.wardName ? ` · ${detail.data.wardName}` : ''} · IP visit {created?.visitNumber}
                </Text>
                {detail.isLoading && <Spinner size="tiny" label="Preparing the slip…" />}
                {detail.data && (
                  <Caption1 className={styles.label}>
                    {humanize(detail.data.type)} admission under {detail.data.consultantName ?? 'the consultant'}
                  </Caption1>
                )}
              </div>
            </DialogContent>
            <DialogActions fluid>
              <Button icon={<PrintRegular />} disabled={!detail.data} onClick={() => void print('slip')}>
                Print slip
              </Button>
              <Button icon={<TagRegular />} disabled={!detail.data} onClick={() => void print('wristband')}>
                Print wristband
              </Button>
              <Button
                onClick={() =>
                  created &&
                  void navigate({ to: '/inpatient/admissions/$admissionId', params: { admissionId: created.id } })
                }
              >
                Open admission
              </Button>
              <Button appearance="primary" onClick={startAnother} autoFocus>
                Admit another
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </Page>
  );
}
