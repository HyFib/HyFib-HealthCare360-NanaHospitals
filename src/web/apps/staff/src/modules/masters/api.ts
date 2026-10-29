import type { Gender, PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions, type QueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { ServiceCategory } from '@/modules/billing/labels';

/**
 * The organisation masters (HMS-03): facilities with their letterhead, departments, specialities, practitioners and the
 * service catalogue. Mirrors `OrganizationEndpoints` (`/api/v1/masters`): reading needs `masters.read`, saving
 * `masters.manage`. A code can't change once saved, and records are taken out of use rather than deleted.
 */

export const MASTERS_MANAGE = 'masters.manage';

export interface Address {
  line1: string | null;
  line2: string | null;
  city: string | null;
  district: string | null;
  state: string | null;
  pinCode: string | null;
  country: string | null;
}

export interface FacilityDto {
  id: string;
  code: string;
  name: string;
  legalName: string | null;
  address: Address;
  phone: string | null;
  email: string | null;
  website: string | null;
  gstin: string | null;
  pan: string | null;
  registrationNumber: string | null;
  nabhNumber: string | null;
  /** ABDM Health Facility Registry id. */
  hfrId: string | null;
  timeZone: string;
  currency: string;
  logoFileId: string | null;
  /** An extra line printed on the letterhead, such as accreditation text. */
  letterheadNote: string | null;
  isActive: boolean;
  version: number;
}

export interface SaveFacilityRequest {
  code: string;
  name: string;
  legalName: string | null;
  address: Address;
  phone: string | null;
  email: string | null;
  website: string | null;
  gstin: string | null;
  pan: string | null;
  registrationNumber: string | null;
  nabhNumber: string | null;
  hfrId: string | null;
  letterheadNote: string | null;
  logoFileId: string | null;
  isActive: boolean;
  version: number | null;
}

export type LetterheadPreviewRequest = Omit<SaveFacilityRequest, 'code' | 'hfrId' | 'isActive' | 'version'>;

/** A server document ready to print: the complete HTML page with its own content security policy. */
export interface PrintableDocumentDto {
  title: string;
  fileName: string;
  pageSize: string;
  landscape: boolean;
  html: string;
}

export type DepartmentKind = 'Clinical' | 'Diagnostic' | 'Support' | 'Administrative';

export interface DepartmentDto {
  id: string;
  facilityId: string;
  code: string;
  name: string;
  kind: DepartmentKind;
  specialtyId: string | null;
  headPractitionerId: string | null;
  location: string | null;
  phone: string | null;
  /** Queue token prefix on display boards ("GM" → GM-012). */
  tokenPrefix: string | null;
  displayOrder: number;
  isActive: boolean;
  version: number;
}

export type SaveDepartmentRequest = Omit<DepartmentDto, 'id' | 'facilityId' | 'version'> & { version: number | null };

export interface SpecialtyDto {
  id: string;
  code: string;
  name: string;
  description: string | null;
  isActive: boolean;
  version: number;
}

export type SaveSpecialtyRequest = Omit<SpecialtyDto, 'id' | 'version'> & { version: number | null };

export type PractitionerKind =
  'Consultant' | 'Resident' | 'Visiting' | 'Nurse' | 'Therapist' | 'Technician' | 'Dietitian' | 'Other';

export interface PractitionerDto {
  id: string;
  code: string;
  title: string;
  firstName: string;
  lastName: string | null;
  displayName: string;
  gender: Gender;
  qualification: string | null;
  registrationCouncil: string | null;
  registrationNumber: string | null;
  /** ABDM Healthcare Professionals Registry id, written 71-1234-5678-9012; identifies the doctor in ABDM records. */
  hprId: string | null;
  specialtyId: string | null;
  specialtyName: string | null;
  departmentId: string | null;
  departmentName: string | null;
  kind: PractitionerKind;
  phone: string | null;
  email: string | null;
  consultationFeeNew: number;
  consultationFeeFollowUp: number;
  /** Days after a paid consultation during which a follow-up is charged at the follow-up fee. */
  followUpValidityDays: number;
  languages: string[];
  bio: string | null;
  photoFileId: string | null;
  signatureFileId: string | null;
  availableForOnlineBooking: boolean;
  availableForTelehealth: boolean;
  /** Facilities where the practitioner consults; empty means all of them. */
  facilityIds: string[];
  isActive: boolean;
  version: number;
}

export type SavePractitionerRequest = Omit<
  PractitionerDto,
  'id' | 'displayName' | 'specialtyName' | 'departmentName' | 'version'
> & { version: number | null };

export interface ServiceItemDto {
  id: string;
  code: string;
  name: string;
  shortName: string | null;
  category: ServiceCategory;
  departmentId: string | null;
  /** The price without a tariff plan, before GST. */
  basePrice: number;
  /** GST percentage. */
  taxRate: number;
  sacCode: string | null;
  isDiscountAllowed: boolean;
  /** The billing counter may change the price on a bill. */
  isPriceEditable: boolean;
  description: string | null;
  isActive: boolean;
  version: number;
  /**
   * The name in local languages, by language code (such as ta or hi): a facility's rate list prints the one in its local
   * language under the English name.
   */
  localNames: Record<string, string>;
}

/** A language a service may be named in, by its code (such as ta), and whether it is an active facility's local language. */
export interface LocalLanguageItem {
  code: string;
  name: string;
  atFacility: boolean;
}

export type SaveServiceItemRequest = Omit<ServiceItemDto, 'id' | 'version'> & { version: number | null };

export interface PractitionerFilters {
  search?: string;
  departmentId?: string;
  specialtyId?: string;
  kind?: PractitionerKind;
  includeInactive?: boolean;
  page?: number;
}

export interface ServiceFilters {
  search?: string;
  category?: ServiceCategory;
  departmentId?: string;
  includeInactive?: boolean;
  page?: number;
}

export const MASTERS_PAGE_SIZE = 25;

const BASE = '/api/v1/masters';

export const mastersKeys = {
  all: ['masters'] as const,
  facilities: ['masters', 'facilities'] as const,
  departments: ['masters', 'departments'] as const,
  specialties: ['masters', 'specialties'] as const,
  practitioners: ['masters', 'practitioners'] as const,
  services: ['masters', 'services'] as const,
};

/** Masters change rarely, so lists stay fresh for five minutes; one record is always read afresh to edit it. */
const LIST_STALE = 5 * 60_000;

export const mastersQueries = {
  facilities: () =>
    queryOptions({
      queryKey: mastersKeys.facilities,
      queryFn: ({ signal }) => api.get<FacilityDto[]>(`${BASE}/facilities`, { signal }),
      staleTime: LIST_STALE,
    }),
  facility: (id: string) =>
    queryOptions({
      queryKey: [...mastersKeys.facilities, id],
      queryFn: ({ signal }) => api.get<FacilityDto>(`${BASE}/facilities/${id}`, { signal }),
      staleTime: 0,
    }),
  departments: (includeInactive: boolean) =>
    queryOptions({
      queryKey: [...mastersKeys.departments, { includeInactive }],
      queryFn: ({ signal }) =>
        api.get<DepartmentDto[]>(`${BASE}/departments`, {
          signal,
          query: { active: includeInactive ? undefined : true },
        }),
      staleTime: LIST_STALE,
    }),
  department: (id: string) =>
    queryOptions({
      queryKey: [...mastersKeys.departments, id],
      queryFn: ({ signal }) => api.get<DepartmentDto>(`${BASE}/departments/${id}`, { signal }),
      staleTime: 0,
    }),
  specialties: () =>
    queryOptions({
      queryKey: mastersKeys.specialties,
      queryFn: ({ signal }) => api.get<SpecialtyDto[]>(`${BASE}/specialties`, { signal }),
      staleTime: LIST_STALE,
    }),
  specialty: (id: string) =>
    queryOptions({
      queryKey: [...mastersKeys.specialties, id],
      queryFn: ({ signal }) => api.get<SpecialtyDto>(`${BASE}/specialties/${id}`, { signal }),
      staleTime: 0,
    }),
  practitioners: (filters: PractitionerFilters) =>
    queryOptions({
      queryKey: [...mastersKeys.practitioners, 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<PractitionerDto>>(`${BASE}/practitioners`, {
          signal,
          query: {
            search: filters.search,
            departmentId: filters.departmentId,
            specialtyId: filters.specialtyId,
            kind: filters.kind,
            active: filters.includeInactive ? undefined : true,
            page: filters.page ?? 1,
            pageSize: MASTERS_PAGE_SIZE,
          },
        }),
      placeholderData: keepPreviousData,
      staleTime: LIST_STALE,
    }),
  practitioner: (id: string) =>
    queryOptions({
      queryKey: [...mastersKeys.practitioners, id],
      queryFn: ({ signal }) => api.get<PractitionerDto>(`${BASE}/practitioners/${id}`, { signal }),
      staleTime: 0,
    }),
  services: (filters: ServiceFilters) =>
    queryOptions({
      queryKey: [...mastersKeys.services, 'list', filters],
      queryFn: ({ signal }) =>
        api.get<PagedResult<ServiceItemDto>>(`${BASE}/services`, {
          signal,
          query: {
            search: filters.search,
            category: filters.category,
            departmentId: filters.departmentId,
            active: filters.includeInactive ? undefined : true,
            page: filters.page ?? 1,
            pageSize: MASTERS_PAGE_SIZE,
          },
        }),
      placeholderData: keepPreviousData,
      staleTime: LIST_STALE,
    }),
  service: (id: string) =>
    queryOptions({
      queryKey: [...mastersKeys.services, id],
      queryFn: ({ signal }) => api.get<ServiceItemDto>(`${BASE}/services/${id}`, { signal }),
      staleTime: 0,
    }),
  /** The languages a service may be named in, marked when one is a facility's local language (it follows the facilities). */
  localLanguages: () =>
    queryOptions({
      queryKey: [...mastersKeys.facilities, 'local-languages'],
      queryFn: ({ signal }) => api.get<LocalLanguageItem[]>(`${BASE}/lookups/local-languages`, { signal }),
      staleTime: LIST_STALE,
    }),
};

function save<T>(path: string, id: string | null, body: T): Promise<unknown> {
  return id === null ? api.post<{ id: string }>(`${BASE}/${path}`, body) : api.put<void>(`${BASE}/${path}/${id}`, body);
}

export const mastersApi = {
  saveFacility: (id: string | null, body: SaveFacilityRequest) => save('facilities', id, body),
  saveDepartment: (id: string | null, body: SaveDepartmentRequest) => save('departments', id, body),
  saveSpecialty: (id: string | null, body: SaveSpecialtyRequest) => save('specialties', id, body),
  savePractitioner: (id: string | null, body: SavePractitionerRequest) => save('practitioners', id, body),
  saveService: (id: string | null, body: SaveServiceItemRequest) => save('services', id, body),
  previewLetterhead: (body: LetterheadPreviewRequest) =>
    api.post<PrintableDocumentDto>(`${BASE}/facilities/letterhead-preview`, body),
};

/**
 * After a master is saved, every screen's copy is out of date: the pickers that offer departments and doctors, the
 * prices billing quotes, the letterhead prints use. Everything is marked stale; only what is on screen reloads now.
 */
export function refreshAfterMasterChange(queryClient: QueryClient): void {
  void queryClient.invalidateQueries();
}
