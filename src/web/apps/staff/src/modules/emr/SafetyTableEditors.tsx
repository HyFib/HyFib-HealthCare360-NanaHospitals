import {
  Button,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  makeStyles,
  MessageBar,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  Spinner,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError } from '@hc360/core';
import {
  applyServerErrors,
  ChoiceField,
  CloseRequestScope,
  NumberField,
  SwitchField,
  TextAreaField,
  TextField,
  useCloseRequests,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useForm, useWatch } from 'react-hook-form';
import { z } from 'zod';
import { askedForApproval, safetyTablesApi, type DoseLimitDto, type DrugInteractionDto } from './prescriptionsApi';
import { doseLimitLoosens, interactionLoosens } from './safety';

const useStyles = makeStyles({ form: { display: 'flex', flexDirection: 'column', gap: '12px' } });

const interactionSchema = z
  .object({
    drugA: z.string().trim().min(1, 'Enter the first generic drug.').max(100),
    drugB: z.string().trim().min(1, 'Enter the second generic drug.').max(100),
    severity: z.enum(['Major', 'Moderate', 'Minor']),
    effect: z.string().trim().min(1, 'Describe what happens.').max(500),
    management: z.string().max(500),
    isActive: z.boolean(),
    reason: z.string().max(500),
  })
  .superRefine((v, ctx) => {
    if (v.drugA.trim().toLowerCase() === v.drugB.trim().toLowerCase()) {
      ctx.addIssue({ code: 'custom', path: ['drugB'], message: 'Choose two different drugs.' });
    }
  });

type InteractionValues = z.infer<typeof interactionSchema>;

/**
 * Create or edit one drug–drug interaction. Major interactions block signing unless overridden with a reason. Adding or
 * strengthening one is saved at once; switching one off, making it less severe or moving it to other drugs is asked for,
 * with a reason, and made once the pharmacy and therapeutics committee approves it.
 */
export function InteractionDrawer({
  interaction,
  onClose,
}: {
  interaction: DrugInteractionDto | 'new' | null;
  onClose: () => void;
}) {
  const closing = useCloseRequests({ onClose });
  return (
    <OverlayDrawer open={interaction !== null} position="end" size="medium" onOpenChange={closing.onOpenChange}>
      {interaction !== null && (
        <CloseRequestScope value={closing}>
          <InteractionForm
            key={interaction === 'new' ? 'new' : interaction.id}
            interaction={interaction === 'new' ? null : interaction}
            onClose={onClose}
          />
        </CloseRequestScope>
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
      reason: '',
    },
  });
  const [drugA, drugB, severity, isActive] = useWatch({
    control: form.control,
    name: ['drugA', 'drugB', 'severity', 'isActive'],
  });
  const loosens = interaction !== null && interactionLoosens(interaction, { drugA, drugB, severity, isActive });
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
        reason: v.reason.trim() || null,
      }),
    onSuccess: (saved) => {
      if (askedForApproval(saved)) {
        notify.success(
          'Asked for approval',
          'The interaction stays as it is until the pharmacy and therapeutics committee approves.',
        );
      } else {
        notify.success('Interaction saved', 'It applies to every prescription checked from now on.');
      }
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

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <form
      onSubmit={form.handleSubmit((v) => {
        if (loosens && !v.reason.trim()) {
          form.setError('reason', { message: 'Say why: it waits for approval.' });
          return;
        }
        save.mutate(v);
      })}
      noValidate
      style={{ display: 'contents' }}
    >
      <DrawerHeader>
        <DrawerHeaderTitle
          action={
            <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
          }
        >
          {interaction ? `${interaction.drugA} + ${interaction.drugB}` : 'Add interaction'}
        </DrawerHeaderTitle>
      </DrawerHeader>
      <DrawerBody>
        <div className={styles.form}>
          <MessageBar intent="info" layout="multiline">
            <MessageBarBody>
              <MessageBarTitle>Generic names</MessageBarTitle>
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
          {interaction?.approvalWaitingSince && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Waiting for approval</MessageBarTitle>A change loosening this interaction is waiting
                for the pharmacy and therapeutics committee.
              </MessageBarBody>
            </MessageBar>
          )}
          {loosens && (
            <>
              <MessageBar intent="warning" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>Waits for approval</MessageBarTitle>
                  Switching an interaction off, making it less severe or moving it to other drugs weakens the alert
                  every prescriber gets: it waits for approval by the pharmacy and therapeutics committee.
                </MessageBarBody>
              </MessageBar>
              <TextAreaField
                control={form.control}
                name="reason"
                label="Why"
                required
                rows={2}
                maxLength={500}
                placeholder="e.g. Regraded as moderate in the committee's review"
              />
            </>
          )}
        </div>
      </DrawerBody>
      <DrawerFooter>
        <Button
          type="submit"
          appearance="primary"
          disabled={save.isPending}
          icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
        >
          {loosens ? 'Ask for approval' : 'Save'}
        </Button>
        <Button onClick={guard.requestClose}>Cancel</Button>
      </DrawerFooter>
      {guard.dialogs}
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
  reason: z.string().max(500),
});

type DoseLimitValues = z.infer<typeof doseLimitSchema>;

/**
 * Create or edit one maximum adult daily dose (summed per ingredient across all current medicines). Adding or lowering one
 * is saved at once; switching one off, raising it, or checking fewer routes waits for the pharmacy and therapeutics
 * committee.
 */
export function DoseLimitDrawer({ limit, onClose }: { limit: DoseLimitDto | 'new' | null; onClose: () => void }) {
  const closing = useCloseRequests({ onClose });
  return (
    <OverlayDrawer open={limit !== null} position="end" size="small" onOpenChange={closing.onOpenChange}>
      {limit !== null && (
        <CloseRequestScope value={closing}>
          <DoseLimitForm
            key={limit === 'new' ? 'new' : limit.id}
            limit={limit === 'new' ? null : limit}
            onClose={onClose}
          />
        </CloseRequestScope>
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
      reason: '',
    },
  });
  const [genericName, maxDailyDoseMg, route, isActive] = useWatch({
    control: form.control,
    name: ['genericName', 'maxDailyDoseMg', 'route', 'isActive'],
  });
  const loosens =
    limit !== null &&
    doseLimitLoosens(limit, { genericName, maxDailyDoseMg: maxDailyDoseMg ?? limit.maxDailyDoseMg, route, isActive });
  const save = useMutation({
    mutationFn: (v: DoseLimitValues) =>
      safetyTablesApi.saveDoseLimit(limit?.id ?? null, {
        genericName: v.genericName.trim(),
        maxDailyDoseMg: v.maxDailyDoseMg ?? 0,
        route: v.route.trim() || null,
        notes: v.notes.trim() || null,
        isActive: v.isActive,
        version: limit?.version ?? null,
        reason: v.reason.trim() || null,
      }),
    onSuccess: (saved) => {
      if (askedForApproval(saved)) {
        notify.success(
          'Asked for approval',
          'The limit stays as it is until the pharmacy and therapeutics committee approves.',
        );
      } else {
        notify.success('Dose limit saved');
      }
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

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <form
      onSubmit={form.handleSubmit((v) => {
        if (v.maxDailyDoseMg === null) {
          form.setError('maxDailyDoseMg', { message: 'Enter the maximum daily dose.' });
          return;
        }
        if (loosens && !v.reason.trim()) {
          form.setError('reason', { message: 'Say why: it waits for approval.' });
          return;
        }
        save.mutate(v);
      })}
      noValidate
      style={{ display: 'contents' }}
    >
      <DrawerHeader>
        <DrawerHeaderTitle
          action={
            <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
          }
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
          {limit?.approvalWaitingSince && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>Waiting for approval</MessageBarTitle>A change loosening this limit is waiting for the
                pharmacy and therapeutics committee.
              </MessageBarBody>
            </MessageBar>
          )}
          {loosens && (
            <>
              <MessageBar intent="warning" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>Waits for approval</MessageBarTitle>
                  Raising a maximum dose, switching it off or checking fewer routes lets higher doses be signed without
                  the block: it waits for approval by the pharmacy and therapeutics committee.
                </MessageBarBody>
              </MessageBar>
              <TextAreaField
                control={form.control}
                name="reason"
                label="Why"
                required
                rows={2}
                maxLength={500}
                placeholder="e.g. The renal unit's protocol allows a higher dose"
              />
            </>
          )}
        </div>
      </DrawerBody>
      <DrawerFooter>
        <Button
          type="submit"
          appearance="primary"
          disabled={save.isPending}
          icon={save.isPending ? <Spinner size="tiny" aria-hidden /> : undefined}
        >
          {loosens ? 'Ask for approval' : 'Save'}
        </Button>
        <Button onClick={guard.requestClose}>Cancel</Button>
      </DrawerFooter>
      {guard.dialogs}
    </form>
  );
}
