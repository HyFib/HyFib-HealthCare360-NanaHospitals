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
 * A change one person asked for and another must approve before it is made (credit notes, write-offs, patient merges,
 * lab report corrections).
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
  /**
   * In the answer to an approval only, never again: a secret the approval made for the person who approved it to hand
   * over in person (the temporary password of an account with no mobile number or e-mail address to send it to).
   */
  handover?: ApprovalHandover | null;
}

/** A secret handed to the approver once: what it is for, the secret, and when it stops working. */
export interface ApprovalHandover {
  label: string;
  secret: string;
  expiresAt: string | null;
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
 * (IApprovalAction): credit notes and write-offs, money not received and refunds not paid out, patient merges, lab
 * report corrections, death certificates a second doctor confirms, goods receipts posted into stock, the pharmacy's
 * cancellations, returns and ward returns not restocked, the masters, access and plans approved only here, and changes
 * to accounts and administration desktops, the drug-safety tables loosened, the billing desk's discount limits raised,
 * charges cancelled, cash shortages and doctors' consultation fees, and the patient's record: its identity, a VIP
 * restriction lifted, the deceased status, a sensitive category, DNR, an allergy and a stay's MLC flag; discharge and
 * death: an absconded stay closed, a death closed pending post-mortem, a confirmed death certificate voided; a visit the
 * doctor has started, cancelled at the desk; an implant or consumable line corrected after the operation started; and
 * a sentinel event closed or re-graded out of sentinel, a personal data breach closed or patients taken off its notification
 * list, and a bulk campaign scheduled.
 * Every kind's approving permission is in it
 * (approvalsMenu.test.ts).
 */
export const APPROVAL_PERMISSIONS = [
  'billing.refunds.request',
  'billing.refunds.approve',
  'patients.merge',
  'lab.results.verify',
  // A doctor who certifies a death withdraws a confirmation they asked for; a second doctor confirms it.
  'mrd.deaths.certify',
  'mrd.deaths.confirm',
  // The store sends a goods receipt for approval (and may withdraw it); someone else checks it and posts it into stock.
  'purchase.grn.receive',
  'purchase.grn.approve',
  // Approved only here: prices, tariffs, packages, fee-sharing and payer plans; access given to a user or a role, and a
  // temporary password to hand over, a two-step verification reset, another user's contact details, a practitioner
  // link, a linked doctor's registration and another administration desktop (admin.grants.approve); a drug handled less
  // strictly; the plan a stay is billed on. Every approving permission the server has is in this list
  // (approvalsMenu.test.ts checks it against serverAccess.json), so whoever may approve a change can find it.
  'masters.prices.approve',
  'admin.grants.approve',
  'inventory.drug_class.approve',
  'billing.discounts.approve',
  // How long the audit trail is kept, and a purge of the months it no longer keeps: one super administrator asks,
  // another approves.
  'admin.system.manage',
  // A setting that loosens a control, or changes where money, patients' data or alerts go: one administrator asks,
  // another who may change settings approves.
  'admin.settings.manage',
  // A pharmacist asks for a cancellation or a return paying money out (someone who approves refunds decides, above), and
  // withdraws it; a ward return not restocked is asked for by one pharmacist who issues to the wards and approved by another.
  'pharmacy.returns',
  'pharmacy.ip.issue',
  // A drug interaction switched off or made less severe, or a maximum dose raised or switched off: whoever keeps the
  // drug-safety tables asks (and may withdraw it), the pharmacy and therapeutics committee approves.
  'clinical.templates.manage',
  'clinical.drug_safety.approve',
  // The billing desk asks for a charge's cancellation (someone who approves discounts decides, above), and a cashier's
  // shift closed short asks a supervisor (someone who approves refunds, above) to accept the shortage: each may withdraw it.
  'billing.charges.manage',
  'billing.shifts',
  // The patient's record (slice 7): the desk asks for an identity change, a VIP restriction lifted or the deceased status
  // (someone who approves identity changes, or who opens restricted records, decides); a doctor asks for a sensitive
  // category to be taken out or DNR taken off (another doctor decides); a clinician asks for an allergy to leave the
  // checks or be lowered (a prescriber decides); the admission desk asks for a stay's MLC flag to be cleared (the MLC
  // register decides). Each may withdraw it.
  'patients.register',
  'patients.identity.approve',
  'patients.restricted.read',
  'patients.sensitive.manage',
  'clinical.alerts.manage',
  'clinical.encounters.sign',
  'clinical.allergies.manage',
  'clinical.prescriptions.sign',
  'inpatient.admissions.admit',
  'mrd.mlc.register',
  // Downtime: a back-entry past its window (the in-charge asks once with the tally; whoever approves late back-entry
  // decides).
  'patients.back_entry.approve',
  // Discharge and death (slice 8): the ward asks for an absconded patient's stay to be closed (the nurse in charge
  // decides) or a medico-legal death's before its certificate (a doctor who confirms certificates decides); the certifier
  // asks to void a confirmed death certificate (another doctor who confirms certificates decides). Each may withdraw it.
  'inpatient.admissions.discharge',
  'nursing.in_charge',
  // A visit the doctor has started, cancelled at the desk (slice 15): the desk asks (and may withdraw it), a front-office
  // supervisor or the treating doctor decides.
  'visits.manage',
  'visits.cancel.approve',
  // The theatre (slice 10): the OT team asks for an implant or consumable line to be corrected after the operation has
  // started; the OT in-charge decides. Each may withdraw it.
  'ot.materials.record',
  'ot.materials.approve',
  // Slice 14: the quality team asks to close a sentinel or never event, or re-grade one out of sentinel (the quality
  // manager decides, never the incident's reporter); the breach team asks to close a breach or take patients off its
  // notification list (the Data Protection Officer decides); someone who sends campaigns asks to schedule one (someone
  // other than its author decides). Each may withdraw it.
  'quality.incidents.manage',
  'quality.incidents.approve',
  'privacy.breaches.manage',
  'privacy.breaches.approve',
  'crm.campaigns.send',
  'crm.campaigns.approve',
  // Insurance money (slice 12): the insurance desk asks for a write-off of what the insurer deducted or rejected
  // (someone who approves insurance write-offs decides), and records an amount the insurer approved on a
  // pre-authorisation (another insurance desk user checks it). Each may withdraw it.
  'insurance.claims.manage',
  'insurance.claims.writeoff.approve',
  'insurance.preauth.manage',
] as const;
