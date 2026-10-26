import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

/**
 * Antimicrobial stewardship: /api/v1/ams. Restricted antibiotics on signed prescriptions wait for the stewardship
 * team's approval (first doses may be given meanwhile, for 24 hours); approved courses are reviewed at 72 hours or when
 * a culture comes back.
 */

export type AwareGroup = 'Access' | 'Watch' | 'Reserve';
export type ApprovalStatus = 'Pending' | 'Approved' | 'Rejected';
export type StewardshipDecision = 'Continue' | 'DeEscalate' | 'Escalate' | 'SwitchToOral' | 'Stop';
export type ApprovalQueue = 'Pending' | 'ReviewDue' | 'All';
export type InfectionSite =
  | 'Respiratory'
  | 'UrinaryTract'
  | 'Bloodstream'
  | 'IntraAbdominal'
  | 'SkinAndSoftTissue'
  | 'BoneAndJoint'
  | 'CentralNervousSystem'
  | 'SurgicalSite'
  | 'FebrileNeutropenia'
  | 'Other';

export interface AntimicrobialApproval {
  id: string;
  patientId: string;
  patientName: string;
  uhid: string;
  prescriptionNumber: string;
  inpatient: boolean;
  drugName: string;
  antimicrobial: string;
  group: AwareGroup;
  dose: string | null;
  route: string | null;
  durationDays: number;
  prescribedByName: string;
  prescribedAt: string;
  site: InfectionSite | null;
  indication: string | null;
  empirical: boolean | null;
  cultureSent: boolean | null;
  status: ApprovalStatus;
  approvalDueBy: string;
  approvalOverdue: boolean;
  decidedByName: string | null;
  decidedAt: string | null;
  approvedDays: number | null;
  decisionNotes: string | null;
  organism: string | null;
  susceptibility: string | null;
  cultureReleasedAt: string | null;
  reviewDue: boolean;
  reviewDecision: StewardshipDecision | null;
  reviewedByName: string | null;
  reviewedAt: string | null;
  reviewNotes: string | null;
  /** The signed-in doctor prescribed it: they give the justification and may review the course. */
  isMine: boolean;
  version: number;
}

export interface StewardshipReport {
  from: string;
  to: string;
  byGroup: { group: AwareGroup; prescribed: number; approved: number; rejected: number; pending: number }[];
  medianHoursToDecision: number | null;
  decidedLate: number;
  cultureSentPercent: number | null;
  reviewed: number;
  reviewDecisions: Record<StewardshipDecision, number>;
}

export const AMS_PERMISSIONS = { approve: 'clinical.ams.approve', read: 'clinical.ams.read' } as const;
export const AMS_READERS = [AMS_PERMISSIONS.read, AMS_PERMISSIONS.approve] as const;

export const SITE_LABELS: Record<InfectionSite, string> = {
  Respiratory: 'Respiratory',
  UrinaryTract: 'Urinary tract',
  Bloodstream: 'Bloodstream',
  IntraAbdominal: 'Intra-abdominal',
  SkinAndSoftTissue: 'Skin and soft tissue',
  BoneAndJoint: 'Bone and joint',
  CentralNervousSystem: 'Central nervous system',
  SurgicalSite: 'Surgical site',
  FebrileNeutropenia: 'Febrile neutropenia',
  Other: 'Other',
};

export const DECISION_LABELS: Record<StewardshipDecision, string> = {
  Continue: 'Continue',
  DeEscalate: 'Narrow (de-escalate)',
  Escalate: 'Broaden (escalate)',
  SwitchToOral: 'Switch to oral',
  Stop: 'Stop',
};

/** Where an approval stands, in a few words for the queue. */
export function approvalState(
  a: Pick<AntimicrobialApproval, 'status' | 'approvalOverdue' | 'reviewDue' | 'reviewDecision'>,
): {
  label: string;
  tone: 'danger' | 'warning' | 'success' | 'subtle' | 'informative';
} {
  if (a.status === 'Rejected') {
    return { label: 'Not approved', tone: 'danger' };
  }
  if (a.status === 'Pending') {
    return a.approvalOverdue
      ? { label: 'Approval overdue', tone: 'danger' }
      : { label: 'Waiting for approval', tone: 'warning' };
  }
  if (a.reviewDecision) {
    return { label: `Reviewed: ${DECISION_LABELS[a.reviewDecision]}`, tone: 'subtle' };
  }
  return a.reviewDue ? { label: 'Review due', tone: 'informative' } : { label: 'Approved', tone: 'success' };
}

export const amsKeys = {
  all: ['ams'] as const,
  queue: (queue: ApprovalQueue, mine: boolean) => ['ams', 'queue', queue, mine] as const,
  report: () => ['ams', 'report'] as const,
};

const base = '/api/v1/ams';

export const amsQueries = {
  queue: (queue: ApprovalQueue, mine: boolean) =>
    queryOptions({
      queryKey: amsKeys.queue(queue, mine),
      queryFn: ({ signal }) =>
        api.get<AntimicrobialApproval[]>(`${base}/approvals`, { query: { queue, mine }, signal }),
    }),
  report: () =>
    queryOptions({
      queryKey: amsKeys.report(),
      queryFn: ({ signal }) => api.get<StewardshipReport>(`${base}/report`, { signal }),
    }),
};

export const amsApi = {
  justify: (
    id: string,
    site: InfectionSite,
    indication: string,
    empirical: boolean,
    cultureSent: boolean,
    version: number,
  ) =>
    api.post<AntimicrobialApproval>(`${base}/approvals/${id}/justification`, {
      site,
      indication,
      empirical,
      cultureSent,
      version,
    }),
  decide: (id: string, approve: boolean, days: number | null, notes: string | null, version: number) =>
    api.post<AntimicrobialApproval>(`${base}/approvals/${id}/decision`, { approve, days, notes, version }),
  review: (id: string, decision: StewardshipDecision, notes: string | null, version: number) =>
    api.post<AntimicrobialApproval>(`${base}/approvals/${id}/review`, { decision, notes, version }),
};
