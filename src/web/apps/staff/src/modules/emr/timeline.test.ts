import { describe, expect, it } from 'vitest';
import {
  groupByDay,
  istDay,
  parseChartTab,
  parseTypes,
  prettyTitle,
  serializeTypes,
  timelineTarget,
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
  });

  it('parses chart tabs from the URL', () => {
    expect(parseChartTab('vitals')).toBe('vitals');
    expect(parseChartTab('secrets')).toBeUndefined();
  });

  it('writes encounter titles in plain words', () => {
    expect(prettyTitle({ type: 'Encounter', title: 'OutPatient encounter' })).toBe('OP consultation');
    expect(prettyTitle({ type: 'Encounter', title: 'HealthCheckup encounter' })).toBe('Health check-up consultation');
    expect(prettyTitle({ type: 'Order', title: 'Order ABC (Stat)' })).toBe('Order ABC (Stat)');
  });
});
