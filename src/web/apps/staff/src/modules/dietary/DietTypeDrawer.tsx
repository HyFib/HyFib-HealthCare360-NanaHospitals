import {
  Button,
  Checkbox,
  DrawerBody,
  DrawerFooter,
  DrawerHeader,
  DrawerHeaderTitle,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Spinner,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { zodResolver } from '@hookform/resolvers/zod';
import { formatInr, isApiError } from '@hc360/core';
import {
  applyServerErrors,
  CheckboxGroup,
  FormGrid,
  NumberField,
  SelectField,
  SwitchField,
  TextAreaField,
  TextField,
  useNotify,
  useUnsavedChanges,
} from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Controller, useForm } from 'react-hook-form';
import { z } from 'zod';
import { enumOptions } from '@/lib/lookups';
import { DIET_CATEGORIES, DIET_CATEGORY_LABEL, dietaryApi, dietaryKeys, dietaryQueries, type DietTypeDto } from './api';
import { MEAL_SLOT_LABEL, MEAL_SLOT_TIME, MEAL_SLOTS, slotsToMask, STANDARD_SLOTS, type MealSlot } from './rules';

const schema = z.object({
  code: z
    .string()
    .trim()
    .min(1, 'Enter a code, e.g. DIABETIC.')
    .max(24)
    .regex(/^[A-Za-z0-9-]+$/, 'Use letters, digits or dashes.'),
  name: z.string().trim().min(1, 'Enter the diet name.').max(100),
  description: z.string().trim().max(500),
  category: z.enum(DIET_CATEGORIES),
  chargeServiceItemId: z.string().nullable(),
  meals: z.array(z.enum(MEAL_SLOTS as unknown as [MealSlot, ...MealSlot[]])).min(1, 'Choose at least one meal round.'),
  displayOrder: z.number({ message: 'Enter a number.' }).int().min(0).max(10_000),
  isActive: z.boolean(),
});

type DietTypeValues = z.infer<typeof schema>;

/** Diet master: the diet, its category, usual meal rounds and the optional daily diet charge. */
export function DietTypeDrawer({ dietType, onClose }: { dietType: DietTypeDto | null; onClose: () => void }) {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const services = useQuery(dietaryQueries.dietServices());
  const form = useForm<DietTypeValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      code: dietType?.code ?? '',
      name: dietType?.name ?? '',
      description: dietType?.description ?? '',
      category: dietType?.category ?? 'Regular',
      chargeServiceItemId: dietType?.chargeServiceItemId ?? null,
      meals: dietType?.defaultMeals.length ? dietType.defaultMeals : [...STANDARD_SLOTS],
      displayOrder: dietType?.displayOrder ?? 0,
      isActive: dietType?.isActive ?? true,
    },
  });

  const save = useMutation({
    mutationFn: async (v: DietTypeValues): Promise<void> => {
      const body = {
        code: v.code,
        name: v.name,
        description: v.description || null,
        category: v.category,
        chargeServiceItemId: v.chargeServiceItemId,
        defaultMealSlots: slotsToMask(v.meals),
        displayOrder: v.displayOrder,
        isActive: v.isActive,
        version: dietType?.version ?? null,
      };
      if (dietType) {
        await dietaryApi.updateDietType(dietType.id, body);
      } else {
        await dietaryApi.createDietType(body);
      }
    },
    onSuccess: () => {
      notify.success(dietType ? 'Diet saved' : 'Diet added');
      void queryClient.invalidateQueries({ queryKey: dietaryKeys.all });
      onClose();
    },
    onError: (error) => applyServerErrors(error, form.setError),
  });

  const serverError = save.error && !(isApiError(save.error) && save.error.isValidation) ? save.error : null;

  const guard = useUnsavedChanges({ dirty: form.formState.isDirty, busy: save.isPending, onClose });

  return (
    <OverlayDrawer open position="end" size="medium" onOpenChange={guard.onOpenChange}>
      <form onSubmit={form.handleSubmit((v) => save.mutate(v))} noValidate style={{ display: 'contents' }}>
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Button appearance="subtle" icon={<DismissRegular />} aria-label="Close" onClick={guard.requestClose} />
            }
          >
            {dietType ? `Edit ${dietType.name}` : 'Add diet'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 12, paddingBottom: 12 }}>
            <FormGrid columns={2} minColumnWidth={180}>
              <TextField
                control={form.control}
                name="code"
                label="Code"
                required
                maxLength={24}
                autoFocus={!dietType}
              />
              <TextField
                control={form.control}
                name="name"
                label="Name"
                required
                maxLength={100}
                placeholder="Diabetic diet"
              />
              <SelectField
                control={form.control}
                name="category"
                label="Category"
                options={enumOptions(DIET_CATEGORIES, DIET_CATEGORY_LABEL)}
              />
              <NumberField control={form.control} name="displayOrder" label="Display order" min={0} max={10_000} />
            </FormGrid>
            <TextAreaField control={form.control} name="description" label="Description" rows={2} maxLength={500} />
            <SelectField
              control={form.control}
              name="chargeServiceItemId"
              label="Daily diet charge"
              placeholder={services.isLoading ? 'Loading…' : 'Not charged'}
              options={(services.data ?? []).map((s) => ({
                value: s.id,
                label: `${s.name} (${s.code}) · ${formatInr(s.basePrice)}/day`,
              }))}
              hint="Charged once a day when diet charges are switched on for the hospital"
            />
            <Controller
              control={form.control}
              name="meals"
              render={({ field, fieldState }) => (
                <CheckboxGroup
                  label="Usual meal rounds"
                  required
                  validationMessage={fieldState.error?.message}
                  validationState={fieldState.error ? 'error' : 'none'}
                >
                  <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(190px, 1fr))' }}>
                    {MEAL_SLOTS.map((slot) => (
                      <Checkbox
                        key={slot}
                        label={`${MEAL_SLOT_LABEL[slot]} (${MEAL_SLOT_TIME[slot]})`}
                        checked={field.value.includes(slot)}
                        onChange={(_, d) =>
                          field.onChange(
                            d.checked
                              ? MEAL_SLOTS.filter((s) => s === slot || field.value.includes(s))
                              : field.value.filter((s) => s !== slot),
                          )
                        }
                      />
                    ))}
                  </div>
                </CheckboxGroup>
              )}
            />
            <SwitchField control={form.control} name="isActive" label="In use" />
            {serverError && (
              <MessageBar intent="error">
                <MessageBarBody>
                  {isApiError(serverError) ? serverError.userMessage : 'Could not save the diet.'}
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
            {dietType ? 'Save' : 'Add diet'}
          </Button>
          <Button onClick={guard.requestClose}>Cancel</Button>
        </DrawerFooter>
      </form>
      {guard.dialogs}
    </OverlayDrawer>
  );
}
