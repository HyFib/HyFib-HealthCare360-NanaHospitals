/**
 * Meal rounds and diet-order helpers. The server's `MealSlot` is a [Flags] enum: single rounds arrive as names
 * ("Lunch"), combinations as comma-separated names ("Breakfast, Lunch") and every DTO also lists the rounds as an
 * array. Requests send the combination as its numeric mask, which the server's enum converter accepts.
 */

export type MealSlot = 'EarlyMorning' | 'Breakfast' | 'MidMorning' | 'Lunch' | 'Evening' | 'Dinner' | 'Bedtime';

export const MEAL_SLOTS: readonly MealSlot[] = [
  'EarlyMorning',
  'Breakfast',
  'MidMorning',
  'Lunch',
  'Evening',
  'Dinner',
  'Bedtime',
];

export const MEAL_SLOT_BIT: Record<MealSlot, number> = {
  EarlyMorning: 1,
  Breakfast: 2,
  MidMorning: 4,
  Lunch: 8,
  Evening: 16,
  Dinner: 32,
  Bedtime: 64,
};

export const MEAL_SLOT_LABEL: Record<MealSlot, string> = {
  EarlyMorning: 'Early morning tea',
  Breakfast: 'Breakfast',
  MidMorning: 'Mid-morning',
  Lunch: 'Lunch',
  Evening: 'Evening snack',
  Dinner: 'Dinner',
  Bedtime: 'Bedtime milk',
};

/** Service times of the rounds (IST), as the server plans them. */
export const MEAL_SLOT_TIME: Record<MealSlot, string> = {
  EarlyMorning: '06:00',
  Breakfast: '08:00',
  MidMorning: '11:00',
  Lunch: '13:00',
  Evening: '16:30',
  Dinner: '19:30',
  Bedtime: '21:30',
};

/** The five standard rounds (early morning, breakfast, lunch, evening, dinner). */
export const STANDARD_SLOTS: readonly MealSlot[] = ['EarlyMorning', 'Breakfast', 'Lunch', 'Evening', 'Dinner'];

export function isMealSlot(value: unknown): value is MealSlot {
  return typeof value === 'string' && (MEAL_SLOTS as readonly string[]).includes(value);
}

/** Rounds → bit mask for requests (0 when none). */
export function slotsToMask(slots: readonly MealSlot[]): number {
  return slots.reduce((mask, slot) => mask | MEAL_SLOT_BIT[slot], 0);
}

/** Bit mask → rounds in service order. */
export function maskToSlots(mask: number): MealSlot[] {
  return MEAL_SLOTS.filter((slot) => (mask & MEAL_SLOT_BIT[slot]) === MEAL_SLOT_BIT[slot]);
}

/** Server flags value ("Breakfast, Lunch", "None", or a number) → rounds in service order. */
export function parseSlots(value: string | number | null | undefined): MealSlot[] {
  if (typeof value === 'number') {
    return maskToSlots(value);
  }
  if (!value) {
    return [];
  }
  const names = value.split(',').map((part) => part.trim());
  return MEAL_SLOTS.filter((slot) => names.includes(slot));
}

/** "Breakfast, lunch, dinner" (short, readable list). */
export function describeSlots(slots: readonly MealSlot[]): string {
  if (slots.length === MEAL_SLOTS.length) {
    return 'All rounds';
  }
  return slots.map((slot) => MEAL_SLOT_LABEL[slot]).join(', ') || 'No meals';
}

/** The round being served or next to be served at an IST wall-clock time "HH:mm" (bedtime after the last round). */
export function currentSlot(timeOfDay: string): MealSlot {
  const next = MEAL_SLOTS.find((slot) => MEAL_SLOT_TIME[slot] >= timeOfDay);
  // Within 30 minutes after a round starts, that round is still being served.
  const serving = [...MEAL_SLOTS].reverse().find((slot) => {
    const [h, m] = MEAL_SLOT_TIME[slot].split(':').map(Number);
    const [th, tm] = timeOfDay.split(':').map(Number);
    const minutes = (th ?? 0) * 60 + (tm ?? 0) - ((h ?? 0) * 60 + (m ?? 0));
    return minutes >= 0 && minutes <= 45;
  });
  return serving ?? next ?? 'Bedtime';
}

export type FoodPreference = 'Vegetarian' | 'NonVegetarian' | 'Eggetarian' | 'Jain';

export const FOOD_PREFERENCES: readonly FoodPreference[] = ['Vegetarian', 'NonVegetarian', 'Eggetarian', 'Jain'];

export const PREFERENCE_LABEL: Record<FoodPreference, string> = {
  Vegetarian: 'Vegetarian',
  NonVegetarian: 'Non-vegetarian',
  Eggetarian: 'Eggetarian',
  Jain: 'Jain (no root vegetables)',
};

export const PREFERENCE_SHORT: Record<FoodPreference, string> = {
  Vegetarian: 'Veg',
  NonVegetarian: 'Non-veg',
  Eggetarian: 'Egg',
  Jain: 'Jain',
};

export const COMMON_RESTRICTIONS = [
  'Low salt',
  'Low sugar',
  'Low fat',
  'Low potassium',
  'Soft',
  'No onion or garlic',
  'High protein',
  'High fibre',
  'Pureed',
];

export const MAX_RESTRICTIONS = 10;

/** Free text or chips → clean restriction list: trimmed, no blanks, no duplicates (case-insensitive), at most 10. */
export function normalizeRestrictions(values: readonly string[]): string[] {
  const seen = new Set<string>();
  const result: string[] = [];
  for (const raw of values.flatMap((v) => v.split(','))) {
    const value = raw.trim().slice(0, 50);
    const key = value.toLowerCase();
    if (value && !seen.has(key)) {
      seen.add(key);
      result.push(value);
    }
  }
  return result.slice(0, MAX_RESTRICTIONS);
}
