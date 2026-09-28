import { dec, mul, round2, sum, toNumber } from './decimal';

/** Counted minus book quantity (positive = found more than the books show). */
export function countVariance(systemQuantity: number, countedQuantity: number): number {
  return toNumber(dec(countedQuantity) - dec(systemQuantity));
}

/** Value of a quantity difference at unit cost, to the paisa. */
export function varianceValue(variance: number, unitCost: number): number {
  return toNumber(round2(mul(dec(variance), dec(unitCost))));
}

export interface CountEntryState {
  lineId: string;
  systemQuantity: number;
  unitCost: number;
  /** null = not counted yet. */
  counted: number | null;
}

export interface CountSummary {
  counted: number;
  total: number;
  withDifference: number;
  /** Net value of all differences at cost (negative = stock missing). */
  netValue: number;
  /** Value of the shortages only (a positive number). */
  shortValue: number;
}

/** Progress and money impact of a count sheet as typed so far. */
export function summarizeCount(entries: readonly CountEntryState[]): CountSummary {
  const counted = entries.filter((e) => e.counted !== null);
  const values = counted.map((e) => varianceValue(countVariance(e.systemQuantity, e.counted ?? 0), e.unitCost));
  return {
    counted: counted.length,
    total: entries.length,
    withDifference: counted.filter((e) => countVariance(e.systemQuantity, e.counted ?? 0) !== 0).length,
    netValue: toNumber(sum(values.map((v) => dec(v)))),
    shortValue: toNumber(sum(values.filter((v) => v < 0).map((v) => -dec(v)))),
  };
}
