import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ApprovalRequestDto } from '@/modules/approvals/api';

/**
 * The DPDP duties added to the privacy module (privacy.md): the erasure ledger, a guardian's consent for a child, consent
 * managers and their artefacts, and the Significant Data Fiduciary register. DTOs mirror the C# records.
 */

// ------------------------------------------------------------------ erasure ledger

export interface ErasureReplayItem {
  ledgerEntryId: string;
  requestId: string;
  requestNumber: string;
  patientId: string;
  applied: string[];
  heldBack: string[];
  patientMissing: boolean;
  erasedAt: string;
}

export interface ErasureLedgerStatus {
  path: string;
  mirrorPath: string | null;
  entries: number;
  rejected: number;
  backupRetentionDays: number;
  replays: ErasureReplayItem[];
}

export interface ErasureReplayOutcome {
  entries: number;
  rejected: number;
  reapplied: ErasureReplayItem[];
  backfilled: number;
  mirrorCompleted: number;
}

// ------------------------------------------------------------------ children

export type GuardianConsentStatus = 'Pending' | 'Verified' | 'Revoked' | 'Expired';
export type GuardianVerificationMethod = 'Otp' | 'Document';
export type GuardianRelationship = 'Parent' | 'LawfulGuardian';

export interface GuardianConsentDto {
  id: string;
  guardianName: string;
  relationship: GuardianRelationship;
  guardianPhone: string;
  guardianIsAdult: boolean;
  method: GuardianVerificationMethod;
  documentType: string | null;
  documentLast4: string | null;
  status: GuardianConsentStatus;
  createdAt: string;
  codeExpiresAt: string | null;
  attemptsLeft: number;
  verifiedAt: string | null;
  revokedAt: string | null;
  revokedReason: string | null;
}

export interface GuardianConsentView {
  patientId: string;
  isChild: boolean;
  dateOfBirth: string | null;
  verified: boolean;
  consents: GuardianConsentDto[];
}

export interface StartGuardianConsentRequest {
  guardianName: string;
  relationship: GuardianRelationship;
  guardianPhone: string;
  guardianIsAdult: boolean;
  method: GuardianVerificationMethod;
  documentType: string | null;
  documentLast4: string | null;
}

// ------------------------------------------------------------------ consent managers

export type ConsentArtefactStatus = 'Active' | 'Withdrawn' | 'Expired';
export type ConsentWithdrawalSource = 'ConsentManager' | 'Portal' | 'Staff';

export interface ConsentManagerDto {
  id: string;
  code: string;
  name: string;
  registrationNumber: string;
  baseUrl: string | null;
  isSimulator: boolean;
  isActive: boolean;
  activeArtefacts: number;
  version: number;
}

export interface ConsentManagerCreated {
  id: string;
  code: string;
  secret: string;
}

export interface SaveConsentManagerRequest {
  code: string;
  name: string;
  registrationNumber: string;
  baseUrl: string | null;
  isSimulator: boolean;
}

export interface ConsentArtefactDto {
  id: string;
  consentManagerId: string;
  consentManagerName: string;
  artefactId: string;
  patientId: string;
  purpose: string;
  dataItems: string[];
  validFrom: string;
  validUntil: string;
  status: ConsentArtefactStatus;
  inForce: boolean;
  givenByGuardian: boolean;
  receivedAt: string;
  withdrawnAt: string | null;
  withdrawnVia: ConsentWithdrawalSource | null;
  withdrawalPending: boolean;
}

// ------------------------------------------------------------------ Significant Data Fiduciary

export type DpiaStatus = 'Draft' | 'Approved' | 'Retired';
export type RiskLevel = 'Low' | 'Medium' | 'High';

export interface DpoContact {
  name: string | null;
  designation: string | null;
  email: string | null;
  phone: string | null;
  address: string | null;
  basedInIndia: boolean;
}

export interface SdfSettings {
  designated: boolean;
  notifiedOn: string | null;
  dpo: DpoContact;
  version?: number | null;
}

export interface SdfOverview {
  settings: SdfSettings;
  dpias: number;
  dpiasDueSoon: number;
  dpiasOverdue: number;
  nextAuditDue: string | null;
  auditOverdue: boolean;
}

export interface DpiaRisk {
  description: string;
  likelihood: RiskLevel;
  impact: RiskLevel;
}

export interface DpiaMitigation {
  description: string;
  owner: string | null;
}

export interface DpiaListItem {
  id: string;
  number: string;
  activity: string;
  status: DpiaStatus;
  highestRisk: RiskLevel | null;
  reviewDue: string;
  reviewOverdue: boolean;
  daysToReview: number;
  approvedAt: string | null;
  approvedByName: string | null;
  awaitingApprovalSince: string | null;
}

export interface DpiaDetail {
  id: string;
  number: string;
  activity: string;
  description: string;
  dataCategories: string[];
  dataPrincipals: string;
  purpose: string;
  risks: DpiaRisk[];
  mitigations: DpiaMitigation[];
  reviewDue: string;
  status: DpiaStatus;
  highestRisk: RiskLevel | null;
  approvedBy: string | null;
  approvedByName: string | null;
  approvedAt: string | null;
  awaitingApprovalSince: string | null;
  version: number;
}

export interface SaveDpiaRequest {
  activity: string;
  description: string;
  dataCategories: string[];
  dataPrincipals: string;
  purpose: string;
  risks: DpiaRisk[];
  mitigations: DpiaMitigation[];
  reviewDue: string;
  version?: number | null;
}

export interface DataAuditDto {
  id: string;
  periodFrom: string;
  periodTo: string;
  auditor: string;
  scope: string;
  findings: string | null;
  significantObservations: string | null;
  completedOn: string | null;
  reportedToBoardOn: string | null;
  reportReference: string | null;
  nextDue: string;
  overdue: boolean;
  version: number;
}

export interface SaveDataAuditRequest {
  periodFrom: string;
  periodTo: string;
  auditor: string;
  scope: string;
  findings: string | null;
  significantObservations: string | null;
  completedOn: string | null;
  reportedToBoardOn: string | null;
  reportReference: string | null;
  nextDue: string | null;
  version?: number | null;
}

const BASE = '/api/v1/privacy';

export const dpdpKeys = {
  ledger: ['privacy', 'erasure-ledger'] as const,
  guardian: (patientId: string) => ['privacy', 'guardian', patientId] as const,
  managers: ['privacy', 'consent-managers'] as const,
  artefacts: (patientId: string) => ['privacy', 'consent-artefacts', patientId] as const,
  sdf: ['privacy', 'sdf'] as const,
  dpias: ['privacy', 'sdf', 'dpias'] as const,
  audits: ['privacy', 'sdf', 'audits'] as const,
};

export const dpdpQueries = {
  ledger: () =>
    queryOptions({
      queryKey: dpdpKeys.ledger,
      queryFn: ({ signal }) => api.get<ErasureLedgerStatus>(`${BASE}/erasure-ledger`, { signal }),
    }),
  guardian: (patientId: string) =>
    queryOptions({
      queryKey: dpdpKeys.guardian(patientId),
      queryFn: ({ signal }) =>
        api.get<GuardianConsentView>(`${BASE}/patients/${patientId}/guardian-consent`, { signal }),
    }),
  managers: () =>
    queryOptions({
      queryKey: dpdpKeys.managers,
      queryFn: ({ signal }) => api.get<ConsentManagerDto[]>(`${BASE}/consent-managers`, { signal }),
    }),
  artefacts: (patientId: string) =>
    queryOptions({
      queryKey: dpdpKeys.artefacts(patientId),
      queryFn: ({ signal }) =>
        api.get<ConsentArtefactDto[]>(`${BASE}/patients/${patientId}/consent-artefacts`, { signal }),
    }),
  sdf: () =>
    queryOptions({ queryKey: dpdpKeys.sdf, queryFn: ({ signal }) => api.get<SdfOverview>(`${BASE}/sdf`, { signal }) }),
  sdfSettings: () =>
    queryOptions({
      queryKey: [...dpdpKeys.sdf, 'settings'],
      queryFn: ({ signal }) => api.get<SdfSettings>(`${BASE}/sdf/settings`, { signal }),
    }),
  dpias: () =>
    queryOptions({
      queryKey: dpdpKeys.dpias,
      queryFn: ({ signal }) => api.get<DpiaListItem[]>(`${BASE}/sdf/dpias`, { signal }),
    }),
  audits: () =>
    queryOptions({
      queryKey: dpdpKeys.audits,
      queryFn: ({ signal }) => api.get<DataAuditDto[]>(`${BASE}/sdf/audits`, { signal }),
    }),
};

export const dpdpApi = {
  replayLedger: () => api.post<ErasureReplayOutcome>(`${BASE}/erasure-ledger/replay`),
  startGuardianConsent: (patientId: string, request: StartGuardianConsentRequest) =>
    api.post<GuardianConsentDto>(`${BASE}/patients/${patientId}/guardian-consent`, request),
  verifyGuardianCode: (patientId: string, consentId: string, code: string) =>
    api.post<GuardianConsentDto>(`${BASE}/patients/${patientId}/guardian-consent/${consentId}/verify`, { code }),
  revokeGuardianConsent: (patientId: string, consentId: string, reason: string) =>
    api.post<void>(`${BASE}/patients/${patientId}/guardian-consent/${consentId}/revoke`, { reason }),
  createManager: (request: SaveConsentManagerRequest) =>
    api.post<ConsentManagerCreated>(`${BASE}/consent-managers`, request),
  deactivateManager: (id: string) => api.post<void>(`${BASE}/consent-managers/${id}/deactivate`),
  withdrawArtefact: (id: string) => api.post<ConsentArtefactDto>(`${BASE}/consent-artefacts/${id}/withdraw`),
  saveSdfSettings: (settings: SdfSettings) => api.put<void>(`${BASE}/sdf/settings`, settings),
  getDpia: (id: string) => api.get<DpiaDetail>(`${BASE}/sdf/dpias/${id}`),
  createDpia: (request: SaveDpiaRequest) => api.post<DpiaDetail>(`${BASE}/sdf/dpias`, request),
  updateDpia: (id: string, request: SaveDpiaRequest) => api.put<DpiaDetail>(`${BASE}/sdf/dpias/${id}`, request),
  submitDpia: (id: string, reason: string, version: number) =>
    api.post<ApprovalRequestDto>(`${BASE}/sdf/dpias/${id}/submit`, { reason, version }),
  retireDpia: (id: string, reason: string, version: number) =>
    api.post<void>(`${BASE}/sdf/dpias/${id}/retire`, { reason, version }),
  createAudit: (request: SaveDataAuditRequest) => api.post<DataAuditDto>(`${BASE}/sdf/audits`, request),
};

export const RISK_LEVELS: RiskLevel[] = ['Low', 'Medium', 'High'];

export const RELATIONSHIP_LABELS: Record<GuardianRelationship, string> = {
  Parent: 'Parent',
  LawfulGuardian: 'Lawful guardian',
};

export const PURPOSE_LABELS: Record<string, string> = {
  DataSharing: 'Sharing records',
  Research: 'Research',
  SmsWhatsAppUpdates: 'Updates by SMS and WhatsApp',
  MarketingCommunication: 'Health offers',
  Photography: 'Photographs',
};
