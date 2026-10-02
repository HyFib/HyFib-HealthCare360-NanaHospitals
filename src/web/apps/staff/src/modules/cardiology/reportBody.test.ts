import { describe, expect, it } from 'vitest';
import type { EcgFindings } from './api';
import { measurementRows } from './reportBody';

const ecg: EcgFindings = {
  heartRate: 118,
  rhythm: 'Sinus tachycardia',
  prInterval: 140,
  qrsDuration: 90,
  qtInterval: 320,
  qtcInterval: 449,
  axis: 'Normal',
  stTChanges: 'ST elevation in V1–V4 <2 mm>',
  interpretation: null,
};

describe('cardiology measurements', () => {
  it('lists only the measurements that were recorded, with units', () => {
    const rows = measurementRows('Ecg', { ecg });
    expect(rows[0]).toEqual(['Heart rate', '118 /min']);
    expect(rows.map(([label]) => label)).not.toContain('Interpretation');
    expect(rows).toContainEqual(['QTc', '449 ms']);
  });

  it('formats treadmill, Holter and ABPM values', () => {
    expect(
      measurementRows('Tmt', {
        tmt: {
          protocol: 'Bruce',
          durationSeconds: 545,
          mets: 10.1,
          maxHeartRate: 152,
          targetHeartRatePercent: 89,
          bpResponse: null,
          symptoms: null,
          stChanges: null,
          reasonForTermination: 'Fatigue',
          result: 'Negative',
        },
      }),
    ).toEqual([
      ['Protocol', 'Bruce'],
      ['Exercise duration', '9:05 min'],
      ['Workload', '10.1 METs'],
      ['Maximum heart rate', '152 /min'],
      ['Target heart rate reached', '89 %'],
      ['Reason for stopping', 'Fatigue'],
      ['Result', 'Negative for inducible ischaemia'],
    ]);
    expect(
      measurementRows('Abpm', {
        abpm: {
          daytimeMeanSystolic: 142,
          daytimeMeanDiastolic: 88,
          nighttimeMeanSystolic: 130,
          nighttimeMeanDiastolic: 80,
          dippingPattern: 'Non-dipper',
        },
      }),
    ).toContainEqual(['Daytime mean BP', '142/88 mmHg']);
    expect(measurementRows('Other', { ecg })).toEqual([]);
  });
});
