import type { StatusTone } from '@hc360/ui';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

// Notifiable diseases (server `IdspEndpoints`): a diagnosis puts a case on the week's presumptive (P) form, a released
// positive test confirms it on the laboratory (L) form, and the surveillance officer reports each week's forms to IHIP,
// a nil report too. Outbreak-prone diseases alert the officer at once.

export const IDSP_FORMS = ['Presumptive', 'Laboratory'] as const;
export type IdspForm = (typeof IDSP_FORMS)[number];

export const CASE_SOURCES = ['Diagnosis', 'LabResult', 'Manual'] as const;
export type NotifiableCaseSource = (typeof CASE_SOURCES)[number];

export type NotifiableCaseStatus = 'Open' | 'Discarded';

export const idspFormLabel: Record<IdspForm, string> = {
  Presumptive: 'P form (presumptive)',
  Laboratory: 'L form (laboratory-confirmed)',
};

export const caseSourceLabel: Record<NotifiableCaseSource, string> = {
  Diagnosis: 'Diagnosis',
  LabResult: 'Lab result',
  Manual: 'Added by hand',
};

export const caseStatusTone: Record<NotifiableCaseStatus, StatusTone> = { Open: 'brand', Discarded: 'informative' };

export interface IdspDisease {
  code: string;
  name: string;
  immediate: boolean;
  icdCodes: string[];
}

export interface IdspCaseRow {
  id: string;
  diseaseCode: string;
  disease: string;
  immediate: boolean;
  source: NotifiableCaseSource;
  status: NotifiableCaseStatus;
  patientId: string;
  patientName: string;
  uhid: string;
  sex: string | null;
  ageYears: number | null;
  address: string | null;
  district: string | null;
  phone: string | null;
  onsetDate: string | null;
  presumedAt: string | null;
  icdCode: string | null;
  confirmedAt: string | null;
  testName: string | null;
  testResult: string | null;
  alertedAt: string | null;
  discardReason: string | null;
  version: number;
}

export interface IdspFormReport {
  reportedOn: string;
  reference: string | null;
  caseCount: number;
  reportedAt: string;
  version: number;
}

export interface IdspFormWeek {
  form: IdspForm;
  cases: IdspCaseRow[];
  report: IdspFormReport | null;
  notReported: number;
  overdue: boolean;
}

export interface IdspWeekDto {
  year: number;
  week: number;
  monday: string;
  sunday: string;
  dueOn: string;
  ended: boolean;
  presumptive: IdspFormWeek;
  laboratory: IdspFormWeek;
  discarded: IdspCaseRow[];
}

export interface IdspWeekSummary {
  year: number;
  week: number;
  monday: string;
  sunday: string;
  dueOn: string;
  presumptiveCases: number;
  laboratoryCases: number;
  presumptiveReported: boolean;
  laboratoryReported: boolean;
  notReported: number;
  overdue: boolean;
}

export interface IdspOverview {
  weeks: IdspWeekSummary[];
  immediate: IdspCaseRow[];
}

export interface ReportIdspWeekRequest {
  reportedOn: string;
  reference: string | null;
  /** The report as read, when sending it again (REL-04); absent on the first report. */
  version?: number;
}

export interface AddIdspCaseRequest {
  patientId: string;
  diseaseCode: string;
  onsetDate: string | null;
  testName: string | null;
  testResult: string | null;
}

export const idspKeys = {
  all: ['mrd', 'idsp'] as const,
  overview: () => ['mrd', 'idsp', 'overview'] as const,
  week: (year: number, week: number) => ['mrd', 'idsp', 'week', year, week] as const,
  case: (id: string) => ['mrd', 'idsp', 'case', id] as const,
  diseases: () => ['mrd', 'idsp', 'diseases'] as const,
};

const base = '/api/v1/idsp';
const weekPath = (year: number, week: number) => `${base}/weeks/${year}/${week}`;

export const idspQueries = {
  overview: () =>
    queryOptions({
      queryKey: idspKeys.overview(),
      queryFn: ({ signal }) => api.get<IdspOverview>(`${base}/weeks`, { signal }),
    }),
  week: (year: number, week: number) =>
    queryOptions({
      queryKey: idspKeys.week(year, week),
      queryFn: ({ signal }) => api.get<IdspWeekDto>(weekPath(year, week), { signal }),
      placeholderData: keepPreviousData,
    }),
  case: (id: string) =>
    queryOptions({
      queryKey: idspKeys.case(id),
      queryFn: ({ signal }) => api.get<IdspCaseRow>(`${base}/cases/${id}`, { signal }),
    }),
  diseases: () =>
    queryOptions({
      queryKey: idspKeys.diseases(),
      queryFn: ({ signal }) => api.get<IdspDisease[]>(`${base}/diseases`, { signal }),
      staleTime: Infinity,
    }),
};

export const idspApi = {
  report: (year: number, week: number, form: IdspForm, request: ReportIdspWeekRequest) =>
    api.put<IdspWeekDto>(`${weekPath(year, week)}/${form}`, request),
  discard: (id: string, reason: string, version: number) =>
    api.post<IdspCaseRow>(`${base}/cases/${id}/discard`, { reason, version }),
  add: (request: AddIdspCaseRequest) => api.post<IdspCaseRow>(`${base}/cases`, request),
  /** The week's P or L line list as CSV, to upload to IHIP (the download is audited). */
  export: (year: number, week: number, form: IdspForm) =>
    api.blob(`${weekPath(year, week)}/export`, { query: { form } }),
};

/** The register, the weekly reports, the cases added or discarded, and the outbreak alerts (medical records, quality). */
export const IDSP_PERMISSION = 'mrd.idsp.report';

/** "2026-W43", as IHIP names the week. */
export const weekLabel = (year: number, week: number) => `${year}-W${String(week).padStart(2, '0')}`;

/**
 * What stops a week's report from being saved: it is sent once the week is over, on a day after its Sunday and not
 * later than today (dates as yyyy-mm-dd). Null when nothing does.
 */
export function reportProblems(reportedOn: string, sunday: string, today: string): string | null {
  if (!reportedOn) {
    return 'Give the date the form was sent.';
  }
  if (reportedOn <= sunday) {
    return 'The form is sent after the week ends.';
  }
  if (reportedOn > today) {
    return 'The date cannot be in the future.';
  }
  return null;
}

/** What a hand-added case still lacks: a patient and a disease, and the test's name when a result is given. */
export function addCaseProblems(input: {
  patientId: string | null;
  diseaseCode: string;
  onsetDate: string;
  testName: string;
  testResult: string;
  today: string;
}): { patient: string | null; disease: string | null; onset: string | null; test: string | null } {
  return {
    patient: input.patientId ? null : 'Choose the patient.',
    disease: input.diseaseCode ? null : 'Choose the disease.',
    onset: input.onsetDate && input.onsetDate > input.today ? 'The onset cannot be in the future.' : null,
    test: input.testResult.trim() && !input.testName.trim() ? 'Name the test that confirmed it.' : null,
  };
}
