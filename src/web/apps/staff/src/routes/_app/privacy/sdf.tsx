import { Button, Caption1, Checkbox, makeStyles, Spinner, Text, tokens } from '@fluentui/react-components';
import { AddRegular, DeleteRegular, SaveRegular, ShieldTaskRegular, SendRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatPhone, normalizePhone } from '@hc360/core';
import {
  applyServerErrors,
  DateField,
  EmptyState,
  ErrorState,
  FormGrid,
  LoadingState,
  Page,
  PageHeader,
  Section,
  SelectField,
  StatusBadge,
  TextAreaField,
  TextField,
  useLeaveGuard,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { createFileRoute } from '@tanstack/react-router';
import { useState } from 'react';
import { Controller, useFieldArray, useForm } from 'react-hook-form';
import { z } from 'zod';
import { RequirePermission } from '@/modules/hr/shared/NoAccess';
import { Stack } from '@/modules/hr/shared/layout';
import {
  dpdpApi,
  dpdpKeys,
  dpdpQueries,
  RISK_LEVELS,
  type DataAuditDto,
  type DpiaListItem,
  type SdfSettings,
} from '@/modules/privacy/dpdpApi';
import { PlainTable } from '@/modules/privacy/PlainTable';

/** The Significant Data Fiduciary page (DPDP Act s.10; Rules r13): the DPO, the impact assessments and the data audits. */
export const SDF_PERMISSION = 'privacy.sdf.manage';

export const Route = createFileRoute('/_app/privacy/sdf')({
  component: () => (
    <RequirePermission permission={SDF_PERMISSION} what="the Significant Data Fiduciary records">
      <SdfPage />
    </RequirePermission>
  ),
});

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  footer: { display: 'flex', justifyContent: 'flex-end', gap: '12px', alignItems: 'center', flexWrap: 'wrap' },
  row: { display: 'flex', gap: '8px', alignItems: 'flex-end', flexWrap: 'wrap' },
  grow: { flexGrow: 1, minWidth: '220px' },
});

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const LEVEL_OPTIONS = RISK_LEVELS.map((level) => ({ value: level, label: level }));

function today(offsetMonths = 0) {
  const d = new Date();
  d.setMonth(d.getMonth() + offsetMonths);
  return d.toISOString().slice(0, 10);
}

function SdfPage() {
  const overview = useQuery(dpdpQueries.sdf());
  return (
    <Page maxWidth={1200}>
      <PageHeader
        title="Significant data fiduciary"
        subtitle="The Data Protection Officer, the data protection impact assessments and the yearly data audit (DPDP Act s.10, Rules 2025 rule 13)."
        icon={<ShieldTaskRegular />}
      />
      {overview.error ? (
        <ErrorState error={overview.error} onRetry={() => void overview.refetch()} />
      ) : !overview.data ? (
        <LoadingState />
      ) : (
        <Stack>
          <Text>
            {overview.data.dpias} impact assessment(s): {overview.data.dpiasDueSoon} due for review within 30 days,{' '}
            {overview.data.dpiasOverdue} overdue. Next data audit: {overview.data.nextAuditDue ?? 'none recorded yet'}
            {overview.data.auditOverdue ? ' (overdue)' : ''}.
          </Text>
          <DpoSection settings={overview.data.settings} />
          <DpiaSection />
          <AuditSection />
        </Stack>
      )}
    </Page>
  );
}

// ------------------------------------------------------------------ the DPO

const dpoSchema = z
  .object({
    designated: z.boolean(),
    notifiedOn: z.string().nullable(),
    name: z.string().trim().max(200),
    designation: z.string().trim().max(100),
    email: z.string().trim().max(200),
    phone: z.string().trim().max(20),
    address: z.string().trim().max(500),
    basedInIndia: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (v.email && !EMAIL.test(v.email)) {
      ctx.addIssue({ code: 'custom', path: ['email'], message: 'Enter a valid e-mail address.' });
    }
    if (v.phone && normalizePhone(v.phone) === null) {
      ctx.addIssue({ code: 'custom', path: ['phone'], message: 'Enter a valid phone number.' });
    }
    if (v.designated && (!v.name || !v.email || !v.phone || !v.basedInIndia)) {
      ctx.addIssue({
        code: 'custom',
        path: ['name'],
        message:
          'A Significant Data Fiduciary names a Data Protection Officer based in India, with an e-mail address and a phone number.',
      });
    }
  });

type DpoValues = z.infer<typeof dpoSchema>;

function DpoSection({ settings }: { settings: SdfSettings }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<DpoValues>({
    resolver: zodResolver(dpoSchema),
    defaultValues: {
      designated: settings.designated,
      notifiedOn: settings.notifiedOn,
      name: settings.dpo.name ?? '',
      designation: settings.dpo.designation ?? '',
      email: settings.dpo.email ?? '',
      phone: settings.dpo.phone ? formatPhone(settings.dpo.phone) : '',
      address: settings.dpo.address ?? '',
      basedInIndia: settings.dpo.basedInIndia,
    },
  });
  const save = useMutation({
    mutationFn: (v: DpoValues) =>
      dpdpApi.saveSdfSettings({
        designated: v.designated,
        notifiedOn: v.notifiedOn || null,
        dpo: {
          name: v.name || null,
          designation: v.designation || null,
          email: v.email || null,
          phone: v.phone ? (normalizePhone(v.phone) ?? v.phone) : null,
          address: v.address || null,
          basedInIndia: v.basedInIndia,
        },
        version: settings.version ?? null,
      }),
    onSuccess: async (_, v) => {
      notify.success('Saved', 'The Data Protection Officer is shown on the privacy notice.');
      form.reset(v);
      await queryClient.invalidateQueries({ queryKey: dpdpKeys.sdf });
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save');
      }
    },
  });
  useLeaveGuard({ dirty: form.formState.isDirty, busy: save.isPending });

  return (
    <Section
      title="Designation and Data Protection Officer"
      description="Once notified by the Central Government, the hospital names a DPO based in India, who answers to its board and is the point of contact for grievances."
    >
      <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate>
        <Stack>
          <Controller
            control={form.control}
            name="designated"
            render={({ field }) => (
              <Checkbox
                checked={field.value}
                onChange={(_, data) => field.onChange(data.checked === true)}
                label="The hospital is notified as a Significant Data Fiduciary"
              />
            )}
          />
          <FormGrid columns={2} minColumnWidth={220}>
            <DateField control={form.control} name="notifiedOn" label="Notified on" />
            <TextField control={form.control} name="name" label="DPO's name" maxLength={200} autoComplete="off" />
            <TextField control={form.control} name="designation" label="Designation" maxLength={100} />
            <TextField control={form.control} name="email" label="E-mail address" type="email" maxLength={200} />
            <TextField control={form.control} name="phone" label="Phone number" type="tel" maxLength={20} />
          </FormGrid>
          <TextAreaField control={form.control} name="address" label="Postal address" rows={2} maxLength={500} />
          <Controller
            control={form.control}
            name="basedInIndia"
            render={({ field }) => (
              <Checkbox
                checked={field.value}
                onChange={(_, data) => field.onChange(data.checked === true)}
                label="The DPO is based in India (s.10(2)(a))"
              />
            )}
          />
          <div className={styles.footer}>
            <Button
              type="submit"
              appearance="primary"
              icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
              disabled={save.isPending || !form.formState.isDirty}
            >
              Save
            </Button>
          </div>
        </Stack>
      </form>
    </Section>
  );
}

// ------------------------------------------------------------------ impact assessments

const dpiaSchema = z.object({
  activity: z.string().trim().min(1, 'Name the processing activity.').max(200),
  description: z.string().trim().min(1, 'Describe the processing.').max(4000),
  dataCategories: z.string().trim().min(1, 'List the personal data it processes.').max(1000),
  dataPrincipals: z.string().trim().min(1, 'Say whose data it is.').max(300),
  purpose: z.string().trim().min(1, 'Say what it is for.').max(1000),
  risks: z
    .array(
      z.object({
        description: z.string().trim().min(1, 'Describe the risk.').max(1000),
        likelihood: z.enum(['Low', 'Medium', 'High']),
        impact: z.enum(['Low', 'Medium', 'High']),
      }),
    )
    .min(1, 'Assess at least one risk.'),
  mitigations: z
    .array(
      z.object({
        description: z.string().trim().min(1, 'Describe the measure.').max(1000),
        owner: z.string().max(200),
      }),
    )
    .min(1, 'Record what is done about the risks.'),
  reviewDue: z.string({ error: 'Set a review date.' }).min(1, 'Set a review date.'),
});

type DpiaValues = z.infer<typeof dpiaSchema>;

const EMPTY_DPIA: DpiaValues = {
  activity: '',
  description: '',
  dataCategories: '',
  dataPrincipals: '',
  purpose: '',
  risks: [{ description: '', likelihood: 'Medium', impact: 'Medium' }],
  mitigations: [{ description: '', owner: '' }],
  reviewDue: today(12),
};

function DpiaSection() {
  const dpias = useQuery(dpdpQueries.dpias());
  const [writing, setWriting] = useState(false);
  return (
    <Section
      title="Data protection impact assessments"
      description="One for each processing activity, reviewed within twelve months. Each is approved by someone other than whoever sends it, under Approvals."
      actions={
        !writing && (
          <Button icon={<AddRegular />} onClick={() => setWriting(true)}>
            New assessment
          </Button>
        )
      }
    >
      <Stack>
        {writing && <DpiaForm onDone={() => setWriting(false)} />}
        {dpias.error ? (
          <ErrorState error={dpias.error} onRetry={() => void dpias.refetch()} />
        ) : !dpias.data ? (
          <LoadingState />
        ) : dpias.data.length === 0 ? (
          <EmptyState
            title="No impact assessments yet"
            description="Write one for each way the hospital processes personal data."
          />
        ) : (
          <DpiaTable rows={dpias.data} />
        )}
      </Stack>
    </Section>
  );
}

function DpiaTable({ rows }: { rows: DpiaListItem[] }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const submit = useMutation({
    mutationFn: async (row: DpiaListItem) => {
      const detail = await dpdpApi.getDpia(row.id);
      return dpdpApi.submitDpia(row.id, 'Ready for approval.', detail.version);
    },
    onSuccess: async () => {
      notify.success('Asked for approval', 'Someone else who approves impact assessments decides under Approvals.');
      await queryClient.invalidateQueries({ queryKey: dpdpKeys.dpias });
    },
    onError: (error) => notify.error(error, 'Could not send it for approval'),
  });
  return (
    <PlainTable
      ariaLabel="Impact assessments"
      rows={rows}
      getRowId={(row) => row.id}
      columns={[
        { key: 'number', header: 'Number', render: (r) => r.number, width: 110 },
        { key: 'activity', header: 'Processing activity', render: (r) => r.activity },
        { key: 'risk', header: 'Highest risk', render: (r) => r.highestRisk ?? '—', width: 110 },
        {
          key: 'status',
          header: 'Status',
          width: 170,
          render: (r) =>
            r.awaitingApprovalSince ? (
              <StatusBadge status="Waiting" tone="warning" label="Waiting for approval" />
            ) : (
              <StatusBadge status={r.status} tone={r.status === 'Approved' ? 'success' : 'informative'} />
            ),
        },
        {
          key: 'review',
          header: 'Review by',
          width: 150,
          render: (r) => (r.reviewOverdue ? `${r.reviewDue} (overdue)` : r.reviewDue),
        },
        {
          key: 'actions',
          header: 'Action',
          width: 190,
          render: (r) =>
            r.status === 'Draft' && !r.awaitingApprovalSince ? (
              <Button
                size="small"
                icon={<SendRegular />}
                disabled={submit.isPending}
                onClick={() => submit.mutate(r)}
                aria-label={`Send ${r.number} for approval`}
              >
                Send for approval
              </Button>
            ) : null,
        },
      ]}
    />
  );
}

function DpiaForm({ onDone }: { onDone: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<DpiaValues>({ resolver: zodResolver(dpiaSchema), defaultValues: EMPTY_DPIA });
  const risks = useFieldArray({ control: form.control, name: 'risks' });
  const mitigations = useFieldArray({ control: form.control, name: 'mitigations' });
  const save = useMutation({
    mutationFn: (v: DpiaValues) =>
      dpdpApi.createDpia({
        activity: v.activity,
        description: v.description,
        dataCategories: v.dataCategories
          .split(',')
          .map((c) => c.trim())
          .filter(Boolean),
        dataPrincipals: v.dataPrincipals,
        purpose: v.purpose,
        risks: v.risks,
        mitigations: v.mitigations.map((m) => ({ description: m.description, owner: m.owner.trim() || null })),
        reviewDue: v.reviewDue,
      }),
    onSuccess: async (created) => {
      notify.success(`${created.number} saved as a draft`, 'Send it for approval when it is ready.');
      form.reset(EMPTY_DPIA);
      await queryClient.invalidateQueries({ queryKey: dpdpKeys.sdf });
      onDone();
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the assessment');
      }
    },
  });
  useLeaveGuard({ dirty: form.formState.isDirty, busy: save.isPending });

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate aria-label="New impact assessment">
      <Stack>
        <FormGrid columns={2} minColumnWidth={240}>
          <TextField
            control={form.control}
            name="activity"
            label="Processing activity"
            required
            maxLength={200}
            autoFocus
          />
          <TextField control={form.control} name="dataPrincipals" label="Whose data" required maxLength={300} />
          <TextField
            control={form.control}
            name="dataCategories"
            label="Personal data processed"
            required
            hint="Separate them with commas, for example Name, Mobile number, Lab results."
          />
          <DateField control={form.control} name="reviewDue" label="Review by" required min={today()} max={today(12)} />
        </FormGrid>
        <TextAreaField
          control={form.control}
          name="description"
          label="Description"
          required
          rows={3}
          maxLength={4000}
        />
        <TextAreaField control={form.control} name="purpose" label="Purpose" required rows={2} maxLength={1000} />
        <Text weight="semibold">Risks to the people whose data it is</Text>
        {risks.fields.map((risk, index) => (
          <div key={risk.id} className={styles.row}>
            <div className={styles.grow}>
              <TextField
                control={form.control}
                name={`risks.${index}.description`}
                label={`Risk ${index + 1}`}
                required
              />
            </div>
            <SelectField
              control={form.control}
              name={`risks.${index}.likelihood`}
              label="Likelihood"
              options={LEVEL_OPTIONS}
            />
            <SelectField control={form.control} name={`risks.${index}.impact`} label="Impact" options={LEVEL_OPTIONS} />
            {risks.fields.length > 1 && (
              <Button
                icon={<DeleteRegular />}
                aria-label={`Remove risk ${index + 1}`}
                onClick={() => risks.remove(index)}
              />
            )}
          </div>
        ))}
        <div>
          <Button
            icon={<AddRegular />}
            onClick={() => risks.append({ description: '', likelihood: 'Medium', impact: 'Medium' })}
          >
            Add a risk
          </Button>
        </div>
        <Text weight="semibold">Measures</Text>
        {mitigations.fields.map((measure, index) => (
          <div key={measure.id} className={styles.row}>
            <div className={styles.grow}>
              <TextField
                control={form.control}
                name={`mitigations.${index}.description`}
                label={`Measure ${index + 1}`}
                required
              />
            </div>
            <TextField control={form.control} name={`mitigations.${index}.owner`} label="Who is responsible" />
            {mitigations.fields.length > 1 && (
              <Button
                icon={<DeleteRegular />}
                aria-label={`Remove measure ${index + 1}`}
                onClick={() => mitigations.remove(index)}
              />
            )}
          </div>
        ))}
        <div>
          <Button icon={<AddRegular />} onClick={() => mitigations.append({ description: '', owner: '' })}>
            Add a measure
          </Button>
        </div>
        <div className={styles.footer}>
          <Button onClick={onDone}>Cancel</Button>
          <Button
            type="submit"
            appearance="primary"
            icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : <SaveRegular />}
            disabled={save.isPending}
          >
            Save as draft
          </Button>
        </div>
      </Stack>
    </form>
  );
}

// ------------------------------------------------------------------ data audits

const auditSchema = z.object({
  periodFrom: z.string().min(1, 'Enter when the period starts.'),
  periodTo: z.string().min(1, 'Enter when the period ends.'),
  auditor: z.string().trim().min(1, 'Name the independent data auditor.').max(200),
  scope: z.string().trim().min(1, 'Describe what was audited.').max(2000),
  findings: z.string().trim().max(4000),
  completedOn: z.string().nullable(),
});

type AuditValues = z.infer<typeof auditSchema>;

function AuditSection() {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const audits = useQuery(dpdpQueries.audits());
  const form = useForm<AuditValues>({
    resolver: zodResolver(auditSchema),
    defaultValues: { periodFrom: '', periodTo: '', auditor: '', scope: '', findings: '', completedOn: null },
  });
  const save = useMutation({
    mutationFn: (v: AuditValues) =>
      dpdpApi.createAudit({
        periodFrom: v.periodFrom,
        periodTo: v.periodTo,
        auditor: v.auditor,
        scope: v.scope,
        findings: v.findings || null,
        significantObservations: null,
        completedOn: v.completedOn || null,
        reportedToBoardOn: null,
        reportReference: null,
        nextDue: null,
      }),
    onSuccess: async (saved: DataAuditDto) => {
      notify.success('Data audit recorded', `The next one is due by ${saved.nextDue}.`);
      form.reset();
      await queryClient.invalidateQueries({ queryKey: dpdpKeys.sdf });
    },
    onError: (error) => {
      if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not record the audit');
      }
    },
  });

  return (
    <Section
      title="Data audits"
      description="Every twelve months, by an independent data auditor; its significant observations are reported to the Board."
    >
      <Stack>
        {audits.data && audits.data.length > 0 ? (
          <PlainTable
            ariaLabel="Data audits"
            rows={audits.data}
            getRowId={(row) => row.id}
            columns={[
              { key: 'period', header: 'Period', render: (r) => `${r.periodFrom} to ${r.periodTo}`, width: 220 },
              { key: 'auditor', header: 'Auditor', render: (r) => r.auditor },
              { key: 'completed', header: 'Completed', render: (r) => r.completedOn ?? '—', width: 120 },
              {
                key: 'next',
                header: 'Next due',
                render: (r) => (r.overdue ? `${r.nextDue} (overdue)` : r.nextDue),
                width: 150,
              },
            ]}
          />
        ) : (
          <Caption1 className={styles.muted}>No data audit is recorded yet.</Caption1>
        )}
        <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate aria-label="Record a data audit">
          <Stack>
            <FormGrid columns={2} minColumnWidth={220}>
              <DateField control={form.control} name="periodFrom" label="Period from" required />
              <DateField control={form.control} name="periodTo" label="Period to" required />
              <TextField
                control={form.control}
                name="auditor"
                label="Independent data auditor"
                required
                maxLength={200}
              />
              <DateField control={form.control} name="completedOn" label="Completed on" />
            </FormGrid>
            <TextAreaField control={form.control} name="scope" label="Scope" required rows={2} maxLength={2000} />
            <TextAreaField control={form.control} name="findings" label="Findings" rows={3} maxLength={4000} />
            <div className={styles.footer}>
              <Button type="submit" appearance="primary" icon={<SaveRegular />} disabled={save.isPending}>
                Record the audit
              </Button>
            </div>
          </Stack>
        </form>
      </Stack>
    </Section>
  );
}
