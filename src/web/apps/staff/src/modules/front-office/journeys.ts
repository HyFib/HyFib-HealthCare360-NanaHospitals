import type { Gender, PagedResult } from '@hc360/core';
import type { PipelineStage } from '@hc360/ui';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

// Where patients are in the hospital: the pipeline of each visit, stay, operation and lab accession, derived on the
// server from what the modules record (docs/modules/analytics.md, "Patient journeys"). Nothing here sets a stage.

export type JourneyKind = 'Outpatient' | 'Inpatient' | 'Surgery' | 'Lab';
export type JourneyStatus = 'Active' | 'Completed' | 'Cancelled';

/** One journey (the server's `JourneyDto`): what it follows, its stages in order and the stage the patient is in. */
export interface JourneyDto {
  kind: JourneyKind;
  /** The visit's, admission's, booking's or lab order's id. */
  id: string;
  /** The visit, admission, booking or accession number. */
  number: string;
  /** The department and doctor, the ward and bed, the operation, the tests. */
  title: string;
  status: JourneyStatus;
  startedAt: string;
  stages: PipelineStage[];
  /** Null once the journey is finished or cancelled. */
  current: PipelineStage | null;
}

/** GET /api/v1/patients/{patientId}/journey */
export interface PatientJourneys {
  patientId: string;
  asOf: string;
  outpatient: JourneyDto | null;
  inpatient: JourneyDto | null;
  surgeries: JourneyDto[];
  labOrders: JourneyDto[];
  /** The record's sensitive category closes its operations to this user, so they are left out. */
  surgeriesWithheld: boolean;
}

/** A row of GET /api/v1/journeys/in-hospital. */
export interface InHospitalPatient {
  patientId: string;
  uhid: string;
  /** Initials and "(restricted)" for a restricted patient the user may not open. */
  patientName: string;
  isRestricted: boolean;
  gender: Gender;
  age: string | null;
  kind: 'Outpatient' | 'Inpatient';
  departmentId: string | null;
  departmentName: string | null;
  wardId: string | null;
  /** The token for an out-patient, the ward and bed for an in-patient. */
  place: string | null;
  arrivedAt: string;
  journey: JourneyDto;
}

export interface InHospitalFilters {
  kind?: 'Outpatient' | 'Inpatient';
  departmentId?: string;
  wardId?: string;
  page?: number;
}

export const JOURNEY_KIND_LABEL: Record<JourneyKind, string> = {
  Outpatient: 'Out-patient visit',
  Inpatient: 'In-patient stay',
  Surgery: 'Operation',
  Lab: 'Lab order',
};

/** A journey's pipelines refresh every minute, as the other live worklists do, and on every visit change in the facility. */
const REFRESH = 60_000;

export const journeyKeys = {
  all: ['journeys'] as const,
  patient: (patientId: string) => ['journeys', 'patient', patientId] as const,
  inHospital: (filters: InHospitalFilters) => ['journeys', 'in-hospital', filters] as const,
};

export const journeyQueries = {
  forPatient: (patientId: string) =>
    queryOptions({
      queryKey: journeyKeys.patient(patientId),
      queryFn: ({ signal }) => api.get<PatientJourneys>(`/api/v1/patients/${patientId}/journey`, { signal }),
      refetchInterval: REFRESH,
    }),
  inHospital: (filters: InHospitalFilters) =>
    queryOptions({
      queryKey: journeyKeys.inHospital(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<InHospitalPatient>>('/api/v1/journeys/in-hospital', {
          query: { ...filters, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: REFRESH,
    }),
};
