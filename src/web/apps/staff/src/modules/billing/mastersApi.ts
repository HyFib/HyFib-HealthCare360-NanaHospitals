import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ServiceItemDto } from './api';
import type { ServiceCategory, TariffType } from './labels';

/** Tariff plans, plan prices and treatment packages: /api/v1/billing/tariffs and /packages (MB-02). */

export interface TariffPlanListItem {
  id: string;
  code: string;
  name: string;
  type: TariffType;
  payerId: string | null;
  payerName: string | null;
  facilityId: string | null;
  defaultDiscountPercent: number;
  isDefault: boolean;
  isActive: boolean;
  priceCount: number;
}

export interface BedMultiplierDto {
  bedServiceItemId: string;
  bedServiceCode: string | null;
  bedServiceName: string | null;
  multiplier: number;
}

export interface TariffPlanDetail {
  id: string;
  code: string;
  name: string;
  type: TariffType;
  payerId: string | null;
  facilityId: string | null;
  defaultDiscountPercent: number;
  isDefault: boolean;
  isActive: boolean;
  description: string | null;
  bedMultipliers: BedMultiplierDto[];
  version: number;
}

export interface SaveTariffPlanRequest {
  code: string;
  name: string;
  type: TariffType;
  payerId: string | null;
  facilityId: string | null;
  defaultDiscountPercent: number;
  isDefault: boolean;
  isActive: boolean;
  description: string | null;
  bedMultipliers: { bedServiceItemId: string; multiplier: number }[] | null;
  version: number | null;
}

export interface TariffPriceDto {
  id: string;
  serviceItemId: string;
  serviceCode: string;
  serviceName: string;
  category: ServiceCategory;
  basePrice: number;
  price: number;
  effectiveFrom: string;
  effectiveTo: string | null;
  version: number;
}

export interface SaveTariffPriceRequest {
  serviceItemId: string;
  price: number;
  effectiveFrom: string;
  effectiveTo: string | null;
}

export interface BulkTariffPricesResult {
  created: number;
  updated: number;
}

export interface TariffPriceFilters {
  search?: string;
  onDate?: string;
  sort?: 'effectiveFrom' | 'price';
  desc?: boolean;
  page?: number;
  pageSize?: number;
}

export interface PackageListItem {
  id: string;
  code: string;
  name: string;
  price: number;
  lengthOfStayDays: number | null;
  tariffPlanId: string | null;
  isActive: boolean;
  inclusionCount: number;
}

export interface PackageInclusionDto {
  id: string;
  serviceItemId: string | null;
  serviceCode: string | null;
  serviceName: string | null;
  category: ServiceCategory | null;
  maxQuantity: number | null;
  maxAmount: number | null;
  notes: string | null;
}

export interface PackageDetail {
  id: string;
  code: string;
  name: string;
  serviceItemId: string;
  serviceCode: string | null;
  price: number;
  lengthOfStayDays: number | null;
  tariffPlanId: string | null;
  description: string | null;
  isActive: boolean;
  inclusions: PackageInclusionDto[];
  version: number;
}

export interface PackageInclusionInput {
  serviceItemId: string | null;
  category: ServiceCategory | null;
  maxQuantity: number | null;
  maxAmount: number | null;
  notes: string | null;
}

export interface SavePackageRequest {
  code: string;
  name: string;
  serviceItemId: string;
  price: number;
  lengthOfStayDays: number | null;
  tariffPlanId: string | null;
  description: string | null;
  isActive: boolean;
  inclusions: PackageInclusionInput[];
  version: number | null;
}

export const mastersKeys = {
  tariffs: ['billing', 'tariffs'] as const,
  packages: ['billing', 'packages'] as const,
};

export const mastersQueries = {
  tariffPlans: (includeInactive = false) =>
    queryOptions({
      queryKey: [...mastersKeys.tariffs, 'plans', includeInactive],
      queryFn: ({ signal }) => api.get<TariffPlanListItem[]>('/api/v1/billing/tariffs', { query: { includeInactive }, signal }),
      staleTime: 5 * 60_000,
    }),
  tariffPlan: (id: string) =>
    queryOptions({
      queryKey: [...mastersKeys.tariffs, 'plan', id],
      queryFn: ({ signal }) => api.get<TariffPlanDetail>(`/api/v1/billing/tariffs/${id}`, { signal }),
    }),
  tariffPrices: (planId: string, filters: TariffPriceFilters) =>
    queryOptions({
      queryKey: [...mastersKeys.tariffs, 'prices', planId, filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<TariffPriceDto>>(`/api/v1/billing/tariffs/${planId}/prices`, { query: { pageSize: 25, ...filters }, signal }),
      placeholderData: keepPreviousData,
    }),
  packages: (includeInactive = false) =>
    queryOptions({
      queryKey: [...mastersKeys.packages, 'list', includeInactive],
      queryFn: ({ signal }) => api.get<PackageListItem[]>('/api/v1/billing/packages', { query: { includeInactive }, signal }),
      staleTime: 5 * 60_000,
    }),
  package: (id: string) =>
    queryOptions({
      queryKey: [...mastersKeys.packages, 'detail', id],
      queryFn: ({ signal }) => api.get<PackageDetail>(`/api/v1/billing/packages/${id}`, { signal }),
    }),
};

export const mastersApi = {
  createPlan: (request: SaveTariffPlanRequest) => api.post<TariffPlanDetail>('/api/v1/billing/tariffs', request),
  updatePlan: (id: string, request: SaveTariffPlanRequest) => api.put<TariffPlanDetail>(`/api/v1/billing/tariffs/${id}`, request),
  savePrice: (planId: string, request: SaveTariffPriceRequest) => api.put<BulkTariffPricesResult>(`/api/v1/billing/tariffs/${planId}/prices`, request),
  savePrices: (planId: string, prices: SaveTariffPriceRequest[]) =>
    api.post<BulkTariffPricesResult>(`/api/v1/billing/tariffs/${planId}/prices/bulk`, { prices }, { timeoutMs: 120_000 }),
  deletePrice: (planId: string, priceId: string) => api.delete<void>(`/api/v1/billing/tariffs/${planId}/prices/${priceId}`),
  createPackage: (request: SavePackageRequest) => api.post<PackageDetail>('/api/v1/billing/packages', request),
  updatePackage: (id: string, request: SavePackageRequest) => api.put<PackageDetail>(`/api/v1/billing/packages/${id}`, request),
};

/**
 * Every price of a plan matching a search (for bulk revisions), page by page. Stops at `limit` rows so a mistaken
 * empty search on a huge plan cannot flood the WiFi.
 */
export async function loadAllPrices(planId: string, filters: Pick<TariffPriceFilters, 'search' | 'onDate'>, limit = 5000): Promise<{ prices: TariffPriceDto[]; truncated: boolean }> {
  const prices: TariffPriceDto[] = [];
  for (let page = 1; ; page++) {
    const result = await api.get<PagedResult<TariffPriceDto>>(`/api/v1/billing/tariffs/${planId}/prices`, { query: { ...filters, page, pageSize: 200 } });
    prices.push(...result.items);
    if (prices.length >= result.totalCount || result.items.length === 0) {
      return { prices, truncated: false };
    }
    if (prices.length >= limit) {
      return { prices, truncated: true };
    }
  }
}

/** The whole active service catalogue (for matching pasted service codes), page by page. */
export async function loadServiceCatalogue(limit = 10_000): Promise<ServiceItemDto[]> {
  const items: ServiceItemDto[] = [];
  for (let page = 1; ; page++) {
    const result = await api.get<PagedResult<ServiceItemDto>>('/api/v1/masters/services', { query: { active: true, page, pageSize: 200, sort: 'code' } });
    items.push(...result.items);
    if (items.length >= result.totalCount || result.items.length === 0 || items.length >= limit) {
      return items;
    }
  }
}
