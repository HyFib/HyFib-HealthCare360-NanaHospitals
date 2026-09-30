import { describe, expect, it } from 'vitest';
import {
  DRUG_SCHEDULES,
  labelledOptions,
  requiresPrescription,
  requiresRegister,
  scheduleLabel,
  workflowTone,
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
