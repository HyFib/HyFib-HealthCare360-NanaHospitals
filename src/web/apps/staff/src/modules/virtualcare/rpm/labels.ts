import type { StatusTone } from '@hc360/ui';
import type {
  ReadingSeverity,
  ReadingSource,
  RpmAlertSeverity,
  RpmAlertStatus,
  RpmDeviceType,
  RpmEnrollmentStatus,
  RpmProgramKind,
  RpmReadingType,
} from './api';

/** Label, canonical unit (as stored by the server) and the units a nurse may type in (converted by the server). */
export const readingTypeInfo: Record<
  RpmReadingType,
  { label: string; unit: string; inputUnits: string[]; plausible: { min: number; max: number } }
> = {
  SystolicBp: { label: 'Systolic BP', unit: 'mmHg', inputUnits: ['mmHg', 'kPa'], plausible: { min: 50, max: 300 } },
  DiastolicBp: { label: 'Diastolic BP', unit: 'mmHg', inputUnits: ['mmHg', 'kPa'], plausible: { min: 20, max: 200 } },
  HeartRate: { label: 'Heart rate', unit: 'bpm', inputUnits: ['bpm'], plausible: { min: 20, max: 300 } },
  SpO2: { label: 'SpO2', unit: '%', inputUnits: ['%'], plausible: { min: 50, max: 100 } },
  Glucose: {
    label: 'Blood glucose',
    unit: 'mg/dL',
    inputUnits: ['mg/dL', 'mmol/L'],
    plausible: { min: 10, max: 1000 },
  },
  Weight: { label: 'Weight', unit: 'kg', inputUnits: ['kg', 'lb'], plausible: { min: 0.5, max: 400 } },
  Temperature: { label: 'Temperature', unit: '°C', inputUnits: ['°C', '°F'], plausible: { min: 30, max: 45 } },
  EcgRhythm: { label: 'ECG rhythm', unit: 'class', inputUnits: ['class'], plausible: { min: 0, max: 2 } },
};

/** ECG readings are stored as a rhythm class. */
export const ecgRhythmLabel: Record<number, string> = {
  0: 'Normal sinus rhythm',
  1: 'Needs review',
  2: 'Atrial fibrillation or worse',
};

export function formatReading(type: RpmReadingType, value: number | null, text?: string | null): string {
  if (type === 'EcgRhythm') {
    return text || (value === null ? '—' : (ecgRhythmLabel[Math.round(value)] ?? `Class ${value}`));
  }
  if (value === null) {
    return '—';
  }
  const rounded = Number.isInteger(value) ? value.toString() : value.toFixed(1);
  const unit = readingTypeInfo[type].unit;
  return unit === '%' ? `${rounded}%` : `${rounded} ${unit}`;
}

export const deviceTypeLabel: Record<RpmDeviceType, string> = {
  BpMonitor: 'Blood pressure monitor',
  Glucometer: 'Glucometer',
  PulseOximeter: 'Pulse oximeter',
  WeightScale: 'Weighing scale',
  Thermometer: 'Thermometer',
  EcgPatch: 'ECG patch',
};

/** What each device type can report (a thermometer sending blood pressure is rejected by the server). */
export const deviceMeasures: Record<RpmDeviceType, RpmReadingType[]> = {
  BpMonitor: ['SystolicBp', 'DiastolicBp', 'HeartRate'],
  Glucometer: ['Glucose'],
  PulseOximeter: ['SpO2', 'HeartRate'],
  WeightScale: ['Weight'],
  Thermometer: ['Temperature'],
  EcgPatch: ['EcgRhythm', 'HeartRate'],
};

export const programKindLabel: Record<RpmProgramKind, string> = {
  Hypertension: 'Hypertension',
  Diabetes: 'Diabetes',
  Cardiac: 'Cardiac care',
  PostDischarge: 'After discharge',
  Custom: 'Other',
};

export const enrollmentStatusText: Record<RpmEnrollmentStatus, { label: string; tone: StatusTone; hint: string }> = {
  Active: { label: 'Active', tone: 'success', hint: 'Readings are checked against the limits and raise alerts.' },
  Paused: { label: 'Paused', tone: 'warning', hint: 'Readings are collected but no alerts are raised.' },
  Completed: { label: 'Completed', tone: 'informative', hint: 'Monitoring has ended.' },
  Withdrawn: { label: 'Withdrawn', tone: 'informative', hint: 'Monitoring has ended.' },
};

export const alertSeverityText: Record<RpmAlertSeverity, { label: string; tone: StatusTone }> = {
  Critical: { label: 'Critical', tone: 'danger' },
  Warning: { label: 'Warning', tone: 'warning' },
};

export const alertStatusText: Record<RpmAlertStatus, { label: string; tone: StatusTone }> = {
  Open: { label: 'New', tone: 'danger' },
  Acknowledged: { label: 'Seen', tone: 'brand' },
  Resolved: { label: 'Resolved', tone: 'success' },
};

export const severityText: Record<ReadingSeverity, { label: string; tone: StatusTone }> = {
  Normal: { label: 'Normal', tone: 'success' },
  Warning: { label: 'Out of range', tone: 'warning' },
  Critical: { label: 'Critical', tone: 'danger' },
};

export const sourceLabel: Record<ReadingSource, string> = {
  Device: 'Device',
  Fhir: 'Health app (FHIR)',
  Manual: 'Entered by staff',
};
