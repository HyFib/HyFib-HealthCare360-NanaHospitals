import { Field, Input } from '@fluentui/react-components';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  FormGrid,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useQueryClient } from '@tanstack/react-query';
import { useId } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { EditorDrawer } from '@/modules/hr/shared/EditorDrawer';
import { qualityApi, qualityKeys, type IndicatorDto } from './api';
import { INDICATOR_FREQUENCIES, INDICATOR_UNITS, indicatorUnitLabel } from './labels';
import { defaultMultiplier } from './rules';

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a short code.')
    .max(32)
    .regex(/^[A-Za-z0-9_-]+$/, "Use letters, digits, '-' or '_'."),
  name: z.string().trim().min(1, 'Enter the indicator name.').max(200),
  category: z.string().trim().min(1, 'Enter a category, e.g. Patient safety.').max(64),
  numeratorDefinition: z.string().trim().min(1, 'Define what is counted.').max(500),
  denominatorDefinition: z.string().trim().min(1, 'Define what it is divided by.').max(500),
  unit: z.enum(INDICATOR_UNITS),
  multiplier: z.number().positive('The multiplier must be above zero.').nullable(),
  target: z.number().nullable(),
  direction: z.enum(['HigherIsBetter', 'LowerIsBetter']),
  frequency: z.enum(INDICATOR_FREQUENCIES),
  nabhReference: z.string().trim().max(32),
  sortOrder: z.number({ error: 'Enter the order.' }).int().min(0).max(10_000),
  isActive: z.boolean(),
});

type Values = z.infer<typeof schema>;

/** Define (or tune) a quality indicator: definitions, unit, target and direction. */
export function IndicatorDrawer({
  indicator,
  categories,
  onClose,
}: {
  indicator: IndicatorDto | null;
  categories: string[];
  onClose: () => void;
}) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const listId = useId();
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: indicator?.code ?? '',
      name: indicator?.name ?? '',
      category: indicator?.category ?? categories[0] ?? '',
      numeratorDefinition: indicator?.numeratorDefinition ?? '',
      denominatorDefinition: indicator?.denominatorDefinition ?? '',
      unit: indicator?.unit ?? 'Percent',
      multiplier: indicator && indicator.multiplier !== defaultMultiplier(indicator.unit) ? indicator.multiplier : null,
      target: indicator?.target ?? null,
      direction: indicator?.direction ?? 'HigherIsBetter',
      frequency: indicator?.frequency ?? 'Monthly',
      nabhReference: indicator?.nabhReference ?? '',
      sortOrder: indicator?.sortOrder ?? 100,
      isActive: indicator?.isActive ?? true,
    },
  });
  const unit = useWatch({ control: form.control, name: 'unit' });

  const submit = async (v: Values) => {
    try {
      await qualityApi.saveIndicator(indicator?.id ?? null, {
        ...v,
        code: v.code.toUpperCase(),
        nabhReference: v.nabhReference || null,
        version: indicator?.version ?? null,
      });
      notify.success('Indicator saved');
      void queryClient.invalidateQueries({ queryKey: qualityKeys.indicators });
      onClose();
    } catch (error) {
      if (isApiError(error) && error.isConflict && error.code !== 'concurrency') {
        form.setError('code', { type: 'server', message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the indicator');
      }
    }
  };

  return (
    <EditorDrawer
      open
      size="medium"
      title={indicator ? indicator.name : 'Add quality indicator'}
      dirty={form.formState.isDirty}
      onClose={onClose}
      onSubmit={form.handleSubmit(submit)}
      busy={form.formState.isSubmitting}
    >
      <FormGrid columns={2} minColumnWidth={160}>
        <TextField control={form.control} name="code" label="Code" required autoFocus maxLength={32} />
        <TextField control={form.control} name="nabhReference" label="NABH reference" maxLength={32} />
      </FormGrid>
      <TextField control={form.control} name="name" label="Name" required maxLength={200} />
      <Controller
        control={form.control}
        name="category"
        render={({ field, fieldState }) => (
          <Field
            label="Category"
            required
            validationMessage={fieldState.error?.message}
            validationState={fieldState.error ? 'error' : 'none'}
          >
            <Input
              value={field.value}
              onChange={(_, d) => field.onChange(d.value)}
              onBlur={field.onBlur}
              list={listId}
              maxLength={64}
            />
            <datalist id={listId}>
              {categories.map((c) => (
                <option key={c} value={c} />
              ))}
            </datalist>
          </Field>
        )}
      />
      <TextAreaField
        control={form.control}
        name="numeratorDefinition"
        label="Numerator (what is counted)"
        required
        rows={2}
        maxLength={500}
      />
      <TextAreaField
        control={form.control}
        name="denominatorDefinition"
        label="Denominator (divided by)"
        required
        rows={2}
        maxLength={500}
      />
      <FormGrid columns={2} minColumnWidth={160}>
        <SelectField
          control={form.control}
          name="unit"
          label="Unit"
          required
          options={INDICATOR_UNITS.map((u) => ({ value: u, label: indicatorUnitLabel[u] }))}
        />
        <NumberField
          control={form.control}
          name="multiplier"
          label="Multiplier"
          min={0}
          hint={`Blank uses ${defaultMultiplier(unit).toLocaleString('en-IN')}`}
        />
        <NumberField control={form.control} name="target" label="Target" hint="Blank: no target" />
        <SelectField
          control={form.control}
          name="frequency"
          label="Measured"
          required
          options={[
            { value: 'Monthly', label: 'Every month' },
            { value: 'Quarterly', label: 'Every quarter' },
            { value: 'Annually', label: 'Every financial year' },
          ]}
        />
        <NumberField control={form.control} name="sortOrder" label="Order" required min={0} />
      </FormGrid>
      <ChoiceField
        control={form.control}
        name="direction"
        label="Good performance is"
        options={[
          { value: 'HigherIsBetter', label: 'At or above the target' },
          { value: 'LowerIsBetter', label: 'At or below the target' },
        ]}
      />
      <SwitchField control={form.control} name="isActive" label="In use (shown on the scorecard)" />
    </EditorDrawer>
  );
}
