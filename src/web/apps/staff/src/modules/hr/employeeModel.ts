/**
 * Employee form model: zod rules mirroring the server validators, defaults, and mapping to/from the API records.
 * Bank details and PAN are never kept in drafts or shown again after saving (the server stores them encrypted).
 */
import { normalizePhone, todayIso } from '@hc360/core';
import { z } from 'zod';
import type { EmployeeDetail, SaveEmployeeRequest } from './api';
import { EDITABLE_EMPLOYEE_STATUSES, EMPLOYMENT_TYPES, PAYMENT_MODES, WEEKDAYS } from './labels';

/** Upper-case letters and digits only (the server's normalisation for PAN, IFSC, UAN…). */
export function normalizeIdentifier(value: string): string {
  return value.toUpperCase().replace(/[^A-Z0-9]/g, '');
}

export const PAN = /^[A-Z]{5}[0-9]{4}[A-Z]$/;
export const IFSC = /^[A-Z]{4}0[A-Z0-9]{6}$/;
const NAME = /^[\p{L} .'-]*$/u;
const PIN_CODE = /^[1-9][0-9]{5}$/;
const TERMINAL_PIN = /^[A-Za-z0-9]{1,24}$/;
const PF_NUMBER = /^[A-Za-z0-9/-]{1,32}$/;

export const isValidPan = (value: string) => PAN.test(normalizeIdentifier(value));
export const isValidIfsc = (value: string) => IFSC.test(normalizeIdentifier(value));
export const isValidAccount = (value: string) => /^\d{9,18}$/.test(value.replace(/[\s-]/g, ''));
export const isValidUan = (value: string) => /^\d{12}$/.test(normalizeIdentifier(value));
export const isValidEsi = (value: string) => /^(\d{10}|\d{17})$/.test(normalizeIdentifier(value));

const person = z.object({ id: z.string(), name: z.string() }).nullable();

export const employeeFormSchema = z
  .object({
    isNew: z.boolean(),
    title: z.string().nullable(),
    firstName: z
      .string()
      .trim()
      .min(1, 'Enter the first name.')
      .max(100)
      .regex(NAME, 'Use letters, spaces, dots, apostrophes or hyphens.'),
    middleName: z.string().trim().max(100).regex(NAME, 'Use letters only.'),
    lastName: z.string().trim().max(100).regex(NAME, 'Use letters only.'),
    gender: z.enum(['Male', 'Female', 'Other', 'Unknown']),
    dateOfBirth: z.string().nullable(),
    phone: z
      .string()
      .trim()
      .refine((v) => !v || normalizePhone(v) !== null, 'Enter a valid mobile number.'),
    email: z
      .string()
      .trim()
      .max(256)
      .refine((v) => !v || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(v), 'Enter a valid email address.'),
    address: z.object({
      line1: z.string().trim().max(200),
      line2: z.string().trim().max(200),
      city: z.string().trim().max(100),
      district: z.string().trim().max(100),
      state: z.string().trim().max(100),
      pinCode: z
        .string()
        .trim()
        .refine((v) => !v || PIN_CODE.test(v), 'PIN codes have 6 digits.'),
    }),
    emergencyContactName: z.string().trim().max(200),
    emergencyContactPhone: z
      .string()
      .trim()
      .refine((v) => !v || normalizePhone(v) !== null, 'Enter a valid phone number.'),
    departmentId: z.string().nullable(),
    designationId: z.string().nullable(),
    employmentType: z.enum(EMPLOYMENT_TYPES),
    status: z.enum(EDITABLE_EMPLOYEE_STATUSES),
    joiningDate: z.string({ error: 'Enter the joining date.' }).min(1, 'Enter the joining date.'),
    confirmationDate: z.string().nullable(),
    reportingManager: person,
    user: person,
    practitionerId: z.string().nullable(),
    biometricPin: z
      .string()
      .trim()
      .refine(
        (v) => !v || TERMINAL_PIN.test(v),
        'Use up to 24 letters or digits, exactly as enrolled on the terminal.',
      ),
    defaultShiftId: z.string().nullable(),
    weeklyOff: z.enum(WEEKDAYS).nullable(),
    isAttendanceExempt: z.boolean(),
    paymentMode: z.enum(PAYMENT_MODES),
    pan: z
      .string()
      .trim()
      .refine((v) => !v || isValidPan(v), 'Enter a valid PAN, for example ABCDE1234F.'),
    removePan: z.boolean(),
    uan: z
      .string()
      .trim()
      .refine((v) => !v || isValidUan(v), 'The UAN has 12 digits.'),
    pfNumber: z
      .string()
      .trim()
      .refine((v) => !v || PF_NUMBER.test(v), 'Enter a valid PF member number.'),
    esiNumber: z
      .string()
      .trim()
      .refine((v) => !v || isValidEsi(v), 'The ESI insurance number has 10 (or 17) digits.'),
    notes: z.string().trim().max(1000),
    bankName: z.string().trim().max(100),
    accountNumber: z.string().trim(),
    confirmAccountNumber: z.string().trim(),
    ifsc: z.string().trim(),
  })
  .superRefine((v, ctx) => {
    const today = todayIso();
    if (v.dateOfBirth) {
      if (v.dateOfBirth > today || v.dateOfBirth < '1930-01-01') {
        ctx.addIssue({ code: 'custom', path: ['dateOfBirth'], message: 'Enter a valid date of birth.' });
      } else if (v.joiningDate && addYears(v.dateOfBirth, 18) > v.joiningDate) {
        ctx.addIssue({
          code: 'custom',
          path: ['dateOfBirth'],
          message: 'Employees must be at least 18 years old on the joining date.',
        });
      }
    }
    if (v.confirmationDate && v.joiningDate && v.confirmationDate < v.joiningDate) {
      ctx.addIssue({ code: 'custom', path: ['confirmationDate'], message: 'Confirmation cannot be before joining.' });
    }
    if (v.isNew && v.paymentMode === 'BankTransfer' && (v.accountNumber || v.ifsc || v.bankName)) {
      if (!isValidAccount(v.accountNumber)) {
        ctx.addIssue({ code: 'custom', path: ['accountNumber'], message: 'Account numbers have 9 to 18 digits.' });
      }
      if (v.accountNumber.replace(/[\s-]/g, '') !== v.confirmAccountNumber.replace(/[\s-]/g, '')) {
        ctx.addIssue({ code: 'custom', path: ['confirmAccountNumber'], message: 'The account numbers do not match.' });
      }
      if (!isValidIfsc(v.ifsc)) {
        ctx.addIssue({ code: 'custom', path: ['ifsc'], message: 'Enter a valid IFSC, for example SBIN0001234.' });
      }
    }
  });

export type EmployeeFormValues = z.infer<typeof employeeFormSchema>;

function addYears(iso: string, years: number): string {
  const [y = 0, m = 1, d = 1] = iso.split('-').map(Number);
  const date = new Date(Date.UTC(y + years, m - 1, d));
  return date.toISOString().slice(0, 10);
}

export function emptyEmployeeForm(): EmployeeFormValues {
  return {
    isNew: true,
    title: null,
    firstName: '',
    middleName: '',
    lastName: '',
    gender: 'Female',
    dateOfBirth: null,
    phone: '',
    email: '',
    address: { line1: '', line2: '', city: '', district: '', state: '', pinCode: '' },
    emergencyContactName: '',
    emergencyContactPhone: '',
    departmentId: null,
    designationId: null,
    employmentType: 'Permanent',
    status: 'Probation',
    joiningDate: todayIso(),
    confirmationDate: null,
    reportingManager: null,
    user: null,
    practitionerId: null,
    biometricPin: '',
    defaultShiftId: null,
    weeklyOff: null,
    isAttendanceExempt: false,
    paymentMode: 'BankTransfer',
    pan: '',
    removePan: false,
    uan: '',
    pfNumber: '',
    esiNumber: '',
    notes: '',
    bankName: '',
    accountNumber: '',
    confirmAccountNumber: '',
    ifsc: '',
  };
}

/** Form values for editing: masked identifiers are never copied into the form (blank means "keep"). */
export function employeeToForm(e: EmployeeDetail): EmployeeFormValues {
  return {
    ...emptyEmployeeForm(),
    isNew: false,
    title: e.title,
    firstName: e.firstName,
    middleName: e.middleName ?? '',
    lastName: e.lastName ?? '',
    gender: e.gender,
    dateOfBirth: e.dateOfBirth,
    phone: e.phone ?? '',
    email: e.email ?? '',
    address: {
      line1: e.address.line1 ?? '',
      line2: e.address.line2 ?? '',
      city: e.address.city ?? '',
      district: e.address.district ?? '',
      state: e.address.state ?? '',
      pinCode: e.address.pinCode ?? '',
    },
    emergencyContactName: e.emergencyContactName ?? '',
    emergencyContactPhone: e.emergencyContactPhone ?? '',
    departmentId: e.departmentId,
    designationId: e.designationId,
    employmentType: e.employmentType,
    status: e.status === 'Separated' ? 'Active' : e.status,
    joiningDate: e.joiningDate,
    confirmationDate: e.confirmationDate,
    reportingManager: e.reportingManagerId
      ? { id: e.reportingManagerId, name: e.reportingManagerName ?? 'Reporting manager' }
      : null,
    user: e.userId ? { id: e.userId, name: '' } : null,
    practitionerId: e.practitionerId,
    biometricPin: e.biometricPin ?? '',
    defaultShiftId: e.defaultShiftId,
    weeklyOff: e.weeklyOff,
    isAttendanceExempt: e.isAttendanceExempt,
    paymentMode: e.paymentMode,
    uan: e.uan ?? '',
    pfNumber: e.pfNumber ?? '',
    esiNumber: e.esiNumber ?? '',
    notes: e.notes ?? '',
  };
}

const orNull = (value: string) => (value.trim() ? value.trim() : null);

/** API payload. On edit a blank PAN keeps the stored one; "remove PAN" sends "". */
export function formToRequest(v: EmployeeFormValues, version: number | null): SaveEmployeeRequest {
  const address = Object.values(v.address).some((part) => part.trim())
    ? {
        line1: orNull(v.address.line1),
        line2: orNull(v.address.line2),
        city: orNull(v.address.city),
        district: orNull(v.address.district),
        state: orNull(v.address.state),
        pinCode: orNull(v.address.pinCode),
        country: 'India',
      }
    : null;
  const pan = v.removePan && !v.isNew ? '' : v.pan.trim() ? normalizeIdentifier(v.pan) : null;
  const bank =
    v.isNew && v.paymentMode === 'BankTransfer' && v.accountNumber.trim()
      ? {
          bankName: orNull(v.bankName),
          accountNumber: v.accountNumber.replace(/[\s-]/g, ''),
          ifsc: normalizeIdentifier(v.ifsc),
        }
      : null;
  return {
    title: v.title ? orNull(v.title) : null,
    firstName: v.firstName.trim(),
    middleName: orNull(v.middleName),
    lastName: orNull(v.lastName),
    gender: v.gender,
    dateOfBirth: v.dateOfBirth || null,
    phone: v.phone.trim() ? normalizePhone(v.phone) : null,
    email: orNull(v.email),
    address,
    emergencyContactName: orNull(v.emergencyContactName),
    emergencyContactPhone: v.emergencyContactPhone.trim() ? normalizePhone(v.emergencyContactPhone) : null,
    departmentId: v.departmentId,
    designationId: v.designationId,
    employmentType: v.employmentType,
    status: v.status,
    joiningDate: v.joiningDate,
    confirmationDate: v.confirmationDate || null,
    reportingManagerId: v.reportingManager?.id ?? null,
    userId: v.user?.id ?? null,
    practitionerId: v.practitionerId,
    biometricPin: orNull(v.biometricPin),
    defaultShiftId: v.defaultShiftId,
    weeklyOff: v.weeklyOff,
    isAttendanceExempt: v.isAttendanceExempt,
    paymentMode: v.paymentMode,
    pan,
    uan: v.uan.trim() ? normalizeIdentifier(v.uan) : null,
    pfNumber: orNull(v.pfNumber),
    esiNumber: v.esiNumber.trim() ? normalizeIdentifier(v.esiNumber) : null,
    photoFileId: null,
    notes: orNull(v.notes),
    bank,
    version,
  };
}

/**
 * Save request that changes only the terminal PIN of an existing employee (assigning an unmatched PIN): everything else
 * is sent back as it is, PAN is kept (null) and bank details are untouched.
 */
export function withBiometricPin(e: EmployeeDetail, pin: string): SaveEmployeeRequest {
  return { ...formToRequest(employeeToForm(e), e.version), biometricPin: pin, photoFileId: e.photoFileId };
}
