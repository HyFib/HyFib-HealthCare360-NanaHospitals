import { describe, expect, it } from 'vitest';
import { addDays, daysBetween, expiryLabel, expiryTone, formatExpiry, formatPercent, formatQty, maskTail, parseNumber } from './format';

describe('inventory formatting', () => {
  it('formats quantities with Indian grouping and units', () => {
    expect(formatQty(125000)).toBe('1,25,000');
    expect(formatQty(2.5, 'ml')).toBe('2.5 ml');
    expect(formatQty(1.23456)).toBe('1.235');
    expect(formatQty(null)).toBe('—');
    expect(formatPercent(12)).toBe('12%');
    expect(formatPercent(0.25)).toBe('0.25%');
  });

  it('describes expiry in plain words with a colour', () => {
    expect(expiryLabel(-1)).toBe('Expired yesterday');
    expect(expiryLabel(-10)).toBe('Expired 10 days ago');
    expect(expiryLabel(0)).toBe('Expires today');
    expect(expiryLabel(1)).toBe('Expires tomorrow');
    expect(expiryLabel(45)).toBe('Expires in 45 days');
    expect(expiryLabel(152)).toBe('Expires in 5 months');
    expect(expiryLabel(null)).toBe('No expiry');
    expect(expiryTone(-1)).toBe('danger');
    expect(expiryTone(30)).toBe('warning');
    expect(expiryTone(400)).toBe('success');
    expect(expiryTone(null)).toBe('informative');
  });

  it('prints pack-style expiry dates', () => {
    expect(formatExpiry('2027-03-31')).toBe('Mar 2027');
    expect(formatExpiry(null)).toBe('—');
  });

  it('does calendar arithmetic without time zones', () => {
    expect(daysBetween('2026-09-26', '2026-12-25')).toBe(90);
    expect(daysBetween('2026-09-26', '2026-09-20')).toBe(-6);
    expect(addDays('2026-09-26', 90)).toBe('2026-12-25');
    expect(addDays('2024-02-28', 1)).toBe('2024-02-29');
  });

  it('masks identifiers and parses typed numbers', () => {
    expect(maskTail('ABCDE1234F')).toBe('XXXXXX234F');
    expect(maskTail('12')).toBe('12');
    expect(parseNumber('1,250.5')).toBe(1250.5);
    expect(parseNumber('  ')).toBeNull();
    expect(parseNumber('abc')).toBeNull();
  });
});

describe('IST calendar date', () => {
  it('uses the hospital time zone, not UTC', async () => {
    const { istDateOf } = await import('./format');
    expect(istDateOf('2026-09-25T19:00:00Z')).toBe('2026-09-26');
    expect(istDateOf('2026-09-25T18:00:00Z')).toBe('2026-09-25');
  });
});
