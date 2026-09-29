import { describe, expect, it } from 'vitest';
import type { PatientDetail } from './api';
import { photoChange } from './photo';
import { emptyPatientForm, fromPatientDetail, patientFormSchema, toPatientPayload } from './validation';

function patient(photoFileId: string | null): PatientDetail {
  return {
    id: 'p-1',
    uhid: 'NH26000001',
    title: null,
    firstName: 'Asha',
    middleName: null,
    lastName: 'Rao',
    fullName: 'Asha Rao',
    gender: 'Female',
    dateOfBirth: '1990-01-15',
    isDateOfBirthEstimated: false,
    age: '36 y',
    bloodGroup: 'Unknown',
    maritalStatus: 'Unknown',
    phone: '9876543210',
    alternatePhone: null,
    email: null,
    address: { line1: null, line2: null, city: null, district: null, state: null, pinCode: null, country: 'India' },
    guardianName: null,
    guardianRelation: null,
    emergencyContactName: null,
    emergencyContactPhone: null,
    emergencyContactRelation: null,
    occupation: null,
    religion: null,
    nationality: 'Indian',
    preferredLanguage: null,
    category: 'General',
    abhaNumberMasked: null,
    abhaAddress: null,
    aadhaarLast4: null,
    photoFileId,
    registeredFacilityId: 'f-1',
    registeredAt: '2026-09-01T10:00:00Z',
    isVip: false,
    isDeceased: false,
    deceasedAt: null,
    mergedIntoPatientId: null,
    notes: null,
    isActive: true,
    version: 3,
  };
}

describe('the photo in the patient form', () => {
  it('starts empty for a new patient and follows the record when editing', () => {
    expect(emptyPatientForm.photoFileId).toBeNull();
    expect(fromPatientDetail(patient('photo-1')).photoFileId).toBe('photo-1');
    expect(fromPatientDetail(patient(null)).photoFileId).toBeNull();
  });

  it('sends a new patient the photo that was taken', () => {
    const values = { ...emptyPatientForm, firstName: 'Asha', gender: 'Female' as const, dateOfBirth: '1990-01-15', photoFileId: 'photo-1' };

    expect(toPatientPayload(values).photoFileId).toBe('photo-1');
    expect(toPatientPayload({ ...values, photoFileId: null }).photoFileId).toBeNull();
  });

  it('accepts a form with or without a photo', () => {
    const values = { ...emptyPatientForm, firstName: 'Asha', gender: 'Female' as const, dateOfBirth: '1990-01-15' };

    expect(patientFormSchema.safeParse(values).success).toBe(true);
    expect(patientFormSchema.safeParse({ ...values, photoFileId: 'photo-1' }).success).toBe(true);
  });

  it('does not clear the photo when the other details are edited and saved', () => {
    // The bug: the edit screen sent no photo, and the server took that to mean "remove it".
    const stored = patient('photo-1');
    const untouched = fromPatientDetail(stored);

    expect(photoChange(stored.photoFileId, untouched.photoFileId)).toEqual({ photoFileId: null, removePhoto: false });
  });

  it('replaces the photo, or removes it, only when the user does', () => {
    const stored = patient('photo-1');

    expect(photoChange(stored.photoFileId, 'photo-2')).toEqual({ photoFileId: 'photo-2', removePhoto: false });
    expect(photoChange(stored.photoFileId, null)).toEqual({ photoFileId: null, removePhoto: true });
  });
});
