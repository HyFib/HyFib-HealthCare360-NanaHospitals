import {
  Caption1,
  Field,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { humanize, isApiError, todayIso } from '@hc360/core';
import {
  applyServerErrors,
  DateField,
  ErrorState,
  FormGrid,
  SelectField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { toOptions, useDepartments } from '@/lib/lookups';
import { addMonths } from '@/modules/hr/shared/dates';
import { EditorDrawer } from '@/modules/hr/shared/EditorDrawer';
import { UserPicker } from '@/modules/hr/shared/UserPicker';
import { qualityApi, qualityKeys, qualityQueries, type RiskDetail } from './api';
import { RatingBadge } from './badges';
import { IMPACT_LABELS, LIKELIHOOD_LABELS, RISK_CATEGORIES, RISK_STATUSES, riskStatusLabel } from './labels';
import { residualProblem, riskRating, riskScore } from './rules';

const level = z.number({ error: 'Choose 1 to 5.' }).int().min(1).max(5);

const schema = z
  .object({
    title: z.string().trim().min(1, 'Name the risk in a few words.').max(200),
    description: z.string().trim().max(2000),
    category: z.enum(RISK_CATEGORIES),
    departmentId: z.string().nullable(),
    likelihood: level,
    impact: level,
    residualLikelihood: z.number().int().min(1).max(5).nullable(),
    residualImpact: z.number().int().min(1).max(5).nullable(),
    existingControls: z.string().trim().max(2000),
    mitigationPlan: z.string().trim().max(2000),
    owner: z.object({ id: z.string(), name: z.string() }).nullable(),
    reviewDate: z.string({ error: 'Choose the next review date.' }).min(1, 'Choose the next review date.'),
    status: z.enum(RISK_STATUSES),
  })
  .superRefine((v, ctx) => {
    const problem = residualProblem(v.likelihood, v.impact, v.residualLikelihood, v.residualImpact);
    if (problem) {
      ctx.addIssue({ code: 'custom', path: ['residualImpact'], message: problem });
    }
  });

type Values = z.infer<typeof schema>;

const useStyles = makeStyles({
  scores: {
    display: 'flex',
    gap: '16px',
    flexWrap: 'wrap',
    alignItems: 'center',
    padding: '8px 10px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
});

const levelOptions = (labels: readonly string[]) =>
  labels.map((label, index) => ({ value: String(index + 1), label: `${index + 1} · ${label}` }));

/** Add or edit a risk; `riskId` null creates one. Scores and ratings are shown live as the levels are chosen. */
export function RiskDrawer({ riskId, onClose }: { riskId: string | null; onClose: () => void }) {
  const risk = useQuery({ ...qualityQueries.risk(riskId ?? ''), enabled: riskId !== null });
  if (riskId && !risk.data) {
    return (
      <EditorDrawer open title="Risk" dirty={false} onClose={onClose} onSubmit={(e) => e.preventDefault()} busy={false}>
        {risk.error ? (
          <ErrorState error={risk.error} onRetry={() => void risk.refetch()} />
        ) : (
          <Spinner label="Loading…" />
        )}
      </EditorDrawer>
    );
  }
  return <RiskForm risk={risk.data ?? null} onClose={onClose} />;
}

function RiskForm({ risk, onClose }: { risk: RiskDetail | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const [busy, setBusy] = useState(false);
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      title: risk?.title ?? '',
      description: risk?.description ?? '',
      category: risk?.category ?? 'PatientSafety',
      departmentId: risk?.departmentId ?? null,
      likelihood: risk?.likelihood ?? 3,
      impact: risk?.impact ?? 3,
      residualLikelihood: risk?.residualLikelihood ?? null,
      residualImpact: risk?.residualImpact ?? null,
      existingControls: risk?.existingControls ?? '',
      mitigationPlan: risk?.mitigationPlan ?? '',
      owner: risk?.ownerUserId ? { id: risk.ownerUserId, name: risk.ownerName ?? 'Owner' } : null,
      reviewDate: risk?.reviewDate ?? addMonths(todayIso(), 3),
      status: risk?.status ?? 'Open',
    },
  });
  // The version the form was filled from, not a newer one read since (REL-04).
  const [loadedVersion] = useState(risk?.version ?? null);
  const [likelihood, impact, rl, ri] = useWatch({
    control: form.control,
    name: ['likelihood', 'impact', 'residualLikelihood', 'residualImpact'],
  });
  const inherent = riskScore(likelihood, impact);
  const residual = rl && ri ? riskScore(rl, ri) : null;

  const submit = async (v: Values) => {
    setBusy(true);
    const request = {
      title: v.title.trim(),
      description: v.description.trim() || null,
      category: v.category,
      departmentId: v.departmentId,
      likelihood: v.likelihood,
      impact: v.impact,
      residualLikelihood: v.residualLikelihood,
      residualImpact: v.residualImpact,
      existingControls: v.existingControls.trim() || null,
      mitigationPlan: v.mitigationPlan.trim() || null,
      ownerUserId: v.owner?.id ?? null,
      reviewDate: v.reviewDate,
      status: v.status,
      version: loadedVersion,
    };
    try {
      if (risk) {
        await qualityApi.updateRisk(risk.id, request);
        notify.success('Risk saved');
      } else {
        const created = await qualityApi.createRisk(request);
        notify.success('Risk added', `${created.riskNumber} · ${created.rating} (${created.score})`);
      }
      void queryClient.invalidateQueries({ queryKey: qualityKeys.risks });
      void queryClient.invalidateQueries({ queryKey: qualityKeys.dashboard });
      onClose();
    } catch (error) {
      if (isApiError(error) && error.code === 'concurrency') {
        notify.error(error, 'Someone else changed this risk');
        void queryClient.invalidateQueries({ queryKey: qualityKeys.risks });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the risk');
      }
    } finally {
      setBusy(false);
    }
  };

  // Levels are numbers in the form; the select works with strings.
  const levelField = (
    name: 'likelihood' | 'impact' | 'residualLikelihood' | 'residualImpact',
    label: string,
    labels: readonly string[],
    optional?: boolean,
  ) => (
    <Controller
      control={form.control}
      name={name}
      render={({ field, fieldState }) => (
        <Field
          label={label}
          required={!optional}
          validationMessage={fieldState.error?.message}
          validationState={fieldState.error ? 'error' : 'none'}
        >
          <Select
            value={field.value === null ? '' : String(field.value)}
            onChange={(_, d) => field.onChange(d.value === '' ? null : Number(d.value))}
            onBlur={field.onBlur}
          >
            {optional && <option value="">Not assessed</option>}
            {levelOptions(labels).map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </Select>
        </Field>
      )}
    />
  );

  return (
    <EditorDrawer
      open
      size="large"
      title={risk ? `${risk.riskNumber} · ${risk.title}` : 'Add a risk'}
      dirty={form.formState.isDirty}
      onClose={onClose}
      onSubmit={form.handleSubmit(submit)}
      busy={busy}
      submitLabel={risk ? 'Save risk' : 'Add risk'}
    >
      <TextField
        control={form.control}
        name="title"
        label="Risk"
        required
        autoFocus
        maxLength={200}
        hint="For example: patient falls in the dialysis unit"
      />
      <TextAreaField
        control={form.control}
        name="description"
        label="Description"
        rows={2}
        maxLength={2000}
        placeholder="Cause, event and consequence"
      />
      <FormGrid columns={2} minColumnWidth={220}>
        <SelectField
          control={form.control}
          name="category"
          label="Category"
          required
          options={RISK_CATEGORIES.map((c) => ({ value: c, label: humanize(c) }))}
        />
        <SelectField
          control={form.control}
          name="departmentId"
          label="Department"
          placeholder="Hospital-wide"
          options={toOptions(departments.data)}
        />
      </FormGrid>
      <Text weight="semibold">Before controls (inherent risk)</Text>
      <FormGrid columns={2} minColumnWidth={200}>
        {levelField('likelihood', 'Likelihood', LIKELIHOOD_LABELS)}
        {levelField('impact', 'Impact', IMPACT_LABELS)}
      </FormGrid>
      <TextAreaField
        control={form.control}
        name="existingControls"
        label="Controls already in place"
        rows={2}
        maxLength={2000}
      />
      <Text weight="semibold">After controls (residual risk)</Text>
      <FormGrid columns={2} minColumnWidth={200}>
        {levelField('residualLikelihood', 'Likelihood', LIKELIHOOD_LABELS, true)}
        {levelField('residualImpact', 'Impact', IMPACT_LABELS, true)}
      </FormGrid>
      <div className={styles.scores} role="status">
        <span>
          Inherent: <RatingBadge rating={riskRating(inherent)} score={inherent} />
        </span>
        <span>
          Residual:{' '}
          {residual !== null ? (
            <RatingBadge rating={riskRating(residual)} score={residual} />
          ) : (
            <Caption1>not assessed</Caption1>
          )}
        </span>
      </div>
      {residual !== null && residual > inherent && (
        <MessageBar intent="error">
          <MessageBarBody>The residual risk cannot be higher than the inherent risk.</MessageBarBody>
        </MessageBar>
      )}
      <TextAreaField control={form.control} name="mitigationPlan" label="Plan to reduce it" rows={2} maxLength={2000} />
      <FormGrid columns={2} minColumnWidth={220}>
        <Controller
          control={form.control}
          name="owner"
          render={({ field }) => <UserPicker label="Risk owner" value={field.value} onChange={field.onChange} />}
        />
        <DateField control={form.control} name="reviewDate" label="Next review" required />
        <SelectField
          control={form.control}
          name="status"
          label="Status"
          required
          options={RISK_STATUSES.map((s) => ({ value: s, label: riskStatusLabel[s] }))}
        />
      </FormGrid>
    </EditorDrawer>
  );
}
