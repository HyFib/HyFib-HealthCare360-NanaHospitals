import {
  Button,
  Caption1,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
  tokens,
} from '@fluentui/react-components';
import { AddRegular, DeleteRegular, DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { isApiError } from '@hc360/core';
import {
  applyServerErrors,
  FormGrid,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
} from '@hc360/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Controller, useFieldArray, useForm } from 'react-hook-form';
import { z } from 'zod';
import { enumOptions, toOptions, useDepartments } from '@/lib/lookups';
import { cssdApi, cssdKeys, type InstrumentSetDto } from './api';
import { METHOD_LABEL, METHODS } from './rules';

const useStyles = makeStyles({
  item: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) 90px auto', gap: '8px', alignItems: 'start' },
  stack: { display: 'flex', flexDirection: 'column', gap: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

const schema = z
  .object({
    code: z
      .string()
      .trim()
      .min(1, 'Enter a code, e.g. MAJOR-TRAY.')
      .max(24)
      .regex(/^[A-Za-z0-9-]+$/, 'Use letters, digits or dashes.'),
    name: z.string().trim().min(1, 'Enter the set name.').max(128),
    category: z.string().trim().max(64),
    ownerDepartmentId: z.string().nullable(),
    method: z.enum(METHODS),
    shelfLifeDays: z.number({ message: 'Enter the days.' }).int().min(1).max(365),
    isActive: z.boolean(),
    notes: z.string().trim().max(500),
    items: z.array(
      z.object({ name: z.string().trim().max(128), quantity: z.number().int().min(1).max(500).nullable() }),
    ),
  })
  .superRefine((v, ctx) => {
    const filled = v.items.filter((i) => i.name);
    if (filled.length === 0) {
      ctx.addIssue({ code: 'custom', path: ['items'], message: 'Add the instruments on the count sheet.' });
    }
    const seen = new Set<string>();
    v.items.forEach((item, index) => {
      if (!item.name) {
        return;
      }
      const key = item.name.toUpperCase();
      if (seen.has(key)) {
        ctx.addIssue({ code: 'custom', path: ['items', index, 'name'], message: 'Listed twice.' });
      }
      seen.add(key);
      if (!item.quantity) {
        ctx.addIssue({ code: 'custom', path: ['items', index, 'quantity'], message: 'How many?' });
      }
    });
  });

type SetValues = z.infer<typeof schema>;

/** Instrument set master with its count sheet (every instrument and how many), used at receipt and at packing. */
export function SetDrawer({ set, onClose }: { set: InstrumentSetDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const departments = useDepartments();
  const form = useForm<SetValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: set?.code ?? '',
      name: set?.name ?? '',
      category: set?.category ?? '',
      ownerDepartmentId: set?.ownerDepartmentId ?? null,
      method: set?.method ?? 'Steam',
      shelfLifeDays: set?.shelfLifeDays ?? 30,
      isActive: set?.isActive ?? true,
      notes: set?.notes ?? '',
      items: set?.items.length
        ? set.items.map((i) => ({ name: i.name, quantity: i.quantity }))
        : [{ name: '', quantity: 1 }],
    },
  });
  const items = useFieldArray({ control: form.control, name: 'items' });

  const save = useMutation({
    mutationFn: async (v: SetValues): Promise<void> => {
      const body = {
        code: v.code,
        name: v.name,
        category: v.category || null,
        ownerDepartmentId: v.ownerDepartmentId,
        method: v.method,
        shelfLifeDays: v.shelfLifeDays,
        isActive: v.isActive,
        notes: v.notes || null,
        items: v.items.filter((i) => i.name && i.quantity).map((i) => ({ name: i.name, quantity: i.quantity! })),
        version: set?.version ?? null,
      };
      if (set) {
        await cssdApi.updateSet(set.id, body);
      } else {
        await cssdApi.createSet(body);
      }
    },
    onSuccess: () => {
      notify.success(set ? 'Set saved' : 'Set added');
      void queryClient.invalidateQueries({ queryKey: cssdKeys.all });
      onClose();
    },
    onError: (error) => applyServerErrors(error, form.setError),
  });

  const itemsError = form.formState.errors.items?.message;
  const serverError = save.error && !(isApiError(save.error) && save.error.isValidation) ? save.error : null;

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={(_, d) => !d.open && onClose()}>
      <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={<Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={onClose} />}
          >
            {set ? `Edit ${set.name}` : 'Add instrument set'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12, paddingBottom: 12 }}>
            <FormGrid columns={2} minColumnWidth={180}>
              <TextField control={form.control} name="code" label="Code" required maxLength={24} autoFocus={!set} />
              <TextField
                control={form.control}
                name="name"
                label="Name"
                required
                maxLength={128}
                placeholder="Major surgery tray"
              />
              <TextField
                control={form.control}
                name="category"
                label="Category"
                maxLength={64}
                placeholder="e.g. General surgery"
              />
              <SelectField
                control={form.control}
                name="ownerDepartmentId"
                label="Owner department"
                placeholder="Shared"
                options={toOptions(departments.data)}
              />
              <SelectField
                control={form.control}
                name="method"
                label="Sterilization method"
                options={enumOptions(METHODS, METHOD_LABEL)}
              />
              <NumberField
                control={form.control}
                name="shelfLifeDays"
                label="Sterile for (days)"
                min={1}
                max={365}
                hint="From the release date"
              />
            </FormGrid>
            <Field
              label="Count sheet"
              required
              validationMessage={itemsError}
              validationState={itemsError ? 'error' : 'none'}
            >
              <div className={styles.stack}>
                {items.fields.map((field, index) => (
                  <div key={field.id} className={styles.item}>
                    <Controller
                      control={form.control}
                      name={`items.${index}.name`}
                      render={({ field: f, fieldState }) => (
                        <Field
                          validationMessage={fieldState.error?.message}
                          validationState={fieldState.error ? 'error' : 'none'}
                        >
                          <Input
                            value={f.value}
                            onChange={(_, d) => f.onChange(d.value)}
                            maxLength={128}
                            placeholder="Instrument"
                            aria-label={`Instrument ${index + 1}`}
                          />
                        </Field>
                      )}
                    />
                    <Controller
                      control={form.control}
                      name={`items.${index}.quantity`}
                      render={({ field: f, fieldState }) => (
                        <Field
                          validationMessage={fieldState.error?.message}
                          validationState={fieldState.error ? 'error' : 'none'}
                        >
                          <Input
                            type="number"
                            min={1}
                            max={500}
                            aria-label={`Quantity of instrument ${index + 1}`}
                            value={f.value === null ? '' : String(f.value)}
                            onChange={(_, d) => f.onChange(d.value === '' ? null : Number(d.value))}
                          />
                        </Field>
                      )}
                    />
                    <Button
                      appearance="subtle"
                      icon={<DeleteRegular />}
                      aria-label={`Remove instrument ${index + 1}`}
                      disabled={items.fields.length === 1}
                      onClick={() => items.remove(index)}
                    />
                  </div>
                ))}
                <div>
                  <Button size="small" icon={<AddRegular />} onClick={() => items.append({ name: '', quantity: 1 })}>
                    Add instrument
                  </Button>
                </div>
                <Caption1 className={styles.muted}>
                  Each instrument once, with how many are in the tray. Changes apply to trays packed from now on.
                </Caption1>
              </div>
            </Field>
            <TextAreaField control={form.control} name="notes" label="Notes" rows={2} maxLength={500} />
            <SwitchField control={form.control} name="isActive" label="In use" />
            {serverError && (
              <MessageBar intent="error">
                <MessageBarBody>
                  {isApiError(serverError) ? serverError.userMessage : 'Could not save the set.'}
                </MessageBarBody>
              </MessageBar>
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
            {set ? 'Save' : 'Add set'}
          </Button>
          <Button onClick={onClose}>Cancel</Button>
        </DrawerFooter>
      </form>
    </OverlayDrawer>
  );
}
