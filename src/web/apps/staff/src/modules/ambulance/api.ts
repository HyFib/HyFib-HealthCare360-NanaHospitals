import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';

// The ambulance desk (server `AmbulanceEndpoints`): the fleet with its certificates, calls taken and vehicles sent, each
// step of a trip as the crew report it, and the trip log with response times. A completed trip for a registered
// patient is charged on their bill: the base fare covers the first 10 km, each kilometre beyond is charged.

export type AmbulanceKind = 'Bls' | 'Als' | 'PatientTransport' | 'Hearse';
export type TripPurpose = 'EmergencyPickup' | 'InterFacilityTransfer' | 'ScheduledTransport' | 'BodyTransport';
export type TripStatus = 'Requested' | 'Dispatched' | 'AtScene' | 'Transporting' | 'Completed' | 'Cancelled';

export const KIND_LABELS: Record<AmbulanceKind, string> = {
  Bls: 'Basic life support',
  Als: 'Advanced life support',
  PatientTransport: 'Patient transport',
  Hearse: 'Hearse',
};

export const PURPOSE_LABELS: Record<TripPurpose, string> = {
  EmergencyPickup: 'Emergency pickup',
  InterFacilityTransfer: 'Transfer to or from a hospital',
  ScheduledTransport: 'Scheduled transport',
  BodyTransport: 'Body transport',
};

export const STATUS_LABELS: Record<TripStatus, string> = {
  Requested: 'Waiting for a vehicle',
  Dispatched: 'On the way',
  AtScene: 'At the pickup',
  Transporting: 'Patient on board',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
};

/** Included kilometres in each base fare (server `AmbulanceCharges.IncludedKm`). */
export const INCLUDED_KM = 10;

export interface AmbulanceVehicle {
  id: string;
  registrationNumber: string;
  kind: AmbulanceKind;
  fitnessValidUntil: string | null;
  insuranceValidUntil: string | null;
  pollutionValidUntil: string | null;
  odometerKm: number;
  isActive: boolean;
  lapsed: string[];
  onTripId: string | null;
  version: number;
}

export interface SaveVehicleRequest {
  id: string | null;
  registrationNumber: string;
  kind: AmbulanceKind;
  fitnessValidUntil: string | null;
  insuranceValidUntil: string | null;
  pollutionValidUntil: string | null;
  odometerKm: number;
  isActive: boolean;
  version: number | null;
}

export interface AmbulanceTrip {
  id: string;
  number: string;
  purpose: TripPurpose;
  patientId: string | null;
  uhid: string | null;
  patientName: string;
  callerName: string;
  callerPhone: string;
  pickupAddress: string;
  dropAddress: string | null;
  complaint: string | null;
  requestedAt: string;
  requestedByName: string;
  status: TripStatus;
  vehicleNumber: string | null;
  vehicleKind: AmbulanceKind | null;
  driverName: string | null;
  crewName: string | null;
  dispatchedAt: string | null;
  atSceneAt: string | null;
  leftSceneAt: string | null;
  completedAt: string | null;
  startOdometerKm: number | null;
  endOdometerKm: number | null;
  kilometres: number | null;
  responseMinutes: number | null;
  chargeWaivedReason: string | null;
  cancelReason: string | null;
  version: number;
}

export interface RequestTripRequest {
  purpose: TripPurpose;
  patientId: string | null;
  patientName: string | null;
  callerName: string;
  callerPhone: string;
  pickupAddress: string;
  dropAddress: string | null;
  complaint: string | null;
}

export interface TripLog {
  from: string;
  to: string;
  trips: AmbulanceTrip[];
  completed: number;
  cancelled: number;
  kilometres: number;
  medianDispatchMinutes: number | null;
  medianResponseMinutes: number | null;
}

export const ambulanceKeys = {
  all: ['ambulance'] as const,
  vehicles: () => ['ambulance', 'vehicles'] as const,
  active: () => ['ambulance', 'trips'] as const,
  log: (from: string, to: string) => ['ambulance', 'log', from, to] as const,
};

const base = '/api/v1/ambulance';

export const ambulanceQueries = {
  vehicles: () =>
    queryOptions({
      queryKey: ambulanceKeys.vehicles(),
      queryFn: ({ signal }) => api.get<AmbulanceVehicle[]>(`${base}/vehicles`, { signal }),
    }),
  active: () =>
    queryOptions({
      queryKey: ambulanceKeys.active(),
      queryFn: ({ signal }) => api.get<AmbulanceTrip[]>(`${base}/trips`, { signal }),
      refetchInterval: 30_000,
    }),
  log: (from: string, to: string) =>
    queryOptions({
      queryKey: ambulanceKeys.log(from, to),
      queryFn: ({ signal }) => api.get<TripLog>(`${base}/trips/log`, { query: { from, to }, signal }),
    }),
};

export const ambulanceApi = {
  saveVehicle: (request: SaveVehicleRequest) => api.post<AmbulanceVehicle[]>(`${base}/vehicles`, request),
  request: (request: RequestTripRequest) => api.post<AmbulanceTrip>(`${base}/trips`, request),
  dispatch: (id: string, vehicleId: string, driverName: string, crewName: string | null, version: number) =>
    api.post<AmbulanceTrip>(`${base}/trips/${id}/dispatch`, { vehicleId, driverName, crewName, version }),
  atScene: (id: string, version: number) => api.post<AmbulanceTrip>(`${base}/trips/${id}/at-scene`, { version }),
  leftScene: (id: string, version: number) => api.post<AmbulanceTrip>(`${base}/trips/${id}/left-scene`, { version }),
  complete: (id: string, endOdometerKm: number, waiveReason: string | null, version: number) =>
    api.post<AmbulanceTrip>(`${base}/trips/${id}/complete`, { endOdometerKm, waiveReason, version }),
  cancel: (id: string, reason: string, version: number) =>
    api.post<AmbulanceTrip>(`${base}/trips/${id}/cancel`, { reason, version }),
};

export const AMBULANCE_PERMISSIONS = {
  /** Take calls, send vehicles and record each trip (front office, nurses, administration). */
  dispatch: 'ambulance.dispatch',
  /** Keep the fleet and its certificates (administration). */
  fleet: 'ambulance.fleet',
} as const;

export const AMBULANCE_READERS = [AMBULANCE_PERMISSIONS.dispatch, AMBULANCE_PERMISSIONS.fleet] as const;

/** The vehicles a trip may be sent in, as the server checks: the right kind, in service, papers in force, not out. */
export function suits(kind: AmbulanceKind, purpose: TripPurpose): boolean {
  switch (purpose) {
    case 'EmergencyPickup':
      return kind === 'Bls' || kind === 'Als';
    case 'InterFacilityTransfer':
      return kind !== 'Hearse';
    case 'ScheduledTransport':
      return kind === 'Bls' || kind === 'PatientTransport';
    case 'BodyTransport':
      return kind === 'Hearse';
  }
}

export function availableFor(vehicles: AmbulanceVehicle[], purpose: TripPurpose): AmbulanceVehicle[] {
  return vehicles.filter((v) => v.isActive && !v.onTripId && v.lapsed.length === 0 && suits(v.kind, purpose));
}

/** What the trip would be charged: the base fare, and the kilometres beyond those it covers. */
export function chargedKm(startKm: number | null, endKm: number): { km: number; beyond: number } | null {
  if (startKm === null || !Number.isFinite(endKm) || endKm < startKm) {
    return null;
  }
  const km = endKm - startKm;
  return { km, beyond: Math.max(0, km - INCLUDED_KM) };
}
