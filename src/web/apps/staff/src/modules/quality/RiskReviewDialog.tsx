import {
  Button,
  Caption1,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Select,
  Spinner,
  Text,
} from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError, todayIso } from '@hc360/core';
import { applyServerErrors, DateField, FormGrid, TextAreaField, useNotify } from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { addDays, addMonths } from '@/modules/hr/shared/dates';
import { qualityApi, qualityKeys, type RiskDetail } from './api';
import { RatingBadge } from './badges';
import { IMPACT_LABELS, LIKELIHOOD_LABELS } from './labels';
import { residualProblem, riskRating, riskScore } from './rules';

const schema = z
  .object({
    likelihood: z.number().int().min(1).max(5),
    impact: z.number().int().min(1).max(5),
    residualLikelihood: z.number().int().min(1).max(5).nullable(),
    residualImpact: z.number().int().min(1).max(5).nullable(),
    existingControls: z.string().trim().max(2000),
    nextReviewDate: z.string({ error: 'Choose the next review date.' }).min(1, 'Choose the next review date.'),
    note: z.string().trim().max(1000),
  })
  .superRefine((v, ctx) => {
    const problem = residualProblem(v.likelihood, v.impact, v.residualLikelihood, v.residualImpact);
    if (problem) {
      ctx.addIssue({ code: 'custom', path: ['residualImpact'], message: problem });
    }
    if (v.nextReviewDate && v.nextReviewDate <= todayIso()) {
      ctx.addIssue({ code: 'custom', path: ['nextReviewDate'], message: 'The next review must be in the future.' });
    }
  });

type Values = z.infer<typeof schema>;

/** Periodic re-assessment: new scores, controls in place and the next review date. */
export function RiskReviewDialog({ open, onClose, risk }: { open: boolean; onClose: () => void; risk: RiskDetail }) {
  return (
    <Dialog open={open} onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 640 }}>{open && <Body onClose={onClose} risk={risk} />}</DialogSurface>
    </Dialog>
  );
}

function Body({ onClose, risk }: { onClose: () => void; risk: RiskDetail }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const [busy, setBusy] = useState(false);
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      likelihood: risk.likelihood,
      impact: risk.impact,
      residualLikelihood: risk.residualLikelihood,
      residualImpact: risk.residualImpact,
      existingControls: risk.existingControls ?? '',
      nextReviewDate: addMonths(todayIso(), 3),
      note: '',
    },
  });
  const [l, i, rl, ri] = useWatch({ control: form.control, name: ['likelihood', 'impact', 'residualLikelihood', 'residualImpact'] });

  const submit = async (v: Values) => {
    setBusy(true);
    try {
      await qualityApi.reviewRisk(risk.id, {
        likelihood: v.likelihood,
        impact: v.impact,
        residualLikelihood: v.residualLikelihood,
        residualImpact: v.residualImpact,
        existingControls: v.existingControls.trim() || null,
        nextReviewDate: v.nextReviewDate,
        note: v.note.trim() || null,
        version: risk.version,
      });
      notify.success('Risk reviewed', `Next review ${v.nextReviewDate}`);
      void queryClient.invalidateQueries({ queryKey: qualityKeys.risks });
      void queryClient.invalidateQueries({ queryKey: qualityKeys.dashboard });
      onClose();
    } catch (error) {
      if (isApiError(error) && error.code === 'concurrency') {
        notify.error(error, 'Someone else changed this risk');
        void queryClient.invalidateQueries({ queryKey: qualityKeys.risks });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not record the review');
      }
    } finally {
      setBusy(false);
    }
  };

  const level = (name: keyof Pick<Values, 'likelihood' | 'impact' | 'residualLikelihood' | 'residualImpact'>, label: string, labels: readonly string[], optional?: boolean) => (
    <Controller
      control={form.control}
      name={name}
      render={({ field, fieldState }) => (
        <Field label={label} validationMessage={fieldState.error?.message} validationState={fieldState.error ? 'error' : 'none'}>
          <Select value={field.value === null ? '' : String(field.value)} onChange={(_, d) => field.onChange(d.value === '' ? null : Number(d.value))}>
            {optional && <option value="">Not assessed</option>}
            {labels.map((text, index) => (
              <option key={text} value={index + 1}>
                {index + 1} · {text}
              </option>
            ))}
          </Select>
        </Field>
      )}
    />
  );

  const inherent = riskScore(l, i);
  const residual = rl && ri ? riskScore(rl, ri) : null;

  return (
    <form onSubmit={form.handleSubmit(submit)} noValidate>
      <DialogBody>
        <DialogTitle>
          Review risk
          <Caption1 block>
            {risk.riskNumber} · {risk.title}
          </Caption1>
        </DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <FormGrid columns={2} minColumnWidth={200}>
            {level('likelihood', 'Likelihood before controls', LIKELIHOOD_LABELS)}
            {level('impact', 'Impact before controls', IMPACT_LABELS)}
            {level('residualLikelihood', 'Likelihood after controls', LIKELIHOOD_LABELS, true)}
            {level('residualImpact', 'Impact after controls', IMPACT_LABELS, true)}
          </FormGrid>
          <Text>
            Inherent <RatingBadge rating={riskRating(inherent)} score={inherent} /> · residual{' '}
            {residual !== null ? <RatingBadge rating={riskRating(residual)} score={residual} /> : 'not assessed'}
          </Text>
          <TextAreaField control={form.control} name="existingControls" label="Controls now in place" rows={2} maxLength={2000} />
          <FormGrid columns={2} minColumnWidth={200}>
            <DateField control={form.control} name="nextReviewDate" label="Next review" required min={addDays(todayIso(), 1)} />
          </FormGrid>
          <TextAreaField control={form.control} name="note" label="Review note" rows={2} maxLength={1000} placeholder="What changed since the last review" />
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose} disabled={busy}>
            Cancel
          </Button>
          <Button type="submit" appearance="primary" disabled={busy} icon={busy ? <Spinner size="tiny" /> : undefined}>
            Record review
          </Button>
        </DialogActions>
      </DialogBody>
    </form>
  );
}
