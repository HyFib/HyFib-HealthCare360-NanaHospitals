import type { AmbulanceTrip, AmbulanceVehicle, TripLog } from '@/modules/ambulance/api';
import type { FakeAnswers } from '../fakeApi';

// Ambulance: a BLS ambulance out on an emergency, a hearse with a lapsed pollution certificate, and today's log.

const id = (suffix: string) => `0192a000-0000-7000-8000-${suffix.padStart(12, '0')}`;
const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString();

const VEHICLES: AmbulanceVehicle[] = [
  {
    id: id('a1'),
    registrationNumber: 'TN 49 AX 2071',
    kind: 'Bls',
    fitnessValidUntil: '2027-03-31',
    insuranceValidUntil: '2027-01-15',
    pollutionValidUntil: '2026-12-31',
    odometerKm: 45200,
    isActive: true,
    lapsed: [],
    onTripId: id('t1'),
    version: 3,
  },
  {
    id: id('a2'),
    registrationNumber: 'TN 49 BH 1188',
    kind: 'Hearse',
    fitnessValidUntil: '2027-03-31',
    insuranceValidUntil: '2027-01-15',
    pollutionValidUntil: '2026-09-30',
    odometerKm: 81230,
    isActive: true,
    lapsed: ['pollution certificate'],
    onTripId: null,
    version: 1,
  },
];

const TRIP: AmbulanceTrip = {
  id: id('t1'),
  number: 'NHAT26000041',
  purpose: 'EmergencyPickup',
  patientId: null,
  uhid: null,
  patientName: 'Unknown, road accident',
  callerName: 'Passer-by',
  callerPhone: '9840099999',
  pickupAddress: 'NH 36 near Thiruvidaimaruthur',
  dropAddress: null,
  complaint: 'Two-wheeler accident, head injury',
  requestedAt: minutesAgo(25),
  requestedByName: 'Front office',
  status: 'AtScene',
  vehicleNumber: 'TN 49 AX 2071',
  vehicleKind: 'Bls',
  driverName: 'Raju',
  crewName: 'EMT Priya',
  dispatchedAt: minutesAgo(23),
  atSceneAt: minutesAgo(8),
  leftSceneAt: null,
  completedAt: null,
  startOdometerKm: 45200,
  endOdometerKm: null,
  kilometres: null,
  responseMinutes: 17,
  chargeWaivedReason: null,
  cancelReason: null,
  version: 3,
};

const LOG: TripLog = {
  from: '2026-10-25',
  to: '2026-10-25',
  trips: [TRIP],
  completed: 0,
  cancelled: 0,
  kilometres: 0,
  medianDispatchMinutes: 2,
  medianResponseMinutes: 17,
};

export const ANSWERS: FakeAnswers = {
  'GET /api/v1/ambulance/vehicles': VEHICLES,
  'GET /api/v1/ambulance/trips': [TRIP],
  'GET /api/v1/ambulance/trips/log': LOG,
};
