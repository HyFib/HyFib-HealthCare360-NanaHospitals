import { isApiError, type Gender, type LookupItem, type PagedResult } from '@hc360/core';
import { keepPreviousData, queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { PatientSummary } from '@/modules/patients/api';
import type { ChargeOutcome, ChargeStatus } from './shared/types';

// ---------------------------------------------------------------- enums (JSON strings, as declared on the server)

export const THEATRE_TYPES = ['Major', 'Minor', 'Cardiac', 'Ortho', 'Emergency'] as const;
export type TheatreType = (typeof THEATRE_TYPES)[number];

export const ANAESTHESIA_TYPES = ['GA', 'Spinal', 'Epidural', 'Regional', 'Local', 'Sedation'] as const;
export type AnaesthesiaType = (typeof ANAESTHESIA_TYPES)[number];

export const SURGERY_PRIORITIES = ['Elective', 'Urgent', 'Emergency'] as const;
export type SurgeryPriority = (typeof SURGERY_PRIORITIES)[number];

export const SURGERY_STATUSES = [
  'Requested',
  'Scheduled',
  'InPreOp',
  'InProgress',
  'Completed',
  'Cancelled',
  'Postponed',
] as const;
export type SurgeryStatus = (typeof SURGERY_STATUSES)[number];

export const LATERALITIES = ['NotApplicable', 'Left', 'Right', 'Bilateral'] as const;
export type Laterality = (typeof LATERALITIES)[number];

export type ChecklistItemState = 'Pending' | 'Done' | 'NotApplicable';

export const SAFETY_PHASES = ['SignIn', 'TimeOut', 'SignOut'] as const;
export type SafetyPhase = (typeof SAFETY_PHASES)[number];

export const ASA_GRADES = ['I', 'II', 'III', 'IV', 'V', 'VI'] as const;
export type AsaGrade = (typeof ASA_GRADES)[number];

export const AIRWAY_DEVICES = ['None', 'FaceMask', 'SupraglotticAirway', 'EndotrachealTube', 'Tracheostomy'] as const;
export type AirwayDevice = (typeof AIRWAY_DEVICES)[number];

export const SPECIMEN_DESTINATIONS = ['Histopathology', 'Microbiology', 'Cytology', 'FrozenSection', 'Other'] as const;
export type SpecimenDestination = (typeof SPECIMEN_DESTINATIONS)[number];

export const MATERIAL_KINDS = ['Implant', 'Consumable'] as const;
export type MaterialKind = (typeof MATERIAL_KINDS)[number];

export type ConflictKind = 'Theatre' | 'Surgeon' | 'Anaesthetist' | 'Patient';

// ---------------------------------------------------------------- theatres, calendar, utilisation

export interface TheatreDto {
  id: string;
  code: string;
  name: string;
  type: TheatreType;
  equipment: string[];
  location: string | null;
  displayOrder: number;
  isActive: boolean;
  version: number;
}

export interface SaveTheatreRequest {
  code: string;
  name: string;
  type: TheatreType;
  equipment: string[] | null;
  location: string | null;
  displayOrder: number;
  isActive: boolean;
  version: number | null;
}

export interface OtCalendarBooking {
  id: string;
  bookingNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  procedureName: string;
  laterality: Laterality;
  priority: SurgeryPriority;
  status: SurgeryStatus;
  theatreId: string | null;
  start: string | null;
  end: string | null;
  surgeonId: string | null;
  surgeonName: string | null;
  anaesthetistName: string | null;
  anaesthesiaType: AnaesthesiaType;
  signInDone: boolean;
  timeOutDone: boolean;
  signOutDone: boolean;
}

export interface OtCalendarTheatre {
  theatreId: string;
  code: string;
  name: string;
  type: TheatreType;
  bookings: OtCalendarBooking[];
}

export interface OtCalendar {
  from: string;
  to: string;
  theatres: OtCalendarTheatre[];
  awaitingSlot: OtCalendarBooking[];
}

export interface TheatreUtilisation {
  theatreId: string;
  casesCompleted: number;
  casesCancelled: number;
  casesPostponed: number;
  bookedMinutes: number;
  actualMinutes: number;
  actualToBookedPercent: number | null;
  availableMinutes: number;
  utilisationPercent: number | null;
  firstCases: number;
  firstCasesOnTime: number;
  firstCaseOnTimePercent: number | null;
  averageTurnoverMinutes: number | null;
}

export interface OtUtilisationRow {
  theatreId: string;
  code: string;
  name: string;
  metrics: TheatreUtilisation;
}

export interface OtUtilisationReport {
  from: string;
  to: string;
  minutesPerDay: number;
  theatres: OtUtilisationRow[];
}

export interface ImplantTraceItem {
  materialId: string;
  surgeryBookingId: string;
  bookingNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  phone: string | null;
  itemName: string;
  manufacturer: string | null;
  lotNumber: string | null;
  serialNumber: string | null;
  recordedAt: string;
}

// ---------------------------------------------------------------- bookings

export interface SurgeryListItem {
  id: string;
  bookingNumber: string;
  patientId: string;
  uhid: string;
  patientName: string;
  gender: Gender;
  age: string | null;
  procedureName: string;
  laterality: Laterality;
  priority: SurgeryPriority;
  status: SurgeryStatus;
  theatreId: string | null;
  theatreCode: string | null;
  scheduledStart: string | null;
  scheduledEnd: string | null;
  preferredDate: string | null;
  surgeonId: string | null;
  surgeonName: string | null;
  anaesthesiaType: AnaesthesiaType;
  signInDone: boolean;
  timeOutDone: boolean;
  signOutDone: boolean;
  chargeStatus: ChargeStatus;
}

export interface ChecklistItemDto {
  key: string;
  text: string;
  allowsNotApplicable: boolean;
  state: ChecklistItemState;
  remarks: string | null;
  recordedBy: string | null;
  recordedAt: string | null;
}

export interface SafetyPhaseDto {
  phase: SafetyPhase;
  isComplete: boolean;
  completedAt: string | null;
  items: ChecklistItemDto[];
}

export interface SurgeryDetail {
  id: string;
  bookingNumber: string;
  patient: PatientSummary | null;
  patientId: string;
  admissionId: string | null;
  visitId: string | null;
  orderItemId: string | null;
  procedureServiceItemId: string | null;
  procedureCode: string | null;
  procedureName: string;
  laterality: Laterality;
  diagnosis: string | null;
  surgeonId: string | null;
  surgeonName: string | null;
  assistantIds: string[];
  anaesthetistId: string | null;
  anaesthetistName: string | null;
  anaesthesiaType: AnaesthesiaType;
  priority: SurgeryPriority;
  preferredDate: string | null;
  theatreId: string | null;
  theatreName: string | null;
  scheduledStart: string | null;
  scheduledEnd: string | null;
  status: SurgeryStatus;
  receivedInPreOpAt: string | null;
  inRoomAt: string | null;
  startedAt: string | null;
  completedAt: string | null;
  preOpComplete: boolean;
  preOpChecklist: ChecklistItemDto[];
  safetyChecklist: SafetyPhaseDto[];
  notes: string | null;
  cancelReason: string | null;
  postponeReason: string | null;
  postponeCount: number;
  chargeStatus: ChargeStatus;
  chargeError: string | null;
  version: number;
}

export interface CreateSurgeryBookingRequest {
  patientId: string;
  admissionId: string | null;
  visitId: string | null;
  procedureServiceItemId: string | null;
  procedureCode: string | null;
  procedureName: string;
  laterality: Laterality;
  diagnosis: string | null;
  surgeonId: string | null;
  assistantIds: string[] | null;
  anaesthetistId: string | null;
  anaesthesiaType: AnaesthesiaType;
  priority: SurgeryPriority;
  preferredDate: string | null;
  theatreId: string | null;
  scheduledStart: string | null;
  scheduledEnd: string | null;
  notes: string | null;
}

export interface UpdateSurgeryPlanRequest {
  procedureServiceItemId: string | null;
  procedureCode: string | null;
  procedureName: string;
  laterality: Laterality;
  diagnosis: string | null;
  surgeonId: string | null;
  assistantIds: string[] | null;
  anaesthetistId: string | null;
  anaesthesiaType: AnaesthesiaType;
  priority: SurgeryPriority;
  preferredDate: string | null;
  notes: string | null;
  version: number;
}

export interface ScheduleSurgeryRequest {
  theatreId: string;
  scheduledStart: string;
  scheduledEnd: string;
  version: number;
}

export interface SurgeryCreated {
  id: string;
  bookingNumber: string;
  status: SurgeryStatus;
}

export interface ChecklistInput {
  key: string;
  state: ChecklistItemState;
  remarks: string | null;
}

/** One booking the chosen slot collides with (409 `surgery.slot_conflict`, in `data`). */
export interface SchedulingConflict {
  bookingId: string;
  kind: ConflictKind;
  start: string;
  end: string;
}

// ---------------------------------------------------------------- clinical records

export interface AnaesthesiaVitals {
  at: string;
  heartRate: number | null;
  systolic: number | null;
  diastolic: number | null;
  spO2: number | null;
  etCo2: number | null;
  temperatureC: number | null;
  respiratoryRate: number | null;
}

export interface AnaesthesiaDrug {
  at: string;
  drug: string;
  dose: string;
  route: string | null;
}

export interface AnaesthesiaFluid {
  at: string;
  fluid: string;
  volumeMl: number;
}

export interface AnaesthesiaRecordDto {
  id: string;
  surgeryBookingId: string;
  patientId: string;
  anaesthetistId: string | null;
  asaGrade: AsaGrade | null;
  asaEmergency: boolean;
  technique: AnaesthesiaType;
  airway: AirwayDevice;
  airwayNotes: string | null;
  anaesthesiaStartAt: string | null;
  anaesthesiaEndAt: string | null;
  vitals: AnaesthesiaVitals[];
  drugs: AnaesthesiaDrug[];
  fluids: AnaesthesiaFluid[];
  estimatedBloodLossMl: number | null;
  urineOutputMl: number | null;
  complications: string | null;
  recoveryNotes: string | null;
  signedAt: string | null;
  signedByName: string | null;
  version: number;
}

export interface SaveAnaesthesiaRecordRequest {
  asaGrade: AsaGrade | null;
  asaEmergency: boolean;
  technique: AnaesthesiaType;
  airway: AirwayDevice;
  airwayNotes: string | null;
  anaesthesiaStartAt: string | null;
  anaesthesiaEndAt: string | null;
  drugs: AnaesthesiaDrug[] | null;
  fluids: AnaesthesiaFluid[] | null;
  estimatedBloodLossMl: number | null;
  urineOutputMl: number | null;
  complications: string | null;
  recoveryNotes: string | null;
  sign: boolean;
  version: number | null;
}

export interface SurgicalSpecimen {
  description: string;
  sentTo: SpecimenDestination;
  label: string | null;
}

export interface OperationNoteDto {
  id: string;
  surgeryBookingId: string;
  patientId: string;
  surgeonId: string | null;
  preOperativeDiagnosis: string | null;
  postOperativeDiagnosis: string | null;
  procedurePerformed: string;
  findings: string | null;
  procedureDetails: string | null;
  specimens: SurgicalSpecimen[];
  drains: string | null;
  estimatedBloodLossMl: number | null;
  complications: string | null;
  postOpOrders: string | null;
  signedAt: string | null;
  signedByName: string | null;
  version: number;
}

export interface SaveOperationNoteRequest {
  preOperativeDiagnosis: string | null;
  postOperativeDiagnosis: string | null;
  procedurePerformed: string;
  findings: string | null;
  procedureDetails: string | null;
  specimens: SurgicalSpecimen[] | null;
  drains: string | null;
  estimatedBloodLossMl: number | null;
  complications: string | null;
  postOpOrders: string | null;
  sign: boolean;
  version: number | null;
}

export interface SurgeryMaterialDto {
  id: string;
  kind: MaterialKind;
  supplyItemId: string | null;
  itemName: string;
  manufacturer: string | null;
  lotNumber: string | null;
  serialNumber: string | null;
  expiryDate: string | null;
  quantity: number;
  unitPrice: number;
  amount: number;
  recordedByName: string | null;
  recordedAt: string;
  chargeStatus: ChargeStatus;
  chargeError: string | null;
  /** What the bill says of the item (NPPA): from the item master for an OT store item, from the pack for a consignment one. */
  brand: string | null;
  specification: string | null;
  /** The MRP of a unit printed on the pack: the lowest of the batches issued, or as typed for a consignment item. */
  mrp: number | null;
  /** The NPPA ceiling price of a unit of a price-controlled implant. */
  ceilingPrice: number | null;
}

/**
 * For an OT store item the item master and the stock issued say what it is, so `brand`, `specification` and `mrp` are for
 * a consignment or patient-supplied item only (an implant needs its brand, maker and MRP). `unitPrice` may not be above
 * the pack's MRP, nor a price-controlled implant's above its NPPA ceiling.
 */
export interface AddSurgeryMaterialRequest {
  kind: MaterialKind;
  supplyItemId: string | null;
  itemName: string;
  manufacturer: string | null;
  lotNumber: string | null;
  serialNumber: string | null;
  expiryDate: string | null;
  quantity: number;
  unitPrice: number;
  brand?: string | null;
  specification?: string | null;
  mrp?: number | null;
}

/** A usable batch of an OT store item (quantities in the item's base unit). */
export interface OtStoreBatch {
  batchId: string;
  batchNumber: string;
  expiryDate: string | null;
  available: number;
  mrp: number;
}

/**
 * An item in stock in the facility's OT store (`GET /api/v1/ot/store-items`), the store surgery materials are issued
 * from. `available` is what can be issued now; `batches` are the first three to issue, nearest expiry first (FEFO), and
 * `mrp` is the MRP of the first. Record it with `supplyItemId = itemId`, `itemName = name` and the chosen batch's
 * `lotNumber = batchNumber` and `expiryDate`.
 */
export interface OtStoreItem {
  itemId: string;
  code: string;
  name: string;
  genericName: string | null;
  manufacturer: string | null;
  type: string;
  baseUnit: string;
  available: number;
  nearestExpiry: string | null;
  mrp: number | null;
  batches: OtStoreBatch[];
  brand?: string | null;
  specification?: string | null;
  /** The NPPA ceiling price of a unit of a price-controlled implant: a unit is charged at most the lower of it and the MRP. */
  ceilingPrice?: number | null;
}

/** Service catalogue item (masters look-up), used to pick the planned procedure. */
export interface ServiceItemDto {
  id: string;
  code: string;
  name: string;
  basePrice: number;
  isActive: boolean;
}

// ---------------------------------------------------------------- queries

export interface CalendarParams {
  date: string;
  view: 'day' | 'week';
  theatreId?: string;
  surgeonId?: string;
}

export interface UtilisationParams {
  from: string;
  to: string;
  theatreId?: string;
  minutesPerDay?: number;
}

export interface BookingFilters {
  from?: string;
  to?: string;
  status?: SurgeryStatus;
  theatreId?: string;
  search?: string;
  sort?: string;
  desc?: boolean;
  page?: number;
}

export const otKeys = {
  all: ['ot'] as const,
  theatres: (includeInactive: boolean) => ['ot', 'theatres', includeInactive] as const,
  calendars: ['ot', 'calendar'] as const,
  calendar: (params: CalendarParams) => ['ot', 'calendar', params] as const,
  utilisation: (params: UtilisationParams) => ['ot', 'utilisation', params] as const,
  bookingLists: ['ot', 'bookings'] as const,
  bookings: (filters: BookingFilters) => ['ot', 'bookings', filters] as const,
  booking: (id: string) => ['ot', 'booking', id] as const,
  anaesthesia: (id: string) => ['ot', 'booking', id, 'anaesthesia'] as const,
  operationNote: (id: string) => ['ot', 'booking', id, 'operation-note'] as const,
  materials: (id: string) => ['ot', 'booking', id, 'materials'] as const,
  storeItems: (search: string) => ['ot', 'store-items', search] as const,
  trace: (lot: string, serial: string) => ['ot', 'implant-trace', lot, serial] as const,
};

/** GET that treats 404 as "not written yet" (anaesthesia record, operation note). */
async function getOrNull<T>(path: string, signal: AbortSignal): Promise<T | null> {
  try {
    return await api.get<T>(path, { signal });
  } catch (error) {
    if (isApiError(error) && error.status === 404) {
      return null;
    }
    throw error;
  }
}

export const otQueries = {
  theatres: (includeInactive = false) =>
    queryOptions({
      queryKey: otKeys.theatres(includeInactive),
      queryFn: ({ signal }) => api.get<TheatreDto[]>('/api/v1/ot/theatres', { query: { includeInactive }, signal }),
      staleTime: 5 * 60_000,
    }),
  calendar: (params: CalendarParams) =>
    queryOptions({
      queryKey: otKeys.calendar(params),
      queryFn: ({ signal }) => api.get<OtCalendar>('/api/v1/ot/calendar', { query: { ...params }, signal }),
      placeholderData: keepPreviousData,
      refetchInterval: 60_000,
    }),
  utilisation: (params: UtilisationParams) =>
    queryOptions({
      queryKey: otKeys.utilisation(params),
      queryFn: ({ signal }) => api.get<OtUtilisationReport>('/api/v1/ot/utilisation', { query: { ...params }, signal }),
      placeholderData: keepPreviousData,
    }),
  bookings: (filters: BookingFilters) =>
    queryOptions({
      queryKey: otKeys.bookings(filters),
      queryFn: ({ signal }) =>
        api.get<PagedResult<SurgeryListItem>>('/api/v1/ot/bookings', { query: { ...filters, pageSize: 25 }, signal }),
      placeholderData: keepPreviousData,
    }),
  booking: (id: string) =>
    queryOptions({
      queryKey: otKeys.booking(id),
      queryFn: ({ signal }) => api.get<SurgeryDetail>(`/api/v1/ot/bookings/${id}`, { signal }),
    }),
  anaesthesia: (id: string) =>
    queryOptions({
      queryKey: otKeys.anaesthesia(id),
      queryFn: ({ signal }) => getOrNull<AnaesthesiaRecordDto>(`/api/v1/ot/bookings/${id}/anaesthesia`, signal),
    }),
  operationNote: (id: string) =>
    queryOptions({
      queryKey: otKeys.operationNote(id),
      queryFn: ({ signal }) => getOrNull<OperationNoteDto>(`/api/v1/ot/bookings/${id}/operation-note`, signal),
    }),
  materials: (id: string) =>
    queryOptions({
      queryKey: otKeys.materials(id),
      queryFn: ({ signal }) => api.get<SurgeryMaterialDto[]>(`/api/v1/ot/bookings/${id}/materials`, { signal }),
    }),
  /** 422 `stock.no_store` or `stock.store_ambiguous` when the facility has no single active OT store. */
  storeItems: (search: string) =>
    queryOptions({
      queryKey: otKeys.storeItems(search),
      queryFn: ({ signal }) =>
        api.get<OtStoreItem[]>('/api/v1/ot/store-items', { query: { search: search || undefined, take: 20 }, signal }),
      staleTime: 30_000,
    }),
  trace: (lot: string, serial: string) =>
    queryOptions({
      queryKey: otKeys.trace(lot, serial),
      queryFn: ({ signal }) =>
        api.get<ImplantTraceItem[]>('/api/v1/ot/implants/trace', {
          query: { lot: lot || undefined, serial: serial || undefined },
          signal,
        }),
      enabled: Boolean(lot || serial),
      staleTime: 0,
    }),
};

const phasePath: Record<SafetyPhase, string> = { SignIn: 'sign-in', TimeOut: 'time-out', SignOut: 'sign-out' };

export const otApi = {
  saveTheatre: async (id: string | null, request: SaveTheatreRequest): Promise<void> => {
    if (id) {
      await api.put<void>(`/api/v1/ot/theatres/${id}`, request);
    } else {
      await api.post<{ id: string }>('/api/v1/ot/theatres', request);
    }
  },
  createBooking: (request: CreateSurgeryBookingRequest) => api.post<SurgeryCreated>('/api/v1/ot/bookings', request),
  updatePlan: (id: string, request: UpdateSurgeryPlanRequest) => api.put<void>(`/api/v1/ot/bookings/${id}`, request),
  schedule: (id: string, request: ScheduleSurgeryRequest) =>
    api.post<void>(`/api/v1/ot/bookings/${id}/schedule`, request),
  postpone: (id: string, reason: string) => api.post<void>(`/api/v1/ot/bookings/${id}/postpone`, { reason }),
  cancel: (id: string, reason: string) => api.post<void>(`/api/v1/ot/bookings/${id}/cancel`, { reason }),
  receive: (id: string) => api.post<void>(`/api/v1/ot/bookings/${id}/receive`),
  /** `version`: the booking the ticks were made on; a checklist changed meanwhile is not written over (REL-04). */
  recordPreOp: (id: string, items: ChecklistInput[], version: number) =>
    api.put<void>(`/api/v1/ot/bookings/${id}/pre-op-checklist`, { items, version }),
  confirmSafety: (id: string, phase: SafetyPhase, items: ChecklistInput[]) =>
    api.post<void>(`/api/v1/ot/bookings/${id}/safety-checklist/${phasePath[phase]}`, { items }),
  start: (id: string) => api.post<void>(`/api/v1/ot/bookings/${id}/start`),
  complete: (id: string) => api.post<void>(`/api/v1/ot/bookings/${id}/complete`),
  retryCharges: (id: string) => api.post<ChargeOutcome>(`/api/v1/ot/bookings/${id}/charges/retry`),
  saveAnaesthesia: (id: string, request: SaveAnaesthesiaRecordRequest) =>
    api.put<AnaesthesiaRecordDto>(`/api/v1/ot/bookings/${id}/anaesthesia`, request),
  addVitals: (id: string, rows: AnaesthesiaVitals[]) =>
    api.post<AnaesthesiaRecordDto>(`/api/v1/ot/bookings/${id}/anaesthesia/vitals`, { rows }),
  saveOperationNote: (id: string, request: SaveOperationNoteRequest) =>
    api.put<OperationNoteDto>(`/api/v1/ot/bookings/${id}/operation-note`, request),
  addMaterial: (id: string, request: AddSurgeryMaterialRequest) =>
    api.post<SurgeryMaterialDto>(`/api/v1/ot/bookings/${id}/materials`, request),
  removeMaterial: (id: string, materialId: string) =>
    api.delete<void>(`/api/v1/ot/bookings/${id}/materials/${materialId}`),
};

/** Procedures from the service catalogue (Surgery category), without the OT time/fee items. */
export async function searchProcedures(search: string): Promise<(LookupItem & { basePrice: number })[]> {
  const items = await api.get<ServiceItemDto[]>('/api/v1/masters/lookups/services', {
    query: { category: 'Surgery', search: search || undefined },
  });
  return items
    .filter((item) => !item.code.toUpperCase().startsWith('OT-'))
    .map((item) => ({ id: item.id, code: item.code, name: item.name, basePrice: item.basePrice }));
}
