import { describe, expect, it } from 'vitest';
import {
  appendLines,
  applyDefaults,
  autoQuantity,
  defaultRoute,
  drugMasterDefaults,
  drugOptionView,
  duplicateGenerics,
  effectiveQuantity,
  emptyLine,
  fromCurrentMedication,
  fromSavedItem,
  medicineNames,
  namedByItsGeneric,
  routeProblem,
  rxFormSchema,
  toItemRequest,
  toRequestBody,
  type RxLineValues,
} from './prescriptionForm';
import type { PrescribableDrug } from './prescriptionsApi';

function line(partial: Partial<RxLineValues>): RxLineValues {
  return { ...emptyLine(), ...partial };
}

const paracetamol = line({
  drugName: 'Dolo 650',
  genericName: 'Paracetamol',
  strength: '650 mg',
  dosageForm: 'Tablet',
  route: 'Oral',
  dose: '1 tab',
  frequency: 'TDS',
  durationDays: 3,
});

describe('quantities', () => {
  it('calculates solid-form quantities and respects an explicit override', () => {
    expect(autoQuantity(paracetamol)).toBe(9);
    expect(effectiveQuantity({ ...paracetamol, quantity: 10 })).toBe(10);
  });

  it('needs an explicit quantity for liquids', () => {
    const syrup = line({ drugName: 'Crocin syrup', dose: '5 ml', frequency: 'TDS', durationDays: 3 });
    expect(effectiveQuantity(syrup)).toBeNull();
    const result = rxFormSchema.safeParse({ lines: [syrup], advice: '', notes: '', refillsAllowed: 0 });
    expect(result.success).toBe(false);
    expect(result.error?.issues.some((issue) => issue.path.join('.') === 'lines.0.quantity')).toBe(true);
  });
});

describe('validation', () => {
  it('accepts a complete prescription', () => {
    expect(
      rxFormSchema.safeParse({ lines: [paracetamol], advice: 'Plenty of fluids', notes: '', refillsAllowed: 0 })
        .success,
    ).toBe(true);
  });

  it('asks for the custom frequency text', () => {
    const custom = { ...paracetamol, frequency: 'Custom' as const, customFrequency: '', quantity: 6 };
    const result = rxFormSchema.safeParse({ lines: [custom], advice: '', notes: '', refillsAllowed: 0 });
    expect(result.error?.issues.map((issue) => issue.path.join('.'))).toContain('lines.0.customFrequency');
  });

  it('asks for the generic name of every medicine, as the server does before signing', () => {
    const typed = { ...paracetamol, genericName: '  ' };
    const result = rxFormSchema.safeParse({ lines: [typed], advice: '', notes: '', refillsAllowed: 0 });
    expect(result.error?.issues.find((issue) => issue.path.join('.') === 'lines.0.genericName')?.message).toBe(
      'Enter the generic name: the prescription names every medicine by it.',
    );
  });
});

describe('names', () => {
  it('names a medicine by its generic name in capitals, then by the product chosen, as the prescription prints it', () => {
    expect(
      medicineNames({ drugName: 'Dolo 650', genericName: 'Paracetamol', strength: '650 mg', dosageForm: 'Tablet' }),
    ).toEqual({
      name: 'PARACETAMOL 650 mg',
      detail: 'Dolo 650 · Tablet',
    });
    expect(
      medicineNames({
        drugName: 'Metformin',
        genericName: 'Metformin hydrochloride',
        strength: '500 mg',
        dosageForm: 'Tablet',
      }),
    ).toEqual({ name: 'METFORMIN HYDROCHLORIDE 500 mg', detail: 'Tablet' });
    expect(
      medicineNames({ drugName: 'Ceftriaxone', genericName: null, strength: '1 g', dosageForm: 'Injection' }),
    ).toEqual({
      name: 'Ceftriaxone 1 g',
      detail: null,
    });
  });

  it('takes a product as its generic only when one name begins the other word for word', () => {
    expect(namedByItsGeneric('Paracetamol 650 mg Tablet', 'Paracetamol')).toBe(true);
    expect(namedByItsGeneric('tramadol', 'TRAMADOL')).toBe(true);
    expect(namedByItsGeneric('Pan', 'Pantoprazole')).toBe(false);
    expect(namedByItsGeneric('Augmentin 625 Duo', 'Amoxicillin + Clavulanic acid')).toBe(false);
  });
});

describe('mapping', () => {
  it('builds the item request', () => {
    const request = toItemRequest({
      ...paracetamol,
      instructions: ' After food ',
      isControlledDrug: true,
      controlledSchedule: 'h1',
    });
    expect(request).toMatchObject({
      drugName: 'Dolo 650',
      genericName: 'Paracetamol',
      frequency: 'TDS',
      durationDays: 3,
      quantity: null,
      instructions: 'After food',
      controlledSchedule: 'H1',
    });
  });

  it('sends one day for STAT doses and drops blank rows', () => {
    const body = toRequestBody({
      lines: [{ ...paracetamol, frequency: 'STAT', durationDays: null }, emptyLine()],
      advice: '',
      notes: '',
      refillsAllowed: 1,
    });
    expect(body.items).toHaveLength(1);
    expect(body.items[0]?.durationDays).toBe(1);
    expect(body.advice).toBeNull();
  });

  it('keeps calculated quantities automatic when reloading a draft', () => {
    const saved = { ...toItemRequest(paracetamol), quantity: 9, durationDays: 3 };
    expect(fromSavedItem(saved).quantity).toBeNull();
    expect(fromSavedItem({ ...saved, quantity: 12 }).quantity).toBe(12);
  });

  it('applies favourite defaults and marks controlled medicines', () => {
    const next = applyDefaults(emptyLine(), {
      drugName: 'Alprazolam 0.25',
      genericName: 'Alprazolam',
      dosageForm: 'Tablet',
      dose: '1 tab',
      frequency: 'HS',
      durationDays: 7,
      isControlledDrug: true,
    });
    expect(next).toMatchObject({
      drugName: 'Alprazolam 0.25',
      frequency: 'HS',
      durationDays: 7,
      isControlledDrug: true,
      controlledSchedule: 'H1',
      route: 'Oral',
    });
  });

  it('replaces a trailing blank row when adding medicines', () => {
    const lines = appendLines([paracetamol, emptyLine()], [line({ drugName: 'Pantoprazole 40' })]);
    expect(lines.map((l) => l.drugName)).toEqual(['Dolo 650', 'Pantoprazole 40']);
  });

  it('spots the same generic twice', () => {
    expect(
      duplicateGenerics([paracetamol, { ...paracetamol, drugName: 'Crocin 500' }, line({ genericName: 'Ibuprofen' })]),
    ).toEqual(['paracetamol']);
  });
});

describe('drug master', () => {
  const drug = (partial: Partial<PrescribableDrug> = {}): PrescribableDrug => ({
    id: 'd1',
    code: 'DRG001',
    name: 'Dolo 650',
    genericName: 'Paracetamol',
    brandName: 'Dolo',
    strength: '650 mg',
    dosageForm: 'Tablet',
    schedule: 'H',
    isControlled: false,
    controlledSchedule: null,
    requiresPrescription: true,
    isColdChain: false,
    baseUnit: 'TAB',
    available: 1200,
    nearestExpiry: '2027-03-31',
    ...partial,
  });

  it('fills the row straight from the server fields', () => {
    expect(drugMasterDefaults(drug())).toEqual({
      drugItemId: 'd1',
      drugName: 'Dolo 650',
      genericName: 'Paracetamol',
      strength: '650 mg',
      dosageForm: 'Tablet',
      route: 'Oral',
      isControlledDrug: false,
      controlledSchedule: null,
    });
  });

  it('keeps the schedule the server gives a controlled medicine', () => {
    const alprazolam = drug({
      name: 'Alprax 0.25',
      genericName: 'Alprazolam',
      schedule: 'H1',
      isControlled: true,
      controlledSchedule: 'X',
    });
    expect(applyDefaults(emptyLine(), drugMasterDefaults(alprazolam))).toMatchObject({
      drugItemId: 'd1',
      isControlledDrug: true,
      controlledSchedule: 'X',
    });
    // An H1 item the master does not mark as controlled stays an ordinary line: the server decides.
    expect(applyDefaults(emptyLine(), drugMasterDefaults(drug({ schedule: 'H1' })))).toMatchObject({
      isControlledDrug: false,
      controlledSchedule: '',
    });
  });

  it('shows stock, or nothing when the facility has no pharmacy store', () => {
    expect(drugOptionView(drug()).stock).toBe('In stock: 1,200 TAB');
    expect(drugOptionView(drug({ available: 0 }))).toMatchObject({ stock: 'Out of stock', outOfStock: true });
    expect(drugOptionView(drug({ available: null }))).toMatchObject({ stock: null, outOfStock: false });
  });

  it('flags controlled and cold-chain medicines in words', () => {
    expect(drugOptionView(drug()).flags).toEqual([]);
    expect(drugOptionView(drug({ isControlled: true, controlledSchedule: 'X', isColdChain: true })).flags).toEqual([
      { kind: 'controlled', label: 'Schedule X' },
      { kind: 'coldChain', label: 'Cold chain' },
    ]);
    expect(drugOptionView(drug({ isControlled: true, controlledSchedule: 'H1' })).flags[0]?.label).toBe('Schedule H1');
  });

  it('describes the medicine by generic, strength and form', () => {
    expect(drugOptionView(drug()).detail).toBe('Paracetamol · 650 mg · Tablet');
    expect(drugOptionView(drug({ genericName: null, dosageForm: null })).detail).toBe('650 mg');
  });
});

describe('route', () => {
  it('takes the route from the dosage form when the form leaves no doubt', () => {
    const routes = Object.fromEntries(
      [
        'Tablet',
        'Tablet (enteric-coated)',
        'Soft gelatin capsule',
        'Orally disintegrating tablet',
        'Powder for oral suspension',
        'Oral solution',
        'IV infusion',
        'Metered-dose inhaler',
        'Nebuliser suspension',
        'Cream',
        'Jelly',
        'Eye drops',
        'Ear drops',
        'Rectal suppository',
      ].map((form) => [form, defaultRoute(form)]),
    );
    expect(routes).toEqual({
      Tablet: 'Oral',
      'Tablet (enteric-coated)': 'Oral',
      'Soft gelatin capsule': 'Oral',
      'Orally disintegrating tablet': 'Oral',
      'Powder for oral suspension': 'Oral',
      'Oral solution': 'Oral',
      'IV infusion': 'IV',
      'Metered-dose inhaler': 'Inhalation',
      'Nebuliser suspension': 'Inhalation',
      Cream: 'Topical',
      Jelly: 'Topical',
      'Eye drops': 'Eye',
      'Ear drops': 'Ear',
      'Rectal suppository': 'Rectal',
    });
  });

  it('leaves the route of an injection, or of a form that does not say, to the doctor', () => {
    expect(defaultRoute('Injection')).toBe('');
    expect(defaultRoute('Powder for injection')).toBe('');
    expect(defaultRoute('Solution')).toBe('');
    expect(defaultRoute(null, 'Inj. Ceftriaxone 1 g')).toBe('');
    expect(defaultRoute(null, 'Paracetamol 500 mg Tablet')).toBe('Oral');
  });

  it('gives an injection from the drug master no route, so it is not signed as oral (it was)', () => {
    const ceftriaxone = drugMasterDefaults({
      id: 'd2',
      name: 'Ceftriaxone 1 g Injection',
      genericName: 'Ceftriaxone',
      strength: '1 g',
      dosageForm: 'Powder for injection',
      isControlled: true,
      controlledSchedule: 'H1',
    });
    const picked = { ...applyDefaults(emptyLine(), ceftriaxone), dose: '1 g', durationDays: 3, quantity: 6 };
    expect(picked.route).toBe('');
    const result = rxFormSchema.safeParse({ lines: [picked], advice: '', notes: '', refillsAllowed: 0 });
    expect(result.error?.issues.find((issue) => issue.path.join('.') === 'lines.0.route')?.message).toBe(
      'Choose how the injection is given: IV, IM or SC.',
    );
    expect(
      rxFormSchema.safeParse({ lines: [{ ...picked, route: 'IV' }], advice: '', notes: '', refillsAllowed: 0 }).success,
    ).toBe(true);
  });

  it('refuses a route the dosage form contradicts, as the server does', () => {
    const injection = line({ drugName: 'Ceftriaxone 1 g Injection', dosageForm: 'Powder for injection' });
    expect(routeProblem({ ...injection, route: 'Oral' })).toBe('An injection is not given by the oral route.');
    expect(routeProblem({ ...injection, route: 'IM' })).toBeNull();
    expect(routeProblem({ ...injection, route: 'Other' })).toBeNull();
    expect(routeProblem({ ...paracetamol, route: 'IV' })).toBe('A medicine taken by mouth is not given IV.');
    expect(routeProblem({ ...paracetamol, route: 'Oral' })).toBeNull();
    expect(routeProblem({ ...paracetamol, route: '' })).toBe('Choose how the medicine is given.');
  });

  it('keeps the route of a medicine continued from its name', () => {
    const continued = (drugName: string) =>
      fromCurrentMedication({
        drugName,
        genericName: null,
        strength: null,
        dose: '1',
        frequency: 'BD',
        durationDays: 3,
      });
    expect(continued('Paracetamol 500 mg Tablet').route).toBe('Oral');
    expect(continued('Ceftriaxone 1 g Injection').route).toBe('');
  });
});
