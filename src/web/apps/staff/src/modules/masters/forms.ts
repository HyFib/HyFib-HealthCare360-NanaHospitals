import { z } from 'zod';
import { SERVICE_CATEGORIES } from '@/modules/billing/labels';
import type {
  DepartmentDto,
  FacilityDto,
  LetterheadPreviewRequest,
  PractitionerDto,
  SaveDepartmentRequest,
  SaveFacilityRequest,
  SavePractitionerRequest,
  SaveServiceItemRequest,
  SaveSpecialtyRequest,
  ServiceItemDto,
  SpecialtyDto,
} from './api';
import { DEPARTMENT_KINDS, GENDERS, isDoctor, PRACTITIONER_KINDS } from './labels';

/**
 * The masters' forms: their rules (the server's, so mistakes show beside the field before saving) and the conversion
 * between a record, the form's values and the save request. Form values are strings and numbers as typed; the request
 * sends empty text as null and codes in capitals.
 */

const CODE = /^[A-Za-z0-9][A-Za-z0-9._-]*$/;
const CODE_MESSAGE = 'Use letters, digits, dots or dashes, starting with a letter or digit.';
const GSTIN = /^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$/;
const PAN = /^[A-Z]{5}[0-9]{4}[A-Z]$/;
const PIN_CODE = /^[1-9][0-9]{5}$/;
const PHONE = /^\+?[0-9][0-9 -]{5,18}$/;
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
const SAC = /^[0-9]{4,8}$/;
const MONEY = /^\d+(\.\d{1,2})?$/;

export const MAX_FEE = 1_000_000;
export const MAX_PRICE = 10_000_000;

/** Codes are permanent: other records and screens refer to them. Said wherever a code is shown in an editor. */
export const PERMANENT_CODE_HINT = "A code can't be changed once it is saved.";

const text = (max: number) => z.string().trim().max(max, `Use at most ${max} characters.`);
const required = (max: number, message: string) => text(max).min(1, message);
const phone = z
  .string()
  .trim()
  .refine((v) => !v || PHONE.test(v), 'Enter a valid phone number.');
const email = text(256).refine((v) => !v || EMAIL.test(v), 'Enter a valid e-mail address.');
const money = (max: number, message: string) =>
  z
    .number({ error: message })
    .min(0, 'It cannot be negative.')
    .max(max, `Enter at most ${max.toLocaleString('en-IN')}.`)
    .refine((v) => MONEY.test(String(v)), 'Use at most two decimal places.');

/** "www.example.in" or a full http(s) address, as the server accepts. */
export function isWebAddress(value: string): boolean {
  const trimmed = value.trim();
  if (!trimmed) {
    return true;
  }
  if (/\s/.test(trimmed)) {
    return false;
  }
  try {
    const url = new URL(trimmed.includes('://') ? trimmed : `https://${trimmed}`);
    return (url.protocol === 'https:' || url.protocol === 'http:') && url.hostname.includes('.');
  } catch {
    return false;
  }
}

const orNull = (value: string): string | null => value.trim() || null;

/**
 * HPR ID (ABDM's Healthcare Professionals Registry): 14 digits, run together or grouped as 71-1234-5678-9012 with spaces
 * or hyphens (same rule as the server). It identifies the doctor in ABDM health records and NHCX claims.
 */
export function isValidHprId(value: string): boolean {
  const trimmed = value.trim();
  return /^[\d -]+$/.test(trimmed) && trimmed.replace(/\D/g, '').length === 14;
}

/**
 * Choices for a picker: the records in use, plus the one already chosen even when it has since been taken out of use (so
 * an older record still shows it, and can keep it).
 */
export function optionsWithCurrent(
  items: readonly { id: string; name: string; isActive: boolean }[] | undefined,
  currentId: string | null,
): { value: string; label: string }[] {
  return (items ?? [])
    .filter((item) => item.isActive || item.id === currentId)
    .map((item) => ({ value: item.id, label: item.isActive ? item.name : `${item.name} (not in use)` }));
}

/** A look-up's results with the current choice kept at the top when the search would leave it out. */
export function withCurrentItem<T extends { id: string }>(items: readonly T[], current: T | null, search: string): T[] {
  return current && !search.trim() && !items.some((item) => item.id === current.id) ? [current, ...items] : [...items];
}

/** Whether any of the texts contains the search term (case-insensitive); an empty term matches everything. */
export function matchesSearch(texts: readonly (string | null | undefined)[], term: string | undefined): boolean {
  const needle = term?.trim().toLowerCase() ?? '';
  return !needle || texts.some((text) => text?.toLowerCase().includes(needle));
}

// ------------------------------------------------------------------------------------------------ facilities

export const facilitySchema = z.object({
  code: z
    .string()
    .trim()
    .regex(/^[A-Za-z]{2,6}$/, 'Use 2–6 letters, such as NH. It starts every UHID and bill number.'),
  name: required(200, 'Enter the name printed on documents.'),
  legalName: text(200),
  line1: text(200),
  line2: text(200),
  city: text(100),
  district: text(100),
  state: text(100),
  pinCode: z
    .string()
    .trim()
    .refine((v) => !v || PIN_CODE.test(v), 'PIN code must be 6 digits.'),
  country: text(100),
  phone,
  email,
  website: text(256).refine(isWebAddress, 'Enter a web address such as www.nanahospitals.in.'),
  gstin: z
    .string()
    .trim()
    .refine((v) => !v || GSTIN.test(v.toUpperCase()), 'Enter a valid 15-character GSTIN.'),
  pan: z
    .string()
    .trim()
    .refine((v) => !v || PAN.test(v.toUpperCase()), 'Enter a valid PAN, such as ABCDE1234F.'),
  registrationNumber: text(64),
  nabhNumber: text(64),
  hfrId: text(64),
  letterheadNote: text(300),
  logoFileId: z.string().nullable(),
  isActive: z.boolean(),
});

export type FacilityForm = z.infer<typeof facilitySchema>;

export function toFacilityForm(facility: FacilityDto | null): FacilityForm {
  const address = facility?.address;
  return {
    code: facility?.code ?? '',
    name: facility?.name ?? '',
    legalName: facility?.legalName ?? '',
    line1: address?.line1 ?? '',
    line2: address?.line2 ?? '',
    city: address?.city ?? '',
    district: address?.district ?? '',
    state: address?.state ?? '',
    pinCode: address?.pinCode ?? '',
    country: address?.country ?? 'India',
    phone: facility?.phone ?? '',
    email: facility?.email ?? '',
    website: facility?.website ?? '',
    gstin: facility?.gstin ?? '',
    pan: facility?.pan ?? '',
    registrationNumber: facility?.registrationNumber ?? '',
    nabhNumber: facility?.nabhNumber ?? '',
    hfrId: facility?.hfrId ?? '',
    letterheadNote: facility?.letterheadNote ?? '',
    logoFileId: facility?.logoFileId ?? null,
    isActive: facility?.isActive ?? true,
  };
}

export function toLetterheadRequest(values: FacilityForm): LetterheadPreviewRequest {
  return {
    name: values.name.trim(),
    legalName: orNull(values.legalName),
    address: {
      line1: orNull(values.line1),
      line2: orNull(values.line2),
      city: orNull(values.city),
      district: orNull(values.district),
      state: orNull(values.state),
      pinCode: orNull(values.pinCode),
      country: orNull(values.country),
    },
    phone: orNull(values.phone),
    email: orNull(values.email),
    website: orNull(values.website),
    gstin: orNull(values.gstin)?.toUpperCase() ?? null,
    pan: orNull(values.pan)?.toUpperCase() ?? null,
    registrationNumber: orNull(values.registrationNumber),
    nabhNumber: orNull(values.nabhNumber),
    letterheadNote: orNull(values.letterheadNote),
    logoFileId: values.logoFileId,
  };
}

/**
 * `version` is the facility's version when the form was filled (null when adding), not the one on screen now: a change
 * saved meanwhile by someone else is then refused rather than overwritten (REL-04). The other masters' requests take it
 * the same way.
 */
export function toFacilityRequest(
  values: FacilityForm,
  existing: Pick<FacilityDto, 'code'> | null,
  version: number | null,
): SaveFacilityRequest {
  return {
    ...toLetterheadRequest(values),
    code: existing?.code ?? values.code.trim().toUpperCase(),
    hfrId: orNull(values.hfrId),
    isActive: values.isActive,
    version,
  };
}

// ------------------------------------------------------------------------------------------------ departments

export const departmentSchema = z.object({
  code: required(16, 'Enter a short code, such as GM.').regex(/^[A-Za-z0-9-]+$/, 'Use letters, digits or dashes.'),
  name: required(128, 'Enter the department name.'),
  kind: z.enum(DEPARTMENT_KINDS),
  specialtyId: z.string().nullable(),
  headPractitionerId: z.string().nullable(),
  location: text(128),
  phone,
  tokenPrefix: z
    .string()
    .trim()
    .regex(/^[A-Za-z]{0,4}$/, 'Use up to 4 letters.'),
  displayOrder: z
    .number({ error: 'Enter a number from 0 to 9999.' })
    .int('Use a whole number.')
    .min(0, 'Enter a number from 0 to 9999.')
    .max(9999, 'Enter a number from 0 to 9999.'),
  isActive: z.boolean(),
});

export type DepartmentForm = z.infer<typeof departmentSchema>;

export function toDepartmentForm(department: DepartmentDto | null, nextOrder = 10): DepartmentForm {
  return {
    code: department?.code ?? '',
    name: department?.name ?? '',
    kind: department?.kind ?? 'Clinical',
    specialtyId: department?.specialtyId ?? null,
    headPractitionerId: department?.headPractitionerId ?? null,
    location: department?.location ?? '',
    phone: department?.phone ?? '',
    tokenPrefix: department?.tokenPrefix ?? '',
    displayOrder: department?.displayOrder ?? nextOrder,
    isActive: department?.isActive ?? true,
  };
}

export function toDepartmentRequest(
  values: DepartmentForm,
  existing: Pick<DepartmentDto, 'code'> | null,
  version: number | null,
): SaveDepartmentRequest {
  return {
    code: existing?.code ?? values.code.trim().toUpperCase(),
    name: values.name.trim(),
    kind: values.kind,
    specialtyId: values.specialtyId,
    headPractitionerId: values.headPractitionerId,
    location: orNull(values.location),
    phone: orNull(values.phone),
    tokenPrefix: orNull(values.tokenPrefix)?.toUpperCase() ?? null,
    displayOrder: values.displayOrder,
    isActive: values.isActive,
    version,
  };
}

/** The next free display order after the departments listed, in steps of ten so one can be slotted in between. */
export function nextDisplayOrder(departments: readonly Pick<DepartmentDto, 'displayOrder'>[]): number {
  const highest = departments.reduce((max, d) => Math.max(max, d.displayOrder), 0);
  return Math.min(9999, (Math.floor(highest / 10) + 1) * 10);
}

// ------------------------------------------------------------------------------------------------ specialities

export const specialtySchema = z.object({
  code: required(16, 'Enter a short code, such as CARDIO.').regex(CODE, CODE_MESSAGE),
  name: required(128, 'Enter the speciality.'),
  description: text(500),
  isActive: z.boolean(),
});

export type SpecialtyForm = z.infer<typeof specialtySchema>;

export function toSpecialtyForm(specialty: SpecialtyDto | null): SpecialtyForm {
  return {
    code: specialty?.code ?? '',
    name: specialty?.name ?? '',
    description: specialty?.description ?? '',
    isActive: specialty?.isActive ?? true,
  };
}

export function toSpecialtyRequest(
  values: SpecialtyForm,
  existing: Pick<SpecialtyDto, 'code'> | null,
  version: number | null,
): SaveSpecialtyRequest {
  return {
    code: existing?.code ?? values.code.trim().toUpperCase(),
    name: values.name.trim(),
    description: orNull(values.description),
    isActive: values.isActive,
    version,
  };
}

// ------------------------------------------------------------------------------------------------ practitioners

/** "English, Tamil, Hindi" → the languages, each once. */
export function splitLanguages(value: string): string[] {
  const seen = new Set<string>();
  return value
    .split(/[,;]/)
    .map((language) => language.trim())
    .filter((language) => {
      const key = language.toLowerCase();
      if (!language || seen.has(key)) {
        return false;
      }
      seen.add(key);
      return true;
    });
}

export const practitionerSchema = z
  .object({
    code: required(16, 'Enter a short code, such as DR012.').regex(CODE, CODE_MESSAGE),
    // The title picker offers "None", which reads as null.
    title: text(16).nullable(),
    firstName: required(100, 'Enter the first name.'),
    lastName: text(100),
    gender: z.enum(GENDERS),
    kind: z.enum(PRACTITIONER_KINDS),
    qualification: text(200),
    registrationCouncil: text(128),
    registrationNumber: text(64),
    hprId: z
      .string()
      .trim()
      .refine((v) => !v || isValidHprId(v), 'Enter a valid 14-digit HPR ID.'),
    specialtyId: z.string().nullable(),
    departmentId: z.string().nullable(),
    phone,
    email,
    consultationFeeNew: money(MAX_FEE, 'Enter the fee (0 if there is none).'),
    consultationFeeFollowUp: money(MAX_FEE, 'Enter the fee (0 if there is none).'),
    followUpValidityDays: z
      .number({ error: 'Enter the days, from 0 to 90.' })
      .int('Use a whole number of days.')
      .min(0, 'Enter the days, from 0 to 90.')
      .max(90, 'Enter the days, from 0 to 90.'),
    languages: text(600)
      .refine((v) => splitLanguages(v).length <= 12, 'List at most 12 languages.')
      .refine((v) => splitLanguages(v).every((l) => l.length <= 40), 'Keep each language under 40 characters.'),
    bio: text(2000),
    photoFileId: z.string().nullable(),
    signatureFileId: z.string().nullable(),
    availableForOnlineBooking: z.boolean(),
    availableForTelehealth: z.boolean(),
    allFacilities: z.boolean(),
    facilityIds: z.array(z.string()),
    isActive: z.boolean(),
  })
  .superRefine((value, ctx) => {
    if (isDoctor(value.kind) && !value.registrationNumber.trim()) {
      ctx.addIssue({
        code: 'custom',
        path: ['registrationNumber'],
        message: 'Enter the medical council registration number; doctors need it to consult and sign.',
      });
    }
    if (!value.allFacilities && value.facilityIds.length === 0) {
      ctx.addIssue({ code: 'custom', path: ['facilityIds'], message: 'Choose at least one facility.' });
    }
  });

export type PractitionerForm = z.infer<typeof practitionerSchema>;

export function toPractitionerForm(practitioner: PractitionerDto | null): PractitionerForm {
  return {
    code: practitioner?.code ?? '',
    title: practitioner ? practitioner.title || null : 'Dr.',
    firstName: practitioner?.firstName ?? '',
    lastName: practitioner?.lastName ?? '',
    gender: practitioner?.gender ?? 'Unknown',
    kind: practitioner?.kind ?? 'Consultant',
    qualification: practitioner?.qualification ?? '',
    registrationCouncil: practitioner?.registrationCouncil ?? '',
    registrationNumber: practitioner?.registrationNumber ?? '',
    hprId: practitioner?.hprId ?? '',
    specialtyId: practitioner?.specialtyId ?? null,
    departmentId: practitioner?.departmentId ?? null,
    phone: practitioner?.phone ?? '',
    email: practitioner?.email ?? '',
    consultationFeeNew: practitioner?.consultationFeeNew ?? 0,
    consultationFeeFollowUp: practitioner?.consultationFeeFollowUp ?? 0,
    followUpValidityDays: practitioner?.followUpValidityDays ?? 7,
    languages: practitioner?.languages.join(', ') ?? '',
    bio: practitioner?.bio ?? '',
    photoFileId: practitioner?.photoFileId ?? null,
    signatureFileId: practitioner?.signatureFileId ?? null,
    availableForOnlineBooking: practitioner?.availableForOnlineBooking ?? false,
    availableForTelehealth: practitioner?.availableForTelehealth ?? false,
    allFacilities: (practitioner?.facilityIds.length ?? 0) === 0,
    facilityIds: practitioner?.facilityIds ?? [],
    isActive: practitioner?.isActive ?? true,
  };
}

export function toPractitionerRequest(
  values: PractitionerForm,
  existing: Pick<PractitionerDto, 'code'> | null,
  version: number | null,
): SavePractitionerRequest {
  return {
    code: existing?.code ?? values.code.trim().toUpperCase(),
    title: values.title?.trim() ?? '',
    firstName: values.firstName.trim(),
    lastName: orNull(values.lastName),
    gender: values.gender,
    kind: values.kind,
    qualification: orNull(values.qualification),
    registrationCouncil: orNull(values.registrationCouncil),
    registrationNumber: orNull(values.registrationNumber),
    hprId: orNull(values.hprId),
    specialtyId: values.specialtyId,
    departmentId: values.departmentId,
    phone: orNull(values.phone),
    email: orNull(values.email),
    consultationFeeNew: values.consultationFeeNew,
    consultationFeeFollowUp: values.consultationFeeFollowUp,
    followUpValidityDays: values.followUpValidityDays,
    languages: splitLanguages(values.languages),
    bio: orNull(values.bio),
    photoFileId: values.photoFileId,
    signatureFileId: values.signatureFileId,
    availableForOnlineBooking: values.availableForOnlineBooking,
    availableForTelehealth: values.availableForTelehealth,
    facilityIds: values.allFacilities ? [] : values.facilityIds,
    isActive: values.isActive,
    version,
  };
}

/** How the practitioner's name will read on lists and documents. */
export function displayNameOf(values: Pick<PractitionerForm, 'title' | 'firstName' | 'lastName'>): string {
  return [values.title ?? '', values.firstName, values.lastName]
    .map((part) => part.trim())
    .filter(Boolean)
    .join(' ');
}

// ------------------------------------------------------------------------------------------------ services

export const serviceSchema = z.object({
  code: required(32, 'Enter a code, such as LAB-CBC.').regex(CODE, CODE_MESSAGE),
  name: required(200, 'Enter the service name printed on bills.'),
  localName: text(200),
  shortName: text(64),
  category: z.enum(SERVICE_CATEGORIES),
  departmentId: z.string().nullable(),
  basePrice: money(MAX_PRICE, 'Enter the price (0 if there is no charge).'),
  taxRate: z.string().refine((v) => {
    const rate = Number(v);
    return v !== '' && Number.isFinite(rate) && rate >= 0 && rate <= 28;
  }, 'Choose the GST rate.'),
  sacCode: z
    .string()
    .trim()
    .refine((v) => !v || SAC.test(v), 'Enter the 4–8 digit SAC or HSN code.'),
  isDiscountAllowed: z.boolean(),
  isPriceEditable: z.boolean(),
  description: text(1000),
  isActive: z.boolean(),
});

export type ServiceForm = z.infer<typeof serviceSchema>;

export function toServiceForm(service: ServiceItemDto | null): ServiceForm {
  return {
    code: service?.code ?? '',
    name: service?.name ?? '',
    localName: service?.localName ?? '',
    shortName: service?.shortName ?? '',
    category: service?.category ?? 'Procedure',
    departmentId: service?.departmentId ?? null,
    basePrice: service?.basePrice ?? 0,
    taxRate: String(service?.taxRate ?? 0),
    sacCode: service?.sacCode ?? '999312',
    isDiscountAllowed: service?.isDiscountAllowed ?? true,
    isPriceEditable: service?.isPriceEditable ?? false,
    description: service?.description ?? '',
    isActive: service?.isActive ?? true,
  };
}

export function toServiceRequest(
  values: ServiceForm,
  existing: Pick<ServiceItemDto, 'code'> | null,
  version: number | null,
): SaveServiceItemRequest {
  return {
    code: existing?.code ?? values.code.trim().toUpperCase(),
    name: values.name.trim(),
    localName: orNull(values.localName),
    shortName: orNull(values.shortName),
    category: values.category,
    departmentId: values.departmentId,
    basePrice: values.basePrice,
    taxRate: Number(values.taxRate),
    sacCode: orNull(values.sacCode),
    isDiscountAllowed: values.isDiscountAllowed,
    isPriceEditable: values.isPriceEditable,
    description: orNull(values.description),
    isActive: values.isActive,
    version,
  };
}
