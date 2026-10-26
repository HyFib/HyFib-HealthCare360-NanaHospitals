import { describe, expect, it } from 'vitest';
import { availableFor, chargedKm, type AmbulanceVehicle } from './api';

const vehicle = (over: Partial<AmbulanceVehicle>): AmbulanceVehicle => ({
  id: 'v',
  registrationNumber: 'TN 49 AX 2071',
  kind: 'Bls',
  fitnessValidUntil: '2027-01-01',
  insuranceValidUntil: '2027-01-01',
  pollutionValidUntil: '2027-01-01',
  odometerKm: 45200,
  isActive: true,
  lapsed: [],
  onTripId: null,
  version: 1,
  ...over,
});

describe('sending a vehicle', () => {
  it('offers only vehicles in service, with papers in force, not out, and suited to the trip', () => {
    const fleet = [
      vehicle({ id: 'bls' }),
      vehicle({ id: 'out', onTripId: 't' }),
      vehicle({ id: 'lapsed', lapsed: ['insurance'] }),
      vehicle({ id: 'pts', kind: 'PatientTransport' }),
      vehicle({ id: 'hearse', kind: 'Hearse' }),
    ];
    expect(availableFor(fleet, 'EmergencyPickup').map((v) => v.id)).toEqual(['bls']);
    expect(availableFor(fleet, 'BodyTransport').map((v) => v.id)).toEqual(['hearse']);
    expect(availableFor(fleet, 'InterFacilityTransfer').map((v) => v.id)).toEqual(['bls', 'pts']);
  });
});

describe('the charge', () => {
  it('covers the first 10 km in the base fare and counts the rest', () => {
    expect(chargedKm(45200, 45224)).toEqual({ km: 24, beyond: 14 });
    expect(chargedKm(45200, 45206)).toEqual({ km: 6, beyond: 0 });
    expect(chargedKm(45200, 45100)).toBeNull();
  });
});
