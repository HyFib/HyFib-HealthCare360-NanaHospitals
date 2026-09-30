import type { Address, BloodGroup, Gender, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ChargeStatus } from '@/modules/ot/shared/types';

// ---------------------------------------------------------------- enums (JSON strings)

export const COMPONENT_TYPES = ['WholeBlood', 'PRBC', 'FFP', 'PlateletConcentrate', 'Cryoprecipitate'] as const;
export type BloodComponentType = (typeof COMPONENT_TYPES)[number];

export const UNIT_STATUSES = ['Quarantine', 'Available', 'Reserved', 'Issued', 'Discarded', 'Expired'] as const;
export type BloodUnitStatus = (typeof UNIT_STATUSES)[number];

export const TTI_RESULTS = ['Pending', 'NonReactive', 'Reactive'] as const;
export type TtiResult = (typeof TTI_RESULTS)[number];

export const DONATION_TYPES = ['Voluntary', 'Replacement'] as const;
export type DonationType = (typeof DONATION_TYPES)[number];

export const BAG_TYPES = ['Single', 'Double', 'Triple', 'Quadruple'] as const;
export type BloodBagType = (typeof BAG_TYPES)[number];

export const DONATION_STATUSES = ['Collected', 'Separated', 'Discarded'] as const;
export type DonationStatus = (typeof DONATION_STATUSES)[number];

export type DonorEligibilityOutcome = 'Eligible' | 'TemporarilyDeferred' | 'PermanentlyDeferred';

export const URGENCIES = ['Routine', 'Urgent', 'Emergency'] as const;
export type BloodRequestUrgency = (typeof URGENCIES)[number];

export const REQUEST_STATUSES = ['Pending', 'InProcess', 'Ready', 'PartiallyIssued', 'Issued', 'Cancelled'] as const;
export type BloodRequestStatus = (typeof REQUEST_STATUSES)[number];

export const CROSSMATCH_METHODS = ['ImmediateSpin', 'AntiHumanGlobulin', 'GelCard', 'Electronic'] as const;
export type CrossmatchMethod = (typeof CROSSMATCH_METHODS)[number];

export const CROSSMATCH_RESULTS = ['Compatible', 'Incompatible'] as const;
export type CrossmatchResult = (typeof CROSSMATCH_RESULTS)[number];

export const REACTION_SYMPTOMS = [
  'Fever',
  'ChillsRigors',
  'Urticaria',
  'Itching',
  'Flushing',
  'Dyspnoea',
  'Wheeze',
  'Hypotension',
  'Hypertension',
  'Tachycardia',
  'ChestPain',
  'BackPain',
  'Haemoglobinuria',
  'Oliguria',
  'NauseaVomiting',
  'Other',
] as const;
export type ReactionSymptom = (typeof REACTION_SYMPTOMS)[number];

export const REACTION_TYPES = [
  'Unclassified',
  'FebrileNonHaemolytic',
  'AllergicMinor',
  'Anaphylactic',
  'AcuteHaemolytic',
  'DelayedHaemolytic',
  'TransfusionAssociatedCirculatoryOverload',
  'TransfusionRelatedAcuteLungInjury',
  'BacterialContamination',
  'Hypotensive',
  'Other',
] as const;
export type ReactionType = (typeof REACTION_TYPES)[number];

export const REACTION_SEVERITIES = ['Mild', 'Moderate', 'Severe', 'LifeThreatening', 'Death'] as const;
export type ReactionSeverity = (typeof REACTION_SEVERITIES)[number];

export const REACTION_STATUSES = ['Reported', 'UnderInvestigation', 'Closed'] as const;
export type ReactionStatus = (typeof REACTION_STATUSES)[number];

/** Every ABO/Rh(D) group except Unknown, in the order used on screens and labels. */
export const KNOWN_GROUPS = [
  'APositive',
  'ANegative',
  'BPositive',
  'BNegative',
  'ABPositive',
  'ABNegative',
  'OPositive',
  'ONegative',
  'BombayPositive',
  'BombayNegative',
] as const satisfies readonly BloodGroup[];
export type KnownBloodGroup = (typeof KNOWN_GROUPS)[number];

// ---------------------------------------------------------------- DTOs (mirror Features/BloodBank/*Contracts.cs)

/** Grouping-independent TTI panel; the server also sends computed flags, which the screens recompute. */
export interface TtiPanel {
  hiv: TtiResult;
  hbsAg: TtiResult;
  hcv: TtiResult;
  syphilis: TtiResult;
  malaria: TtiResult;
}

export interface BloodUnitDto {
  id: string;
  unitNumber: string;
  donationId: string;
  componentType: BloodComponentType;
  bloodGroup: BloodGroup;
  volumeMl: number;
  collectedAt: string;
  expiresAt: string;
  status: BloodUnitStatus;
  tti: TtiPanel;
  storageLocation: string | null;
  reservedForRequestId: string | null;
  issuedAt: string | null;
  discardReason: string | null;
  isExpired: boolean;
}

export interface InventoryCell {
  component: BloodComponentType;
  bloodGroup: BloodGroup;
  available: number;
  reserved: number;
  quarantine: number;
  expiringSoon: number;
}

export interface ExpiringUnit {
  id: string;
  unitNumber: string;
  component: BloodComponentType;
  bloodGroup: BloodGroup;
  expiresAt: string;
  status: BloodUnitStatus;
}

export interface BloodInventoryDashboard {
  asOf: string;
  stock: InventoryCell[];
  expiringSoon: ExpiringUnit[];
  openRequests: number;
  emergencyRequests: number;
  unitsInQuarantine: number;
}

export interface SaveDonorRequest {
  firstName: string;
  lastName: string | null;
  gender: Gender;
  dateOfBirth: string;
  bloodGroup: BloodGroup | null;
  phone: string;
  email: string | null;
  address: Address | null;
  occupation: string | null;
  consentToNotify: boolean;
  notes: string | null;
  isActive: boolean;
  version: number | null;
}

export interface DonorListItem {
  id: string;
  donorNumber: string;
  fullName: string;
  gender: Gender;
  age: string;
  bloodGroup: BloodGroup;
  bloodGroupConfirmed: boolean;
  phoneMasked: string;
  donationCount: number;
  lastDonationAt: string | null;
  isPermanentlyDeferred: boolean;
  deferredUntil: string | null;
  nextEligibleOn: string | null;
}

export interface ScreeningAnswer {
  key: string;
  yes: boolean;
}

export interface DonorScreeningDto {
  id: string;
  screenedAt: string;
  screenedByName: string | null;
  haemoglobinGdl: number;
  weightKg: number;
  systolic: number;
  diastolic: number;
  pulse: number;
  temperatureC: number;
  answers: ScreeningAnswer[];
  outcome: DonorEligibilityOutcome;
  reasons: string[];
  deferredUntil: string | null;
  maxBagVolumeMl: number | null;
  donationId: string | null;
}

export interface DonationListItem {
  id: string;
  bagNumber: string;
  donorId: string;
  donorNumber: string;
  donorName: string;
  bagType: BloodBagType;
  volumeMl: number;
  type: DonationType;
  collectedAt: string;
  status: DonationStatus;
  bloodGroup: BloodGroup;
  ttiComplete: boolean;
  ttiReactive: boolean;
}

export interface DonorDetail {
  id: string;
  donorNumber: string;
  firstName: string;
  lastName: string | null;
  fullName: string;
  gender: Gender;
  dateOfBirth: string;
  age: string;
  bloodGroup: BloodGroup;
  bloodGroupConfirmed: boolean;
  phone: string;
  email: string | null;
  address: Address;
  occupation: string | null;
  consentToNotify: boolean;
  donationCount: number;
  lastDonationAt: string | null;
  nextEligibleOn: string | null;
  deferredUntil: string | null;
  isPermanentlyDeferred: boolean;
  deferralReason: string | null;
  notes: string | null;
  isActive: boolean;
  screenings: DonorScreeningDto[];
  donations: DonationListItem[];
  version: number;
}

export interface DonorQuestionDto {
  key: string;
  text: string;
  onlyFor: Gender | null;
  deferralDays: number | null;
  permanentDeferral: boolean;
}

export interface ScreenDonorRequest {
  haemoglobinGdl: number;
  weightKg: number;
  systolic: number;
  diastolic: number;
  pulse: number;
  temperatureC: number;
  answers: ScreeningAnswer[];
}

export interface CollectDonationRequest {
  screeningId: string;
  bagNumber: string | null;
  bagType: BloodBagType;
  volumeMl: number;
  type: DonationType;
  replacementForPatientId: string | null;
  collectedAt: string | null;
  donorReaction: string | null;
  notes: string | null;
}

export interface ComponentPlan {
  type: BloodComponentType;
  volumeMl: number | null;
  storageLocation: string | null;
}

export interface RecordLabResultsRequest extends TtiPanel {
  bloodGroup: BloodGroup;
}

export interface DonationDetail {
  id: string;
  bagNumber: string;
  donorId: string;
  donorNumber: string;
  donorName: string;
  bagType: BloodBagType;
  volumeMl: number;
  type: DonationType;
  replacementForPatientId: string | null;
  collectedAt: string;
  collectedByName: string | null;
  status: DonationStatus;
  bloodGroup: BloodGroup;
  tti: TtiPanel;
  labResultsAt: string | null;
  separatedAt: string | null;
  donorReaction: string | null;
  notes: string | null;
  components: BloodUnitDto[];
  version: number;
}

export interface CreateBloodRequestRequest {
  patientId: string;
  admissionId: string | null;
  visitId: string | null;
  componentType: BloodComponentType;
  units: number;
  urgency: BloodRequestUrgency;
  indication: string;
  requiredBy: string | null;
  haemoglobinGdl: number | null;
  plateletCount: number | null;
}

export interface BloodRequestListItem {
  id: string;
  requestNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  componentType: BloodComponentType;
  unitsRequested: number;
  unitsReserved: number;
  unitsIssued: number;
  urgency: BloodRequestUrgency;
  status: BloodRequestStatus;
  patientBloodGroup: BloodGroup;
  createdAt: string;
  requiredBy: string | null;
}

export interface CrossMatchDto {
  id: string;
  componentId: string;
  unitNumber: string;
  method: CrossmatchMethod;
  result: CrossmatchResult;
  performedAt: string;
  performedByName: string | null;
  remarks: string | null;
  isValid: boolean;
  chargeStatus: ChargeStatus;
}

export interface BloodIssueDto {
  id: string;
  requestId: string;
  componentId: string;
  unitNumber: string;
  componentType: BloodComponentType;
  unitBloodGroup: BloodGroup;
  isEmergencyRelease: boolean;
  issuedAt: string;
  issuedByName: string | null;
  issuedToName: string;
  returnedAt: string | null;
  restocked: boolean | null;
  returnNotes: string | null;
  chargeStatus: ChargeStatus;
  chargeError: string | null;
}

export interface BloodRequestDetail {
  id: string;
  requestNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  admissionId: string | null;
  visitId: string | null;
  orderItemId: string | null;
  componentType: BloodComponentType;
  unitsRequested: number;
  unitsReserved: number;
  unitsIssued: number;
  urgency: BloodRequestUrgency;
  indication: string;
  requiredBy: string | null;
  requestedByName: string | null;
  haemoglobinGdl: number | null;
  plateletCount: number | null;
  patientBloodGroup: BloodGroup;
  groupedAt: string | null;
  groupedByName: string | null;
  emergencyReleaseAuthorisedBy: string | null;
  emergencyReleaseReason: string | null;
  status: BloodRequestStatus;
  cancelReason: string | null;
  crossMatches: CrossMatchDto[];
  issues: BloodIssueDto[];
  version: number;
}

export interface CompatibleUnitDto {
  id: string;
  unitNumber: string;
  bloodGroup: BloodGroup;
  volumeMl: number;
  expiresAt: string;
  reservedForThisRequest: boolean;
}

export interface ReportReactionRequest {
  onsetAt: string;
  symptoms: ReactionSymptom[];
  vitals: string | null;
  transfusionStopped: boolean;
  volumeTransfusedMl: number | null;
  actionsTaken: string | null;
  severity: ReactionSeverity;
}

export interface CloseReactionRequest {
  type: ReactionType;
  severity: ReactionSeverity;
  findings: string;
  reportedToHaemovigilance: boolean;
}

export interface TransfusionReactionDto {
  id: string;
  issueId: string;
  requestId: string;
  patientId: string;
  uhid: string;
  patientName: string;
  unitNumber: string;
  reportedAt: string;
  reportedByName: string | null;
  onsetAt: string;
  symptoms: ReactionSymptom[];
  vitals: string | null;
  transfusionStopped: boolean;
  volumeTransfusedMl: number | null;
  actionsTaken: string | null;
  severity: ReactionSeverity;
  type: ReactionType;
  status: ReactionStatus;
  investigationFindings: string | null;
  reportedToHaemovigilance: boolean;
  closedAt: string | null;
  closedByName: string | null;
}

// ---------------------------------------------------------------- queries

export interface UnitFilters {
  status?: BloodUnitStatus;
  component?: BloodComponentType;
  bloodGroup?: BloodGroup;
  search?: string;
  page?: number;
}

export interface DonorFilters {
  bloodGroup?: BloodGroup;
  eligibleOnly?: boolean;
  search?: string;
  page?: number;
}

export interface DonationFilters {
  status?: DonationStatus;
  pendingResults?: boolean;
  search?: string;
  page?: number;
}

export interface RequestFilters {
  status?: BloodRequestStatus;
  closed?: boolean;
  search?: string;
  page?: number;
}

export const bloodKeys = {
  all: ['blood-bank'] as const,
  inventory: ['blood-bank', 'inventory'] as const,
  unitLists: ['blood-bank', 'units'] as const,
  units: (filters: UnitFilters) => ['blood-bank', 'units', filters] as const,
  unitByNumber: (number: string) => ['blood-bank', 'unit-by-number', number] as const,
  questions: ['blood-bank', 'donor-questions'] as const,
  donors: (filters: DonorFilters) => ['blood-bank', 'donors', filters] as const,
  donor: (id: string) => ['blood-bank', 'donor', id] as const,
  donations: (filters: DonationFilters) => ['blood-bank', 'donations', filters] as const,
  donation: (id: string) => ['blood-bank', 'donation', id] as const,
  requestLists: ['blood-bank', 'requests'] as const,
  requests: (filters: RequestFilters) => ['blood-bank', 'requests', filters] as const,
  request: (id: string) => ['blood-bank', 'request', id] as const,
  compatible: (requestId: string) => ['blood-bank', 'request', requestId, 'compatible-units'] as const,
  reactions: (status: ReactionStatus | undefined, page: number) =>
    ['blood-bank', 'reactions', status ?? 'all', page] as const,
};

export const bloodQueries = {
  inventory: () =>
    queryOptions({
      queryKey: bloodKeys.inventory,
      queryFn: ({ signal }) => api.get<BloodInventoryDashboard>('/api/v1/blood-bank/inventory', { signal }),
      refetchInterval: 60_000,
    }),
  units: (filters: UnitFilters) =>
    queryOptions({
      queryKey: bloodKeys.units(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<BloodUnitDto>>('/api/v1/blood-bank/units', { query: { ...filters, pageSize: 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
  unitByNumber: (number: string) =>
    queryOptions({
      queryKey: bloodKeys.unitByNumber(number),
      queryFn: ({ signal }) =>
        api.get<BloodUnitDto>(`/api/v1/blood-bank/units/by-number/${encodeURIComponent(number)}`, { signal }),
      enabled: number.length > 0,
      retry: false,
      staleTime: 0,
    }),
  donorQuestions: () =>
    queryOptions({
      queryKey: bloodKeys.questions,
      queryFn: ({ signal }) => api.get<DonorQuestionDto[]>('/api/v1/blood-bank/donor-questions', { signal }),
      staleTime: 60 * 60_000,
    }),
  donors: (filters: DonorFilters) =>
    queryOptions({
      queryKey: bloodKeys.donors(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<DonorListItem>>('/api/v1/blood-bank/donors', {
          query: { ...filters, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  donor: (id: string) =>
    queryOptions({
      queryKey: bloodKeys.donor(id),
      queryFn: ({ signal }) => api.get<DonorDetail>(`/api/v1/blood-bank/donors/${id}`, { signal }),
    }),
  donations: (filters: DonationFilters) =>
    queryOptions({
      queryKey: bloodKeys.donations(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<DonationListItem>>('/api/v1/blood-bank/donations', {
          query: { ...filters, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  donation: (id: string) =>
    queryOptions({
      queryKey: bloodKeys.donation(id),
      queryFn: ({ signal }) => api.get<DonationDetail>(`/api/v1/blood-bank/donations/${id}`, { signal }),
    }),
  requests: (filters: RequestFilters) =>
    queryOptions({
      queryKey: bloodKeys.requests(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<BloodRequestListItem>>('/api/v1/blood-bank/requests', {
          query: {
            status: filters.status,
            openOnly: filters.closed ? false : undefined,
            search: filters.search,
            page: filters.page,
            pageSize: 25,
          },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  request: (id: string) =>
    queryOptions({
      queryKey: bloodKeys.request(id),
      queryFn: ({ signal }) => api.get<BloodRequestDetail>(`/api/v1/blood-bank/requests/${id}`, { signal }),
    }),
  compatibleUnits: (requestId: string) =>
    queryOptions({
      queryKey: bloodKeys.compatible(requestId),
      queryFn: ({ signal }) =>
        api.get<CompatibleUnitDto[]>(`/api/v1/blood-bank/requests/${requestId}/compatible-units`, { signal }),
    }),
  reactions: (status: ReactionStatus | undefined, page: number) =>
    queryOptions({
      queryKey: bloodKeys.reactions(status, page),
      queryFn: ({ signal }) =>
        api.get<PagedResult<TransfusionReactionDto>>('/api/v1/blood-bank/reactions', {
          query: { status, page, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
};

export const bloodApi = {
  saveDonor: async (id: string | null, request: SaveDonorRequest): Promise<string> => {
    if (id) {
      await api.put<void>(`/api/v1/blood-bank/donors/${id}`, request);
      return id;
    }
    const created = await api.post<{ id: string }>('/api/v1/blood-bank/donors', request);
    return created.id;
  },
  deferDonor: (id: string, until: string | null, permanent: boolean, reason: string) =>
    api.post<void>(`/api/v1/blood-bank/donors/${id}/defer`, { until, permanent, reason }),
  screenDonor: (id: string, request: ScreenDonorRequest) =>
    api.post<DonorScreeningDto>(`/api/v1/blood-bank/donors/${id}/screenings`, request),
  collect: (donorId: string, request: CollectDonationRequest) =>
    api.post<{ id: string }>(`/api/v1/blood-bank/donors/${donorId}/donations`, request),
  separate: (donationId: string, components: ComponentPlan[]) =>
    api.post<BloodUnitDto[]>(`/api/v1/blood-bank/donations/${donationId}/components`, { components }),
  recordLabResults: (donationId: string, request: RecordLabResultsRequest) =>
    api.put<void>(`/api/v1/blood-bank/donations/${donationId}/lab-results`, request),
  discardUnit: (unitId: string, reason: string) =>
    api.post<void>(`/api/v1/blood-bank/units/${unitId}/discard`, { reason }),
  createRequest: (request: CreateBloodRequestRequest) =>
    api.post<{ id: string; requestNumber: string }>('/api/v1/blood-bank/requests', request),
  cancelRequest: (id: string, reason: string) => api.post<void>(`/api/v1/blood-bank/requests/${id}/cancel`, { reason }),
  groupPatient: (id: string, bloodGroup: BloodGroup) =>
    api.post<void>(`/api/v1/blood-bank/requests/${id}/grouping`, { bloodGroup }),
  emergencyRelease: (id: string, authorisedBy: string, reason: string) =>
    api.post<void>(`/api/v1/blood-bank/requests/${id}/emergency-release`, { authorisedBy, reason }),
  crossMatch: (
    id: string,
    componentId: string,
    method: CrossmatchMethod,
    result: CrossmatchResult,
    remarks: string | null,
  ) =>
    api.post<CrossMatchDto>(`/api/v1/blood-bank/requests/${id}/crossmatches`, { componentId, method, result, remarks }),
  issue: (id: string, componentId: string, issuedToName: string) =>
    api.post<BloodIssueDto>(`/api/v1/blood-bank/requests/${id}/issues`, { componentId, issuedToName }),
  returnIssue: (issueId: string, temperatureMaintained: boolean, notes: string | null) =>
    api.post<BloodIssueDto>(`/api/v1/blood-bank/issues/${issueId}/return`, { temperatureMaintained, notes }),
  reportReaction: (issueId: string, request: ReportReactionRequest) =>
    api.post<{ id: string }>(`/api/v1/blood-bank/issues/${issueId}/reactions`, request),
  investigateReaction: (id: string) => api.post<void>(`/api/v1/blood-bank/reactions/${id}/investigate`),
  closeReaction: (id: string, request: CloseReactionRequest) =>
    api.post<void>(`/api/v1/blood-bank/reactions/${id}/close`, request),
};
