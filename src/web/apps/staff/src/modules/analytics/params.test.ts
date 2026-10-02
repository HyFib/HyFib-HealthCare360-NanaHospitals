import { describe, expect, it } from 'vitest';
import type { ReportParameter } from './api';
import {
  addDays,
  applyPeriodOverride,
  daysInRange,
  describeSchedule,
  drillInputs,
  exportFileName,
  isRelativePeriod,
  moveItem,
  nextWidgetId,
  ordinal,
  parseEmails,
  parseReportSearch,
  previousRange,
  resolvePeriod,
  sanitizeInputs,
  scheduleInputs,
  setInput,
  startOfWeek,
  toSearchInputs,
  withDefaults,
} from './params';

const parameter = (
  key: string,
  kind: ReportParameter['kind'],
  defaultValue: string | null = null,
): ReportParameter => ({
  key,
  label: key,
  kind,
  required: false,
  defaultValue,
  choices: null,
  min: null,
  max: null,
  hint: null,
});

const PARAMETERS: ReportParameter[] = [
  parameter('period', 'Period', 'last30Days'),
  parameter('facilityId', 'Facility'),
  parameter('departmentId', 'Department'),
  parameter('groupBy', 'Choice', 'department'),
  parameter('days', 'Number', '90'),
];

describe('report inputs in the URL', () => {
  it('keeps camelCase inputs with short values and drops everything else', () => {
    const inputs = sanitizeInputs({
      period: 'lastMonth',
      days: 90,
      departmentId: ' 5f0c7a52-3b6e-4c43-9a3b-9a1c1d2e3f40 ',
      page: 3,
      'bad key': 'x',
      Upper: 'x',
      empty: '',
      long: 'x'.repeat(65),
      nested: { a: 1 },
    });
    expect(inputs).toEqual({ period: 'lastMonth', days: '90', departmentId: '5f0c7a52-3b6e-4c43-9a3b-9a1c1d2e3f40' });
  });

  it('caps the number of inputs', () => {
    const raw = Object.fromEntries(Array.from({ length: 40 }, (_, i) => [`key${i}`, 'v']));
    expect(Object.keys(sanitizeInputs(raw))).toHaveLength(30);
  });

  it('parses paging, sorting and the chart view', () => {
    const search = parseReportSearch({ period: 'today', page: 2, size: 100, sort: 'revenue', desc: true, view: 'Pie' });
    expect(search).toEqual({ period: 'today', page: 2, size: 100, sort: 'revenue', desc: true, view: 'Pie' });
    expect(parseReportSearch({ page: 0, size: 7, sort: 'drop table', desc: 'yes', view: 'Radar' })).toEqual({});
  });

  it('writes whole numbers as numbers so URLs stay readable', () => {
    expect(toSearchInputs({ days: '90', period: 'lastMonth', from: '2026-09-01' })).toEqual({
      days: 90,
      period: 'lastMonth',
      from: '2026-09-01',
    });
  });
});

describe('form defaults', () => {
  it('fills the report defaults the server applies', () => {
    expect(withDefaults(PARAMETERS, {})).toEqual({ period: 'last30Days', groupBy: 'department', days: '90' });
  });

  it('treats dates without a period as custom dates and ignores undeclared inputs', () => {
    expect(withDefaults(PARAMETERS, { from: '2026-09-01', to: '2026-09-10', wardId: 'x' })).toEqual({
      period: 'custom',
      from: '2026-09-01',
      to: '2026-09-10',
      groupBy: 'department',
      days: '90',
    });
  });

  it('drops custom dates when a named period is chosen', () => {
    const custom = { period: 'custom', from: '2026-09-01', to: '2026-09-10' };
    expect(setInput(custom, 'period', 'lastMonth')).toEqual({ period: 'lastMonth' });
    expect(setInput(custom, 'from', '2026-09-02')).toEqual({ period: 'custom', from: '2026-09-02', to: '2026-09-10' });
    expect(setInput({ departmentId: 'a' }, 'departmentId', null)).toEqual({});
  });
});

describe('drill-down', () => {
  it('opens the target with the same period and facility plus the row key', () => {
    const inputs = drillInputs(
      { period: 'custom', from: '2026-09-01', to: '2026-09-15', facilityId: 'f1', groupBy: 'department' },
      { reportId: 'revenue-by-doctor', parameterKey: 'departmentId', label: 'Doctors', parameters: { view: 'doctor' } },
      'd1',
    );
    expect(inputs).toEqual({
      period: 'custom',
      from: '2026-09-01',
      to: '2026-09-15',
      facilityId: 'f1',
      view: 'doctor',
      departmentId: 'd1',
    });
  });
});

describe('schedules and dashboards', () => {
  it('never keeps a facility or fixed dates in a schedule', () => {
    expect(
      scheduleInputs({ period: 'custom', from: '2026-09-01', to: '2026-09-02', facilityId: 'f', departmentId: 'd' }),
    ).toEqual({ departmentId: 'd' });
    expect(scheduleInputs({ period: 'lastWeek', facilityId: 'f' })).toEqual({ period: 'lastWeek' });
  });

  it('applies a dashboard-wide period only to reports that have a period', () => {
    expect(
      applyPeriodOverride(PARAMETERS, { period: 'custom', from: 'a', to: 'b', departmentId: 'd' }, 'lastMonth'),
    ).toEqual({ period: 'lastMonth', departmentId: 'd' });
    expect(applyPeriodOverride([parameter('storeId', 'Store')], { storeId: 's' }, 'lastMonth')).toEqual({
      storeId: 's',
    });
    expect(applyPeriodOverride(PARAMETERS, { period: 'today' }, undefined)).toEqual({ period: 'today' });
  });

  it('describes schedules in words', () => {
    expect(describeSchedule('Daily', '07:00', null, null)).toBe('Every day at 07:00 IST');
    expect(describeSchedule('Weekly', '08:30', 'Monday', null)).toBe('Every Monday at 08:30 IST');
    expect(describeSchedule('Monthly', '06:00', null, 1)).toBe('On the 1st of every month at 06:00 IST');
    expect(describeSchedule('Monthly', '06:00', null, 31)).toBe('On the last day of every month at 06:00 IST');
    expect([1, 2, 3, 4, 11, 12, 13, 21, 22, 23].map(ordinal)).toEqual([
      '1st',
      '2nd',
      '3rd',
      '4th',
      '11th',
      '12th',
      '13th',
      '21st',
      '22nd',
      '23rd',
    ]);
  });

  it('splits pasted e-mail addresses and reports the invalid ones', () => {
    expect(parseEmails('MIS@nana.in, ceo@nana.in; mis@nana.in  bad@ <cfo@nana.in>')).toEqual({
      valid: ['mis@nana.in', 'ceo@nana.in', 'cfo@nana.in'],
      invalid: ['bad@'],
    });
  });

  it('reorders widgets and creates unique ids', () => {
    expect(moveItem(['a', 'b', 'c'], 0, 1)).toEqual(['b', 'a', 'c']);
    expect(moveItem(['a', 'b', 'c'], 2, 1)).toEqual(['a', 'b', 'c']);
    expect(nextWidgetId(['w1', 'w7', 'custom'])).toBe('w8');
    expect(nextWidgetId([])).toBe('w1');
  });
});

describe('periods (IST calendar)', () => {
  const today = '2026-09-16'; // a Wednesday

  it('resolves presets like the server', () => {
    expect(resolvePeriod('today', today)).toEqual({ from: today, to: today });
    expect(resolvePeriod('yesterday', today)).toEqual({ from: '2026-09-15', to: '2026-09-15' });
    expect(resolvePeriod('last7Days', today)).toEqual({ from: '2026-09-10', to: today });
    expect(resolvePeriod('last30Days', today)).toEqual({ from: '2026-08-18', to: today });
    expect(resolvePeriod('thisWeek', today)).toEqual({ from: '2026-09-14', to: today });
    expect(resolvePeriod('lastWeek', today)).toEqual({ from: '2026-09-07', to: '2026-09-13' });
    expect(resolvePeriod('thisMonth', today)).toEqual({ from: '2026-09-01', to: today });
    expect(resolvePeriod('lastMonth', today)).toEqual({ from: '2026-08-01', to: '2026-08-31' });
    expect(resolvePeriod('thisQuarter', today)).toEqual({ from: '2026-07-01', to: today });
    expect(resolvePeriod('lastQuarter', today)).toEqual({ from: '2026-04-01', to: '2026-06-30' });
    expect(resolvePeriod('thisFinancialYear', today)).toEqual({ from: '2026-04-01', to: today });
    expect(resolvePeriod('lastFinancialYear', today)).toEqual({ from: '2025-04-01', to: '2026-03-31' });
    expect(resolvePeriod('custom', today)).toBeNull();
  });

  it('handles year and financial-year boundaries', () => {
    expect(resolvePeriod('lastMonth', '2026-01-10')).toEqual({ from: '2025-12-01', to: '2025-12-31' });
    expect(resolvePeriod('lastQuarter', '2026-02-10')).toEqual({ from: '2025-10-01', to: '2025-12-31' });
    expect(resolvePeriod('thisFinancialYear', '2026-02-10')).toEqual({ from: '2025-04-01', to: '2026-02-10' });
    expect(resolvePeriod('lastMonth', '2024-03-15')).toEqual({ from: '2024-02-01', to: '2024-02-29' });
  });

  it('starts weeks on Monday', () => {
    expect(startOfWeek('2026-09-13')).toBe('2026-09-07'); // Sunday
    expect(startOfWeek('2026-09-14')).toBe('2026-09-14'); // Monday
  });

  it('compares with the previous period of the same length', () => {
    expect(previousRange('2026-09-01', '2026-09-16')).toEqual({ from: '2026-08-16', to: '2026-08-31' });
    expect(previousRange('2026-09-16', '2026-09-16')).toEqual({ from: '2026-09-15', to: '2026-09-15' });
    expect(daysInRange('2026-09-01', '2026-09-30')).toBe(30);
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
  });

  it('knows which periods move with the calendar', () => {
    expect(isRelativePeriod('lastMonth')).toBe(true);
    expect(isRelativePeriod('custom')).toBe(false);
    expect(isRelativePeriod('someday')).toBe(false);
  });

  it('names export files like the server', () => {
    expect(exportFileName('revenue-by-department', '2026-09-01', '2026-09-15', today, 'Xlsx')).toBe(
      'revenue-by-department_20260901-20260915.xlsx',
    );
    expect(exportFileName('op-census', '2026-09-15', '2026-09-15', today, 'Csv')).toBe('op-census_20260915.csv');
    expect(exportFileName('near-expiry-stock', null, null, today, 'Xlsx')).toBe('near-expiry-stock_20260916.xlsx');
    expect(exportFileName('bed-occupancy', '2026-09-01', '2026-09-30', today, 'Pdf')).toBe(
      'bed-occupancy_20260901-20260930.pdf',
    );
  });
});
