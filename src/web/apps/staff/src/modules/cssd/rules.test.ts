import { describe, expect, it } from 'vitest';
import { checkParameters, compareCounts, daysToExpiry, minimumSteamMinutes } from './rules';

describe('sterilizer parameters (mirror of the server)', () => {
  it.each([
    [121, 15, true],
    [121, 14, false],
    [126, 10, true],
    [126, 9, false],
    [134, 3, true],
    [134, 2, false],
    [138, 3, true],
    [139, 30, false],
    [120, 60, false],
  ])('steam at %i °C for %i min passes: %s', (temperature, minutes, ok) => {
    expect(checkParameters('Steam', temperature, minutes).ok).toBe(ok);
  });

  it('explains why a steam cycle fails', () => {
    expect(checkParameters('Steam', 139, 10).message).toBe('Steam temperature 139 °C is above the 138 °C limit.');
    expect(checkParameters('Steam', 120, 30).message).toBe('Steam at 120 °C is below the 121 °C sterilizing temperature.');
    expect(checkParameters('Steam', 121, 10).message).toBe('Steam at 121 °C needs at least 15 minutes (recorded 10).');
  });

  it('checks ethylene oxide and plasma ranges', () => {
    expect(checkParameters('EthyleneOxide', 55, 60).ok).toBe(true);
    expect(checkParameters('EthyleneOxide', 55, 59).message).toBe('Ethylene oxide cycles need at least 60 minutes of exposure (recorded 59).');
    expect(checkParameters('EthyleneOxide', 64, 120).message).toBe('Ethylene oxide cycles run at 37–63 °C (recorded 64 °C).');
    expect(checkParameters('Plasma', 50, 24).ok).toBe(true);
    expect(checkParameters('Plasma', 39, 30).ok).toBe(false);
  });

  it('knows the minimum steam time per temperature band', () => {
    expect(minimumSteamMinutes(121)).toBe(15);
    expect(minimumSteamMinutes(125.9)).toBe(15);
    expect(minimumSteamMinutes(126)).toBe(10);
    expect(minimumSteamMinutes(134)).toBe(3);
    expect(minimumSteamMinutes(138.5)).toBeNull();
  });
});

describe('tray count check (mirror of the server)', () => {
  const sheet = [
    { name: 'Artery forceps', quantity: 6 },
    { name: 'Scissors', quantity: 2 },
    { name: 'Needle holder', quantity: 1 },
  ];

  it('passes a complete tray', () => {
    const result = compareCounts(sheet, [
      { itemName: 'artery forceps', received: 6, damaged: 0 },
      { itemName: 'SCISSORS', received: 2, damaged: 0 },
      { itemName: 'Needle holder', received: 1, damaged: 0 },
    ]);
    expect(result.hasDiscrepancy).toBe(false);
    expect(result.expected).toBe(9);
    expect(result.received).toBe(9);
    expect(result.discrepancies).toEqual([]);
  });

  it('treats uncounted items as missing and reports extras and damage', () => {
    const result = compareCounts(sheet, [
      { itemName: 'Artery forceps', received: 5, damaged: 1 },
      { itemName: 'Scissors', received: 3, damaged: 0 },
      { itemName: 'Retractor', received: 1, damaged: 0 },
    ]);
    expect(result.missing).toBe(2);
    expect(result.extra).toBe(2);
    expect(result.damaged).toBe(1);
    expect(result.discrepancies).toEqual([
      'Artery forceps: 1 missing, 1 damaged',
      'Scissors: 1 extra',
      'Needle holder: 1 missing',
      'Retractor: 1 not on the count sheet',
    ]);
  });
});

describe('pack expiry', () => {
  it('counts days to expiry', () => {
    expect(daysToExpiry('2026-09-28', '2026-09-26')).toBe(2);
    expect(daysToExpiry('2026-09-25', '2026-09-26')).toBe(-1);
    expect(daysToExpiry(null, '2026-09-26')).toBeNull();
  });
});
