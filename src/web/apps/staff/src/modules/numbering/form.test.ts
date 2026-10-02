import { describe, expect, it } from 'vitest';
import { filterSeries, seriesAreas, sortSeries, type NumberSeriesItem, type NumberSeriesList } from './api';
import { editorContext, numberSeriesFormSchema, toForm, toSaveRequest } from './form';

function series(overrides: Partial<NumberSeriesItem> = {}): NumberSeriesItem {
  return {
    key: 'billing.bill.op',
    name: 'OP bill',
    area: 'Billing',
    scope: 'facility',
    format: '{FAC}OB{YY}{SEQ:7}',
    resetPolicy: 'Yearly',
    lastNumber: 123,
    nextExample: 'NHOB260000124',
    facilityCode: 'NH',
    isCustomised: false,
    isLocked: false,
    lockedReason: null,
    maxLength: 24,
    defaultFormat: '{FAC}OB{YY}{SEQ:7}',
    defaultResetPolicy: 'Yearly',
    ...overrides,
  };
}

const uhid = series({
  key: 'uhid',
  name: 'UHID (patient number)',
  area: 'Patients',
  scope: 'global',
  format: '{FAC}{YY}{SEQ:6}',
  resetPolicy: 'Never',
  defaultFormat: '{FAC}{YY}{SEQ:6}',
  defaultResetPolicy: 'Never',
  isLocked: true,
  lockedReason: 'Other systems read UHIDs by their shape, so the format cannot change.',
  maxLength: 20,
});

const ipBill = series({
  key: 'billing.bill.ip',
  name: 'IP bill',
  format: '{FAC}IB{YY}{SEQ:7}',
  defaultFormat: '{FAC}IB{YY}{SEQ:7}',
});
const list: NumberSeriesList = { facilityCount: 1, longestFacilityCode: 2, series: [series(), ipBill, uhid] };

describe('the save request', () => {
  it('sends the trimmed format and the values the editor started from', () => {
    expect(
      toSaveRequest({ format: ' {FAC}/OP/{YY}/{SEQ:6} ', resetPolicy: 'Monthly', startAt: 500 }, series()),
    ).toEqual({
      format: '{FAC}/OP/{YY}/{SEQ:6}',
      resetPolicy: 'Monthly',
      startAt: 500,
      expectedFormat: '{FAC}OB{YY}{SEQ:7}',
      expectedResetPolicy: 'Yearly',
    });
  });

  it('keeps the format and reset of a locked series as they are', () => {
    expect(toSaveRequest({ format: 'CHANGED{SEQ}', resetPolicy: 'Daily', startAt: 250_001 }, uhid)).toEqual({
      format: '{FAC}{YY}{SEQ:6}',
      resetPolicy: 'Never',
      startAt: 250_001,
      expectedFormat: '{FAC}{YY}{SEQ:6}',
      expectedResetPolicy: 'Never',
    });
  });
});

describe('the form rules', () => {
  const schema = numberSeriesFormSchema(series(), editorContext(list, 'billing.bill.op'));

  it('accepts a good change', () => {
    expect(schema.safeParse({ ...toForm(series()), format: '{FAC}/OP/{YY}/{SEQ:6}' }).success).toBe(true);
  });

  it('puts each problem on its field', () => {
    const result = schema.safeParse({ format: '{FAC}IB{YY}{SEQ:7}', resetPolicy: 'Yearly', startAt: 100 });

    expect(result.success).toBe(false);
    expect(result.error?.issues.map((issue) => [issue.path.join('.'), issue.message])).toEqual([
      [
        'format',
        'IP bill already uses this format. Change the fixed text so the two kinds of number can be told apart.',
      ],
      ['startAt', 'Number 123 has already been issued this year. Enter a number above 123.'],
    ]);
  });

  it('compares a format with the other series of the list only', () => {
    expect(editorContext(list, 'billing.bill.op').otherFormats.map((o) => o.name)).toEqual([
      'IP bill',
      'UHID (patient number)',
    ]);
  });
});

describe('the list', () => {
  const rows = [
    series(),
    ipBill,
    uhid,
    series({ key: 'lab.sample', name: 'Sample tube barcode', area: 'Laboratory', format: '{SEQ:9}' }),
  ];

  it('is ordered by area then name, or by name', () => {
    expect(sortSeries(rows, 'area', false).map((s) => s.name)).toEqual([
      'IP bill',
      'OP bill',
      'Sample tube barcode',
      'UHID (patient number)',
    ]);
    expect(sortSeries(rows, 'name', true).map((s) => s.name)[0]).toBe('UHID (patient number)');
    expect(seriesAreas(rows)).toEqual(['Billing', 'Laboratory', 'Patients']);
  });

  it('is filtered by area and by name, area or format', () => {
    expect(filterSeries(rows, 'Billing', undefined).map((s) => s.name)).toEqual(['OP bill', 'IP bill']);
    expect(filterSeries(rows, undefined, 'seq:9').map((s) => s.name)).toEqual(['Sample tube barcode']);
    expect(filterSeries(rows, undefined, 'patients').map((s) => s.name)).toEqual(['UHID (patient number)']);
  });
});
