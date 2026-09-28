import { describe, expect, it } from 'vitest';
import { currentSlot, describeSlots, maskToSlots, MEAL_SLOTS, normalizeRestrictions, parseSlots, slotsToMask, STANDARD_SLOTS } from './rules';

describe('meal slots (server flags enum)', () => {
  it('uses the server bit values', () => {
    expect(slotsToMask(STANDARD_SLOTS)).toBe(59);
    expect(slotsToMask(MEAL_SLOTS)).toBe(127);
    expect(slotsToMask([])).toBe(0);
  });

  it('round-trips masks in service order', () => {
    expect(maskToSlots(59)).toEqual(['EarlyMorning', 'Breakfast', 'Lunch', 'Evening', 'Dinner']);
    expect(maskToSlots(slotsToMask(['Dinner', 'Breakfast']))).toEqual(['Breakfast', 'Dinner']);
  });

  it('parses the comma-separated names the server sends', () => {
    expect(parseSlots('EarlyMorning, Breakfast, Lunch, Evening, Dinner')).toEqual(STANDARD_SLOTS);
    expect(parseSlots('Lunch')).toEqual(['Lunch']);
    expect(parseSlots('None')).toEqual([]);
    expect(parseSlots(72)).toEqual(['Lunch', 'Bedtime']);
    expect(parseSlots(null)).toEqual([]);
  });

  it('describes rounds in plain words', () => {
    expect(describeSlots(['Breakfast', 'Lunch'])).toBe('Breakfast, Lunch');
    expect(describeSlots(MEAL_SLOTS)).toBe('All rounds');
    expect(describeSlots([])).toBe('No meals');
  });

  it.each([
    ['05:00', 'EarlyMorning'],
    ['07:59', 'Breakfast'],
    ['08:30', 'Breakfast'],
    ['09:00', 'MidMorning'],
    ['13:40', 'Lunch'],
    ['14:00', 'Evening'],
    ['23:00', 'Bedtime'],
  ])('at %s the round to deliver is %s', (time, slot) => {
    expect(currentSlot(time)).toBe(slot);
  });
});

describe('diet restrictions', () => {
  it('cleans, splits and de-duplicates', () => {
    expect(normalizeRestrictions([' Low salt ', 'low SALT', 'Low sugar, soft', '', ' , '])).toEqual(['Low salt', 'Low sugar', 'soft']);
  });

  it('keeps at most 10 restrictions of 50 characters', () => {
    const many = Array.from({ length: 14 }, (_, i) => `Item ${i}`);
    expect(normalizeRestrictions(many)).toHaveLength(10);
    expect(normalizeRestrictions(['x'.repeat(80)])[0]).toHaveLength(50);
  });
});
