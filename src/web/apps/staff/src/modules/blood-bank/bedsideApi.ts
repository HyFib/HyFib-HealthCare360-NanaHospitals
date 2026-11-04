import type { BloodGroup } from '@hc360/core';
import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { BloodComponentType } from './api';

// The ward's side of a unit of blood: the bedside check by two staff that starts it, observations while it runs, and
// its end.

export const TRANSFUSION_STATUSES = ['InProgress', 'Completed', 'Stopped'] as const;
export type TransfusionStatus = (typeof TRANSFUSION_STATUSES)[number];

export const OBSERVATION_KINDS = ['Baseline', 'FifteenMinutes', 'Later', 'End'] as const;
export type TransfusionObservationKind = (typeof OBSERVATION_KINDS)[number];

/** Temperature, pulse, blood pressure, respiratory rate, and oxygen saturation when measured. */
export interface TransfusionObservationValues {
  temperatureC: number | null;
  pulsePerMinute: number | null;
  systolicMmHg: number | null;
  diastolicMmHg: number | null;
  respiratoryRate: number | null;
  spO2Percent: number | null;
}

export interface TransfusionObservationDto extends TransfusionObservationValues {
  kind: TransfusionObservationKind;
  recordedAt: string;
  recordedByName: string;
}

export interface TransfusionDto {
  id: string;
  issueId: string;
  startedAt: string;
  startedByName: string;
  witnessName: string;
  consentId: string | null;
  consentDeferredReason: string | null;
  status: TransfusionStatus;
  endedAt: string | null;
  endedByName: string | null;
  volumeMl: number | null;
  stopReason: string | null;
  observations: TransfusionObservationDto[];
  version: number;
}

/** A unit issued for the patient, as the ward sees it. */
export interface WardBloodUnit {
  issueId: string;
  requestNumber: string;
  unitNumber: string;
  componentType: BloodComponentType;
  unitBloodGroup: BloodGroup;
  volumeMl: number;
  expiresAt: string;
  isEmergencyRelease: boolean;
  /** When the cross-match stops standing (72 hours); null for a unit released in an emergency. */
  crossMatchValidUntil: string | null;
  issuedAt: string;
  issuedToName: string;
  /** Four hours after it left the blood bank: not started by then, it goes back to be discarded. */
  startBy: string;
  returnedAt: string | null;
  /** The patient's transfusion consent for this stay, if recorded. */
  consentCapturedAt: string | null;
  transfusion: TransfusionDto | null;
}

export interface PatientBloodUnits {
  patientId: string;
  uhid: string;
  patientName: string;
  patientBloodGroup: BloodGroup;
  units: WardBloodUnit[];
}

export interface StartTransfusionRequest {
  /** The UHID as read from the patient's wristband (scanned, or typed from it). */
  wristbandUhid: string;
  /** The unit number as read from the bag. */
  bagUnitNumber: string;
  witnessConfirmationId: string;
  baseline: TransfusionObservationValues;
  /** A unit released in an emergency only: why the patient's consent cannot be taken before it is started. */
  consentDeferredReason: string | null;
}

export interface RecordTransfusionObservationRequest {
  kind: 'FifteenMinutes' | 'Later';
  recordedAt: string | null;
  values: TransfusionObservationValues;
  version: number;
}

export interface EndTransfusionRequest {
  endedAt: string | null;
  stopped: boolean;
  stopReason: string | null;
  volumeMl: number;
  observation: TransfusionObservationValues;
  version: number;
}

export const bedsideKeys = {
  units: (patientId: string) => ['blood-bank', 'patient-units', patientId] as const,
};

export const bedsideQueries = {
  units: (patientId: string) =>
    queryOptions({
      queryKey: bedsideKeys.units(patientId),
      queryFn: ({ signal }) => api.get<PatientBloodUnits>(`/api/v1/blood-bank/patients/${patientId}/units`, { signal }),
      refetchInterval: 60_000,
    }),
};

export const bedsideApi = {
  start: (issueId: string, request: StartTransfusionRequest) =>
    api.post<TransfusionDto>(`/api/v1/blood-bank/issues/${issueId}/transfusion`, request),
  observe: (transfusionId: string, request: RecordTransfusionObservationRequest) =>
    api.post<TransfusionDto>(`/api/v1/blood-bank/transfusions/${transfusionId}/observations`, request),
  end: (transfusionId: string, request: EndTransfusionRequest) =>
    api.post<TransfusionDto>(`/api/v1/blood-bank/transfusions/${transfusionId}/end`, request),
};
