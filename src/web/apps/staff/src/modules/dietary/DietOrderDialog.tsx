import {
  Button,
  Caption1,
  Checkbox,
  Combobox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  Option,
  Select,
  Spinner,
  Textarea,
  tokens,
  ToggleButton,
} from '@fluentui/react-components';
import { FoodRegular } from '@fluentui/react-icons';
import { isApiError, todayIso, useDebouncedValue, type LookupItem } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { searchPractitioners } from '@/lib/lookups';
import { addDays } from '@/modules/inpatient/time';
import { dietaryApi, dietaryKeys, dietaryQueries, DIET_CATEGORY_LABEL, type DietOrderDto } from './api';
import {
  COMMON_RESTRICTIONS,
  FOOD_PREFERENCES,
  MEAL_SLOT_LABEL,
  MEAL_SLOT_TIME,
  MEAL_SLOTS,
  normalizeRestrictions,
  PREFERENCE_LABEL,
  slotsToMask,
  type FoodPreference,
  type MealSlot,
} from './rules';

const useStyles = makeStyles({
  stack: { display: 'flex', flexDirection: 'column', gap: '12px' },
  two: { display: 'grid', gap: '12px', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', '@media (max-width: 560px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  chips: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  slots: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(180px, 1fr))', gap: '2px 12px' },
  muted: { color: tokens.colorNeutralForeground3 },
});

export interface DietAdmission {
  admissionId: string;
  patientName: string;
  place?: string | null;
}

/**
 * Write (or change) the diet for an admitted patient. The new order replaces the current one and today's meal plan
 * is updated at once; nil by mouth cancels the next meal immediately.
 */
export function DietOrderDialog({ admission, current, onClose }: { admission: DietAdmission; current?: DietOrderDto | null; onClose: () => void }) {
  const styles = useStyles();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const types = useQuery(dietaryQueries.dietTypes());
  const [dietTypeId, setDietTypeId] = useState(current?.dietTypeId ?? '');
  const [preference, setPreference] = useState<FoodPreference>(current?.preference ?? 'Vegetarian');
  const [restrictions, setRestrictions] = useState<string[]>(current?.restrictions ?? []);
  const [otherRestriction, setOtherRestriction] = useState('');
  const [allergies, setAllergies] = useState(current?.allergyNotes ?? '');
  const [instructions, setInstructions] = useState(current?.instructions ?? '');
  const [fluid, setFluid] = useState(current?.fluidRestrictionMl ? String(current.fluidRestrictionMl) : '');
  const [calories, setCalories] = useState(current?.calorieTargetKcal ? String(current.calorieTargetKcal) : '');
  const [slots, setSlots] = useState<MealSlot[] | null>(current?.meals ?? null);
  const [startDate, setStartDate] = useState(todayIso());
  const [doctorTerm, setDoctorTerm] = useState('');
  const [doctor, setDoctor] = useState<LookupItem | null>(null);
  const doctorSearch = useDebouncedValue(doctorTerm.trim(), 250);
  const doctors = useQuery({
    queryKey: ['lookups', 'practitioners', 'search', doctorSearch],
    queryFn: () => searchPractitioners(doctorSearch),
    enabled: doctorSearch.length >= 2,
    staleTime: 5 * 60_000,
  });
  const type = types.data?.find((t) => t.id === dietTypeId) ?? null;
  const rounds = slots ?? type?.defaultMeals ?? [];
  const nilByMouth = type?.category === 'NilByMouth';

  const fluidNumber = fluid === '' ? null : Number(fluid);
  const calorieNumber = calories === '' ? null : Number(calories);
  const fluidOk = fluidNumber === null || (Number.isInteger(fluidNumber) && fluidNumber >= 100 && fluidNumber <= 5000);
  const caloriesOk = calorieNumber === null || (Number.isInteger(calorieNumber) && calorieNumber >= 300 && calorieNumber <= 5000);
  const valid = Boolean(type) && fluidOk && caloriesOk && (nilByMouth || rounds.length > 0);

  const toggleRestriction = (value: string) =>
    setRestrictions((current) => (current.some((r) => r.toLowerCase() === value.toLowerCase()) ? current.filter((r) => r.toLowerCase() !== value.toLowerCase()) : [...current, value]));

  const create = useMutation({
    mutationFn: () =>
      dietaryApi.createOrder({
        admissionId: admission.admissionId,
        dietTypeId,
        preference,
        restrictions: normalizeRestrictions([...restrictions, otherRestriction]),
        allergyNotes: allergies.trim() || null,
        instructions: instructions.trim() || null,
        fluidRestrictionMl: fluidNumber,
        calorieTargetKcal: calorieNumber,
        mealSlots: slots === null ? null : slotsToMask(slots),
        startDate,
        orderedByPractitionerId: doctor?.id ?? null,
      }),
    onSuccess: (order) => {
      notify.success(`${order.dietName ?? 'Diet'} ordered for ${admission.patientName}`, 'Today’s meal plan has been updated.');
      void queryClient.invalidateQueries({ queryKey: dietaryKeys.all });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !create.isPending && onClose()}>
      <DialogSurface style={{ maxWidth: 720 }}>
        <form
          onSubmit={(event) => {
            event.preventDefault();
            if (valid && !create.isPending) {
              create.mutate();
            }
          }}
        >
          <DialogBody>
            <DialogTitle>
              {current ? 'Change diet' : 'Diet order'} for {admission.patientName}
              {admission.place && (
                <Caption1 block className={styles.muted}>
                  {admission.place}
                </Caption1>
              )}
            </DialogTitle>
            <DialogContent className={styles.stack}>
              <div className={styles.two}>
                <Field label="Diet" required hint={type ? DIET_CATEGORY_LABEL[type.category] : undefined}>
                  <Select
                    value={dietTypeId}
                    onChange={(_, d) => {
                      setDietTypeId(d.value);
                      setSlots(null);
                    }}
                    autoFocus
                  >
                    <option value="">Choose a diet</option>
                    {types.data?.map((t) => (
                      <option key={t.id} value={t.id}>
                        {t.name} ({t.code})
                      </option>
                    ))}
                  </Select>
                </Field>
                <Field label="Starts">
                  <Select value={startDate} onChange={(_, d) => setStartDate(d.value)}>
                    <option value={todayIso()}>Today</option>
                    <option value={addDays(todayIso(), 1)}>Tomorrow</option>
                  </Select>
                </Field>
              </div>
              {nilByMouth && (
                <MessageBar intent="warning">
                  <MessageBarBody>Nil by mouth cancels the next meal immediately and stops meals until a new diet is ordered.</MessageBarBody>
                </MessageBar>
              )}
              <Field label="Food preference">
                <div className={styles.chips} role="radiogroup" aria-label="Food preference">
                  {FOOD_PREFERENCES.map((p) => (
                    <ToggleButton key={p} role="radio" aria-checked={preference === p} checked={preference === p} onClick={() => setPreference(p)}>
                      {PREFERENCE_LABEL[p]}
                    </ToggleButton>
                  ))}
                </div>
              </Field>
              <Field label="Restrictions" hint="Up to 10">
                <div className={styles.stack}>
                  <div className={styles.chips}>
                    {COMMON_RESTRICTIONS.map((r) => (
                      <ToggleButton key={r} size="small" shape="circular" checked={restrictions.some((x) => x.toLowerCase() === r.toLowerCase())} onClick={() => toggleRestriction(r)}>
                        {r}
                      </ToggleButton>
                    ))}
                  </div>
                  <Input value={otherRestriction} onChange={(_, d) => setOtherRestriction(d.value)} placeholder="Other restrictions, separated by commas" maxLength={200} />
                </div>
              </Field>
              <Field label="Allergies (food)" hint="Shown in red on the meal list and the kitchen sheet">
                <Input value={allergies} onChange={(_, d) => setAllergies(d.value)} maxLength={500} placeholder="e.g. Peanuts, prawns" />
              </Field>
              <div className={styles.two}>
                <Field label="Fluid limit (ml/day)" validationMessage={fluidOk ? undefined : '100 to 5000 ml'} validationState={fluidOk ? 'none' : 'error'}>
                  <Input type="number" inputMode="numeric" min={100} max={5000} value={fluid} onChange={(_, d) => setFluid(d.value)} />
                </Field>
                <Field label="Calories (kcal/day)" validationMessage={caloriesOk ? undefined : '300 to 5000 kcal'} validationState={caloriesOk ? 'none' : 'error'}>
                  <Input type="number" inputMode="numeric" min={300} max={5000} value={calories} onChange={(_, d) => setCalories(d.value)} />
                </Field>
              </div>
              {!nilByMouth && (
                <Field label="Meal rounds" required hint={slots === null ? 'The diet’s usual rounds' : undefined}>
                  <div className={styles.slots}>
                    {MEAL_SLOTS.map((slot) => (
                      <Checkbox
                        key={slot}
                        label={`${MEAL_SLOT_LABEL[slot]} (${MEAL_SLOT_TIME[slot]})`}
                        checked={rounds.includes(slot)}
                        onChange={(_, d) => setSlots(d.checked ? MEAL_SLOTS.filter((s) => s === slot || rounds.includes(s)) : rounds.filter((s) => s !== slot))}
                      />
                    ))}
                  </div>
                </Field>
              )}
              <Field label="Instructions for the kitchen">
                <Textarea value={instructions} onChange={(_, d) => setInstructions(d.value)} maxLength={500} rows={2} placeholder="e.g. Small frequent feeds, no spicy food" />
              </Field>
              <Field label="Ordered by (doctor)" hint="Leave empty when you order it yourself">
                <Combobox
                  placeholder="Type a doctor's name"
                  value={doctorTerm || doctor?.name || ''}
                  selectedOptions={doctor ? [doctor.id] : []}
                  onChange={(event) => {
                    setDoctorTerm(event.target.value);
                    if (!event.target.value) {
                      setDoctor(null);
                    }
                  }}
                  onOptionSelect={(_, d) => {
                    setDoctor((doctors.data ?? []).find((x) => x.id === d.optionValue) ?? null);
                    setDoctorTerm('');
                  }}
                  clearable
                >
                  {(doctors.data ?? []).map((d) => (
                    <Option key={d.id} value={d.id} text={d.name}>
                      {d.name}
                    </Option>
                  ))}
                </Combobox>
              </Field>
              {create.error && (
                <MessageBar intent="error" layout="multiline">
                  <MessageBarBody>{isApiError(create.error) ? create.error.userMessage : 'The diet could not be ordered.'}</MessageBarBody>
                </MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose} disabled={create.isPending}>
                Cancel
              </Button>
              <Button type="submit" appearance="primary" disabled={!valid || create.isPending} icon={create.isPending ? <Spinner size="tiny" /> : <FoodRegular />}>
                {current ? 'Change diet' : 'Order diet'}
              </Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  );
}
