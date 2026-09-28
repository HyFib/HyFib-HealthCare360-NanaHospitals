import {
  Button,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  Spinner,
  Subtitle2,
  Tab,
  TabList,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { applyServerErrors, ErrorState, FormGrid, FullRow, NumberField, SelectField, SwitchField, TextAreaField, TextField, useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import {
  CONTAINER_TYPES,
  LAB_SECTIONS,
  labApi,
  labKeys,
  labQueries,
  RESULT_TYPES,
  SPECIMEN_TYPES,
  type LabTestDetail,
  type SaveLabTestRequest,
} from '../api';
import { ServiceLookup } from '../common/ServiceLookup';
import { containerInfo, resultTypeLabel, sectionLabel, specimenLabel } from '../labels';
import { ComponentsEditor } from './ComponentsEditor';
import { parseList } from './ranges';
import { RangesEditor } from './RangesEditor';

const CODE = /^[A-Za-z0-9_-]+$/;
const CHANNEL = /^[A-Za-z0-9._%#^-]+(:[A-Za-z0-9._%#^-]+)?$/;

const schema = z
  .object({
    code: z.string().trim().min(1, 'Enter a code, e.g. LAB-CBC.').max(32).regex(CODE, 'Use letters, digits, - or _.'),
    name: z.string().trim().min(1, 'Enter the test name.').max(200),
    shortName: z.string().trim().max(32),
    section: z.enum(LAB_SECTIONS),
    specimenType: z.enum(SPECIMEN_TYPES),
    container: z.enum(CONTAINER_TYPES),
    method: z.string().trim().max(200),
    tatHours: z.number().int('Whole hours only.').min(0).max(1440).nullable(),
    loinc: z
      .string()
      .trim()
      .refine((v) => !v || /^[0-9]{1,7}-[0-9]$/.test(v), 'Enter a LOINC code such as 718-7.'),
    serviceItemId: z.string().nullable(),
    isOrderable: z.boolean(),
    isPanel: z.boolean(),
    resultType: z.enum(RESULT_TYPES),
    unit: z.string().trim().max(32),
    decimals: z.number().int().min(0, '0 to 4').max(4, '0 to 4').nullable(),
    options: z.string(),
    channels: z.string(),
    qcGroup: z
      .string()
      .trim()
      .max(32)
      .refine((v) => !v || CODE.test(v), 'Use letters, digits, - or _.'),
    deltaThresholdPercent: z.number().min(0).max(10000).nullable(),
    allowAutoValidation: z.boolean(),
    preparation: z.string().trim().max(500),
    drawSeparately: z.boolean(),
    interpretation: z.string().trim().max(2000),
    defaultReferralLabId: z.string().nullable(),
    displayOrder: z.number().int().nullable(),
    isActive: z.boolean(),
  })
  .superRefine((values, ctx) => {
    if (values.resultType === 'Options' && parseList(values.options).length === 0) {
      ctx.addIssue({ code: 'custom', path: ['options'], message: 'List the allowed answers, e.g. Negative, Positive.' });
    }
    if (values.isOrderable && !values.serviceItemId) {
      ctx.addIssue({ code: 'custom', path: ['serviceItemId'], message: 'Link an orderable test to its billable service.' });
    }
    const bad = parseList(values.channels).filter((channel) => channel.length > 64 || !CHANNEL.test(channel));
    if (bad.length > 0) {
      ctx.addIssue({ code: 'custom', path: ['channels'], message: `Use CHANNEL or ANALYSER:CHANNEL (check ${bad.join(', ')}).` });
    }
  });

type TestValues = z.infer<typeof schema>;

function defaults(test: LabTestDetail | null): TestValues {
  return {
    code: test?.code ?? '',
    name: test?.name ?? '',
    shortName: test?.shortName ?? '',
    section: test?.section ?? 'Biochemistry',
    specimenType: test?.specimenType ?? 'Serum',
    container: test?.container ?? 'Plain',
    method: test?.method ?? '',
    tatHours: test?.tatHours ?? 24,
    loinc: test?.loinc ?? '',
    serviceItemId: test?.serviceItemId ?? null,
    isOrderable: test?.isOrderable ?? true,
    isPanel: test?.isPanel ?? false,
    resultType: test?.resultType ?? 'Numeric',
    unit: test?.unit ?? '',
    decimals: test?.decimals ?? 1,
    options: (test?.options ?? []).join(', '),
    channels: (test?.instrumentChannelCodes ?? []).join(', '),
    qcGroup: test?.qcGroup ?? '',
    deltaThresholdPercent: test?.deltaThresholdPercent ?? null,
    allowAutoValidation: test?.allowAutoValidation ?? false,
    preparation: test?.preparation ?? '',
    drawSeparately: test?.drawSeparately ?? false,
    interpretation: test?.interpretation ?? '',
    defaultReferralLabId: test?.defaultReferralLabId ?? null,
    displayOrder: test?.displayOrder ?? 0,
    isActive: test?.isActive ?? true,
  };
}

function toRequest(values: TestValues, version: number | null): SaveLabTestRequest {
  const options = parseList(values.options);
  const channels = parseList(values.channels);
  return {
    code: values.code.toUpperCase(),
    name: values.name,
    shortName: values.shortName || null,
    section: values.section,
    specimenType: values.specimenType,
    container: values.container,
    method: values.method || null,
    tatHours: values.tatHours ?? 0,
    loinc: values.loinc || null,
    serviceItemId: values.serviceItemId,
    isOrderable: values.isOrderable,
    isPanel: values.isPanel,
    resultType: values.resultType,
    unit: values.unit || null,
    decimals: values.decimals ?? 0,
    options: options.length ? options : null,
    instrumentChannelCodes: channels.length ? channels.map((c) => c.toUpperCase()) : null,
    qcGroup: values.qcGroup ? values.qcGroup.toUpperCase() : null,
    deltaThresholdPercent: values.deltaThresholdPercent,
    allowAutoValidation: values.allowAutoValidation,
    preparation: values.preparation || null,
    drawSeparately: values.drawSeparately,
    interpretation: values.interpretation || null,
    defaultReferralLabId: values.defaultReferralLabId,
    displayOrder: values.displayOrder ?? 0,
    isActive: values.isActive,
    version,
  };
}

const useStyles = makeStyles({
  body: { display: 'flex', flexDirection: 'column', gap: '16px' },
  group: { display: 'flex', flexDirection: 'column', gap: '10px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

type TabId = 'details' | 'ranges' | 'components';

/**
 * Test master editor (SRS LIS-01): what the test is, how it is collected and billed, how results are entered and
 * validated, its reference ranges (single analytes) or its analytes (panels).
 */
export function TestDrawer({ testId, onClose, onCreated }: { testId: string | null; onClose: () => void; onCreated?: (id: string) => void }) {
  const detail = useQuery({ ...labQueries.test(testId ?? ''), enabled: testId !== null, staleTime: 0 });

  if (testId && !detail.data) {
    return (
      <OverlayDrawer open position="end" size="large" onOpenChange={(_, data) => !data.open && onClose()}>
        <DrawerHeader>
          <DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}>Lab test</DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>{detail.error ? <ErrorState error={detail.error} onRetry={() => void detail.refetch()} /> : <Spinner label="Loading the test…" />}</DrawerBody>
      </OverlayDrawer>
    );
  }
  return <TestEditor test={detail.data ?? null} onClose={onClose} onCreated={onCreated} />;
}

function TestEditor({ test, onClose, onCreated }: { test: LabTestDetail | null; onClose: () => void; onCreated?: (id: string) => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [tab, setTab] = useState<TabId>('details');
  const referralLabs = useQuery(labQueries.referralLabs());
  const form = useForm<TestValues>({ resolver: zodResolver(schema), defaultValues: defaults(test) });
  const [resultType, isPanel, isOrderable] = useWatch({ control: form.control, name: ['resultType', 'isPanel', 'isOrderable'] });

  const save = useMutation({
    mutationFn: (values: TestValues) => labApi.saveTest(test?.id ?? null, toRequest(values, test?.version ?? null)),
    onSuccess: (result) => {
      void queryClient.invalidateQueries({ queryKey: labKeys.masters });
      if (!test && result?.id) {
        notify.success('Test created', isPanel ? 'Now add its analytes.' : 'Now add its reference ranges.');
        onCreated?.(result.id);
      } else {
        notify.success('Test saved');
      }
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the test');
      }
    },
  });

  return (
    <OverlayDrawer open position="end" size="large" onOpenChange={(_, data) => !data.open && onClose()}>
      <form onSubmit={form.handleSubmit((values) => save.mutate(values))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}>
            {test ? `${test.name} (${test.code})` : 'New lab test'}
          </DrawerHeaderTitle>
          {test && (
            <TabList selectedValue={tab} onTabSelect={(_, data) => setTab(data.value as TabId)}>
              <Tab value="details">Details</Tab>
              {!test.isPanel && <Tab value="ranges">Reference ranges ({test.ranges.length})</Tab>}
              {test.isPanel && <Tab value="components">Panel analytes ({test.components.length})</Tab>}
            </TabList>
          )}
        </DrawerHeader>
        <DrawerBody>
          <div className={styles.body} hidden={tab !== 'details'}>
            {test?.isProvisional && (
              <MessageBar intent="warning" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>Provisional test</MessageBarTitle>
                  Created automatically from a doctor's order for a service without a lab test. Complete the set-up so results can be flagged and reported.
                </MessageBarBody>
              </MessageBar>
            )}
            <section className={styles.group}>
              <Subtitle2>The test</Subtitle2>
              <FormGrid columns={3}>
                <TextField control={form.control} name="code" label="Code" required maxLength={32} hint="On labels, bills and analyser messages" />
                <TextField control={form.control} name="name" label="Name" required maxLength={200} />
                <TextField control={form.control} name="shortName" label="Short name" maxLength={32} hint="Used on tube labels" />
                <SelectField control={form.control} name="section" label="Bench" options={LAB_SECTIONS.map((value) => ({ value, label: sectionLabel[value] }))} />
                <TextField control={form.control} name="method" label="Method" maxLength={200} />
                <TextField control={form.control} name="loinc" label="LOINC code" maxLength={10} />
              </FormGrid>
              <FormGrid columns={3}>
                <SwitchField control={form.control} name="isPanel" label="Panel of several analytes" disabled={Boolean(test)} />
                <SwitchField control={form.control} name="isOrderable" label="Can be ordered" />
                <SwitchField control={form.control} name="isActive" label="In use" />
              </FormGrid>
            </section>

            <section className={styles.group}>
              <Subtitle2>Collection and billing</Subtitle2>
              <FormGrid columns={3}>
                <SelectField control={form.control} name="specimenType" label="Specimen" options={SPECIMEN_TYPES.map((value) => ({ value, label: specimenLabel[value] }))} />
                <SelectField
                  control={form.control}
                  name="container"
                  label="Container"
                  options={CONTAINER_TYPES.map((value) => ({ value, label: `${containerInfo[value].label} (${containerInfo[value].capName})` }))}
                />
                <NumberField control={form.control} name="tatHours" label="Target turnaround (hours)" min={0} max={1440} />
                <FullRow>
                  <Controller
                    control={form.control}
                    name="serviceItemId"
                    render={({ field, fieldState }) => (
                      <ServiceLookup
                        category="Laboratory"
                        value={field.value}
                        onChange={field.onChange}
                        required={isOrderable}
                        validationMessage={fieldState.error?.message}
                      />
                    )}
                  />
                </FullRow>
                <FullRow>
                  <TextField control={form.control} name="preparation" label="Patient preparation" maxLength={500} placeholder="e.g. Fasting 8 to 12 hours; water allowed" />
                </FullRow>
                <SwitchField control={form.control} name="drawSeparately" label="Always its own tube (timed samples, e.g. post-meal sugar)" />
                <SelectField
                  control={form.control}
                  name="defaultReferralLabId"
                  label="Done by a reference laboratory"
                  hint="Leave as in-house unless the test is always sent out"
                  placeholder="In-house"
                  options={(referralLabs.data ?? []).map((lab) => ({ value: lab.id, label: lab.name }))}
                />
              </FormGrid>
            </section>

            {!isPanel && (
              <section className={styles.group}>
                <Subtitle2>Results</Subtitle2>
                <FormGrid columns={3}>
                  <SelectField control={form.control} name="resultType" label="Result type" options={RESULT_TYPES.map((value) => ({ value, label: resultTypeLabel[value] }))} />
                  {resultType === 'Numeric' && (
                    <>
                      <TextField control={form.control} name="unit" label="Unit" maxLength={32} placeholder="e.g. mg/dL" />
                      <NumberField control={form.control} name="decimals" label="Decimals printed" min={0} max={4} />
                    </>
                  )}
                  {resultType === 'Options' && (
                    <FullRow>
                      <TextAreaField control={form.control} name="options" label="Allowed answers" required rows={2} hint="Comma separated, e.g. Negative, Positive" />
                    </FullRow>
                  )}
                  <FullRow>
                    <TextField control={form.control} name="channels" label="Analyser channels" hint="Comma separated: HGB, or XN550:HGB for one analyser" />
                  </FullRow>
                  <TextField control={form.control} name="qcGroup" label="QC group" hint="Tests sharing QC, e.g. GLU for fasting and post-meal sugar" />
                  <NumberField control={form.control} name="deltaThresholdPercent" label="Delta check (%)" min={0} hint="Empty: the hospital default" />
                  <SwitchField control={form.control} name="allowAutoValidation" label="Normal analyser results may be verified automatically" />
                  <FullRow>
                    <TextAreaField control={form.control} name="interpretation" label="Interpretation printed on the report" rows={3} maxLength={2000} />
                  </FullRow>
                </FormGrid>
              </section>
            )}
          </div>
          {test && tab === 'ranges' && !test.isPanel && <RangesEditor test={test} />}
          {test && tab === 'components' && test.isPanel && <ComponentsEditor test={test} />}
        </DrawerBody>
        {tab === 'details' && (
          <DrawerFooter>
            <Button type="submit" appearance="primary" disabled={save.isPending} icon={save.isPending ? <Spinner size="tiny" /> : undefined}>
              {test ? 'Save' : 'Create test'}
            </Button>
            <Button onClick={onClose}>Close</Button>
          </DrawerFooter>
        )}
      </form>
    </OverlayDrawer>
  );
}
