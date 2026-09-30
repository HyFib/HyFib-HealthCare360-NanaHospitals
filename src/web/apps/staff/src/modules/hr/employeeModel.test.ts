import { describe, expect, it } from 'vitest';
import type { EmployeeDetail } from './api';
import {
  emptyEmployeeForm,
  employeeFormSchema,
  employeeToForm,
  formToRequest,
  isValidIfsc,
  isValidPan,
  normalizeIdentifier,
  withBiometricPin,
} from './employeeModel';

const detail: EmployeeDetail = {
  id: 'e1',
  employeeCode: 'NHEMP00042',
  title: 'Ms.',
  firstName: 'Priya',
  middleName: null,
  lastName: 'Raman',
  fullName: 'Ms. Priya Raman',
  gender: 'Female',
  dateOfBirth: '1994-05-12',
  phone: '+919876543210',
  email: 'priya@example.com',
  address: { line1: '12 Anna Salai', city: 'Chennai', state: 'Tamil Nadu', pinCode: '600002' },
  emergencyContactName: null,
  emergencyContactPhone: null,
  departmentId: 'd1',
  departmentName: 'Nursing',
  designationId: 'g1',
  designationName: 'Staff nurse',
  employmentType: 'Permanent',
  status: 'Active',
  joiningDate: '2020-06-01',
  confirmationDate: '2020-12-01',
  leavingDate: null,
  separationReason: null,
  separationNote: null,
  reportingManagerId: 'm1',
  reportingManagerName: 'Dr. Meena',
  userId: 'u1',
  practitionerId: null,
  biometricPin: '42',
  defaultShiftId: 's1',
  weeklyOff: 'Sunday',
  isAttendanceExempt: false,
  paymentMode: 'BankTransfer',
  bankName: 'SBI',
  bankAccountMasked: 'XXXXXXXX1234',
  bankIfscMasked: 'SBIN0XXXXXX',
  panMasked: 'XXXXXX234F',
  uan: '100200300400',
  pfNumber: 'TN/MAS/12345/678',
  esiNumber: null,
  photoFileId: 'p1',
  notes: null,
  version: 7,
};

describe('employee identifiers', () => {
  it('normalises and validates PAN and IFSC like the server', () => {
    expect(normalizeIdentifier(' abcde 1234f ')).toBe('ABCDE1234F');
    expect(isValidPan('abcde1234f')).toBe(true);
    expect(isValidPan('ABCD1234F')).toBe(false);
    expect(isValidIfsc('sbin0001234')).toBe(true);
    expect(isValidIfsc('SBIN1001234')).toBe(false);
  });
});

describe('employee form rules', () => {
  const valid = { ...emptyEmployeeForm(), firstName: 'Priya', joiningDate: '2026-09-01' };

  it('accepts a minimal new employee', () => {
    expect(employeeFormSchema.safeParse(valid).success).toBe(true);
  });

  it('requires bank details to be typed twice and valid', () => {
    const result = employeeFormSchema.safeParse({
      ...valid,
      accountNumber: '123456789012',
      confirmAccountNumber: '123456789013',
      ifsc: 'SBIN0001234',
    });
    expect(result.success).toBe(false);
    expect(result.error?.issues.map((issue) => issue.path.join('.'))).toEqual(['confirmAccountNumber']);
  });

  it('refuses under-age joiners and confirmation before joining', () => {
    const result = employeeFormSchema.safeParse({
      ...valid,
      dateOfBirth: '2010-01-01',
      confirmationDate: '2026-08-01',
    });
    expect(result.error?.issues.map((issue) => issue.path.join('.')).sort()).toEqual([
      'confirmationDate',
      'dateOfBirth',
    ]);
  });
});

describe('employee payloads', () => {
  it('never copies masked identifiers into the edit form', () => {
    const form = employeeToForm(detail);
    expect(form.pan).toBe('');
    expect(form.accountNumber).toBe('');
    expect(form.isNew).toBe(false);
    expect(form.reportingManager).toEqual({ id: 'm1', name: 'Dr. Meena' });
  });

  it('keeps PAN on edit unless removal is asked for', () => {
    const form = employeeToForm(detail);
    expect(formToRequest(form, 7).pan).toBeNull();
    expect(formToRequest({ ...form, removePan: true }, 7).pan).toBe('');
    expect(formToRequest({ ...form, pan: 'abcde1234f' }, 7).pan).toBe('ABCDE1234F');
    expect(formToRequest(form, 7).bank).toBeNull();
    expect(formToRequest(form, 7).version).toBe(7);
  });

  it('sends bank details only when creating', () => {
    const request = formToRequest(
      {
        ...emptyEmployeeForm(),
        firstName: 'Ravi',
        accountNumber: '1234 5678 9012',
        confirmAccountNumber: '123456789012',
        ifsc: 'sbin0001234',
      },
      null,
    );
    expect(request.bank).toEqual({ bankName: null, accountNumber: '123456789012', ifsc: 'SBIN0001234' });
    expect(request.phone).toBeNull();
  });

  it('changes only the terminal PIN when assigning one', () => {
    const request = withBiometricPin(detail, '77');
    expect(request.biometricPin).toBe('77');
    expect(request.pan).toBeNull();
    expect(request.bank).toBeNull();
    expect(request.photoFileId).toBe('p1');
    expect(request.firstName).toBe('Priya');
    expect(request.weeklyOff).toBe('Sunday');
    expect(request.phone).toBe('+919876543210');
  });
});
