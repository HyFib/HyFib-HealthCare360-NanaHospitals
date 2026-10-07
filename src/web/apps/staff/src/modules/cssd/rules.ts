/**
 * CSSD rules mirrored from the server so screens can warn before saving: validated sterilizer parameters
 * (`SterilizationParameters`) and the tray count check (`InstrumentCountCheck`). The server applies them again and is
 * the record (a cycle outside the range fails automatically; a discrepancy alerts the recall team).
 */

export type SterilizationMethod = 'Steam' | 'EthyleneOxide' | 'Plasma';

export const METHODS: readonly SterilizationMethod[] = ['Steam', 'EthyleneOxide', 'Plasma'];

export const METHOD_LABEL: Record<SterilizationMethod, string> = {
  Steam: 'Steam (autoclave)',
  EthyleneOxide: 'Ethylene oxide (ETO)',
  Plasma: 'Hydrogen peroxide plasma',
};

export const METHOD_HINT: Record<SterilizationMethod, string> = {
  Steam: '121 °C for 15 min, 126 °C for 10 min or 134 °C for 3 min; never above 138 °C',
  EthyleneOxide: '37–63 °C for at least 60 minutes',
  Plasma: '40–60 °C for at least 24 minutes',
};

export const STEAM_MAX_C = 138;

/** The A0 a wash cycle must reach (ISO 15883-1): 90 °C held for 5 minutes gives 3000. The server applies the same rule. */
export const REQUIRED_A0 = 3000;

/** A0 of a temperature held for a time: each second counts 10^((T − 80) / 10), computed as the server does. */
export function washA0(temperatureC: number, holdMinutes: number): number {
  return Math.floor(10 ** ((temperatureC - 80) / 10) * (holdMinutes * 60));
}

export interface ParameterCheck {
  ok: boolean;
  message: string | null;
}

/** Minimum steam exposure (minutes) at a temperature, or null when the temperature cannot sterilize. */
export function minimumSteamMinutes(temperatureC: number): number | null {
  if (temperatureC > STEAM_MAX_C) {
    return null;
  }
  if (temperatureC >= 134) {
    return 3;
  }
  if (temperatureC >= 126) {
    return 10;
  }
  if (temperatureC >= 121) {
    return 15;
  }
  return null;
}

function range(
  name: string,
  temperatureC: number,
  exposureMinutes: number,
  minC: number,
  maxC: number,
  minMinutes: number,
): ParameterCheck {
  if (temperatureC < minC || temperatureC > maxC) {
    return { ok: false, message: `${name} cycles run at ${minC}–${maxC} °C (recorded ${temperatureC} °C).` };
  }
  return exposureMinutes < minMinutes
    ? {
        ok: false,
        message: `${name} cycles need at least ${minMinutes} minutes of exposure (recorded ${exposureMinutes}).`,
      }
    : { ok: true, message: null };
}

/** Whether a cycle printout is within the validated range for its method (same bounds and wording as the server). */
export function checkParameters(
  method: SterilizationMethod,
  temperatureC: number,
  exposureMinutes: number,
): ParameterCheck {
  switch (method) {
    case 'Steam': {
      if (temperatureC > STEAM_MAX_C) {
        return { ok: false, message: `Steam temperature ${temperatureC} °C is above the ${STEAM_MAX_C} °C limit.` };
      }
      const minimum = minimumSteamMinutes(temperatureC);
      if (minimum === null) {
        return { ok: false, message: `Steam at ${temperatureC} °C is below the 121 °C sterilizing temperature.` };
      }
      return exposureMinutes < minimum
        ? {
            ok: false,
            message: `Steam at ${temperatureC} °C needs at least ${minimum} minutes (recorded ${exposureMinutes}).`,
          }
        : { ok: true, message: null };
    }
    case 'EthyleneOxide':
      return range('Ethylene oxide', temperatureC, exposureMinutes, 37, 63, 60);
    case 'Plasma':
      return range('Plasma', temperatureC, exposureMinutes, 40, 60, 24);
    default:
      return { ok: false, message: 'Unknown sterilization method.' };
  }
}

// ------------------------------------------------------------------ tray count check

export interface CountSheetLine {
  name: string;
  quantity: number;
}

export interface CountedItem {
  itemName: string;
  received: number;
  damaged: number;
}

export interface ItemCountResult {
  itemName: string;
  expected: number;
  received: number;
  damaged: number;
  missing: number;
  extra: number;
  hasDiscrepancy: boolean;
}

export interface CountCheckResult {
  expected: number;
  received: number;
  missing: number;
  extra: number;
  damaged: number;
  items: ItemCountResult[];
  discrepancies: string[];
  hasDiscrepancy: boolean;
}

const norm = (name: string) => name.trim().toUpperCase();

function describe(item: ItemCountResult): string {
  const parts: string[] = [];
  if (item.missing > 0) {
    parts.push(`${item.missing} missing`);
  }
  if (item.extra > 0) {
    parts.push(item.expected === 0 ? `${item.extra} not on the count sheet` : `${item.extra} extra`);
  }
  if (item.damaged > 0) {
    parts.push(`${item.damaged} damaged`);
  }
  return `${item.itemName}: ${parts.join(', ')}`;
}

/**
 * Compares a returned tray with its count sheet. Items not counted count as not received (a tray never passes by
 * omission); counted items not on the sheet are extras. Names match case-insensitively.
 */
export function compareCounts(
  countSheet: readonly CountSheetLine[],
  counted: readonly CountedItem[],
): CountCheckResult {
  const countedByName = new Map<string, { name: string; received: number; damaged: number }>();
  for (const c of counted) {
    const key = norm(c.itemName);
    const current = countedByName.get(key) ?? { name: c.itemName.trim(), received: 0, damaged: 0 };
    current.received += c.received;
    current.damaged += c.damaged;
    countedByName.set(key, current);
  }
  const sheet = new Map<string, { name: string; quantity: number }>();
  for (const line of countSheet) {
    const key = norm(line.name);
    const current = sheet.get(key) ?? { name: line.name.trim(), quantity: 0 };
    current.quantity += line.quantity;
    sheet.set(key, current);
  }

  const toResult = (itemName: string, expected: number, received: number, damaged: number): ItemCountResult => {
    const missing = Math.max(0, expected - received);
    const extra = Math.max(0, received - expected);
    return {
      itemName,
      expected,
      received,
      damaged,
      missing,
      extra,
      hasDiscrepancy: missing > 0 || extra > 0 || damaged > 0,
    };
  };

  const items = [
    ...[...sheet.entries()].map(([key, line]) => {
      const c = countedByName.get(key);
      return toResult(line.name, line.quantity, c?.received ?? 0, c?.damaged ?? 0);
    }),
    ...[...countedByName.entries()]
      .filter(([key]) => !sheet.has(key))
      .map(([, c]) => toResult(c.name, 0, c.received, c.damaged)),
  ];
  const sum = (pick: (i: ItemCountResult) => number) => items.reduce((total, i) => total + pick(i), 0);
  const result = {
    expected: sum((i) => i.expected),
    received: sum((i) => i.received),
    missing: sum((i) => i.missing),
    extra: sum((i) => i.extra),
    damaged: sum((i) => i.damaged),
    items,
    discrepancies: items.filter((i) => i.hasDiscrepancy).map(describe),
  };
  return { ...result, hasDiscrepancy: result.missing > 0 || result.extra > 0 || result.damaged > 0 };
}

/** Days until a pack's sterility expires ("yyyy-MM-dd" dates); negative when expired. */
export function daysToExpiry(sterileUntil: string | null, today: string): number | null {
  if (!sterileUntil) {
    return null;
  }
  return Math.round((Date.parse(`${sterileUntil}T00:00:00Z`) - Date.parse(`${today}T00:00:00Z`)) / 86_400_000);
}
