import { HOSPITAL_TIME_ZONE } from '@hc360/core';
import type { StatusTone } from '@hc360/ui';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

// Notifiable diseases (server `IdspEndpoints`): a diagnosis puts a case on the week's presumptive (P) form, a released
// positive test confirms it on the laboratory (L) form, and the surveillance officer reports each week's forms to IHIP,
// a nil report too. Outbreak-prone diseases alert the officer at once. Tuberculosis is not on the forms: each case is
// notified on Ni-kshay, and waits here until its Ni-kshay ID is recorded.

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
  /** Notified on Ni-kshay (tuberculosis), not on the IHIP forms. */
  nikshay: boolean;
  icdCodes: string[];
}

export interface IdspCaseRow {
  id: string;
  diseaseCode: string;
  disease: string;
  immediate: boolean;
  nikshay: boolean;
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
  /** For tuberculosis: the Ni-kshay ID it was notified under, and the day, once it is. */
  nikshayId: string | null;
  nikshayNotifiedOn: string | null;
  version: number;
}

/** Tuberculosis cases to notify on Ni-kshay, the longest waiting first, and those notified in the last 90 days. */
export interface IdspTuberculosis {
  toNotify: IdspCaseRow[];
  notified: IdspCaseRow[];
}

export interface RecordNikshayRequest {
  nikshayId: string;
  notifiedOn: string;
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
  tuberculosis: () => ['mrd', 'idsp', 'tuberculosis'] as const,
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
  tuberculosis: () =>
    queryOptions({
      queryKey: idspKeys.tuberculosis(),
      queryFn: ({ signal }) => api.get<IdspTuberculosis>(`${base}/tuberculosis`, { signal }),
    }),
};

export const idspApi = {
  report: (year: number, week: number, form: IdspForm, request: ReportIdspWeekRequest) =>
    api.put<IdspWeekDto>(`${weekPath(year, week)}/${form}`, request),
  discard: (id: string, reason: string, version: number) =>
    api.post<IdspCaseRow>(`${base}/cases/${id}/discard`, { reason, version }),
  add: (request: AddIdspCaseRequest) => api.post<IdspCaseRow>(`${base}/cases`, request),
  /** A tuberculosis case was notified on Ni-kshay: its Ni-kshay ID and the day (correcting them again is allowed). */
  recordNikshay: (id: string, request: RecordNikshayRequest) =>
    api.post<IdspCaseRow>(`${base}/cases/${id}/nikshay`, request),
  /** The week's P or L line list as CSV, to upload to IHIP (the download is audited). */
  export: (year: number, week: number, form: IdspForm) =>
    api.blob(`${weekPath(year, week)}/export`, { query: { form } }),
};

/** The register, the weekly reports, the cases added or discarded, and the outbreak alerts (medical records, quality). */
export const IDSP_PERMISSION = 'mrd.idsp.report';

const istDate = new Intl.DateTimeFormat('en-CA', { timeZone: HOSPITAL_TIME_ZONE });

/**
 * The day (yyyy-mm-dd, in India) a case was first diagnosed or confirmed, whichever came first: it cannot be notified
 * before.
 */
export function caseFirstDay(row: Pick<IdspCaseRow, 'presumedAt' | 'confirmedAt'>): string | null {
  const times = [row.presumedAt, row.confirmedAt].filter((at): at is string => at !== null).map((at) => Date.parse(at));
  return times.length > 0 ? istDate.format(new Date(Math.min(...times))) : null;
}

/** Days a tuberculosis case has waited to be notified on Ni-kshay, from its first diagnosis or result to today. */
export function daysWaiting(row: Pick<IdspCaseRow, 'presumedAt' | 'confirmedAt'>, today: string): number | null {
  const first = caseFirstDay(row);
  return first ? Math.max(0, Math.round((Date.parse(today) - Date.parse(first)) / 86_400_000)) : null;
}

/**
 * What stops a Ni-kshay ID from being recorded: the ID as Ni-kshay gave it (letters and digits), and the day it was
 * notified, from the case's first day to today (dates as yyyy-mm-dd).
 */
export function nikshayProblems(
  nikshayId: string,
  notifiedOn: string,
  firstDay: string | null,
  today: string,
): { id: string | null; date: string | null } {
  const id = nikshayId.trim();
  return {
    id: !id
      ? 'Enter the Ni-kshay ID.'
      : !/^[A-Za-z0-9]{1,32}$/.test(id)
        ? 'Enter the ID as Ni-kshay gave it: letters and digits.'
        : null,
    date: !notifiedOn
      ? 'Give the day it was notified.'
      : notifiedOn > today
        ? 'The date cannot be in the future.'
        : firstDay && notifiedOn < firstDay
          ? 'It cannot be notified before it was diagnosed.'
          : null,
  };
}

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
