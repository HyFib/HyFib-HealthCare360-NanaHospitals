import { describe, expect, it } from 'vitest';
import type { PatientDetail } from './api';
import { photoChange } from './photo';
import {
  emptyPatientForm,
  fromPatientDetail,
  isValidAadhaar,
  isValidAbhaAddress,
  isValidAbhaNumber,
  patientFormSchema,
  toPatientPayload,
} from './validation';

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
    const values = {
      ...emptyPatientForm,
      firstName: 'Asha',
      gender: 'Female' as const,
      dateOfBirth: '1990-01-15',
      photoFileId: 'photo-1',
    };

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

describe('ABHA details in the patient form (SRS EHR-08)', () => {
  const valid = { ...emptyPatientForm, firstName: 'Meera', gender: 'Female' as const, dateOfBirth: '1984-04-12' };

  it('accepts any 14-digit ABHA number, grouped or run together', () => {
    expect(isValidAbhaNumber('91123456789010')).toBe(true);
    expect(isValidAbhaNumber('91-1234-5678-9010')).toBe(true);
    expect(isValidAbhaNumber(' 91 1234 5678 9010 ')).toBe(true);
  });

  it('does not ask for a check digit, which ABDM does not publish', () => {
    // The ABHA number of the example patient in the NRCeS implementation guide for ABDM (ndhm.in 6.5.0), and the HPR
    // ids of its example doctors, which ABDM issues in the same 14-digit form: none ends in a Verhoeff check digit, and
    // the form must take such numbers.
    expect(isValidAbhaNumber('22-7225-4829-5255')).toBe(true);
    expect(isValidAbhaNumber('21-1521-3828-3227')).toBe(true);
    expect(isValidAbhaNumber('25-1531-3528-3228')).toBe(true);
    for (const last of '0123456789') {
      expect(isValidAbhaNumber(`91-1234-5678-901${last}`)).toBe(true);
    }
  });

  it('refuses a wrong length and stray characters', () => {
    expect(isValidAbhaNumber('9112345678901')).toBe(false); // 13 digits
    expect(isValidAbhaNumber('911234567890100')).toBe(false); // 15 digits
    expect(isValidAbhaNumber('91/1234/5678/9010')).toBe(false);
    expect(isValidAbhaNumber('91-1234-5678-901O')).toBe(false); // a letter O
  });

  it('accepts ABHA addresses at @abdm and the @sbx sandbox only', () => {
    expect(isValidAbhaAddress('meera.k@abdm')).toBe(true);
    expect(isValidAbhaAddress('Meera_K@ABDM')).toBe(true);
    expect(isValidAbhaAddress('meera1984@sbx')).toBe(true);
    expect(isValidAbhaAddress('meera.k@gmail.com')).toBe(false);
    expect(isValidAbhaAddress('.meera@abdm')).toBe(false);
    expect(isValidAbhaAddress('meera..k@abdm')).toBe(false);
    expect(isValidAbhaAddress('me@abdm')).toBe(false);
    expect(isValidAbhaAddress('meera.k')).toBe(false);
  });

  it('keeps the Aadhaar rule as it was', () => {
    expect(isValidAadhaar('2345 6789 0124')).toBe(true);
    expect(isValidAadhaar('2345 6789 0123')).toBe(false);
  });

  it('shows the message on the field that is wrong, and leaves blank fields alone', () => {
    expect(patientFormSchema.safeParse(valid).success).toBe(true);
    expect(
      patientFormSchema.safeParse({ ...valid, abhaNumber: '91-1234-5678-9010', abhaAddress: 'meera.k@abdm' }).success,
    ).toBe(true);

    expect(patientFormSchema.safeParse({ ...valid, abhaNumber: '22-7225-4829-5255' }).success).toBe(true);

    const number = patientFormSchema.safeParse({ ...valid, abhaNumber: '91-1234-5678-901' });
    expect(number.success).toBe(false);
    expect(number.error?.issues.map((i) => i.path.join('.'))).toEqual(['abhaNumber']);
    expect(number.error?.issues[0]?.message).toBe('Enter a valid 14-digit ABHA number.');

    const address = patientFormSchema.safeParse({ ...valid, abhaAddress: 'meera.k@gmail.com' });
    expect(address.error?.issues.map((i) => i.path.join('.'))).toEqual(['abhaAddress']);
  });

  it('lets a record whose ABHA address predates the check be edited, and checks it once it is changed', () => {
    // An address saved before the format check, such as an old @ndhm handle, comes back with every edit.
    const editing = { ...fromPatientDetail({ ...patient(null), abhaAddress: 'meera.k@ndhm' }), phone: '9876500000' };
    expect(editing.abhaAddressOnFile).toBe('meera.k@ndhm');
    expect(patientFormSchema.safeParse(editing).success).toBe(true);
    expect(patientFormSchema.safeParse({ ...editing, abhaAddress: 'MEERA.K@NDHM' }).success).toBe(true);
    expect(patientFormSchema.safeParse({ ...editing, abhaAddress: '' }).success).toBe(true);
    expect(patientFormSchema.safeParse({ ...editing, abhaAddress: 'meera.rao@abdm' }).success).toBe(true);

    const changed = patientFormSchema.safeParse({ ...editing, abhaAddress: 'meera.rao@ndhm' });
    expect(changed.error?.issues.map((i) => i.path.join('.'))).toEqual(['abhaAddress']);
    expect(changed.error?.issues[0]?.message).toBe(
      'Enter the ABHA address as name@abdm (letters, digits, and single dots or underscores).',
    );
    expect(toPatientPayload(editing)).not.toHaveProperty('abhaAddressOnFile');
  });

  it('shows every wrong field at once, the ABHA address among them', () => {
    const result = patientFormSchema.safeParse({ ...valid, phone: '12', abhaAddress: 'meera.k@gmail.com' });
    expect(result.error?.issues.map((i) => i.path.join('.')).sort()).toEqual(['abhaAddress', 'phone']);
  });
});
