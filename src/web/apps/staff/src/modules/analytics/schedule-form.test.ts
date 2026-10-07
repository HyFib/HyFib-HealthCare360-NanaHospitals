import { describe, expect, it } from 'vitest';
import type { ReportCatalogueItem, ScheduleDto } from './api';
import {
  defaultSchedulePeriod,
  isExtractName,
  normalizeExtractName,
  scheduleFormDefaults,
  scheduleFormSchema,
  scheduleToRequest,
  toSaveRequest,
  type ScheduleForm,
} from './schedule-form';

const revenue = { id: 'revenue-by-department', title: 'Revenue by department' } as ReportCatalogueItem;

function form(overrides: Partial<ScheduleForm> = {}): ScheduleForm {
  return { ...scheduleFormDefaults(null, 'Email', revenue), recipients: ['md@nanahospitals.in'], ...overrides };
}

function errors(value: ScheduleForm): Record<string, string> {
  const result = scheduleFormSchema.safeParse(value);
  return result.success
    ? {}
    : Object.fromEntries(result.error.issues.map((issue) => [issue.path.join('.'), issue.message]));
}

describe('schedule form', () => {
  it('sends an e-mail daily, weekly or monthly to at least one address', () => {
    expect(errors(form())).toEqual({});
    expect(errors(form({ recipients: [] }))).toEqual({ recipients: 'Add at least one e-mail address.' });
    expect(errors(form({ frequency: 'Hourly' }))).toEqual({ frequency: 'E-mails go daily, weekly or monthly.' });
    expect(errors(form({ frequency: 'Weekly', dayOfWeek: null }))).toEqual({
      dayOfWeek: 'Choose the day of the week.',
    });
  });

  it('names an extract with lower-case letters, digits and dashes, and needs no addresses', () => {
    const extract = scheduleFormDefaults(null, 'ExtractFolder', revenue);
    expect(extract).toMatchObject({
      destination: 'ExtractFolder',
      extractName: 'revenue-by-department',
      format: 'Csv',
      recipients: [],
      time: '06:00',
    });
    expect(errors(extract)).toEqual({});
    expect(errors({ ...extract, frequency: 'Hourly', time: '00:15' })).toEqual({});
    expect(errors({ ...extract, extractName: 'Daily revenue!' })).toEqual({
      extractName: 'Name the file with lower-case letters, digits and dashes (at most 64), for example daily-revenue.',
    });
    expect(errors({ ...extract, extractName: '' })).toHaveProperty('extractName');
    expect(errors({ ...extract, extractName: 'nul' })).toEqual({
      extractName: 'Windows keeps this name for a device. Choose another, for example daily-revenue.',
    });
    expect(errors({ ...extract, extractName: 'console' })).toEqual({});
    expect(isExtractName('-revenue')).toBe(false);
    expect(isExtractName('a'.repeat(65))).toBe(false);
    expect(isExtractName('ward-2-census')).toBe(true);
    expect(normalizeExtractName('Daily Revenue_IP')).toBe('daily-revenue-ip');
  });

  it('saves an extract as a CSV file with its name and no recipients', () => {
    const extract: ScheduleForm = {
      ...scheduleFormDefaults(null, 'ExtractFolder', revenue),
      frequency: 'Hourly',
      time: '00:15',
      dayOfWeek: 'Monday',
      recipients: ['left@over.in'],
    };

    expect(toSaveRequest(extract, { period: 'thisFinancialYear' }, null)).toEqual({
      name: 'Revenue by department',
      reportId: 'revenue-by-department',
      parameters: { period: 'thisFinancialYear' },
      format: 'Csv',
      frequency: 'Hourly',
      time: '00:15',
      dayOfWeek: null,
      dayOfMonth: null,
      recipients: [],
      isActive: true,
      version: null,
      destination: 'ExtractFolder',
      extractName: 'revenue-by-department',
    });
    expect(toSaveRequest(form({ extractName: 'stale' }), {}, 3)).toMatchObject({
      destination: 'Email',
      extractName: null,
      recipients: ['md@nanahospitals.in'],
      version: 3,
    });
  });

  it('covers the period that just ended for an e-mail and the financial year for an extract', () => {
    expect(defaultSchedulePeriod('Email', 'Daily')).toBe('yesterday');
    expect(defaultSchedulePeriod('Email', 'Monthly')).toBe('lastMonth');
    expect(defaultSchedulePeriod('ExtractFolder', 'Hourly')).toBe('thisFinancialYear');
    expect(defaultSchedulePeriod('ExtractFolder', 'Monthly')).toBe('thisFinancialYear');
  });

  it('pauses a saved extract as it is', () => {
    const saved = {
      ...toSaveRequest(scheduleFormDefaults(null, 'ExtractFolder', revenue), {}, 7),
      id: 's1',
      version: 7,
    } as unknown as ScheduleDto;

    expect(scheduleToRequest(saved, false)).toMatchObject({
      destination: 'ExtractFolder',
      extractName: 'revenue-by-department',
      isActive: false,
      version: 7,
    });
  });
});
