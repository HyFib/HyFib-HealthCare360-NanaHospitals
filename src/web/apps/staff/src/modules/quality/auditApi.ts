import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { CapaStatus, CapaType } from './labels';

/**
 * NABH internal audits. Mirrors `AuditEndpoints` (`/api/v1/quality/audits`): checklists and the schedule
 * (`quality.audits.manage`), findings by the lead auditor, a CAPA for each non-compliance (`quality.capa.manage`), and
 * closure once every CAPA is verified by someone other than its owner.
 */

export const AUDITS_MANAGE = 'quality.audits.manage';

export type NabhChapter = 'Aac' | 'Cop' | 'Mom' | 'Pre' | 'Hic' | 'Psq' | 'Rom' | 'Fms' | 'Hrm' | 'Ims';
export type AuditRecurrence = 'Once' | 'Monthly' | 'Quarterly' | 'HalfYearly' | 'Yearly';
export type AuditStatus = 'Scheduled' | 'InProgress' | 'Completed' | 'Closed' | 'Cancelled';
export type FindingResult = 'Compliant' | 'NonCompliant' | 'NotApplicable';

export const CHAPTERS: readonly NabhChapter[] = ['Aac', 'Cop', 'Mom', 'Pre', 'Hic', 'Psq', 'Rom', 'Fms', 'Hrm', 'Ims'];
export const RECURRENCES: readonly AuditRecurrence[] = ['Once', 'Monthly', 'Quarterly', 'HalfYearly', 'Yearly'];

export const CHAPTER_LABELS: Record<NabhChapter, string> = {
  Aac: 'AAC · Access, assessment and continuity of care',
  Cop: 'COP · Care of patients',
  Mom: 'MOM · Management of medication',
  Pre: 'PRE · Patient rights and education',
  Hic: 'HIC · Hospital infection control',
  Psq: 'PSQ · Patient safety and quality improvement',
  Rom: 'ROM · Responsibilities of management',
  Fms: 'FMS · Facility management and safety',
  Hrm: 'HRM · Human resource management',
  Ims: 'IMS · Information management system',
};

export const RECURRENCE_LABELS: Record<AuditRecurrence, string> = {
  Once: 'Once',
  Monthly: 'Every month',
  Quarterly: 'Every quarter',
  HalfYearly: 'Every six months',
  Yearly: 'Every year',
};

export const AUDIT_STATUS_LABELS: Record<AuditStatus, string> = {
  Scheduled: 'Scheduled',
  InProgress: 'In progress',
  Completed: 'CAPAs open',
  Closed: 'Closed',
  Cancelled: 'Cancelled',
};

export const RESULT_LABELS: Record<FindingResult, string> = {
  Compliant: 'Compliant',
  NonCompliant: 'Not compliant',
  NotApplicable: 'Not applicable',
};

export interface AuditItem {
  key: string;
  text: string;
  reference: string | null;
}

export interface AuditTemplate {
  id: string;
  code: string;
  title: string;
  chapter: NabhChapter;
  description: string | null;
  items: AuditItem[];
  isActive: boolean;
  version: number;
}

export interface AuditListItem {
  id: string;
  auditNumber: string;
  title: string;
  chapter: NabhChapter;
  area: string | null;
  scheduledOn: string;
  leadAuditorUserId: string;
  leadAuditorName: string | null;
  recurrence: AuditRecurrence;
  status: AuditStatus;
  isOverdue: boolean;
  compliancePercent: number | null;
  nonCompliances: number;
  openCapas: number;
}

export interface AuditFinding {
  id: string;
  order: number;
  itemKey: string;
  itemText: string;
  reference: string | null;
  result: FindingResult | null;
  observation: string | null;
  capaId: string | null;
  capaNumber: string | null;
  capaStatus: CapaStatus | null;
  capaOwnerName: string | null;
  capaDueDate: string | null;
}

export interface AuditDetail {
  id: string;
  auditNumber: string;
  templateId: string;
  title: string;
  chapter: NabhChapter;
  departmentId: string | null;
  area: string | null;
  scheduledOn: string;
  leadAuditorUserId: string;
  leadAuditorName: string | null;
  recurrence: AuditRecurrence;
  status: AuditStatus;
  startedAt: string | null;
  completedAt: string | null;
  closedAt: string | null;
  summary: string | null;
  cancelReason: string | null;
  compliancePercent: number | null;
  completionGaps: string[];
  findings: AuditFinding[];
  nextAuditId: string | null;
  canRecord: boolean;
  version: number;
}

export interface ScheduleAuditRequest {
  templateId: string;
  scheduledOn: string;
  leadAuditorUserId: string;
  departmentId: string | null;
  area: string | null;
  recurrence: AuditRecurrence;
}

export const auditKeys = { all: ['quality', 'audits'] as const };

export const auditQueries = {
  list: (status?: AuditStatus) =>
    queryOptions({
      queryKey: ['quality', 'audits', 'list', status ?? 'open'],
      queryFn: ({ signal }) => api.get<AuditListItem[]>('/api/v1/quality/audits', { query: { status }, signal }),
    }),
  mine: () =>
    queryOptions({
      queryKey: ['quality', 'audits', 'mine'],
      queryFn: ({ signal }) => api.get<AuditListItem[]>('/api/v1/quality/audits/mine', { signal }),
    }),
  audit: (id: string) =>
    queryOptions({
      queryKey: ['quality', 'audits', 'detail', id],
      queryFn: ({ signal }) => api.get<AuditDetail>(`/api/v1/quality/audits/${id}`, { signal }),
    }),
  templates: () =>
    queryOptions({
      queryKey: ['quality', 'audits', 'templates'],
      queryFn: ({ signal }) => api.get<AuditTemplate[]>('/api/v1/quality/audits/templates', { signal }),
    }),
};

export const auditApi = {
  schedule: (request: ScheduleAuditRequest) => api.post<AuditDetail>('/api/v1/quality/audits', request),
  record: (id: string, itemKey: string, result: FindingResult, observation: string | null, version: number) =>
    api.post<AuditDetail>(`/api/v1/quality/audits/${id}/findings`, { itemKey, result, observation, version }),
  raiseCapa: (
    id: string,
    findingId: string,
    type: CapaType,
    action: string,
    ownerUserId: string,
    dueDate: string,
    version: number,
  ) =>
    api.post<AuditDetail>(`/api/v1/quality/audits/${id}/findings/${findingId}/capa`, {
      type,
      action,
      ownerUserId,
      dueDate,
      version,
    }),
  complete: (id: string, summary: string | null, version: number) =>
    api.post<AuditDetail>(`/api/v1/quality/audits/${id}/complete`, { summary, version }),
  close: (id: string, version: number) => api.post<AuditDetail>(`/api/v1/quality/audits/${id}/close`, { version }),
  cancel: (id: string, reason: string, version: number) =>
    api.post<AuditDetail>(`/api/v1/quality/audits/${id}/cancel`, { reason, version }),
};
