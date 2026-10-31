import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/** Where a maker-checker request stands. */
export type ApprovalStatus = 'Pending' | 'Approved' | 'Rejected' | 'Withdrawn' | 'Failed';

/** The inbox's lists: waiting for my decision, asked for by me, or about one record. */
export type ApprovalView = 'ToDecide' | 'Mine' | 'Subject';

export interface ApprovalDetail {
  label: string;
  value: string;
}

/**
 * A change one person asked for and another must approve before it is made (credit notes, write-offs, patient merges).
 * The server decides what each user sees and may do: `canDecide` is false for the person who asked.
 */
export interface ApprovalRequestDto {
  id: string;
  kind: string;
  title: string;
  summary: string;
  details: ApprovalDetail[];
  reason: string;
  status: ApprovalStatus;
  isExpired: boolean;
  subjectType: string;
  subjectId: string;
  patientId: string | null;
  requestedBy: string;
  requestedByName: string;
  requestedAt: string;
  expiresAt: string;
  decidedBy: string | null;
  decidedByName: string | null;
  decidedAt: string | null;
  decisionNote: string | null;
  /** What the approval made (a credit note's number), or why the change could not be made. */
  outcome: string | null;
  canDecide: boolean;
  canWithdraw: boolean;
  version: number;
}

export interface ApprovalFilters {
  view: ApprovalView;
  status?: ApprovalStatus;
  subjectType?: string;
  subjectId?: string;
  page?: number;
}

export const approvalKeys = {
  all: ['approvals'] as const,
  list: (filters: ApprovalFilters) => ['approvals', 'list', filters] as const,
  detail: (id: string) => ['approvals', 'detail', id] as const,
};

export const approvalQueries = {
  list: (filters: ApprovalFilters) =>
    queryOptions({
      queryKey: approvalKeys.list(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<ApprovalRequestDto>>('/api/v1/approvals', { query: { pageSize: 25, ...filters }, signal }),
      placeholderData: keepPreviousData,
    }),
  detail: (id: string) =>
    queryOptions({
      queryKey: approvalKeys.detail(id),
      queryFn: ({ signal }) => api.get<ApprovalRequestDto>(`/api/v1/approvals/${id}`, { signal }),
    }),
};

export const approvalsApi = {
  /** Makes the change: 422 `approval.stale` when the record changed since it was asked for, `approval.self` (403) for the maker. */
  approve: (id: string, note: string | null, version: number) =>
    api.post<ApprovalRequestDto>(`/api/v1/approvals/${id}/approve`, { note, version }),
  reject: (id: string, reason: string, version: number) =>
    api.post<ApprovalRequestDto>(`/api/v1/approvals/${id}/reject`, { reason, version }),
  withdraw: (id: string, version: number) =>
    api.post<ApprovalRequestDto>(`/api/v1/approvals/${id}/withdraw`, { version }),
};

export const APPROVAL_STATUS_LABEL: Record<ApprovalStatus, string> = {
  Pending: 'Waiting for approval',
  Approved: 'Approved',
  Rejected: 'Rejected',
  Withdrawn: 'Withdrawn',
  Failed: 'Approved, not made',
};

/**
 * Who may ask for or decide a change of each kind: the inbox is for them. Kept with the server's kinds
 * (IApprovalAction): credit notes and write-offs, and patient merges.
 */
export const APPROVAL_PERMISSIONS = ['billing.refunds.request', 'billing.refunds.approve', 'patients.merge'] as const;
