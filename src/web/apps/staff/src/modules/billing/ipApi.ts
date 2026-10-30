import type { PagedResult, PayerType } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { BillDetail, DeviceDetails } from './api';
import type { ChargeStatus, IpAccountStatus, ServiceCategory } from './labels';

/** In-patient accounts: /api/v1/billing/ip-accounts (IPB-01..08). */

export interface IpAccountListItem {
  id: string;
  admissionId: string;
  admissionNumber: string;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  payerType: PayerType;
  payerName: string | null;
  status: IpAccountStatus;
  admittedAt: string;
  dischargedAt: string | null;
  estimatedAmount: number;
  depositBalance: number;
  hasPackage: boolean;
}

export interface IpAccountDetail {
  id: string;
  admissionId: string;
  admissionNumber: string;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  visitId: string;
  payerType: PayerType;
  payerId: string | null;
  payerName: string | null;
  tariffPlanId: string | null;
  tariffPlanName: string | null;
  policyId: string | null;
  attendingPractitionerId: string | null;
  attendingPractitionerName: string | null;
  bedServiceItemId: string | null;
  bedServiceCode: string | null;
  admittedAt: string;
  dischargeInitiatedAt: string | null;
  dischargedAt: string | null;
  status: IpAccountStatus;
  estimatedAmount: number;
  depositReceived: number;
  depositAdjusted: number;
  depositRefunded: number;
  depositBalance: number;
  packageId: string | null;
  packageName: string | null;
  dischargeWithDuesApproved: boolean;
  dischargeApprovalReason: string | null;
  notes: string | null;
  version: number;
  /** Why the stay is free of cost by law (BNSS 2023 s.397, with the MLC entry): no advance is taken and its bills come to nothing. */
  freeTreatmentReason?: string | null;
}

export interface UpdateIpAccountRequest {
  estimatedAmount: number;
  payerType: PayerType;
  payerId: string | null;
  tariffPlanId: string | null;
  policyId: string | null;
  repriceUnbilledCharges: boolean;
  notes: string | null;
  version: number;
}

export interface RunningBillCategory {
  category: ServiceCategory;
  items: number;
  gross: number;
  discount: number;
  tax: number;
  net: number;
}

export interface RunningBillDay {
  date: string;
  items: number;
  net: number;
}

export interface RunningBillLine {
  chargeId: string;
  serviceDate: string;
  serviceCode: string;
  description: string;
  category: ServiceCategory;
  quantity: number;
  unitPrice: number;
  netAmount: number;
  status: ChargeStatus;
  billId: string | null;
  practitionerName: string | null;
  isReturn: boolean;
  /** An implant's or another device's bill details (NPPA); null for a service. */
  device?: DeviceDetails | null;
}

export interface PackageLineStatus {
  chargeId: string;
  serviceCode: string;
  description: string;
  gross: number;
  covered: number;
  excess: number;
}

export interface PackageStatus {
  packageId: string;
  code: string;
  name: string;
  packagePrice: number;
  actuals: number;
  covered: number;
  excess: number;
  variance: number;
  lines: PackageLineStatus[];
}

export interface InsuranceEstimate {
  payerId: string | null;
  payerName: string | null;
  coverageLimit: number | null;
  coPayPercent: number;
  payerShare: number;
  patientShare: number;
}

export interface RunningBill {
  ipAccountId: string;
  admissionNumber: string;
  status: IpAccountStatus;
  byCategory: RunningBillCategory[];
  byDate: RunningBillDay[];
  lines: RunningBillLine[];
  grossTotal: number;
  discountTotal: number;
  taxTotal: number;
  netTotal: number;
  unbilledNet: number;
  billedNet: number;
  estimatedPatientPayable: number;
  paidOnBills: number;
  depositBalance: number;
  estimatedBalance: number;
  estimatedAmount: number;
  package: PackageStatus | null;
  insurance: InsuranceEstimate | null;
  returnsNet: number;
  /** Why the stay is free of cost by law: the estimate the patient pays is nothing. */
  freeTreatmentReason?: string | null;
}

export interface SettlementResult {
  /** None for a stay whose interim bills took every service (or with no services): it has no final bill. */
  finalBillId: string | null;
  advanceAdjusted: number;
  remainingDue: number;
  refundId: string | null;
  refundRequested: number;
}

export interface ClearanceDto {
  cleared: boolean;
  outstanding: number;
  reason: string | null;
  unbilledCount: number;
  unbilledAmount: number;
  hasFinalBill: boolean;
  patientDue: number;
  depositBalance: number;
  dischargeWithDuesApproved: boolean;
  /** Every service is on a finalized interim bill: a final bill would hold nothing, so none is needed. */
  billedOnInterimBills: boolean;
}

export interface IpAccountFilters {
  status?: IpAccountStatus;
  search?: string;
  page?: number;
}

export const ipKeys = {
  all: ['billing', 'ip'] as const,
  account: (id: string) => ['billing', 'ip', 'account', id] as const,
};

export const ipQueries = {
  accounts: (filters: IpAccountFilters) =>
    queryOptions({
      queryKey: [...ipKeys.all, 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<IpAccountListItem>>('/api/v1/billing/ip-accounts', {
          query: { pageSize: 25, ...filters },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  account: (id: string) =>
    queryOptions({
      queryKey: ipKeys.account(id),
      queryFn: ({ signal }) => api.get<IpAccountDetail>(`/api/v1/billing/ip-accounts/${id}`, { signal }),
    }),
  runningBill: (id: string) =>
    queryOptions({
      queryKey: [...ipKeys.account(id), 'running-bill'],
      queryFn: ({ signal }) => api.get<RunningBill>(`/api/v1/billing/ip-accounts/${id}/running-bill`, { signal }),
    }),
  packageStatus: (id: string) =>
    queryOptions({
      queryKey: [...ipKeys.account(id), 'package'],
      // No package assigned comes back as an empty body.
      queryFn: async ({ signal }) =>
        (await api.get<PackageStatus | null | undefined>(`/api/v1/billing/ip-accounts/${id}/package-status`, {
          signal,
        })) ?? null,
    }),
  clearance: (id: string) =>
    queryOptions({
      queryKey: [...ipKeys.account(id), 'clearance'],
      queryFn: ({ signal }) => api.get<ClearanceDto>(`/api/v1/billing/ip-accounts/${id}/clearance`, { signal }),
    }),
};

export const ipApi = {
  update: (id: string, request: UpdateIpAccountRequest) => api.put<void>(`/api/v1/billing/ip-accounts/${id}`, request),
  assignPackage: (id: string, packageId: string | null, version: number) =>
    api.put<void>(`/api/v1/billing/ip-accounts/${id}/package`, { packageId, version }),
  createInterimBill: (id: string, upTo: string | null) =>
    api.post<BillDetail>(`/api/v1/billing/ip-accounts/${id}/interim-bills`, { upTo }),
  prepareFinalBill: (id: string) => api.post<BillDetail>(`/api/v1/billing/ip-accounts/${id}/final-bill`),
  settle: (id: string, idempotencyKey?: string) =>
    api.post<SettlementResult>(`/api/v1/billing/ip-accounts/${id}/settle`, undefined, { idempotencyKey }),
  /** Made on the account as it was read (`version`): refused (409) if the account changed since, e.g. a discharge revoked. */
  approveDischargeWithDues: (id: string, reason: string, version: number) =>
    api.post<void>(`/api/v1/billing/ip-accounts/${id}/discharge-approval`, { reason, version }),
};
