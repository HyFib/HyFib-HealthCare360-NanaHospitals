import { describe, expect, it } from 'vitest';
import { formatAge, formatDateOnly, formatInr, formatPhone, humanize, initials, isIndianMobile, normalizePhone } from './format';

describe('format', () => {
  it('formats rupees with Indian digit grouping', () => {
    expect(formatInr(1234567.5)).toBe('₹12,34,567.50');
    expect(formatInr(null)).toBe('—');
  });

  it('formats compact ages the way Indian hospital prints do', () => {
    const asOf = new Date(2026, 8, 25);
    expect(formatAge('1987-03-14', asOf)).toBe('39Y 6M');
    expect(formatAge('2026-04-13', asOf)).toBe('5M 12D');
    expect(formatAge('2026-09-16', asOf)).toBe('9D');
    expect(formatAge(null, asOf)).toBe('—');
  });

  it('formats calendar dates without a time-zone shift', () => {
    expect(formatDateOnly('1987-03-14')).toBe('14 Mar 1987');
  });

  it('humanizes enum names', () => {
    expect(humanize('OutPatient')).toBe('Out patient');
    expect(humanize('SmsWhatsAppUpdates')).toBe('Sms whats app updates');
  });

  it('pretty-prints Indian mobile numbers and initials', () => {
    expect(formatPhone('+919876543210')).toBe('+91 98765 43210');
    expect(initials('Dr. Arjun Mehta')).toBe('AM');
    expect(initials('Ravi')).toBe('R');
  });

  it('normalises phone numbers like the server', () => {
    expect(normalizePhone('98765 43210')).toBe('+919876543210');
    expect(normalizePhone('09876543210')).toBe('+919876543210');
    expect(normalizePhone('+91 98765-43210')).toBe('+919876543210');
    expect(normalizePhone('0044 20 7946 0958')).toBe('+442079460958');
    expect(normalizePhone('12345')).toBeNull();
    expect(normalizePhone('')).toBeNull();
    expect(isIndianMobile('9876543210')).toBe(true);
    expect(isIndianMobile('5876543210')).toBe(false);
    expect(isIndianMobile('044 2345 6789')).toBe(false);
  });
});
