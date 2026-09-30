import { describe, expect, it } from 'vitest';
import {
  celsiusToFahrenheit,
  computeBmi,
  emptyVitals,
  fahrenheitToCelsius,
  flagsFor,
  formatBp,
  formatTemperature,
  hasAnyMeasurement,
  parameterFor,
  toVitalsPayload,
  trendSeries,
  validateVitals,
  vitalChips,
  worstFlag,
  type VitalFlag,
  type VitalsReading,
} from './vitals';

const fever: VitalFlag = {
  parameter: 'temperature',
  value: 38.6,
  direction: 'High',
  severity: 'Abnormal',
  message: 'Fever',
};
const hypoxia: VitalFlag = {
  parameter: 'spo2',
  value: 86,
  direction: 'Low',
  severity: 'Critical',
  message: 'Severe hypoxaemia (SpO₂ < 90%)',
};

function reading(partial: Partial<VitalsReading>): VitalsReading {
  return {
    recordedAt: '2026-09-20T04:30:00Z',
    temperatureCelsius: null,
    pulse: null,
    respiratoryRate: null,
    systolicBp: null,
    diastolicBp: null,
    spO2: null,
    weightKg: null,
    bmi: null,
    painScore: null,
    bloodGlucose: null,
    gcs: null,
    ...partial,
  };
}

describe('formatting', () => {
  it('formats blood pressure and temperature', () => {
    expect(formatBp(120, 80)).toBe('120/80 mmHg');
    expect(formatBp(null, 80)).toBe('—/80 mmHg');
    expect(formatBp(null, null)).toBe('—');
    expect(formatTemperature(37)).toBe('37.0 °C (98.6 °F)');
    expect(formatTemperature(null)).toBe('—');
  });

  it('converts between temperature scales', () => {
    expect(celsiusToFahrenheit(38.5)).toBe(101.3);
    expect(fahrenheitToCelsius(102)).toBe(38.9);
  });

  it('computes BMI like the server', () => {
    expect(computeBmi(170, 70)).toBe(24.2);
    expect(computeBmi(null, 70)).toBeNull();
    expect(computeBmi(20, 70)).toBeNull();
  });
});

describe('validation', () => {
  it('accepts plausible values and flags typing errors', () => {
    expect(validateVitals({ ...emptyVitals, pulse: 88, spO2: 97 })).toEqual({});
    const errors = validateVitals({ ...emptyVitals, pulse: 880, temperature: 38.5, temperatureUnit: 'Fahrenheit' });
    expect(errors.pulse).toContain('between 20 and 300');
    expect(errors.temperature).toContain('77 and 113 °F');
  });

  it('needs the diastolic pressure below the systolic', () => {
    expect(validateVitals({ ...emptyVitals, systolicBp: 80, diastolicBp: 90 }).diastolicBp).toContain(
      'lower than systolic',
    );
  });

  it('knows when anything was measured', () => {
    expect(hasAnyMeasurement(emptyVitals)).toBe(false);
    expect(hasAnyMeasurement({ ...emptyVitals, painScore: 0 })).toBe(true);
  });

  it('builds the request with nulls for empty fields', () => {
    const payload = toVitalsPayload({ ...emptyVitals, pulse: 72, notes: '  after walk ' }, 'enc-1');
    expect(payload).toMatchObject({
      encounterId: 'enc-1',
      pulse: 72,
      spO2: null,
      notes: 'after walk',
      temperatureUnit: 'Fahrenheit',
    });
  });
});

describe('flags', () => {
  it('maps form fields to server parameters', () => {
    expect(parameterFor('spO2')).toBe('spo2');
    expect(parameterFor('weightKg')).toBe('bmi');
    expect(parameterFor('pulse')).toBe('pulse');
  });

  it('finds the flags of a parameter and the most serious one', () => {
    expect(flagsFor([fever, hypoxia], 'spo2')).toEqual([hypoxia]);
    expect(worstFlag([fever, hypoxia])).toBe(hypoxia);
    expect(worstFlag([])).toBeUndefined();
  });

  it('builds summary chips with their flags', () => {
    const chips = vitalChips(
      reading({ systolicBp: 150, diastolicBp: 95, spO2: 86, temperatureCelsius: 38.6, flags: [fever, hypoxia] }),
    );
    expect(chips.map((c) => c.label)).toEqual(['BP', 'SpO₂', 'Temp']);
    expect(chips.find((c) => c.label === 'SpO₂')?.flag).toBe(hypoxia);
    expect(chips.find((c) => c.label === 'BP')?.flag).toBeUndefined();
  });
});

describe('trendSeries', () => {
  it('orders readings oldest first and skips readings without the metric', () => {
    const series = trendSeries(
      [
        reading({ recordedAt: '2026-09-21T04:30:00Z', systolicBp: 130, diastolicBp: 85 }),
        reading({ recordedAt: '2026-09-19T04:30:00Z', systolicBp: 150, diastolicBp: 95 }),
        reading({ recordedAt: '2026-09-20T04:30:00Z', pulse: 90 }),
      ],
      'bp',
    );
    expect(series.at).toEqual(['2026-09-19T04:30:00Z', '2026-09-21T04:30:00Z']);
    expect(series.series).toEqual([
      { name: 'Systolic', values: [150, 130] },
      { name: 'Diastolic', values: [95, 85] },
    ]);
  });

  it('keeps gaps as null when only part of a metric was measured', () => {
    const series = trendSeries([reading({ systolicBp: 140 })], 'bp');
    expect(series.series[1]).toEqual({ name: 'Diastolic', values: [null] });
  });
});
