import {
  Button,
  Caption1,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Radio,
  RadioGroup,
  Spinner,
  Switch,
  Text,
  tokens,
} from '@fluentui/react-components';
import { EyeOffRegular, SendRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatRelative, useHotkey } from '@hc360/core';
import {
  applyServerErrors,
  FormGrid,
  Section,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments } from '@/lib/lookups';
import { usePermission } from '@/lib/permissions';
import { useFormDraft } from '@/lib/useFormDraft';
import { fromIstInput, nowIstInput } from '@/modules/hr/shared/dates';
import { PatientPicker } from '@/modules/patients/PatientPicker';
import { qualityApi, type IncidentReceipt } from './api';
import {
  INCIDENT_SEVERITIES,
  INCIDENT_TYPES,
  incidentTypeLabel,
  SENTINEL_KINDS,
  sentinelKindLabel,
  severityHelp,
  severityLabel,
} from './labels';
import { isSentinel } from './rules';

const MIN_DESCRIPTION = 20;

const schema = z.object({
  type: z
    .enum(INCIDENT_TYPES)
    .nullable()
    .refine((v) => Boolean(v), 'Choose what kind of incident it was.'),
  severity: z.enum(INCIDENT_SEVERITIES),
  occurredAt: z
    .string()
    .min(1, 'Enter when it happened.')
    .refine((v) => {
      const iso = fromIstInput(v);
      return iso !== null && Date.parse(iso) <= Date.now() + 5 * 60_000;
    }, 'The time cannot be in the future.'),
  location: z.string().trim().min(1, 'Say where it happened, e.g. Ward 3B, bed 12.').max(150),
  departmentId: z.string().nullable(),
  patient: z.object({ id: z.string(), uhid: z.string(), fullName: z.string() }).nullable(),
  description: z
    .string()
    .trim()
    .min(MIN_DESCRIPTION, `Describe what happened (at least ${MIN_DESCRIPTION} characters).`)
    .max(4000),
  immediateAction: z.string().trim().max(2000),
  witnesses: z.string().trim().max(500),
  sentinelKind: z.enum(SENTINEL_KINDS).nullable(),
  flagAsSentinel: z.boolean(),
  isAnonymous: z.boolean(),
});

type Values = z.infer<typeof schema>;

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '16px' },
  severity: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(250px, 1fr))', gap: '4px 16px' },
  severityHelp: { color: tokens.colorNeutralForeground3, display: 'block', marginLeft: '28px', marginTop: '-6px' },
  anonymous: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    padding: '12px 14px',
    borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  footer: {
    position: 'sticky',
    bottom: 0,
    display: 'flex',
    justifyContent: 'flex-end',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
    padding: '12px 16px',
    backgroundColor: tokens.colorNeutralBackground1,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    boxShadow: tokens.shadow8,
    borderRadius: tokens.borderRadiusLarge,
    zIndex: 1,
  },
  counter: { color: tokens.colorNeutralForeground3 },
});

function defaults(): Values {
  return {
    type: null,
    severity: 'NearMiss',
    occurredAt: nowIstInput(),
    location: '',
    departmentId: null,
    patient: null,
    description: '',
    immediateAction: '',
    witnesses: '',
    sentinelKind: null,
    flagAsSentinel: false,
    isAnonymous: false,
  };
}

/**
 * Incident report for every staff member (IRMS-01). Plain questions, a reassuring tone (reporting is about learning,
 * not blame) and a clearly explained anonymous option. Ctrl+Enter sends.
 */
export function ReportIncidentForm({
  onReported,
}: {
  onReported: (receipt: IncidentReceipt, anonymous: boolean) => void;
}) {
  const styles = useStyles();
  const notify = useNotify();
  const departments = useDepartments();
  const canLookUpPatients = usePermission('patients.read');
  const [busy, setBusy] = useState(false);
  const [initial] = useState(defaults);
  const form = useForm<Values>({ resolver: zodResolver(schema), defaultValues: initial, mode: 'onTouched' });
  const [severity, sentinelKind, flagAsSentinel, isAnonymous, description] = useWatch({
    control: form.control,
    name: ['severity', 'sentinelKind', 'flagAsSentinel', 'isAnonymous', 'description'],
  });
  // Anonymous reports are never kept as a local draft under the reporter's name.
  const draft = useFormDraft(isAnonymous ? null : form, isAnonymous ? null : 'quality-incident-report');
  const sentinel = isSentinel(severity, sentinelKind, flagAsSentinel);

  const submit = async (values: Values) => {
    const occurredAt = fromIstInput(values.occurredAt);
    if (!values.type || !occurredAt) {
      return;
    }
    setBusy(true);
    try {
      const receipt = await qualityApi.report({
        type: values.type,
        severity: values.severity,
        occurredAt,
        location: values.location.trim(),
        departmentId: values.departmentId,
        patientId: values.patient?.id ?? null,
        involvedStaffUserIds: null,
        description: values.description.trim(),
        immediateAction: values.immediateAction.trim() || null,
        witnesses: values.witnesses.trim() || null,
        sentinelKind: values.sentinelKind,
        flagAsSentinel: values.flagAsSentinel,
        isAnonymous: values.isAnonymous,
      });
      draft.clear();
      form.reset(defaults());
      onReported(receipt, values.isAnonymous);
    } catch (error) {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'The report was not sent');
      }
    } finally {
      setBusy(false);
    }
  };

  useHotkey('mod+enter', () => void form.handleSubmit(submit)(), { allowInInputs: true, enabled: !busy });

  return (
    <form onSubmit={form.handleSubmit(submit)} noValidate className={styles.form}>
      {draft.saved && !isAnonymous && (
        <MessageBar intent="info">
          <MessageBarBody>
            An unsent report from {formatRelative(new Date(draft.saved.savedAt).toISOString())} was found on this
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

      <Section title="What happened">
        <FormGrid columns={3}>
          <SelectField
            control={form.control}
            name="type"
            label="Kind of incident"
            required
            placeholder="Choose"
            options={INCIDENT_TYPES.map((t) => ({ value: t, label: incidentTypeLabel[t] }))}
          />
          <Controller
            control={form.control}
            name="occurredAt"
            render={({ field, fieldState }) => (
              <Field
                label="When it happened"
                required
                validationMessage={fieldState.error?.message}
                validationState={fieldState.error ? 'error' : 'none'}
                hint="Hospital time"
              >
                <Input
                  type="datetime-local"
                  value={field.value}
                  max={nowIstInput()}
                  onChange={(_, d) => field.onChange(d.value)}
                  onBlur={field.onBlur}
                  ref={field.ref}
                />
              </Field>
            )}
          />
          <TextField
            control={form.control}
            name="location"
            label="Where"
            required
            placeholder="Ward, room or area"
            maxLength={150}
          />
          <SelectField
            control={form.control}
            name="departmentId"
            label="Department"
            placeholder="Not sure / not applicable"
            options={toOptions(departments.data)}
          />
          {canLookUpPatients && (
            <Controller
              control={form.control}
              name="patient"
              render={({ field }) => (
                <PatientPicker
                  label="Patient involved (if any)"
                  value={field.value}
                  onChange={field.onChange}
                  hint="Links the report to the patient's record for the quality team."
                />
              )}
            />
          )}
        </FormGrid>
        <div style={{ height: 12 }} />
        <TextAreaField
          control={form.control}
          name="description"
          label="Describe what happened"
          required
          rows={5}
          maxLength={4000}
          placeholder="What happened, in the order it happened. Facts only; there is no need to name who was at fault."
        />
        <Caption1 className={styles.counter}>
          {description.trim().length < MIN_DESCRIPTION
            ? `${MIN_DESCRIPTION - description.trim().length} more characters needed`
            : `${description.trim().length} / 4000`}
        </Caption1>
        <div style={{ height: 8 }} />
        <FormGrid columns={2}>
          <TextAreaField
            control={form.control}
            name="immediateAction"
            label="What was done straight away"
            rows={2}
            maxLength={2000}
            placeholder="For example: doctor informed, vitals checked, area cordoned off"
          />
          <TextAreaField
            control={form.control}
            name="witnesses"
            label="Witnesses"
            rows={2}
            maxLength={500}
            placeholder="Names or roles of people who saw it (optional)"
          />
        </FormGrid>
      </Section>

      <Section
        title="How serious was it"
        description="Your best judgement is enough; the quality team reviews every grade."
      >
        <Controller
          control={form.control}
          name="severity"
          render={({ field }) => (
            <RadioGroup
              value={field.value}
              onChange={(_, d) => field.onChange(d.value)}
              aria-label="How serious was it"
              className={styles.severity}
            >
              {INCIDENT_SEVERITIES.map((s) => (
                <div key={s}>
                  <Radio value={s} label={severityLabel[s]} />
                  <Caption1 className={styles.severityHelp}>{severityHelp[s]}</Caption1>
                </div>
              ))}
            </RadioGroup>
          )}
        />
        <div style={{ height: 12 }} />
        <FormGrid columns={2}>
          <SelectField
            control={form.control}
            name="sentinelKind"
            label="Is it one of these 'never events'?"
            placeholder="No"
            options={SENTINEL_KINDS.map((k) => ({ value: k, label: sentinelKindLabel[k] }))}
          />
          <SwitchField
            control={form.control}
            name="flagAsSentinel"
            label="Treat as a sentinel (very serious) event"
            hint="Alerts the quality team immediately."
          />
        </FormGrid>
        {sentinel && (
          <MessageBar intent="error" layout="multiline" style={{ marginTop: 12 }}>
            <MessageBarBody>
              <MessageBarTitle>This will be treated as a sentinel event</MessageBarTitle>
              The quality team is alerted as soon as you send it, and a root-cause analysis will follow. Make sure the
              patient is safe and the doctor on duty knows.
            </MessageBarBody>
          </MessageBar>
        )}
      </Section>

      <Section>
        <div className={styles.anonymous}>
          <Controller
            control={form.control}
            name="isAnonymous"
            render={({ field }) => (
              <Switch
                checked={field.value}
                onChange={(_, d) => {
                  field.onChange(d.checked);
                  if (d.checked) {
                    draft.clear();
                  }
                }}
                label={
                  <Text weight="semibold">
                    <EyeOffRegular aria-hidden /> Report anonymously
                  </Text>
                }
              />
            )}
          />
          <Text>
            When this is on, your name is not stored with the report, not even in the system's audit records, and alerts
            never name you. The report will not appear under "My reports", and the quality team cannot contact you about
            it, so please give enough detail. You get a report number to quote if you want to follow up.
          </Text>
        </div>
      </Section>

      <div className={styles.footer}>
        <Caption1 className={styles.counter}>Ctrl+Enter sends the report</Caption1>
        <Button
          type="submit"
          appearance="primary"
          size="large"
          icon={busy ? <Spinner size="tiny" aria-hidden /> : <SendRegular />}
          disabled={busy}
        >
          {isAnonymous ? 'Send anonymously' : 'Send report'}
        </Button>
      </div>
    </form>
  );
}
