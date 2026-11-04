import { describe, expect, it } from 'vitest';
import {
  DRUG_SCHEDULES,
  labelledOptions,
  requiresPrescription,
  requiresRegister,
  scheduleLabel,
  weakensClass,
  workflowTone,
  type DrugSchedule,
} from './labels';

describe('drug schedule rules (Item.NeedsPrescription / NeedsRegister)', () => {
  it('needs a prescription for H, H1, X and controlled items only', () => {
    expect(DRUG_SCHEDULES.filter((s) => requiresPrescription(s, false))).toEqual(['H', 'H1', 'X']);
    expect(requiresPrescription('Otc', true)).toBe(true);
  });

  it('writes the register for H1, X and controlled items only', () => {
    expect(DRUG_SCHEDULES.filter((s) => requiresRegister(s, false))).toEqual(['H1', 'X']);
    expect(requiresRegister('H', true)).toBe(true);
    expect(requiresRegister('H', false)).toBe(false);
  });

  it('labels enums in plain words', () => {
    expect(labelledOptions(['H', 'X'] as const, scheduleLabel)).toEqual([
      { value: 'H', label: 'Schedule H' },
      { value: 'X', label: 'Schedule X' },
    ]);
    expect(workflowTone('InTransit')).toBe('brand');
    expect(workflowTone('Pending')).toBeUndefined();
  });
});

describe('ledger reference names', () => {
  it('names the documents behind stock movements', async () => {
    const { referenceModuleLabel } = await import('./labels');
    expect(referenceModuleLabel('pharmacy.ip_issue')).toBe('IP issue');
    expect(referenceModuleLabel('cssd.pack_use')).toBe('Pack use');
    expect(referenceModuleLabel(null)).toBe('—');
  });
});

describe('a drug handled less strictly (DrugSafetyClass.IsWeakenedBy)', () => {
  const drug = (schedule: DrugSchedule, isControlled = false, isHighAlert = false) => ({
    schedule,
    isControlled,
    isHighAlert,
  });

  it('is no longer controlled, no longer high-alert, or on a lower schedule, whatever is made stricter with it', () => {
    expect(weakensClass(drug('X', true), drug('X'))).toBe(true);
    expect(weakensClass(drug('H', false, true), drug('H'))).toBe(true);
    expect(weakensClass(drug('X'), drug('H1'))).toBe(true);
    expect(weakensClass(drug('H'), drug('None'))).toBe(true);
    expect(weakensClass(drug('H', true), drug('X', false, true))).toBe(true);
  });

  it('is not one handled as strictly or more strictly, nor one moved among schedules that prescribe nothing', () => {
    expect(weakensClass(drug('H'), drug('H'))).toBe(false);
    expect(weakensClass(drug('H'), drug('H1', true, true))).toBe(false);
    expect(weakensClass(drug('G'), drug('Otc'))).toBe(false);
  });
});
