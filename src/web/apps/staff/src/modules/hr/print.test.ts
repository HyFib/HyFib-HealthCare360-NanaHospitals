import { describe, expect, it } from 'vitest';
import type { PayslipDto } from './api';
import { amountInWords, payslipHtml } from './print';

describe('amount in words', () => {
  it('uses Indian grouping', () => {
    expect(amountInWords(0)).toBe('Rupees Zero Only');
    expect(amountInWords(45)).toBe('Rupees Forty-Five Only');
    expect(amountInWords(1005)).toBe('Rupees One Thousand Five Only');
    expect(amountInWords(145320.5)).toBe('Rupees One Lakh Forty-Five Thousand Three Hundred Twenty and Fifty Paise Only');
    expect(amountInWords(12_500_000)).toBe('Rupees One Crore Twenty-Five Lakh Only');
    expect(amountInWords(99.99)).toBe('Rupees Ninety-Nine and Ninety-Nine Paise Only');
  });
});

describe('payslip print', () => {
  const payslip: PayslipDto = {
    id: 'p1',
    payrollRunId: 'r1',
    runStatus: 'Approved',
    year: 2026,
    month: 9,
    facilityName: 'Nana <Hospitals>',
    employeeId: 'e1',
    employeeCode: 'NHEMP00042',
    employeeName: 'Priya "PR" Raman',
    designationName: 'Staff nurse',
    departmentName: 'Nursing',
    panMasked: 'XXXXXX234F',
    uan: null,
    daysInMonth: 30,
    payableDays: 29,
    lopDays: 1,
    missingAttendanceDays: 0,
    earnings: [{ code: 'BASIC', name: 'Basic', fullAmount: 20000, amount: 19333.33, isStatutory: false }],
    deductions: [{ code: 'PF', name: 'Provident fund', fullAmount: 1800, amount: 1800, isStatutory: true }],
    grossEarnings: 19333.33,
    totalDeductions: 1800,
    netPay: 17533.33,
    pfWage: 15000,
    employerPf: 1800,
    esiWage: 0,
    employerEsi: 0,
    paymentMode: 'BankTransfer',
    bankAccountMasked: 'XXXXXXXX1234',
    warnings: [],
    hasErrors: false,
  };

  it('escapes every value and shows only masked identifiers', () => {
    const html = payslipHtml(payslip, '2026-09-30T10:00:00Z');
    expect(html).toContain('Nana &lt;Hospitals&gt;');
    expect(html).toContain('Priya &quot;PR&quot; Raman');
    expect(html).toContain('XXXXXXXX1234');
    expect(html).toContain('September 2026');
    expect(html).toContain('Rupees Seventeen Thousand Five Hundred Thirty-Three and Thirty-Three Paise Only');
    expect(html).not.toContain('<Hospitals>');
  });
});
