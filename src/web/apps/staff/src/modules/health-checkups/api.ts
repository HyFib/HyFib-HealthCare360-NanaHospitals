import type { PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ServiceCategory } from '@/modules/emr/orderCart';
import type { BookingStatus, ItineraryStatus } from './itinerary';

export type PackageGender = 'Any' | 'Male' | 'Female';
export type PackageItemKind = 'Investigation' | 'Consultation';

export interface PackageItemDto {
  id: string;
  kind: PackageItemKind;
  serviceItemId: string | null;
  serviceCode: string;
  name: string;
  category: ServiceCategory;
  departmentCode: string | null;
  gender: PackageGender;
  minAgeYears: number | null;
  sequence: number;
}

export interface PackageDto {
  id: string;
  code: string;
  name: string;
  description: string | null;
  gender: PackageGender;
  minAgeYears: number | null;
  maxAgeYears: number | null;
  price: number;
  serviceItemId: string | null;
  isActive: boolean;
  items: PackageItemDto[];
  version: number;
}

export interface PackageItemRequest {
  kind: PackageItemKind;
  serviceItemId: string | null;
  departmentCode: string | null;
  gender: PackageGender;
  minAgeYears: number | null;
}

export interface SavePackageRequest {
  code: string;
  name: string;
  description: string | null;
  gender: PackageGender;
  minAgeYears: number | null;
  maxAgeYears: number | null;
  price: number;
  isActive: boolean;
  items: PackageItemRequest[];
  version: number | null;
}

export interface PackageEligibility {
  isEligible: boolean;
  reason: string | null;
  excludedItems: string[];
}

export interface ItineraryItemDto {
  id: string;
  sequence: number;
  departmentId: string | null;
  departmentName: string;
  description: string;
  status: ItineraryStatus;
  completedAt: string | null;
  notes: string | null;
}

export interface BookingDto {
  id: string;
  bookingNumber: string;
  patientId: string;
  uhid: string | null;
  patientName: string | null;
  packageId: string;
  packageCode: string;
  packageName: string;
  scheduledDate: string;
  status: BookingStatus;
  visitId: string | null;
  orderId: string | null;
  corporateBatchId: string | null;
  corporateName: string | null;
  /** The company of a corporate batch, a corporate payer: the check-up is billed to it. */
  corporatePayerId: string | null;
  notes: string | null;
  checkedInAt: string | null;
  summary: string | null;
  recommendations: string | null;
  reviewedByPractitionerId: string | null;
  reviewedAt: string | null;
  completedAt: string | null;
  cancelReason: string | null;
  itinerary: ItineraryItemDto[];
  version: number;
}

export interface BookCheckupRequest {
  patientId: string;
  packageId: string;
  scheduledDate: string;
  visitId: string | null;
  notes: string | null;
}

export interface CorporateBatchRequest {
  /** The company: a corporate payer of the payer master (Insurance → Payers). */
  payerId: string;
  contactPerson: string | null;
  contactPhone: string | null;
  reference: string | null;
  packageId: string;
  scheduledDate: string;
  patientIds: string[];
  notes: string | null;
}

export interface CorporateBatchResult {
  batchId: string;
  batchNumber: string;
  booked: number;
  skipped: { patientId: string; reason: string }[];
}

export interface CorporateBatchDto {
  id: string;
  batchNumber: string;
  /** Null on batches booked before a batch named its company's payer. */
  payerId: string | null;
  corporateName: string;
  contactPerson: string | null;
  contactPhone: string | null;
  reference: string | null;
  packageId: string;
  scheduledDate: string;
  bookedCount: number;
  skippedCount: number;
  notes: string | null;
  createdAt: string;
}

export interface CorporateBatchDetail {
  batch: CorporateBatchDto;
  bookings: BookingDto[];
}

export interface BookingListParams {
  date?: string;
  status?: BookingStatus;
  search?: string;
  page?: number;
}

export const checkupKeys = {
  all: ['health-checkups'] as const,
  packages: (search: string, includeInactive: boolean) =>
    ['health-checkups', 'packages', search, includeInactive] as const,
  package: (id: string) => ['health-checkups', 'package', id] as const,
  eligibility: (packageId: string, patientId: string) =>
    ['health-checkups', 'eligibility', packageId, patientId] as const,
  bookings: (params: BookingListParams) => ['health-checkups', 'bookings', params] as const,
  booking: (id: string) => ['health-checkups', 'booking', id] as const,
  batches: (search: string, page: number) => ['health-checkups', 'batches', search, page] as const,
  batch: (id: string) => ['health-checkups', 'batch', id] as const,
};

export const checkupQueries = {
  packages: (search = '', includeInactive = false) =>
    queryOptions({
      queryKey: checkupKeys.packages(search, includeInactive),
      queryFn: ({ signal }) =>
        api.get<PackageDto[]>('/api/v1/health-checkups/packages', {
          query: { search: search || undefined, includeInactive: includeInactive || undefined },
          signal,
        }),
      staleTime: 60_000,
    }),
  package: (id: string) =>
    queryOptions({
      queryKey: checkupKeys.package(id),
      queryFn: ({ signal }) => api.get<PackageDto>(`/api/v1/health-checkups/packages/${id}`, { signal }),
    }),
  eligibility: (packageId: string, patientId: string) =>
    queryOptions({
      queryKey: checkupKeys.eligibility(packageId, patientId),
      queryFn: ({ signal }) =>
        api.get<PackageEligibility>(`/api/v1/health-checkups/packages/${packageId}/eligibility`, {
          query: { patientId },
          signal,
        }),
    }),
  bookings: (params: BookingListParams) =>
    queryOptions({
      queryKey: checkupKeys.bookings(params),
      queryFn: ({ signal }) =>
        api.get<PagedResult<BookingDto>>('/api/v1/health-checkups/bookings', {
          query: { ...params, pageSize: 50 },
          signal,
        }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  booking: (id: string) =>
    queryOptions({
      queryKey: checkupKeys.booking(id),
      queryFn: ({ signal }) => api.get<BookingDto>(`/api/v1/health-checkups/bookings/${id}`, { signal }),
    }),
  batches: (search = '', page = 1) =>
    queryOptions({
      queryKey: checkupKeys.batches(search, page),
      queryFn: ({ signal }) =>
        api.get<PagedResult<CorporateBatchDto>>('/api/v1/health-checkups/corporate-batches', {
          query: { search: search || undefined, page, pageSize: 25 },
          signal,
        }),
      placeholderData: keepPreviousData,
    }),
  batch: (id: string) =>
    queryOptions({
      queryKey: checkupKeys.batch(id),
      queryFn: ({ signal }) =>
        api.get<CorporateBatchDetail>(`/api/v1/health-checkups/corporate-batches/${id}`, { signal }),
    }),
};

export const checkupApi = {
  savePackage: (id: string | null, request: SavePackageRequest) =>
    id
      ? api.put<PackageDto>(`/api/v1/health-checkups/packages/${id}`, request)
      : api.post<PackageDto>('/api/v1/health-checkups/packages', request),
  book: (request: BookCheckupRequest) => api.post<BookingDto>('/api/v1/health-checkups/bookings', request),
  checkIn: (id: string, visitId: string | null) =>
    api.post<BookingDto>(`/api/v1/health-checkups/bookings/${id}/check-in`, { visitId }),
  completeStop: (id: string, itemId: string, notes: string | null) =>
    api.post<BookingDto>(`/api/v1/health-checkups/bookings/${id}/itinerary/${itemId}/complete`, { notes }),
  skipStop: (id: string, itemId: string, reason: string) =>
    api.post<BookingDto>(`/api/v1/health-checkups/bookings/${id}/itinerary/${itemId}/skip`, { reason }),
  saveSummary: (id: string, summary: string, recommendations: string | null) =>
    api.put<BookingDto>(`/api/v1/health-checkups/bookings/${id}/summary`, { summary, recommendations }),
  complete: (id: string) => api.post<BookingDto>(`/api/v1/health-checkups/bookings/${id}/complete`),
  cancel: (id: string, reason: string) =>
    api.post<BookingDto>(`/api/v1/health-checkups/bookings/${id}/cancel`, { reason }),
  noShow: (id: string) => api.post<BookingDto>(`/api/v1/health-checkups/bookings/${id}/no-show`),
  bookBatch: (request: CorporateBatchRequest) =>
    api.post<CorporateBatchResult>('/api/v1/health-checkups/corporate-batches', request),
};

/**
 * The full health check-up report as the server prints it and makes its PDF: the doctor's summary and recommendations
 * with every released result of the package (see `@/lib/documents`).
 */
export const checkupReportDocument = (bookingId: string) => `/api/v1/health-checkups/bookings/${bookingId}/report`;
