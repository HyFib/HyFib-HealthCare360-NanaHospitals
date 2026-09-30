import {
  Button,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  NumberField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm } from 'react-hook-form';
import { z } from 'zod';
import { safetyTablesApi, type DoseLimitDto, type DrugInteractionDto } from './prescriptionsApi';

const useStyles = makeStyles({ form: { display: 'flex', flexDirection: 'column', gap: '12px' } });

const interactionSchema = z
  .object({
    drugA: z.string().trim().min(1, 'Enter the first generic drug.').max(100),
    drugB: z.string().trim().min(1, 'Enter the second generic drug.').max(100),
    severity: z.enum(['Major', 'Moderate', 'Minor']),
    effect: z.string().trim().min(1, 'Describe what happens.').max(500),
    management: z.string().max(500),
    isActive: z.boolean(),
  })
  .superRefine((v, ctx) => {
    if (v.drugA.trim().toLowerCase() === v.drugB.trim().toLowerCase()) {
      ctx.addIssue({ code: 'custom', path: ['drugB'], message: 'Choose two different drugs.' });
    }
  });

type InteractionValues = z.infer<typeof interactionSchema>;

/** Create or edit one drug–drug interaction. Major interactions block signing unless overridden with a reason. */
export function InteractionDrawer({
  interaction,
  onClose,
}: {
  interaction: DrugInteractionDto | 'new' | null;
  onClose: () => void;
}) {
  return (
    <OverlayDrawer
      open={interaction !== null}
      position="end"
      size="medium"
      onOpenChange={(_, d) => !d.open && onClose()}
    >
      {interaction !== null && (
        <InteractionForm
          key={interaction === 'new' ? 'new' : interaction.id}
          interaction={interaction === 'new' ? null : interaction}
          onClose={onClose}
        />
      )}
    </OverlayDrawer>
  );
}

function InteractionForm({ interaction, onClose }: { interaction: DrugInteractionDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<InteractionValues>({
    resolver: zodResolver(interactionSchema),
    defaultValues: {
      drugA: interaction?.drugA ?? '',
      drugB: interaction?.drugB ?? '',
      severity: interaction?.severity ?? 'Moderate',
      effect: interaction?.effect ?? '',
      management: interaction?.management ?? '',
      isActive: interaction?.isActive ?? true,
    },
  });
  const save = useMutation({
    mutationFn: (v: InteractionValues) =>
      safetyTablesApi.saveInteraction(interaction?.id ?? null, {
        drugA: v.drugA.trim(),
        drugB: v.drugB.trim(),
        severity: v.severity,
        effect: v.effect.trim(),
        management: v.management.trim() || null,
        isActive: v.isActive,
        version: interaction?.version ?? null,
      }),
    onSuccess: () => {
      notify.success('Interaction saved', 'It applies to every prescription checked from now on.');
      void queryClient.invalidateQueries({ queryKey: ['emr', 'drug-interactions'] });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'interaction.exists') {
        form.setError('drugB', { message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the interaction');
      }
    },
  });

  return (
    <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          {interaction ? `${interaction.drugA} + ${interaction.drugB}` : 'Add interaction'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.form}>
          <MessageBar intent="info">
            <MessageBarBody>
              Use generic ingredient names (warfarin, potassium chloride). They are matched as whole words against each
              medicine's composition.
            </MessageBarBody>
          </MessageBar>
          <TextField
            control={form.control}
            name="drugA"
            label="First drug (generic)"
            required
            autoFocus
            maxLength={100}
          />
          <TextField control={form.control} name="drugB" label="Second drug (generic)" required maxLength={100} />
          <ChoiceField
            control={form.control}
            name="severity"
            label="Severity"
            required
            options={[
              { value: 'Major', label: 'Major (blocks signing)' },
              { value: 'Moderate', label: 'Moderate (warning)' },
              { value: 'Minor', label: 'Minor (warning)' },
            ]}
          />
          <TextAreaField
            control={form.control}
            name="effect"
            label="Effect"
            required
            rows={2}
            maxLength={500}
            placeholder="e.g. Additive bleeding risk"
          />
          <TextAreaField
            control={form.control}
            name="management"
            label="What to do"
            rows={2}
            maxLength={500}
            placeholder="e.g. Avoid; if essential, monitor INR closely"
          />
          <SwitchField control={form.control} name="isActive" label="Active (checked on prescriptions)" />
        </div>
      </DrawerBody>
      <DrawerFooter>
        <Button
          type="submit"
          appearance="primary"
          disabled={save.isPending}
          icon={save.isPending ? <Spinner size="tiny" /> : undefined}
        >
          Save
        </Button>
        <Button onClick={onClose}>Cancel</Button>
      </DrawerFooter>
    </form>
  );
}

const doseLimitSchema = z.object({
  genericName: z.string().trim().min(1, 'Enter the generic name.').max(100),
  maxDailyDoseMg: z
    .number({ error: 'Enter the maximum daily dose.' })
    .positive('Enter a dose above zero.')
    .max(999999)
    .nullable(),
  route: z.string().max(32),
  notes: z.string().max(300),
  isActive: z.boolean(),
});

type DoseLimitValues = z.infer<typeof doseLimitSchema>;

/** Create or edit one maximum adult daily dose (summed per ingredient across all current medicines). */
export function DoseLimitDrawer({ limit, onClose }: { limit: DoseLimitDto | 'new' | null; onClose: () => void }) {
  return (
    <OverlayDrawer open={limit !== null} position="end" size="small" onOpenChange={(_, d) => !d.open && onClose()}>
      {limit !== null && (
        <DoseLimitForm
          key={limit === 'new' ? 'new' : limit.id}
          limit={limit === 'new' ? null : limit}
          onClose={onClose}
        />
      )}
    </OverlayDrawer>
  );
}

function DoseLimitForm({ limit, onClose }: { limit: DoseLimitDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const form = useForm<DoseLimitValues>({
    resolver: zodResolver(doseLimitSchema),
    defaultValues: {
      genericName: limit?.genericName ?? '',
      maxDailyDoseMg: limit?.maxDailyDoseMg ?? null,
      route: limit?.route ?? '',
      notes: limit?.notes ?? '',
      isActive: limit?.isActive ?? true,
    },
  });
  const save = useMutation({
    mutationFn: (v: DoseLimitValues) =>
      safetyTablesApi.saveDoseLimit(limit?.id ?? null, {
        genericName: v.genericName.trim(),
        maxDailyDoseMg: v.maxDailyDoseMg ?? 0,
        route: v.route.trim() || null,
        notes: v.notes.trim() || null,
        isActive: v.isActive,
        version: limit?.version ?? null,
      }),
    onSuccess: () => {
      notify.success('Dose limit saved');
      void queryClient.invalidateQueries({ queryKey: ['emr', 'dose-limits'] });
      onClose();
    },
    onError: (error) => {
      if (isApiError(error) && error.code === 'dose_limit.exists') {
        form.setError('genericName', { message: error.userMessage });
      } else if (!applyServerErrors(error, form.setError)) {
        notify.error(error, 'Could not save the dose limit');
      }
    },
  });

  return (
    <form
      onSubmit={form.handleSubmit((v) => {
        if (v.maxDailyDoseMg === null) {
          form.setError('maxDailyDoseMg', { message: 'Enter the maximum daily dose.' });
          return;
        }
        save.mutate(v);
      })}
      noValidate
      style={{ display: 'contents' }}
    >
      <DrawerHeader>
        <DrawerHeaderTitle
          action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
        >
          {limit ? limit.genericName : 'Add dose limit'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.form}>
          <TextField
            control={form.control}
            name="genericName"
            label="Generic name"
            required
            autoFocus
            maxLength={100}
            placeholder="e.g. paracetamol"
          />
          <NumberField
            control={form.control}
            name="maxDailyDoseMg"
            label="Maximum per day (mg)"
            required
            min={0}
            step={1}
          />
          <TextField
            control={form.control}
            name="route"
            label="Route"
            maxLength={32}
            hint="Leave blank for any route, or e.g. oral"
          />
          <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={300} />
          <SwitchField control={form.control} name="isActive" label="Active (checked on prescriptions)" />
        </div>
      </DrawerBody>
      <DrawerFooter>
        <Button
          type="submit"
          appearance="primary"
          disabled={save.isPending}
          icon={save.isPending ? <Spinner size="tiny" /> : undefined}
        >
          Save
        </Button>
        <Button onClick={onClose}>Cancel</Button>
      </DrawerFooter>
    </form>
  );
}
