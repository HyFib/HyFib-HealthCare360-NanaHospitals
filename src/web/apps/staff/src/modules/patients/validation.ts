import { normalizePhone, todayIso, type Address, type BloodGroup, type Gender, type MaritalStatus } from '@hc360/core';
import { z } from 'zod';
import type { GuardianRelation, PatientCategory, PatientDetail, PatientFormPayload } from './api';

export const TITLES = ['Mr', 'Mrs', 'Ms', 'Master', 'Baby', 'Dr'] as const;

export const LANGUAGES = [
  'English',
  'Hindi',
  'Tamil',
  'Telugu',
  'Kannada',
  'Malayalam',
  'Marathi',
  'Bengali',
  'Gujarati',
  'Punjabi',
  'Odia',
  'Urdu',
  'Assamese',
] as const;

export const INDIAN_STATES = [
  'Andaman and Nicobar Islands',
  'Andhra Pradesh',
  'Arunachal Pradesh',
  'Assam',
  'Bihar',
  'Chandigarh',
  'Chhattisgarh',
  'Dadra and Nagar Haveli and Daman and Diu',
  'Delhi',
  'Goa',
  'Gujarat',
  'Haryana',
  'Himachal Pradesh',
  'Jammu and Kashmir',
  'Jharkhand',
  'Karnataka',
  'Kerala',
  'Ladakh',
  'Lakshadweep',
  'Madhya Pradesh',
  'Maharashtra',
  'Manipur',
  'Meghalaya',
  'Mizoram',
  'Nagaland',
  'Odisha',
  'Puducherry',
  'Punjab',
  'Rajasthan',
  'Sikkim',
  'Tamil Nadu',
  'Telangana',
  'Tripura',
  'Uttar Pradesh',
  'Uttarakhand',
  'West Bengal',
] as const;

const D = [
  [0, 1, 2, 3, 4, 5, 6, 7, 8, 9],
  [1, 2, 3, 4, 0, 6, 7, 8, 9, 5],
  [2, 3, 4, 0, 1, 7, 8, 9, 5, 6],
  [3, 4, 0, 1, 2, 8, 9, 5, 6, 7],
  [4, 0, 1, 2, 3, 9, 5, 6, 7, 8],
  [5, 9, 8, 7, 6, 0, 4, 3, 2, 1],
  [6, 5, 9, 8, 7, 1, 0, 4, 3, 2],
  [7, 6, 5, 9, 8, 2, 1, 0, 4, 3],
  [8, 7, 6, 5, 9, 3, 2, 1, 0, 4],
  [9, 8, 7, 6, 5, 4, 3, 2, 1, 0],
];
const P = [
  [0, 1, 2, 3, 4, 5, 6, 7, 8, 9],
  [1, 5, 7, 6, 2, 8, 3, 0, 9, 4],
  [5, 8, 0, 3, 7, 9, 6, 1, 4, 2],
  [8, 9, 1, 6, 0, 4, 3, 5, 2, 7],
  [9, 4, 5, 3, 1, 2, 6, 8, 7, 0],
  [4, 2, 8, 6, 5, 7, 3, 9, 0, 1],
  [2, 7, 9, 3, 8, 0, 6, 4, 1, 5],
  [7, 0, 4, 6, 9, 1, 3, 2, 5, 8],
];

/** Aadhaar: 12 digits, first digit 2–9, valid Verhoeff checksum (same rule as the server). */
export function isValidAadhaar(value: string): boolean {
  const digits = value.replace(/\D/g, '');
  if (!/^[2-9]\d{11}$/.test(digits)) {
    return false;
  }
  let check = 0;
  [...digits].reverse().forEach((ch, i) => {
    check = D[check]![P[i % 8]![Number(ch)]!]!;
  });
  return check === 0;
}

const NAME = /^[\p{L}\p{M} .'-]*$/u;
const phone = z
  .string()
  .trim()
  .refine((v) => !v || normalizePhone(v) !== null, 'Enter a valid phone number.');

export const patientFormSchema = z
  .object({
    title: z.string(),
    firstName: z
      .string()
      .trim()
      .min(1, 'First name is required.')
      .max(100, 'Use at most 100 characters.')
      .regex(NAME, 'Use letters, spaces, dots, apostrophes or hyphens.'),
    middleName: z.string().trim().max(100).regex(NAME, 'Use letters only.'),
    lastName: z.string().trim().max(100).regex(NAME, 'Use letters only.'),
    gender: z.enum(['Male', 'Female', 'Other', 'Unknown'], { error: 'Choose the gender.' }),
    ageMode: z.enum(['dob', 'age']),
    dateOfBirth: z.string(),
    ageYears: z.number().int('Whole years only.').min(0).max(130, 'Check the age.').nullable(),
    ageMonths: z.number().int('Whole months only.').min(0).max(11, 'Months must be 0 to 11.').nullable(),
    bloodGroup: z.string(),
    maritalStatus: z.string(),
    phone,
    alternatePhone: phone,
    email: z
      .string()
      .trim()
      .max(256)
      .refine((v) => !v || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v), 'Enter a valid email address.'),
    preferredLanguage: z.string(),
    address: z.object({
      line1: z.string().trim().max(200),
      line2: z.string().trim().max(200),
      city: z.string().trim().max(100),
      district: z.string().trim().max(100),
      state: z.string(),
      pinCode: z
        .string()
        .trim()
        .refine((v) => !v || /^[1-9]\d{5}$/.test(v), 'PIN code must be 6 digits.'),
    }),
    guardianRelation: z.string(),
    guardianName: z.string().trim().max(200).regex(NAME, 'Use letters only.'),
    emergencyContactName: z.string().trim().max(200),
    emergencyContactPhone: phone,
    emergencyContactRelation: z.string().trim().max(64),
    occupation: z.string().trim().max(100),
    religion: z.string().trim().max(64),
    nationality: z.string().trim().max(64),
    category: z.string(),
    abhaNumber: z
      .string()
      .trim()
      .refine((v) => !v || v.replace(/\D/g, '').length === 14, 'ABHA number must have 14 digits.'),
    abhaAddress: z.string().trim().max(128),
    aadhaarNumber: z
      .string()
      .trim()
      .refine((v) => !v || isValidAadhaar(v), 'Enter a valid 12-digit Aadhaar number.'),
    isVip: z.boolean(),
    notes: z.string().max(2000, 'Use at most 2000 characters.'),
  })
  .superRefine((v, ctx) => {
    if (v.ageMode === 'dob') {
      if (!v.dateOfBirth) {
        ctx.addIssue({ code: 'custom', path: ['dateOfBirth'], message: 'Enter the date of birth, or switch to "Age only".' });
      } else if (v.dateOfBirth > todayIso() || v.dateOfBirth < '1900-01-01') {
        ctx.addIssue({ code: 'custom', path: ['dateOfBirth'], message: 'Enter a valid date of birth.' });
      }
    } else if (v.ageYears === null && v.ageMonths === null) {
      ctx.addIssue({ code: 'custom', path: ['ageYears'], message: 'Enter the age in years (and months for infants).' });
    }
  });

export type PatientFormValues = z.infer<typeof patientFormSchema>;

export const emptyPatientForm: PatientFormValues = {
  title: '',
  firstName: '',
  middleName: '',
  lastName: '',
  gender: '' as Gender,
  ageMode: 'dob',
  dateOfBirth: '',
  ageYears: null,
  ageMonths: null,
  bloodGroup: '',
  maritalStatus: '',
  phone: '',
  alternatePhone: '',
  email: '',
  preferredLanguage: '',
  address: { line1: '', line2: '', city: '', district: '', state: '', pinCode: '' },
  guardianRelation: '',
  guardianName: '',
  emergencyContactName: '',
  emergencyContactPhone: '',
  emergencyContactRelation: '',
  occupation: '',
  religion: '',
  nationality: 'Indian',
  category: 'General',
  abhaNumber: '',
  abhaAddress: '',
  aadhaarNumber: '',
  isVip: false,
  notes: '',
};

const orNull = (value: string): string | null => (value.trim() ? value.trim() : null);

/** Form values → API payload (empty strings become null; either a DOB or an age is sent). */
export function toPatientPayload(values: PatientFormValues): PatientFormPayload {
  const address = values.address;
  const hasAddress = Object.values(address).some((part) => part.trim() !== '');
  return {
    title: orNull(values.title),
    firstName: values.firstName.trim(),
    middleName: orNull(values.middleName),
    lastName: orNull(values.lastName),
    gender: values.gender,
    dateOfBirth: values.ageMode === 'dob' ? orNull(values.dateOfBirth) : null,
    ageYears: values.ageMode === 'age' ? values.ageYears : null,
    ageMonths: values.ageMode === 'age' ? values.ageMonths : null,
    phone: orNull(values.phone),
    alternatePhone: orNull(values.alternatePhone),
    email: orNull(values.email),
    address: hasAddress
      ? ({
          line1: orNull(address.line1),
          line2: orNull(address.line2),
          city: orNull(address.city),
          district: orNull(address.district),
          state: orNull(address.state),
          pinCode: orNull(address.pinCode),
          country: 'India',
        } as Address)
      : null,
    bloodGroup: (orNull(values.bloodGroup) as BloodGroup | null) ?? null,
    maritalStatus: (orNull(values.maritalStatus) as MaritalStatus | null) ?? null,
    guardianName: orNull(values.guardianName),
    guardianRelation: (orNull(values.guardianRelation) as GuardianRelation | null) ?? null,
    emergencyContactName: orNull(values.emergencyContactName),
    emergencyContactPhone: orNull(values.emergencyContactPhone),
    emergencyContactRelation: orNull(values.emergencyContactRelation),
    occupation: orNull(values.occupation),
    religion: orNull(values.religion),
    nationality: orNull(values.nationality),
    preferredLanguage: orNull(values.preferredLanguage),
    category: (orNull(values.category) as PatientCategory | null) ?? null,
    abhaNumber: orNull(values.abhaNumber),
    abhaAddress: orNull(values.abhaAddress),
    aadhaarNumber: orNull(values.aadhaarNumber),
    photoFileId: null,
    isVip: values.isVip,
    notes: orNull(values.notes),
  };
}

/** Existing record → form values. Identifiers are write-only (masked on read), so they start empty. */
export function fromPatientDetail(p: PatientDetail): PatientFormValues {
  return {
    ...emptyPatientForm,
    title: p.title ?? '',
    firstName: p.firstName,
    middleName: p.middleName ?? '',
    lastName: p.lastName ?? '',
    gender: p.gender,
    ageMode: 'dob',
    dateOfBirth: p.dateOfBirth ?? '',
    bloodGroup: p.bloodGroup === 'Unknown' ? '' : p.bloodGroup,
    maritalStatus: p.maritalStatus === 'Unknown' ? '' : p.maritalStatus,
    phone: p.phone ?? '',
    alternatePhone: p.alternatePhone ?? '',
    email: p.email ?? '',
    preferredLanguage: p.preferredLanguage ?? '',
    address: {
      line1: p.address.line1 ?? '',
      line2: p.address.line2 ?? '',
      city: p.address.city ?? '',
      district: p.address.district ?? '',
      state: p.address.state ?? '',
      pinCode: p.address.pinCode ?? '',
    },
    guardianRelation: p.guardianRelation ?? '',
    guardianName: p.guardianName ?? '',
    emergencyContactName: p.emergencyContactName ?? '',
    emergencyContactPhone: p.emergencyContactPhone ?? '',
    emergencyContactRelation: p.emergencyContactRelation ?? '',
    occupation: p.occupation ?? '',
    religion: p.religion ?? '',
    nationality: p.nationality ?? '',
    category: p.category,
    abhaAddress: p.abhaAddress ?? '',
    isVip: p.isVip,
    notes: p.notes ?? '',
  };
}
