import { describe, expect, it } from 'vitest';
import type { KpiDefinition, KpiDefinitions, ReportDefinition } from './api';
import { filterDefinitions, kpiDirection } from './definitions';

const occupancy: KpiDefinition = {
  key: 'occupancy',
  label: 'Bed occupancy',
  unit: 'percent',
  higherIsBetter: true,
  formula: 'Inpatient days (midnight census) ÷ (beds in service × days) × 100.',
  source: 'Admissions and discharges, and the beds in service',
  target: null,
  audience: 'Operations',
  reportId: 'bed-occupancy',
  reportTitle: 'Bed occupancy',
};

const labTat: KpiDefinition = {
  ...occupancy,
  key: 'labTat',
  label: 'Lab turnaround (median)',
  unit: 'minutes',
  higherIsBetter: false,
  formula: 'Median minutes from lab order to report release for tests ordered in the period.',
  source: 'Laboratory orders and report release',
  reportId: 'lab-volume-tat',
  reportTitle: 'Lab volume and turnaround',
};

const noShow: ReportDefinition = {
  id: 'appointment-no-show',
  title: 'Appointment no-shows',
  category: 'OutPatient',
  categoryLabel: 'Out-patient',
  description: 'Booked appointments the patient did not attend.',
  definition: 'No-show % = no-shows ÷ booked appointments × 100.',
  containsPatientIdentifiers: false,
  canSchedule: true,
  kpiKeys: [],
};

const definitions: KpiDefinitions = { kpis: [occupancy, labTat], reports: [noShow] };

describe('KPI and report definitions', () => {
  it('lists everything when there is no search', () => {
    expect(filterDefinitions(definitions, undefined)).toBe(definitions);
    expect(filterDefinitions(definitions, '   ')).toBe(definitions);
  });

  it('matches every word across the name, formula and source', () => {
    expect(filterDefinitions(definitions, 'Median release').kpis.map((k) => k.key)).toEqual(['labTat']);
    expect(filterDefinitions(definitions, 'beds census').kpis.map((k) => k.key)).toEqual(['occupancy']);
    expect(filterDefinitions(definitions, 'beds median').kpis).toEqual([]);
  });

  it('finds reports by their formula and their id', () => {
    expect(filterDefinitions(definitions, 'no show').reports).toEqual([noShow]);
    expect(filterDefinitions(definitions, 'booked').kpis).toEqual([]);
    expect(filterDefinitions(definitions, 'occupancy').reports).toEqual([]);
  });

  it('says which way is better and the target when there is one', () => {
    expect(kpiDirection(occupancy)).toBe('Higher is better');
    expect(kpiDirection({ higherIsBetter: false, target: '240 minutes' })).toBe('Lower is better · target 240 minutes');
  });
});
