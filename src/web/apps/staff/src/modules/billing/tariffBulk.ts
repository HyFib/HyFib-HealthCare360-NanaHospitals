import { fromPaise, maxBig, parseRupees, roundDiv, toCentiPercent, toPaise } from './money';

/** Bulk tariff price updates: revise by a percentage, or paste a price sheet from Excel. Pure and unit-tested. */

export const PRICE_ROUNDING = ['paise', 'rupee', 'five', 'ten'] as const;
export type PriceRounding = (typeof PRICE_ROUNDING)[number];
export const PRICE_ROUNDING_LABEL: Record<PriceRounding, string> = {
  paise: 'Keep paise',
  rupee: 'Nearest rupee',
  five: 'Nearest ₹5',
  ten: 'Nearest ₹10',
};

const ROUNDING_STEP: Record<PriceRounding, bigint> = { paise: 1n, rupee: 100n, five: 500n, ten: 1000n };

/**
 * New price after a percentage change (negative for a reduction), rounded half away from zero to the chosen step.
 * Never below zero.
 */
export function revisePrice(price: number, percent: number, rounding: PriceRounding): number {
  const paise = toPaise(price);
  const factor = 10_000n + toCentiPercent(percent);
  const raw = roundDiv(paise * factor, 10_000n);
  const step = ROUNDING_STEP[rounding];
  return fromPaise(maxBig(roundDiv(raw, step) * step, 0n));
}

export interface PriceSheetRow {
  line: number;
  code: string;
  price: number;
}

export interface PriceSheetProblem {
  line: number;
  message: string;
}

/**
 * Reads rows pasted from Excel or a CSV: service code then price ("CBC⇥350", "CBC,350", "CBC 350"). Blank lines are
 * skipped, a header row is ignored, and each problem names its line so it can be fixed in the sheet.
 */
export function parsePriceSheet(text: string): { rows: PriceSheetRow[]; problems: PriceSheetProblem[] } {
  const rows: PriceSheetRow[] = [];
  const problems: PriceSheetProblem[] = [];
  const seen = new Map<string, number>();
  const lines = text.split(/\r?\n/);

  lines.forEach((raw, index) => {
    const lineNumber = index + 1;
    const trimmed = raw.trim();
    if (!trimmed) {
      return;
    }
    let code: string;
    let priceText: string;
    if (trimmed.includes('\t')) {
      const cells = trimmed.split('\t').map((cell) => cell.trim());
      code = cells[0] ?? '';
      priceText = cells.slice(1).find((cell) => cell !== '') ?? '';
    } else {
      const match = /^([^,;\s]+)[,;\s]+(.+)$/.exec(trimmed);
      code = match?.[1] ?? trimmed;
      priceText = match?.[2]?.trim() ?? '';
    }
    code = code.toUpperCase();

    const price = parseRupees(priceText, { allowZero: true });
    if (!price.ok) {
      if (rows.length === 0 && problems.length === 0 && !/\d/.test(priceText)) {
        return; // header row such as "Code  Price"
      }
      problems.push({ line: lineNumber, message: priceText ? `${code}: ${price.error}` : `${code}: the price is missing.` });
      return;
    }
    if (!/^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(code)) {
      problems.push({ line: lineNumber, message: `"${code}" is not a service code.` });
      return;
    }
    const first = seen.get(code);
    if (first !== undefined) {
      problems.push({ line: lineNumber, message: `${code} is listed twice (also on line ${first}).` });
      return;
    }
    seen.set(code, lineNumber);
    rows.push({ line: lineNumber, code, price: price.rupees });
  });

  return { rows, problems };
}

/** Splits a list into batches the server accepts (at most 500 prices per request). */
export function inBatches<T>(items: readonly T[], size = 500): T[][] {
  const batches: T[][] = [];
  for (let start = 0; start < items.length; start += size) {
    batches.push(items.slice(start, start + size));
  }
  return batches;
}
