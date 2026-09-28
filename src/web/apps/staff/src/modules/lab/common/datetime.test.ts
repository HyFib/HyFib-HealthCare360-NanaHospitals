import { describe, expect, it } from 'vitest';
import { fromZonedInput, isInFuture, toZonedInput } from './datetime';

describe('hospital-time datetime inputs', () => {
  it('shows an instant on the IST wall clock', () => {
    expect(toZonedInput(new Date('2026-09-26T04:30:00Z'))).toBe('2026-09-26T10:00');
    expect(toZonedInput(new Date('2026-09-25T18:45:00Z'))).toBe('2026-09-26T00:15');
  });

  it('reads an IST wall-clock value back as the right instant', () => {
    expect(fromZonedInput('2026-09-26T10:00')).toBe('2026-09-26T04:30:00.000Z');
    expect(fromZonedInput('2026-09-26T00:15')).toBe('2026-09-25T18:45:00.000Z');
  });

  it('round-trips and rejects incomplete values', () => {
    const value = toZonedInput(new Date('2026-01-01T12:34:00Z'));
    expect(fromZonedInput(value)).toBe('2026-01-01T12:34:00.000Z');
    expect(fromZonedInput('2026-09-26')).toBeNull();
    expect(fromZonedInput('')).toBeNull();
  });

  it('works for another time zone', () => {
    expect(toZonedInput(new Date('2026-07-01T12:00:00Z'), 'UTC')).toBe('2026-07-01T12:00');
    expect(fromZonedInput('2026-07-01T12:00', 'UTC')).toBe('2026-07-01T12:00:00.000Z');
  });

  it('detects times beyond the clock-skew tolerance', () => {
    const now = new Date('2026-09-26T10:00:00Z');
    expect(isInFuture('2026-09-26T10:04:00Z', now)).toBe(false);
    expect(isInFuture('2026-09-26T10:06:00Z', now)).toBe(true);
  });
});
