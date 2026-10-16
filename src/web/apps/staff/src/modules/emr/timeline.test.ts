import { describe, expect, it } from 'vitest';
import {
  CHART_TABS,
  chartTabLabel,
  groupByDay,
  istDay,
  parseChartTab,
  parseEncounterTypes,
  parseTypes,
  prettyTitle,
  serializeEncounterTypes,
  serializeTypes,
  TIMELINE_ENCOUNTER_TYPES,
  timelineTarget,
  toggleEncounterType,
  toggleType,
  type TimelineEntry,
} from './timeline';

function entry(partial: Partial<TimelineEntry>): TimelineEntry {
  return {
    type: 'Note',
    id: 'x',
    occurredAt: '2026-09-20T05:00:00Z',
    title: 'Note',
    summary: null,
    status: null,
    encounterId: null,
    isCritical: false,
    ...partial,
  };
}

describe('type filter', () => {
  it('parses and drops unknown types', () => {
    expect(parseTypes('Encounter,Vitals,Nonsense,Vitals')).toEqual(['Encounter', 'Vitals']);
    expect(parseTypes('')).toBeUndefined();
    expect(parseTypes('Nonsense')).toBeUndefined();
    expect(parseTypes(42)).toBeUndefined();
  });

  it('serialises in a stable order and treats all-selected as no filter', () => {
    expect(serializeTypes(['Vitals', 'Encounter'])).toBe('Encounter,Vitals');
    expect(serializeTypes([])).toBeUndefined();
    expect(serializeTypes(undefined)).toBeUndefined();
  });

  it('toggles types on and off', () => {
    expect(toggleType(undefined, 'Order')).toEqual(['Order']);
    expect(toggleType(['Order', 'Note'], 'Order')).toEqual(['Note']);
    expect(toggleType(['Order'], 'Order')).toBeUndefined();
  });

  it('includes procedures', () => {
    expect(parseTypes('Procedure')).toEqual(['Procedure']);
    expect(serializeTypes(['Note', 'Procedure', 'Result'])).toBe('Result,Procedure,Note');
  });
});

describe('kind-of-care filter', () => {
  it('parses and drops unknown kinds of encounter', () => {
    expect(parseEncounterTypes('InPatient,Emergency,Ward,InPatient')).toEqual(['InPatient', 'Emergency']);
    expect(parseEncounterTypes(' ')).toBeUndefined();
    expect(parseEncounterTypes('Ward')).toBeUndefined();
    expect(parseEncounterTypes(undefined)).toBeUndefined();
  });

  it('serialises in a stable order and treats every kind as no filter', () => {
    expect(serializeEncounterTypes(['Emergency', 'OutPatient'])).toBe('OutPatient,Emergency');
    expect(serializeEncounterTypes([...TIMELINE_ENCOUNTER_TYPES])).toBeUndefined();
    expect(serializeEncounterTypes([])).toBeUndefined();
    expect(serializeEncounterTypes(undefined)).toBeUndefined();
  });

  it('toggles kinds on and off', () => {
    expect(toggleEncounterType(undefined, 'Telehealth')).toEqual(['Telehealth']);
    expect(toggleEncounterType(['Telehealth', 'HealthCheckup'], 'Telehealth')).toEqual(['HealthCheckup']);
    expect(toggleEncounterType(['InPatient'], 'InPatient')).toBeUndefined();
  });
});

describe('groupByDay', () => {
  it('uses the IST calendar day', () => {
    expect(istDay('2026-09-20T19:00:00Z')).toBe('2026-09-21');
    expect(istDay('2026-09-20T17:00:00Z')).toBe('2026-09-20');
  });

  it('groups consecutive entries of the same day', () => {
    const days = groupByDay([
      entry({ id: 'a', occurredAt: '2026-09-21T06:00:00Z' }),
      entry({ id: 'b', occurredAt: '2026-09-21T03:00:00Z' }),
      entry({ id: 'c', occurredAt: '2026-09-19T03:00:00Z' }),
    ]);
    expect(days.map((d) => [d.day, d.entries.map((e) => e.id)])).toEqual([
      ['2026-09-21', ['a', 'b']],
      ['2026-09-19', ['c']],
    ]);
  });
});

describe('navigation', () => {
  it('opens encounters and entries that belong to one', () => {
    expect(timelineTarget(entry({ type: 'Encounter', id: 'e1' }))).toEqual({ kind: 'encounter', encounterId: 'e1' });
    expect(timelineTarget(entry({ type: 'Prescription', encounterId: 'e2' }))).toEqual({
      kind: 'encounter',
      encounterId: 'e2',
    });
  });

  it('falls back to the chart tab', () => {
    expect(timelineTarget(entry({ type: 'Immunization' }))).toEqual({ kind: 'tab', tab: 'immunisations' });
    expect(timelineTarget(entry({ type: 'DischargeSummary' }))).toEqual({ kind: 'tab', tab: 'admissions' });
    expect(timelineTarget(entry({ type: 'Result', encounterId: 'e3' }))).toEqual({ kind: 'tab', tab: 'orders' });
    expect(timelineTarget(entry({ type: 'Procedure', encounterId: 'e4' }))).toEqual({ kind: 'tab', tab: 'procedures' });
  });

  it('parses chart tabs from the URL', () => {
    expect(parseChartTab('vitals')).toBe('vitals');
    expect(parseChartTab('consents')).toBe('consents');
    expect(parseChartTab('secrets')).toBeUndefined();
  });

  it('lists the consents next to the documents', () => {
    expect(CHART_TABS.slice(CHART_TABS.indexOf('documents'), CHART_TABS.indexOf('documents') + 2)).toEqual([
      'documents',
      'consents',
    ]);
    expect(chartTabLabel.consents).toBe('Consents');
  });

  it('writes encounter titles in plain words', () => {
    expect(prettyTitle({ type: 'Encounter', title: 'OutPatient encounter' })).toBe('OP consultation');
    expect(prettyTitle({ type: 'Encounter', title: 'HealthCheckup encounter' })).toBe('Health check-up consultation');
    expect(prettyTitle({ type: 'Order', title: 'Order ABC (Stat)' })).toBe('Order ABC (Stat)');
  });
});
