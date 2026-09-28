import {
  Button,
  Caption1,
  Divider,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { CheckmarkCircleRegular, DeleteRegular, DocumentArrowLeftRegular, DropRegular, InfoRegular, SaveRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatInr, formatRelative, useHotkey } from '@hc360/core';
import { applyServerErrors, ChoiceField, FormGrid, LookupField, Page, PageHeader, Section, TextAreaField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute, useNavigate } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { searchPractitioners } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { useFormDraft } from '@/lib/useFormDraft';
import { CONTAINER_TYPES, LAB_PERMISSIONS, LAB_SECTIONS, labApi, labKeys, type LabOrderCreated, type LabTestListItem } from '@/modules/lab/api';
import { TubeChip } from '@/modules/lab/badges';
import { searchId } from '@/modules/lab/common/search';
import { sectionLabel } from '@/modules/lab/labels';
import { TestPicker } from '@/modules/lab/TestPicker';
import { patientQueries } from '@/modules/patients/api';
import { PatientPicker } from '@/modules/patients/PatientPicker';

export const Route = createFileRoute('/_app/lab/orders/new')({
  validateSearch: (s: Record<string, unknown>): { patientId?: string } => ({ patientId: searchId(s.patientId) }),
  component: NewLabOrderPage,
});

const testSchema = z.object({
  id: z.string(),
  code: z.string(),
  name: z.string(),
  price: z.number().nullable(),
  container: z.enum(CONTAINER_TYPES),
  section: z.enum(LAB_SECTIONS),
  preparation: z.string().nullable(),
  tatHours: z.number(),
  isPanel: z.boolean(),
  componentCount: z.number(),
});

const schema = z
  .object({
    patient: z.object({ id: z.string(), uhid: z.string(), fullName: z.string() }).nullable(),
    tests: z.array(testSchema),
    priority: z.enum(['Routine', 'Urgent', 'Stat']),
    source: z.enum(['WalkIn', 'OutsideReferral']),
    referringDoctorName: z.string().trim().max(200),
    orderingPractitionerId: z.string().nullable(),
    clinicalNotes: z.string().trim().max(1000),
  })
  .superRefine((values, ctx) => {
    if (!values.patient) {
      ctx.addIssue({ code: 'custom', path: ['patient'], message: 'Find the patient by UHID, mobile or name. Register new patients first.' });
    }
    if (values.tests.length === 0) {
      ctx.addIssue({ code: 'custom', path: ['tests'], message: 'Add at least one test.' });
    }
    if (values.tests.length > 50) {
      ctx.addIssue({ code: 'custom', path: ['tests'], message: 'At most 50 tests per order.' });
    }
    if (values.source === 'OutsideReferral' && !values.referringDoctorName) {
      ctx.addIssue({ code: 'custom', path: ['referringDoctorName'], message: 'Enter the referring doctor for an outside referral.' });
    }
  });

type OrderValues = z.infer<typeof schema>;
type PickedTest = OrderValues['tests'][number];

const EMPTY: OrderValues = {
  patient: null,
  tests: [],
  priority: 'Routine',
  source: 'WalkIn',
  referringDoctorName: '',
  orderingPractitionerId: null,
  clinicalNotes: '',
};

function toPicked(test: LabTestListItem): PickedTest {
  return {
    id: test.id,
    code: test.code,
    name: test.name,
    price: test.price,
    container: test.container,
    section: test.section,
    preparation: test.preparation,
    tatHours: test.tatHours,
    isPanel: test.isPanel,
    componentCount: test.componentCount,
  };
}

const useStyles = makeStyles({
  layout: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) 360px',
    gap: '16px',
    alignItems: 'start',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  main: { display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0 },
  aside: { position: 'sticky', top: '16px', display: 'flex', flexDirection: 'column', gap: '12px' },
  chosen: { display: 'flex', flexDirection: 'column', gap: '4px', margin: 0, padding: 0, listStyleType: 'none' },
  test: {
    display: 'grid',
    gridTemplateColumns: 'minmax(0, 1fr) auto auto',
    gap: '2px 12px',
    alignItems: 'center',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  muted: { color: tokens.colorNeutralForeground3 },
  prep: { display: 'flex', gap: '6px', alignItems: 'center', color: tokens.colorPaletteMarigoldForeground1 },
  total: { display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' },
  amount: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  done: { display: 'flex', flexDirection: 'column', gap: '12px' },
  accession: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase600, fontWeight: tokens.fontWeightSemibold, color: tokens.colorBrandForeground1 },
  tube: { display: 'flex', gap: '10px', alignItems: 'center', flexWrap: 'wrap' },
  mono: { fontFamily: tokens.fontFamilyMonospace },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
});

/**
 * Lab counter registration for walk-in and outside-referral patients (SRS LIS-02). Doctors' orders arrive from the
 * clinical order entry by themselves. Keyboard: find the patient, type tests and press Enter after each, Ctrl+S places
 * the order. The typed order is kept on this computer until it is placed.
 */
function NewLabOrderPage() {
  const { patientId } = Route.useSearch();
  const preset = useQuery({ ...patientQueries.detail(patientId ?? ''), enabled: Boolean(patientId) });
  if (patientId && preset.isLoading) {
    return (
      <Page>
        <Spinner label="Opening the patient…" />
      </Page>
    );
  }
  const patient = preset.data ? { id: preset.data.id, uhid: preset.data.uhid, fullName: preset.data.fullName } : null;
  return <OrderForm key={patient?.id ?? 'new'} initialPatient={patient} />;
}

function OrderForm({ initialPatient }: { initialPatient: OrderValues['patient'] }) {
  const styles = useStyles();
  const notify = useNotify();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const canCollect = usePermission(LAB_PERMISSIONS.collect);
  const [created, setCreated] = useState<{ order: LabOrderCreated; patientName: string } | null>(null);
  const form = useForm<OrderValues>({ resolver: zodResolver(schema), defaultValues: { ...EMPTY, patient: initialPatient } });
  const draft = useFormDraft(form, 'lab-order-new');
  const [tests, source] = useWatch({ control: form.control, name: ['tests', 'source'] });
  const total = tests.reduce((sum, test) => sum + (test.price ?? 0), 0);
  const unpriced = tests.filter((test) => test.price === null).length;
  const preparation = [...new Set(tests.map((test) => test.preparation).filter((p): p is string => Boolean(p)))];

  const create = useMutation({
    mutationFn: (values: OrderValues) =>
      labApi.createOrder({
        patientId: values.patient!.id,
        visitId: null,
        testIds: values.tests.map((test) => test.id),
        priority: values.priority,
        source: values.source,
        orderingPractitionerId: values.orderingPractitionerId,
        referringDoctorName: values.referringDoctorName || null,
        clinicalNotes: values.clinicalNotes || null,
      }),
    onSuccess: (order, values) => {
      draft.clear();
      setCreated({ order, patientName: values.patient!.fullName });
      void queryClient.invalidateQueries({ queryKey: labKeys.orders });
      void queryClient.invalidateQueries({ queryKey: labKeys.samples });
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not place the lab order');
      }
    },
  });

  const submit = form.handleSubmit((values) => create.mutate(values));
  useHotkey('mod+s', () => void submit(), { allowInInputs: true, enabled: !create.isPending && !created });

  if (created) {
    const { order } = created;
    return (
      <Page maxWidth={900}>
        <PageHeader title="Lab order placed" subtitle={created.patientName} breadcrumbs={[{ label: 'Lab orders', onClick: () => void navigate({ to: '/lab/orders' }) }, { label: 'New order' }]} />
        <Section>
          <div className={styles.done} role="status">
            <span>
              <CheckmarkCircleRegular fontSize={28} color={tokens.colorPaletteGreenForeground1} /> <Text size={400}>Accession number</Text>
            </span>
            <span className={styles.accession}>{order.accessionNumber}</span>
            {order.chargeWarnings.length > 0 && (
              <MessageBar intent="warning" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>Some charges were not posted to the bill</MessageBarTitle>
                  {order.chargeWarnings.join(' · ')}. The tests are booked; retry the charges from the order once billing is available.
                </MessageBarBody>
              </MessageBar>
            )}
            <Text weight="semibold">Tubes to collect</Text>
            {order.samples.map((sample) => (
              <div key={sample.id} className={styles.tube}>
                <TubeChip container={sample.container} />
                <Caption1 className={styles.muted}>{sectionLabel[sample.section]}</Caption1>
                <Caption1 className={styles.mono}>{sample.barcode}</Caption1>
                <Text>{sample.tests.join(', ')}</Text>
              </div>
            ))}
            <Caption1 className={styles.muted}>Out-patients and walk-ins pay at the billing counter before the sample is drawn.</Caption1>
            <div className={styles.actions}>
              {canCollect && (
                <Button
                  appearance="primary"
                  icon={<DropRegular />}
                  autoFocus
                  onClick={() => void navigate({ to: '/lab/orders/$orderId', params: { orderId: order.id }, search: { collect: true } })}
                >
                  Collect samples now
                </Button>
              )}
              <Button onClick={() => void navigate({ to: '/lab/orders/$orderId', params: { orderId: order.id } })}>Open the order</Button>
              <Button
                appearance="subtle"
                onClick={() => {
                  form.reset(EMPTY);
                  setCreated(null);
                }}
              >
                New order
              </Button>
            </div>
          </div>
        </Section>
      </Page>
    );
  }

  return (
    <Page>
      <PageHeader
        title="New lab order"
        subtitle="Walk-in and outside-referral patients. Type a test and press Enter to add it; Ctrl+S places the order."
        breadcrumbs={[{ label: 'Lab orders', onClick: () => void navigate({ to: '/lab/orders' }) }, { label: 'New order' }]}
      />

      {draft.saved && (
        <MessageBar intent="info" icon={<DocumentArrowLeftRegular />}>
          <MessageBarBody>An order that was not placed ({formatRelative(new Date(draft.saved.savedAt).toISOString())}) was found on this computer.</MessageBarBody>
          <MessageBarActions>
            <Button onClick={draft.restore}>Restore it</Button>
            <Button appearance="subtle" onClick={draft.discard}>
              Discard
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      <form onSubmit={(event) => void submit(event)} noValidate>
        <div className={styles.layout}>
          <div className={styles.main}>
            <Section title="Patient">
              <Controller
                control={form.control}
                name="patient"
                render={({ field, fieldState }) => (
                  <PatientPicker value={field.value} onChange={field.onChange} required validationMessage={fieldState.error?.message} hint="New patient? Register them first, then come back." />
                )}
              />
            </Section>

            <Section title="Tests">
              <Controller
                control={form.control}
                name="tests"
                render={({ field, fieldState }) => (
                  <>
                    <TestPicker
                      selectedIds={field.value.map((test) => test.id)}
                      onAdd={(test) => field.onChange([...field.value, toPicked(test)])}
                      validationMessage={fieldState.error?.message}
                    />
                    {field.value.length > 0 && (
                      <ul className={styles.chosen} aria-label="Tests on this order" style={{ marginTop: 12 }}>
                        {field.value.map((test) => (
                          <li key={test.id} className={styles.test}>
                            <span>
                              <Text weight="semibold">{test.name}</Text> <Caption1 className={styles.muted}>{test.code}</Caption1>
                            </span>
                            <Text>{test.price === null ? '—' : formatInr(test.price, { whole: true })}</Text>
                            <Button
                              appearance="subtle"
                              icon={<DeleteRegular />}
                              aria-label={`Remove ${test.name}`}
                              onClick={() => field.onChange(field.value.filter((t) => t.id !== test.id))}
                            />
                            <Caption1 className={styles.muted}>
                              <TubeChip container={test.container} /> {sectionLabel[test.section]}
                              {test.isPanel ? ` · ${test.componentCount} parameters` : ''}
                              {test.tatHours ? ` · report in ${test.tatHours} h` : ''}
                            </Caption1>
                          </li>
                        ))}
                      </ul>
                    )}
                  </>
                )}
              />
            </Section>

            <Section title="Referral and notes">
              <FormGrid columns={2}>
                <ChoiceField
                  control={form.control}
                  name="source"
                  label="Patient came"
                  options={[
                    { value: 'WalkIn', label: 'On their own (walk-in)' },
                    { value: 'OutsideReferral', label: 'With an outside prescription' },
                  ]}
                />
                <ChoiceField
                  control={form.control}
                  name="priority"
                  label="Priority"
                  options={[
                    { value: 'Routine', label: 'Routine' },
                    { value: 'Urgent', label: 'Urgent' },
                    { value: 'Stat', label: 'STAT' },
                  ]}
                />
                {source === 'OutsideReferral' && (
                  <TextField control={form.control} name="referringDoctorName" label="Referring doctor" required placeholder="e.g. Dr. R. Kulkarni, Kothrud" maxLength={200} />
                )}
                <LookupField
                  control={form.control}
                  name="orderingPractitionerId"
                  label="Doctor of this hospital (if any)"
                  load={searchPractitioners}
                  hint="Receives the report and critical-value calls"
                />
              </FormGrid>
              <div style={{ height: 12 }} />
              <TextAreaField control={form.control} name="clinicalNotes" label="Clinical notes for the lab" rows={2} maxLength={1000} placeholder="e.g. Fever for 5 days, on antibiotics" />
            </Section>
          </div>

          <aside className={styles.aside}>
            <Section title="Summary">
              <div className={styles.total}>
                <Text>
                  {tests.length} {tests.length === 1 ? 'test' : 'tests'}
                </Text>
                <span className={styles.amount}>{formatInr(total, { whole: true })}</span>
              </div>
              <Caption1 className={styles.muted} block>
                Estimate at list prices{unpriced ? `; ${unpriced} without a price` : ''}. Diagnostic services are exempt from GST. The bill is made at the billing
                counter.
              </Caption1>
              {preparation.length > 0 && (
                <>
                  <Divider style={{ margin: '12px 0 8px' }} />
                  <div className={styles.prep}>
                    <InfoRegular aria-hidden />
                    <Caption1>Tell the patient: {preparation.join(' · ')}</Caption1>
                  </div>
                </>
              )}
            </Section>
            <Button
              type="submit"
              appearance="primary"
              size="large"
              icon={create.isPending ? <Spinner size="tiny" /> : <SaveRegular />}
              disabled={create.isPending}
              aria-keyshortcuts="Control+S"
            >
              Place order
            </Button>
          </aside>
        </div>
      </form>
    </Page>
  );
}
