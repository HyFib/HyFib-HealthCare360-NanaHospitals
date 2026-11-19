import { describe, expect, it } from 'vitest';
import { asSafetyAlerts, medicineKey, wardAlertsFromProblem, type WardSafetyAlert } from './wardOrderSafety';

const allergy: WardSafetyAlert = {
  key: 'class-allergy:penicillins:x',
  kind: 'ClassAllergy',
  severity: 'Major',
  isBlocking: true,
  message: 'ALLERGY: Augmentin 625 belongs to Penicillins; the patient is allergic to Penicillins (ANAPHYLAXIS).',
  detail: null,
};

describe("a ward order's safety check", () => {
  it('shows the alerts as the prescription review takes them, naming no line', () => {
    expect(asSafetyAlerts([allergy])).toEqual([{ ...allergy, itemIds: [] }]);
  });

  it('reads the alerts a ward order was refused with, and nothing from another refusal', () => {
    expect(wardAlertsFromProblem('medication.safety_block', [allergy, { unrelated: true }])).toEqual([
      { ...allergy, itemIds: [] },
    ]);
    expect(wardAlertsFromProblem('medication.safety_block', undefined)).toEqual([]);
    expect(wardAlertsFromProblem('medication.read_back_required', [allergy])).toBeNull();
    expect(wardAlertsFromProblem(undefined, [allergy])).toBeNull();
  });

  it('checks the order again when the medicine changes, not when only its instructions or start do', () => {
    const order = {
      drugItemId: null,
      drugName: 'Augmentin 625',
      genericName: '',
      strength: '',
      dose: '1 tablet',
      route: 'Oral',
      frequency: 'BD',
    };

    expect(medicineKey({ ...order, drugName: ' Augmentin 625 ' })).toBe(medicineKey(order));
    expect(medicineKey({ ...order, dose: '2 tablets' })).not.toBe(medicineKey(order));
    expect(medicineKey({ ...order, frequency: 'TDS' })).not.toBe(medicineKey(order));
    expect(medicineKey({ ...order, drugItemId: '0192a000-0000-7000-8000-000000000001' })).not.toBe(medicineKey(order));
    expect(medicineKey({ ...order, route: undefined })).toBe(medicineKey({ ...order, route: '' }));
  });
});
