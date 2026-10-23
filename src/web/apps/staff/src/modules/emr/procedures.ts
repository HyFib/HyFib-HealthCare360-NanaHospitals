/**
 * Structured procedure records (EHR-01): the API, and the pure helpers the chart's procedure tab uses to turn its form
 * into a request and a record back into the form for a correction (unit-tested).
 */
import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { fromIstInput, toIstInput } from '@/modules/hr/shared/dates';

export type ProcedureSide = 'NotApplicable' | 'Left' | 'Right' | 'Bilateral';
export type ProcedureSource = 'Recorded' | 'OperationTheatre';
export type ProcedureStatus = 'Completed' | 'Amended' | 'EnteredInError';

export const PROCEDURE_SIDES: readonly ProcedureSide[] = ['NotApplicable', 'Left', 'Right', 'Bilateral'];

export const procedureSideLabel: Record<ProcedureSide, string> = {
  NotApplicable: 'Not applicable',
  Left: 'Left',
  Right: 'Right',
  Bilateral: 'Both sides',
};

export interface ProcedureDto {
  id: string;
  patientId: string;
  encounterId: string | null;
  admissionId: string | null;
  performedAt: string;
  code: string | null;
  codeSystem: string | null;
  display: string;
  performerPractitionerId: string | null;
  /** The performing practitioner's name, or the name recorded for a performer from elsewhere. */
  performerName: string | null;
  side: ProcedureSide;
  bodySite: string | null;
  outcome: string | null;
  notes: string | null;
  source: ProcedureSource;
  status: ProcedureStatus;
  statusReason: string | null;
  statusChangedAt: string | null;
  version: number;
}

export interface ProcedureFields {
  performedAt: string;
  code: string | null;
  display: string | null;
  performerPractitionerId: string | null;
  performerName: string | null;
  side: ProcedureSide;
  bodySite: string | null;
  outcome: string | null;
  notes: string | null;
}

export interface RecordProcedureRequest extends ProcedureFields {
  encounterId: string | null;
}

export interface CorrectProcedureRequest extends ProcedureFields {
  reason: string;
  /** The record as it was shown: one corrected meanwhile is not corrected again over it (REL-04). */
  version: number;
}

/** What the form edits: the time as an IST `datetime-local` value, every text as a string ('' for none). */
export interface ProcedureFormValues {
  performedAt: string;
  code: string;
  display: string;
  performerName: string;
  side: ProcedureSide;
  bodySite: string;
  outcome: string;
  notes: string;
}

export function emptyProcedureForm(now: string): ProcedureFormValues {
  return {
    performedAt: now,
    code: '',
    display: '',
    performerName: '',
    side: 'NotApplicable',
    bodySite: '',
    outcome: '',
    notes: '',
  };
}

/**
 * A record back into the form, for its correction. The performer is kept when the doctor leaves the name alone: a
 * practitioner from the hospital's list stays linked, and only a name typed for someone from elsewhere is editable.
 */
export function procedureToForm(p: ProcedureDto): ProcedureFormValues {
  return {
    performedAt: toIstInput(p.performedAt),
    code: p.code ?? '',
    display: p.display,
    performerName: p.performerPractitionerId ? '' : (p.performerName ?? ''),
    side: p.side,
    bodySite: p.bodySite ?? '',
    outcome: p.outcome ?? '',
    notes: p.notes ?? '',
  };
}

const blank = (value: string) => value.trim() || null;

/**
 * The request fields from the form. With neither a practitioner nor a name, the server takes the recording doctor as
 * the performer, so a correction passes the original practitioner back to keep it.
 */
export function procedureFields(
  v: ProcedureFormValues,
  performerPractitionerId: string | null = null,
): ProcedureFields {
  const performerName = blank(v.performerName);
  return {
    performedAt: fromIstInput(v.performedAt) ?? v.performedAt,
    code: blank(v.code),
    display: blank(v.display),
    performerPractitionerId: performerName ? null : performerPractitionerId,
    performerName,
    side: v.side,
    bodySite: blank(v.bodySite),
    outcome: blank(v.outcome),
    notes: blank(v.notes),
  };
}

/** "0DTJ4ZZ Laparoscopic appendicectomy · Right" — the line the list and the timeline show. */
export function procedureLabel(p: Pick<ProcedureDto, 'code' | 'display' | 'side' | 'bodySite'>): string {
  const where = [p.side === 'NotApplicable' ? null : procedureSideLabel[p.side], p.bodySite].filter(Boolean).join(', ');
  return `${p.code ? `${p.code} ` : ''}${p.display}${where ? ` · ${where}` : ''}`;
}

export const procedureKeys = {
  list: (patientId: string, includeErrors: boolean) =>
    ['emr', 'patient', patientId, 'procedures', includeErrors] as const,
};

export const procedureQueries = {
  list: (patientId: string, includeErrors = false) =>
    queryOptions({
      queryKey: procedureKeys.list(patientId, includeErrors),
      queryFn: ({ signal }) =>
        api.get<ProcedureDto[]>(`/api/v1/emr/patients/${patientId}/procedures`, {
          query: { includeEnteredInError: includeErrors || undefined },
          signal,
        }),
    }),
};

export const procedureApi = {
  record: (patientId: string, request: RecordProcedureRequest) =>
    api.post<ProcedureDto>(`/api/v1/emr/patients/${patientId}/procedures`, request),
  correct: (id: string, request: CorrectProcedureRequest) =>
    api.put<ProcedureDto>(`/api/v1/emr/procedures/${id}`, request),
  enteredInError: (id: string, reason: string) =>
    api.post<ProcedureDto>(`/api/v1/emr/procedures/${id}/entered-in-error`, { reason }),
};
