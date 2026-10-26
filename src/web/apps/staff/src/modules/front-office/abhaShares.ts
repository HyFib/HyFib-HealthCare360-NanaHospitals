import type { Gender } from '@hc360/core';
import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { emptyPatientForm, type PatientFormValues } from '@/modules/patients/validation';

/**
 * Scan and share (ABDM M1): patients who scanned the hospital's QR code with their ABHA app at a counter and shared
 * their ABHA profile. Each has a token number for the counter; the desk registers them with the form filled in from the
 * share, or opens the record the share matched, and takes the share off the queue.
 */
export type AbhaShareStatus = 'Waiting' | 'Registered' | 'Dismissed';

export interface AbhaSharePatient {
  id: string;
  uhid: string;
  fullName: string;
}

export interface AbhaShareRow {
  id: string;
  tokenNumber: number;
  counter: string;
  name: string;
  /** ABDM's code: M, F, O or U. */
  gender: string | null;
  yearOfBirth: number | null;
  abhaAddress: string;
  abhaNumberMasked: string | null;
  phoneMasked: string | null;
  status: AbhaShareStatus;
  sharedAt: string;
  expiresAt: string;
  patient: AbhaSharePatient | null;
  /** How the patient on record was matched: ABHA_ADDRESS, ABHA_NUMBER or MOBILE. */
  matchedBy: string | null;
}

export interface AbhaShareDetail {
  id: string;
  tokenNumber: number;
  counter: string;
  name: string;
  gender: string | null;
  yearOfBirth: number | null;
  monthOfBirth: number | null;
  dayOfBirth: number | null;
  abhaAddress: string;
  abhaNumber: string | null;
  phone: string | null;
  addressLine: string | null;
  district: string | null;
  state: string | null;
  pincode: string | null;
  status: AbhaShareStatus;
  patient: AbhaSharePatient | null;
  matchedBy: string | null;
}

export const abhaShareQueries = {
  today: () =>
    queryOptions({
      queryKey: ['abdm', 'shares'],
      queryFn: ({ signal }) => api.get<AbhaShareRow[]>('/api/v1/abdm/shares', { signal }),
      // Patients scan while they wait in line.
      refetchInterval: 15_000,
    }),
  detail: (id: string) =>
    queryOptions({
      queryKey: ['abdm', 'shares', id],
      queryFn: ({ signal }) => api.get<AbhaShareDetail>(`/api/v1/abdm/shares/${id}`, { signal }),
    }),
};

export const abhaShareApi = {
  registered: (id: string, patientId: string) =>
    api.post<AbhaShareRow>(`/api/v1/abdm/shares/${id}/registered`, { patientId }),
  dismiss: (id: string) => api.post<AbhaShareRow>(`/api/v1/abdm/shares/${id}/dismiss`),
};

/** The four digits the patient's app shows. */
export const tokenLabel = (tokenNumber: number) => String(tokenNumber).padStart(4, '0');

const GENDERS: Record<string, Gender> = { M: 'Male', F: 'Female', O: 'Other' };

/** ABDM's gender code as the form's gender, or empty for the desk to choose. */
export const genderOf = (code: string | null): Gender => (code && GENDERS[code]) || ('' as Gender);

const MATCHES: Record<string, string> = {
  ABHA_ADDRESS: 'ABHA address',
  ABHA_NUMBER: 'ABHA number',
  MOBILE: 'mobile number, name and age',
};

/** How the record was matched, in words. */
export const matchedByLabel = (matchedBy: string | null) => (matchedBy ? (MATCHES[matchedBy] ?? matchedBy) : null);

const pad = (n: number) => String(n).padStart(2, '0');

/**
 * The registration form filled in from a share: the name split into first and last, the date of birth when the ABHA
 * has all of it (else the age from the year), the mobile number without +91, the ABHA number and address, and the
 * address. The desk checks it with the patient before saving.
 */
export function formFromShare(share: AbhaShareDetail, today = new Date()): PatientFormValues {
  const [firstName = '', ...rest] = share.name.trim().split(/\s+/);
  const lastName = rest.pop() ?? '';
  const { yearOfBirth: year, monthOfBirth: month, dayOfBirth: day } = share;
  const fullDate = year && month && day ? `${year}-${pad(month)}-${pad(day)}` : '';
  const validDate = fullDate && !Number.isNaN(new Date(fullDate).getTime()) ? fullDate : '';
  const ageYears = !validDate && year ? Math.max(0, today.getFullYear() - year) : null;
  return {
    ...emptyPatientForm,
    firstName,
    middleName: rest.join(' '),
    lastName,
    gender: genderOf(share.gender),
    ageMode: validDate || !year ? 'dob' : 'age',
    dateOfBirth: validDate,
    ageYears,
    phone: share.phone?.replace(/^\+91/, '') ?? '',
    abhaNumber: share.abhaNumber ?? '',
    abhaAddress: share.abhaAddress,
    address: {
      ...emptyPatientForm.address,
      line1: share.addressLine ?? '',
      district: share.district ?? '',
      state: share.state ?? '',
      pinCode: share.pincode ?? '',
    },
  };
}
