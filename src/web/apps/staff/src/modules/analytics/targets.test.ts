import { describe, expect, it } from 'vitest';
import type { KpiTargetItem } from './api';
import { parseTargetForm, targetBasis, targetFormDefaults, targetUnitHint } from './targets';

const visits: KpiTargetItem = {
  key: 'opVisits',
  label: 'OP visits',
  unit: 'count',
  higherIsBetter: true,
  monthly: true,
  formula: 'Check-ins, excluding cancelled visits.',
  target: null,
};

const alos: KpiTargetItem = { ...visits, key: 'alos', label: 'Average length of stay', unit: 'days', higherIsBetter: false, monthly: false };

describe('KPI target form', () => {
  it('starts empty with the hospital administrator told, or from the stored target', () => {
    expect(targetFormDefaults(visits, ['Accountant', 'HospitalAdmin'])).toEqual({
      target: '',
      amberThreshold: '',
      redThreshold: '',
      ownerRole: 'HospitalAdmin',
    });
    const stored = {
      ...visits,
      target: {
        target: 3000,
        amberThreshold: 2500,
        redThreshold: 2000,
        ownerRole: 'Accountant',
        setByName: 'Meera',
        setAt: '2026-11-01T05:00:00Z',
        lastStatus: null,
        lastValue: null,
        lastCheckedAt: null,
        version: 7,
      },
    };
    expect(targetFormDefaults(stored, ['HospitalAdmin'])).toEqual({
      target: '3000',
      amberThreshold: '2500',
      redThreshold: '2000',
      ownerRole: 'Accountant',
    });
    expect(parseTargetForm(targetFormDefaults(stored, []), stored).request).toEqual({
      target: 3000,
      amberThreshold: 2500,
      redThreshold: 2000,
      ownerRole: 'Accountant',
      version: 7,
    });
  });

  it('reads Indian-grouped numbers and sends no version for a first target', () => {
    expect(
      parseTargetForm({ target: '30,00,000', amberThreshold: '27,00,000.50', redThreshold: '24,00,000', ownerRole: 'Accountant' }, visits)
        .request,
    ).toEqual({ target: 3_000_000, amberThreshold: 2_700_000.5, redThreshold: 2_400_000, ownerRole: 'Accountant' });
  });

  it('names thresholds on the wrong side of the target, as the server does', () => {
    expect(parseTargetForm({ target: '3000', amberThreshold: '3500', redThreshold: '2000', ownerRole: 'X' }, visits).errors).toEqual({
      amberThreshold: 'Higher is better for this KPI, so the amber threshold must be at or below the target.',
    });
    expect(parseTargetForm({ target: '3000', amberThreshold: '2500', redThreshold: '2600', ownerRole: 'X' }, visits).errors).toEqual({
      redThreshold: 'The red threshold must be at or below the amber threshold.',
    });
    expect(parseTargetForm({ target: '4', amberThreshold: '3', redThreshold: '6', ownerRole: 'X' }, alos).errors).toEqual({
      amberThreshold: 'Lower is better for this KPI, so the amber threshold must be at or above the target.',
    });
    expect(parseTargetForm({ target: '4', amberThreshold: '5', redThreshold: '4.5', ownerRole: 'X' }, alos).errors).toEqual({
      redThreshold: 'The red threshold must be at or above the amber threshold.',
    });
    expect(parseTargetForm({ target: '4', amberThreshold: '5', redThreshold: '6', ownerRole: 'X' }, alos).request).toBeDefined();
  });

  it('asks for numbers and an owner', () => {
    const errors = parseTargetForm({ target: 'lots', amberThreshold: '1.234', redThreshold: '', ownerRole: '' }, visits).errors;
    expect(Object.keys(errors ?? {}).sort()).toEqual(['amberThreshold', 'ownerRole', 'redThreshold', 'target']);
  });

  it('says how the target is read and in what unit', () => {
    expect(targetBasis(visits)).toBe('A monthly figure, prorated to the period shown; higher is better.');
    expect(targetBasis(alos)).toBe('Compared as it is; lower is better.');
    expect(targetUnitHint('inr', true)).toBe('Rupees per month');
    expect(targetUnitHint('percent', false)).toBe('Percent');
  });
});
