/**
 * Vital-sign helpers (pure, unit-tested): form values, plausibility bounds (the same as the server, so typing slips
 * are caught before a request), BMI, display formatting, abnormal-flag lookup and trend series for charts.
 */

export type FlagDirection = 'Low' | 'High';
export type FlagSeverity = 'Abnormal' | 'Critical';
export type TemperatureUnit = 'Celsius' | 'Fahrenheit';
export type GlucoseTiming = 'Random' | 'Fasting' | 'PostPrandial';

/** Parameter names used by the server's flags (camelCase, note `spo2`). */
export type VitalParameter =
  | 'temperature'
  | 'pulse'
  | 'respiratoryRate'
  | 'spo2'
  | 'painScore'
  | 'gcs'
  | 'systolicBp'
  | 'diastolicBp'
  | 'bmi'
  | 'bloodGlucose';

export interface VitalFlag {
  parameter: string;
  value: number;
  direction: FlagDirection;
  severity: FlagSeverity;
  message: string;
}

/** Numeric entry fields of the vitals form. */
export type VitalField =
  | 'temperature'
  | 'pulse'
  | 'respiratoryRate'
  | 'systolicBp'
  | 'diastolicBp'
  | 'spO2'
  | 'heightCm'
  | 'weightKg'
  | 'painScore'
  | 'bloodGlucose'
  | 'gcs';

export interface VitalsValues {
  temperature: number | null;
  temperatureUnit: TemperatureUnit;
  pulse: number | null;
  respiratoryRate: number | null;
  systolicBp: number | null;
  diastolicBp: number | null;
  spO2: number | null;
  onSupplementalOxygen: boolean;
  heightCm: number | null;
  weightKg: number | null;
  painScore: number | null;
  bloodGlucose: number | null;
  glucoseTiming: GlucoseTiming;
  gcs: number | null;
  notes: string;
}

export const emptyVitals: VitalsValues = {
  temperature: null,
  temperatureUnit: 'Fahrenheit',
  pulse: null,
  respiratoryRate: null,
  systolicBp: null,
  diastolicBp: null,
  spO2: null,
  onSupplementalOxygen: false,
  heightCm: null,
  weightKg: null,
  painScore: null,
  bloodGlucose: null,
  glucoseTiming: 'Random',
  gcs: null,
  notes: '',
};

const NUMERIC_FIELDS: readonly VitalField[] = [
  'temperature',
  'pulse',
  'respiratoryRate',
  'systolicBp',
  'diastolicBp',
  'spO2',
  'heightCm',
  'weightKg',
  'painScore',
  'bloodGlucose',
  'gcs',
];

interface Limit {
  min: number;
  max: number;
  label: string;
  unit: string;
}

/** Physiologically plausible bounds (reject typing errors, not clinical values). Mirrors RecordVitalsRequestValidator. */
export function limitFor(field: VitalField, temperatureUnit: TemperatureUnit = 'Celsius'): Limit {
  switch (field) {
    case 'temperature':
      return temperatureUnit === 'Fahrenheit' ? { min: 77, max: 113, label: 'Temperature', unit: '°F' } : { min: 25, max: 45, label: 'Temperature', unit: '°C' };
    case 'pulse':
      return { min: 20, max: 300, label: 'Pulse', unit: '/min' };
    case 'respiratoryRate':
      return { min: 4, max: 80, label: 'Respiratory rate', unit: '/min' };
    case 'systolicBp':
      return { min: 40, max: 300, label: 'Systolic BP', unit: 'mmHg' };
    case 'diastolicBp':
      return { min: 20, max: 200, label: 'Diastolic BP', unit: 'mmHg' };
    case 'spO2':
      return { min: 30, max: 100, label: 'SpO₂', unit: '%' };
    case 'heightCm':
      return { min: 30, max: 250, label: 'Height', unit: 'cm' };
    case 'weightKg':
      return { min: 0.3, max: 400, label: 'Weight', unit: 'kg' };
    case 'painScore':
      return { min: 0, max: 10, label: 'Pain score', unit: '/10' };
    case 'bloodGlucose':
      return { min: 10, max: 1500, label: 'Blood glucose', unit: 'mg/dL' };
    case 'gcs':
      return { min: 3, max: 15, label: 'GCS', unit: '' };
  }
}

function present(value: number | null | undefined): value is number {
  return value !== null && value !== undefined && !Number.isNaN(value);
}

export function hasAnyMeasurement(values: VitalsValues): boolean {
  return NUMERIC_FIELDS.some((field) => present(values[field]));
}

/** Field → message for values outside the plausible range (empty when everything typed is plausible). */
export function validateVitals(values: VitalsValues): Partial<Record<VitalField, string>> {
  const errors: Partial<Record<VitalField, string>> = {};
  for (const field of NUMERIC_FIELDS) {
    const value = values[field];
    if (!present(value)) {
      continue;
    }
    const limit = limitFor(field, values.temperatureUnit);
    if (value < limit.min || value > limit.max) {
      errors[field] = `${limit.label} must be between ${limit.min} and ${limit.max}${limit.unit && !limit.unit.startsWith('/') ? ` ${limit.unit}` : ''}.`;
    }
  }
  if (present(values.systolicBp) && present(values.diastolicBp) && values.diastolicBp >= values.systolicBp && !errors.diastolicBp) {
    errors.diastolicBp = 'Diastolic pressure must be lower than systolic.';
  }
  return errors;
}

/** kg/m² rounded to one decimal (as the server does); null unless both values are plausible. */
export function computeBmi(heightCm: number | null | undefined, weightKg: number | null | undefined): number | null {
  if (!present(heightCm) || !present(weightKg) || heightCm <= 30 || weightKg <= 0) {
    return null;
  }
  const metres = heightCm / 100;
  return Math.round((weightKg / (metres * metres)) * 10) / 10;
}

export function fahrenheitToCelsius(value: number): number {
  return Math.round((((value - 32) * 5) / 9) * 10) / 10;
}

export function celsiusToFahrenheit(value: number): number {
  return Math.round(((value * 9) / 5 + 32) * 10) / 10;
}

/** "37.2 °C (99.0 °F)" — both scales, because Indian wards use °F and doctors read °C. */
export function formatTemperature(celsius: number | null | undefined): string {
  if (!present(celsius)) {
    return '—';
  }
  return `${celsius.toFixed(1)} °C (${celsiusToFahrenheit(celsius).toFixed(1)} °F)`;
}

export function formatBp(systolic: number | null | undefined, diastolic: number | null | undefined): string {
  if (!present(systolic) && !present(diastolic)) {
    return '—';
  }
  return `${present(systolic) ? systolic : '—'}/${present(diastolic) ? diastolic : '—'} mmHg`;
}

/** The server's flag parameter for a form field (weight/height drive the BMI flag). */
export function parameterFor(field: VitalField): VitalParameter | null {
  switch (field) {
    case 'spO2':
      return 'spo2';
    case 'heightCm':
    case 'weightKg':
      return 'bmi';
    default:
      return field;
  }
}

export function flagsFor(flags: readonly VitalFlag[] | undefined, parameter: VitalParameter | null): VitalFlag[] {
  return parameter ? (flags ?? []).filter((flag) => flag.parameter === parameter) : [];
}

/** The most serious flag (critical before abnormal), or undefined when all is in range. */
export function worstFlag(flags: readonly VitalFlag[] | undefined): VitalFlag | undefined {
  const list = flags ?? [];
  return list.find((flag) => flag.severity === 'Critical') ?? list[0];
}

export function flagTone(flag: VitalFlag | undefined): 'danger' | 'warning' | undefined {
  return flag ? (flag.severity === 'Critical' ? 'danger' : 'warning') : undefined;
}

/** A recorded set of vitals as returned by the server (VitalsDto / VitalsSnapshot subset). */
export interface VitalsReading {
  recordedAt: string;
  temperatureCelsius: number | null;
  pulse: number | null;
  respiratoryRate?: number | null;
  systolicBp: number | null;
  diastolicBp: number | null;
  spO2: number | null;
  weightKg: number | null;
  bmi: number | null;
  painScore: number | null;
  bloodGlucose: number | null;
  gcs: number | null;
  flags?: VitalFlag[];
}

export interface VitalChip {
  parameter: VitalParameter;
  label: string;
  value: string;
  flag?: VitalFlag;
}

/** Compact, labelled values for summaries ("BP 150/95 mmHg", flagged ones carry their worst flag). */
export function vitalChips(reading: VitalsReading): VitalChip[] {
  const chips: VitalChip[] = [];
  const flagged = (parameter: VitalParameter) => worstFlag(flagsFor(reading.flags, parameter));
  if (present(reading.systolicBp) || present(reading.diastolicBp)) {
    chips.push({
      parameter: 'systolicBp',
      label: 'BP',
      value: formatBp(reading.systolicBp, reading.diastolicBp),
      flag: worstFlag([...flagsFor(reading.flags, 'systolicBp'), ...flagsFor(reading.flags, 'diastolicBp')]),
    });
  }
  if (present(reading.pulse)) {
    chips.push({ parameter: 'pulse', label: 'Pulse', value: `${reading.pulse}/min`, flag: flagged('pulse') });
  }
  if (present(reading.spO2)) {
    chips.push({ parameter: 'spo2', label: 'SpO₂', value: `${reading.spO2}%`, flag: flagged('spo2') });
  }
  if (present(reading.temperatureCelsius)) {
    chips.push({ parameter: 'temperature', label: 'Temp', value: formatTemperature(reading.temperatureCelsius), flag: flagged('temperature') });
  }
  if (present(reading.respiratoryRate)) {
    chips.push({ parameter: 'respiratoryRate', label: 'RR', value: `${reading.respiratoryRate}/min`, flag: flagged('respiratoryRate') });
  }
  if (present(reading.bloodGlucose)) {
    chips.push({ parameter: 'bloodGlucose', label: 'Glucose', value: `${reading.bloodGlucose} mg/dL`, flag: flagged('bloodGlucose') });
  }
  if (present(reading.painScore)) {
    chips.push({ parameter: 'painScore', label: 'Pain', value: `${reading.painScore}/10`, flag: flagged('painScore') });
  }
  if (present(reading.gcs)) {
    chips.push({ parameter: 'gcs', label: 'GCS', value: `${reading.gcs}/15`, flag: flagged('gcs') });
  }
  if (present(reading.weightKg)) {
    chips.push({ parameter: 'bmi', label: 'Weight', value: `${reading.weightKg} kg${present(reading.bmi) ? ` · BMI ${reading.bmi}` : ''}`, flag: flagged('bmi') });
  }
  return chips;
}

export type TrendMetric = 'bp' | 'pulse' | 'spo2' | 'temperature' | 'respiratoryRate' | 'bloodGlucose' | 'weight' | 'painScore';

export interface TrendMetricInfo {
  id: TrendMetric;
  label: string;
  unit: string;
  /** Usual adult reference band, drawn as guide lines. */
  reference?: { low: number; high: number };
}

export const TREND_METRICS: readonly TrendMetricInfo[] = [
  { id: 'bp', label: 'Blood pressure', unit: 'mmHg', reference: { low: 90, high: 140 } },
  { id: 'pulse', label: 'Pulse', unit: '/min', reference: { low: 60, high: 100 } },
  { id: 'spo2', label: 'SpO₂', unit: '%', reference: { low: 95, high: 100 } },
  { id: 'temperature', label: 'Temperature', unit: '°C', reference: { low: 36, high: 37.5 } },
  { id: 'respiratoryRate', label: 'Respiratory rate', unit: '/min', reference: { low: 12, high: 20 } },
  { id: 'bloodGlucose', label: 'Blood glucose', unit: 'mg/dL', reference: { low: 70, high: 180 } },
  { id: 'weight', label: 'Weight', unit: 'kg' },
  { id: 'painScore', label: 'Pain score', unit: '/10' },
];

export interface TrendSeries {
  /** Reading times, oldest first. */
  at: string[];
  series: { name: string; values: (number | null)[] }[];
}

function metricValues(reading: VitalsReading, metric: TrendMetric): { name: string; value: number | null }[] {
  const v = (value: number | null | undefined) => (present(value) ? value : null);
  switch (metric) {
    case 'bp':
      return [
        { name: 'Systolic', value: v(reading.systolicBp) },
        { name: 'Diastolic', value: v(reading.diastolicBp) },
      ];
    case 'pulse':
      return [{ name: 'Pulse', value: v(reading.pulse) }];
    case 'spo2':
      return [{ name: 'SpO₂', value: v(reading.spO2) }];
    case 'temperature':
      return [{ name: 'Temperature', value: v(reading.temperatureCelsius) }];
    case 'respiratoryRate':
      return [{ name: 'Respiratory rate', value: v(reading.respiratoryRate) }];
    case 'bloodGlucose':
      return [{ name: 'Blood glucose', value: v(reading.bloodGlucose) }];
    case 'weight':
      return [{ name: 'Weight', value: v(reading.weightKg) }];
    case 'painScore':
      return [{ name: 'Pain score', value: v(reading.painScore) }];
  }
}

/** Chart series for one metric, oldest reading first, skipping readings without that measurement. */
export function trendSeries(readings: readonly VitalsReading[], metric: TrendMetric): TrendSeries {
  const rows = readings
    .map((reading) => ({ at: reading.recordedAt, values: metricValues(reading, metric) }))
    .filter((row) => row.values.some((entry) => entry.value !== null))
    .toSorted((a, b) => Date.parse(a.at) - Date.parse(b.at));
  const names = metricValues({ recordedAt: '', temperatureCelsius: null, pulse: null, systolicBp: null, diastolicBp: null, spO2: null, weightKg: null, bmi: null, painScore: null, bloodGlucose: null, gcs: null }, metric).map(
    (entry) => entry.name,
  );
  return {
    at: rows.map((row) => row.at),
    series: names.map((name, index) => ({ name, values: rows.map((row) => row.values[index]?.value ?? null) })),
  };
}

export interface RecordVitalsPayload {
  encounterId: string | null;
  recordedAt: string | null;
  temperature: number | null;
  temperatureUnit: TemperatureUnit;
  pulse: number | null;
  respiratoryRate: number | null;
  systolicBp: number | null;
  diastolicBp: number | null;
  spO2: number | null;
  onSupplementalOxygen: boolean;
  heightCm: number | null;
  weightKg: number | null;
  painScore: number | null;
  bloodGlucose: number | null;
  glucoseTiming: GlucoseTiming;
  gcs: number | null;
  notes: string | null;
}

/** RecordVitalsRequest from the form (empty inputs become null). */
export function toVitalsPayload(values: VitalsValues, encounterId: string | null, recordedAt: string | null = null): RecordVitalsPayload {
  const n = (value: number | null) => (present(value) ? value : null);
  return {
    encounterId,
    recordedAt,
    temperature: n(values.temperature),
    temperatureUnit: values.temperatureUnit,
    pulse: n(values.pulse),
    respiratoryRate: n(values.respiratoryRate),
    systolicBp: n(values.systolicBp),
    diastolicBp: n(values.diastolicBp),
    spO2: n(values.spO2),
    onSupplementalOxygen: values.onSupplementalOxygen,
    heightCm: n(values.heightCm),
    weightKg: n(values.weightKg),
    painScore: n(values.painScore),
    bloodGlucose: n(values.bloodGlucose),
    glucoseTiming: values.glucoseTiming,
    gcs: n(values.gcs),
    notes: values.notes.trim() || null,
  };
}
