import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

// Adverse events with medical devices (server `MvpiEndpoints`), reported to the Materiovigilance Programme of India (MvPI,
// Indian Pharmacopoeia Commission, Medical Devices Rules 2017) on its medical device adverse event (MDAE) reporting form.
// The quality team raises a report from an incident of the register (an equipment failure, or one marked as involving a
// device) or for a patient, fills in the device (an implant from the patient's bill), and records it as sent with MvPI's
// reference. A sent report does not change: a follow-up report names it, and only its reference is corrected, with a
// reason. The form names the patient by initials.

export const MVPI_SERIOUSNESS = [
  ['Death', 'Death'],
  ['LifeThreatening', 'Life-threatening'],
  ['Hospitalisation', 'Hospitalisation, initial or prolonged'],
  ['Disability', 'Disability'],
  ['CongenitalAnomaly', 'Congenital anomaly'],
  ['RequiredIntervention', 'Required intervention to prevent permanent impairment or damage'],
  ['Other', 'Other medically important'],
] as const;

export const MVPI_OUTCOMES = ['Recovered', 'Recovering', 'NotRecovered', 'Fatal', 'Unknown'] as const;
export type MvpiPatientOutcome = (typeof MVPI_OUTCOMES)[number];
export const mvpiOutcomeLabel: Record<MvpiPatientOutcome, string> = {
  Recovered: 'Recovered',
  Recovering: 'Recovering',
  NotRecovered: 'Not recovered',
  Fatal: 'Fatal',
  Unknown: 'Unknown',
};

export const RISK_CLASSES = ['A', 'B', 'C', 'D'] as const;
export type DeviceRiskClass = (typeof RISK_CLASSES)[number];
export const riskClassLabel: Record<DeviceRiskClass, string> = {
  A: 'Class A (low)',
  B: 'Class B (low to moderate)',
  C: 'Class C (moderate to high)',
  D: 'Class D (high)',
};

export const DEVICE_USES = ['Unknown', 'SingleUse', 'Reusable'] as const;
export type DeviceUse = (typeof DEVICE_USES)[number];
export const deviceUseLabel: Record<DeviceUse, string> = {
  Unknown: 'Not known',
  SingleUse: 'Single use',
  Reusable: 'Reusable',
};

export const DEVICE_OPERATORS = ['HealthProfessional', 'Patient', 'Other'] as const;
export type DeviceOperator = (typeof DEVICE_OPERATORS)[number];
export const deviceOperatorLabel: Record<DeviceOperator, string> = {
  HealthProfessional: 'Health professional',
  Patient: 'Patient',
  Other: 'Someone else (a caregiver)',
};

export const DEVICE_AVAILABILITIES = ['Available', 'NotAvailable', 'ReturnedToManufacturer'] as const;
export type DeviceAvailability = (typeof DEVICE_AVAILABILITIES)[number];
export const deviceAvailabilityLabel: Record<DeviceAvailability, string> = {
  Available: 'Kept for evaluation',
  NotAvailable: 'Not available',
  ReturnedToManufacturer: 'Returned to the manufacturer',
};

export type MvpiReportStatus = 'Draft' | 'Submitted';

/**
 * The server sends `MvpiSeriousness` as a comma-separated list of names ("Hospitalisation, RequiredIntervention"), "None"
 * when not serious; it takes the same back.
 */
export type MvpiSeriousness = string;

export interface MvpiDevice {
  name: string | null;
  genericName: string | null;
  riskClass: DeviceRiskClass | null;
  manufacturer: string | null;
  manufacturerAddress: string | null;
  importer: string | null;
  modelNumber: string | null;
  catalogueNumber: string | null;
  serialNumber: string | null;
  lotNumber: string | null;
  udi: string | null;
  manufacturedOn: string | null;
  expiresOn: string | null;
  softwareVersion: string | null;
  implantable: boolean;
  implantedOn: string | null;
  explantedOn: string | null;
  use: DeviceUse;
  reprocessed: boolean;
  operatedBy: DeviceOperator;
  availability: DeviceAvailability;
  keptAt: string | null;
  returnedOn: string | null;
}

export const blankDevice = (): MvpiDevice => ({
  name: null,
  genericName: null,
  riskClass: null,
  manufacturer: null,
  manufacturerAddress: null,
  importer: null,
  modelNumber: null,
  catalogueNumber: null,
  serialNumber: null,
  lotNumber: null,
  udi: null,
  manufacturedOn: null,
  expiresOn: null,
  softwareVersion: null,
  implantable: false,
  implantedOn: null,
  explantedOn: null,
  use: 'Unknown',
  reprocessed: false,
  operatedBy: 'HealthProfessional',
  availability: 'Available',
  keptAt: null,
  returnedOn: null,
});

export interface MvpiReportRef {
  id: string;
  number: string;
  status: MvpiReportStatus;
}

export interface MvpiReport {
  id: string;
  number: string;
  status: MvpiReportStatus;
  reportedOn: string;
  incidentId: string | null;
  incidentNumber: string | null;
  followUpOfId: string | null;
  followUpOfNumber: string | null;
  followUpOfReference: string | null;
  followUps: MvpiReportRef[];
  patientId: string | null;
  noPatient: boolean;
  patientInitials: string | null;
  uhid: string | null;
  ageYears: number | null;
  sex: string | null;
  weightKg: number | null;
  eventOn: string;
  awareOn: string;
  location: string | null;
  description: string | null;
  seriousness: MvpiSeriousness;
  deathOn: string | null;
  outcome: MvpiPatientOutcome;
  device: MvpiDevice;
  reporterName: string;
  reporterDesignation: string | null;
  reporterDepartment: string | null;
  submittedOn: string | null;
  submittedByName: string | null;
  reference: string | null;
  referenceCorrectionReason: string | null;
  referenceCorrectedByName: string | null;
  referenceCorrectedAt: string | null;
  /** What MvPI still needs before the report can be recorded as sent, by field. */
  missing: Record<string, string>;
  version: number;
}

export interface MvpiReportListItem {
  id: string;
  number: string;
  status: MvpiReportStatus;
  reportedOn: string;
  eventOn: string;
  patientInitials: string | null;
  uhid: string | null;
  noPatient: boolean;
  deviceName: string | null;
  manufacturer: string | null;
  serious: boolean;
  outcome: MvpiPatientOutcome;
  incidentNumber: string | null;
  followUpOfNumber: string | null;
  submittedOn: string | null;
  reference: string | null;
  reporterName: string;
}

export interface ChargedDeviceItem {
  description: string;
  brand: string | null;
  manufacturer: string | null;
  specification: string | null;
  batch: string | null;
  serial: string | null;
  isImplant: boolean;
  usedOn: string;
}

export interface MvpiPrefill {
  incidentId: string | null;
  incidentNumber: string | null;
  patientId: string | null;
  patientInitials: string | null;
  uhid: string | null;
  ageYears: number | null;
  sex: string | null;
  eventOn: string;
  awareOn: string;
  location: string | null;
  description: string | null;
  seriousness: MvpiSeriousness;
  outcome: MvpiPatientOutcome;
  device: MvpiDevice;
  devices: ChargedDeviceItem[];
}

export interface SaveMvpiRequest {
  incidentId: string | null;
  patientId: string | null;
  noPatient: boolean;
  weightKg: number | null;
  eventOn: string;
  awareOn: string;
  location: string | null;
  description: string | null;
  seriousness: MvpiSeriousness;
  deathOn: string | null;
  outcome: MvpiPatientOutcome;
  device: MvpiDevice;
  reporterDesignation: string | null;
  reporterDepartment: string | null;
  version?: number;
}

export interface MvpiFilters {
  status?: MvpiReportStatus;
  from?: string;
  to?: string;
}

export const mvpiKeys = {
  all: ['quality', 'mvpi'] as const,
  list: (filters: MvpiFilters) => ['quality', 'mvpi', 'list', filters] as const,
  report: (id: string) => ['quality', 'mvpi', 'report', id] as const,
  prefill: (incidentId: string | null, patientId: string | null) =>
    ['quality', 'mvpi', 'prefill', incidentId ?? '', patientId ?? ''] as const,
};

const base = '/api/v1/quality/mvpi';

export const mvpiQueries = {
  list: (filters: MvpiFilters) =>
    queryOptions({
      queryKey: mvpiKeys.list(filters),
      queryFn: ({ signal }) => api.get<MvpiReportListItem[]>(base, { query: { ...filters }, signal }),
    }),
  report: (id: string) =>
    queryOptions({
      queryKey: mvpiKeys.report(id),
      queryFn: ({ signal }) => api.get<MvpiReport>(`${base}/${id}`, { signal }),
    }),
  prefill: (incidentId: string | null, patientId: string | null) =>
    queryOptions({
      queryKey: mvpiKeys.prefill(incidentId, patientId),
      queryFn: ({ signal }) =>
        api.get<MvpiPrefill>(`${base}/prefill`, {
          query: { incidentId: incidentId ?? undefined, patientId: patientId ?? undefined },
          signal,
        }),
      staleTime: 0,
    }),
};

export const mvpiApi = {
  raise: (request: SaveMvpiRequest) => api.post<MvpiReport>(base, request),
  correct: (id: string, request: SaveMvpiRequest) => api.put<MvpiReport>(`${base}/${id}`, request),
  submit: (id: string, submittedOn: string, submittedBy: string | null, reference: string | null, version: number) =>
    api.post<MvpiReport>(`${base}/${id}/submit`, { submittedOn, submittedBy, reference, version }),
  correctReference: (id: string, reference: string, reason: string, version: number) =>
    api.post<MvpiReport>(`${base}/${id}/reference`, { reference, reason, version }),
  followUp: (id: string) => api.post<MvpiReport>(`${base}/${id}/follow-up`),
};

/** The printable MDAE form of a report (`<path>/document`, `<path>/pdf`). */
export const mvpiDocument = (id: string) => `${base}/${id}`;

/** The seriousness names chosen, as the server takes them ("None" when none). */
export const mvpiSeriousnessValue = (chosen: readonly string[]) => (chosen.length === 0 ? 'None' : chosen.join(', '));

/** The names in a seriousness the server sent. */
export const mvpiSeriousnessNames = (value: MvpiSeriousness) =>
  value === 'None'
    ? []
    : value
        .split(',')
        .map((v) => v.trim())
        .filter(Boolean);

/** A device charged to the patient, as the device of the report: what the bill says of it. */
export function deviceFromCharge(charged: ChargedDeviceItem, current: MvpiDevice): MvpiDevice {
  const item = charged.description.replace(/^Implant: /, '');
  return {
    ...current,
    name: charged.brand ?? item,
    genericName: charged.specification
      ? charged.brand
        ? `${item}: ${charged.specification}`
        : charged.specification
      : charged.brand
        ? item
        : current.genericName,
    manufacturer: charged.manufacturer,
    lotNumber: charged.batch,
    serialNumber: charged.serial,
    implantable: charged.isImplant,
    implantedOn: charged.isImplant ? charged.usedOn : null,
    explantedOn: charged.isImplant ? current.explantedOn : null,
    use: charged.isImplant ? 'SingleUse' : current.use,
  };
}

const blank = (value: string | null | undefined) => !value || !value.trim();

/**
 * What stops a draft from being saved, field by field (null when nothing does): how its facts fit together, as the server
 * checks them. Dates as yyyy-mm-dd.
 */
export function mvpiDraftProblems(input: {
  patientId: string | null;
  noPatient: boolean;
  eventOn: string;
  awareOn: string;
  outcome: MvpiPatientOutcome;
  seriousness: readonly string[];
  deathOn: string;
  device: MvpiDevice;
  today: string;
}) {
  const d = input.device;
  const death = input.seriousness.includes('Death');
  return {
    patient:
      input.patientId && input.noPatient ? 'Choose the patient, or say no patient was involved: not both.' : null,
    eventOn: !input.eventOn
      ? 'When did it happen?'
      : input.eventOn > input.today
        ? 'It cannot be in the future.'
        : null,
    awareOn:
      !input.awareOn || input.awareOn > input.today || (input.eventOn && input.awareOn < input.eventOn)
        ? 'On or after the event, and not in the future.'
        : null,
    outcome: (input.outcome === 'Fatal') !== death ? 'A fatal outcome and death as the seriousness go together.' : null,
    deathOn:
      input.deathOn && (!death || input.deathOn < input.eventOn || input.deathOn > input.today)
        ? 'With death as the seriousness: on or after the event, and not in the future.'
        : null,
    manufacturedOn:
      d.manufacturedOn && (d.manufacturedOn > input.today || (d.expiresOn && d.expiresOn < d.manufacturedOn))
        ? 'Made before it expires, and not in the future.'
        : null,
    implantedOn:
      d.implantable && d.implantedOn && input.eventOn && d.implantedOn > input.eventOn
        ? 'Implanted on or before the event.'
        : null,
    explantedOn:
      d.implantable &&
      d.explantedOn &&
      (d.explantedOn > input.today || (d.implantedOn !== null && d.explantedOn < d.implantedOn))
        ? 'Explanted after it was implanted, and not in the future.'
        : null,
    returnedOn:
      d.availability === 'ReturnedToManufacturer' &&
      d.returnedOn &&
      (d.returnedOn < input.eventOn || d.returnedOn > input.today)
        ? 'After the event, and not in the future.'
        : null,
  };
}

/**
 * What MvPI needs before a report is recorded as sent (the server's `MvpiReport.Missing`), as messages: the patient or
 * that there was none, where and what happened, the date of a death, the device named well enough to find it again, and
 * the reporter's designation.
 */
export function mvpiMissing(input: {
  patientId: string | null;
  noPatient: boolean;
  location: string;
  description: string;
  seriousness: readonly string[];
  deathOn: string;
  device: MvpiDevice;
  reporterDesignation: string;
}): string[] {
  const d = input.device;
  return [
    !input.patientId && !input.noPatient ? 'the patient, or that no patient was involved' : null,
    blank(input.location) ? 'where it happened' : null,
    blank(input.description) ? 'what happened' : null,
    input.seriousness.includes('Death') && !input.deathOn ? 'the date of death' : null,
    blank(d.name) ? "the device's brand or trade name" : null,
    blank(d.manufacturer) ? "the device's manufacturer" : null,
    [d.modelNumber, d.catalogueNumber, d.serialNumber, d.lotNumber, d.udi].every(blank)
      ? "the device's model, catalogue, serial or lot number, or its UDI"
      : null,
    d.implantable && !d.implantedOn ? 'the date it was implanted' : null,
    d.availability === 'Available' && blank(d.keptAt) ? 'where the device is kept' : null,
    d.availability === 'ReturnedToManufacturer' && !d.returnedOn ? 'the date it went back to the manufacturer' : null,
    blank(input.reporterDesignation) ? "the reporter's designation" : null,
  ].filter((m): m is string => m !== null);
}
