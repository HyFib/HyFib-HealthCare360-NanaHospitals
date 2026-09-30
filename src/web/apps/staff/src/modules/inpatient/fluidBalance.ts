import { atIst, addDays, NURSING_SHIFTS, shiftAt, type NursingShift } from './time';

/**
 * Fluid balance arithmetic for the intake/output chart, mirroring the server's `FluidBalance`: the chart day runs
 * 08:00 to 08:00 IST (the nursing day), with shift-wise and route-wise subtotals. The client uses it for the running
 * balance column and instant feedback; the server's totals stay the record.
 */

export type FluidDirection = 'Intake' | 'Output';

export type FluidRoute =
  | 'Oral'
  | 'IntravenousFluid'
  | 'IntravenousMedication'
  | 'BloodProduct'
  | 'TubeFeed'
  | 'Urine'
  | 'Drain'
  | 'Vomitus'
  | 'Stool'
  | 'NasogastricAspirate'
  | 'BloodLoss'
  | 'Other';

export const INTAKE_ROUTES: readonly FluidRoute[] = [
  'Oral',
  'IntravenousFluid',
  'IntravenousMedication',
  'BloodProduct',
  'TubeFeed',
  'Other',
];

export const OUTPUT_ROUTES: readonly FluidRoute[] = [
  'Urine',
  'Drain',
  'Vomitus',
  'Stool',
  'NasogastricAspirate',
  'BloodLoss',
  'Other',
];

export const ROUTE_LABEL: Record<FluidRoute, string> = {
  Oral: 'Oral',
  IntravenousFluid: 'IV fluid',
  IntravenousMedication: 'IV medicine',
  BloodProduct: 'Blood product',
  TubeFeed: 'Tube feed',
  Urine: 'Urine',
  Drain: 'Drain',
  Vomitus: 'Vomit',
  Stool: 'Stool',
  NasogastricAspirate: 'NG aspirate',
  BloodLoss: 'Blood loss',
  Other: 'Other',
};

/** Routes that fit a direction (urine is always an output); "Other" fits both. */
export function routeMatches(direction: FluidDirection, route: FluidRoute): boolean {
  if (route === 'Other') {
    return true;
  }
  return direction === 'Intake' ? INTAKE_ROUTES.includes(route) : OUTPUT_ROUTES.includes(route);
}

export function routesFor(direction: FluidDirection): readonly FluidRoute[] {
  return direction === 'Intake' ? INTAKE_ROUTES : OUTPUT_ROUTES;
}

/** The chart window of a duty date: 08:00 IST that day to 08:00 IST the next day (ISO instants). */
export function chartDay(dutyDate: string): { from: string; to: string } {
  return { from: atIst(dutyDate, 8), to: atIst(addDays(dutyDate, 1), 8) };
}

export interface FluidEntryInput {
  recordedAt: string;
  direction: FluidDirection;
  route: FluidRoute;
  volumeMl: number;
}

export interface FluidTotals {
  intakeMl: number;
  outputMl: number;
  balanceMl: number;
}

export interface ShiftTotals extends FluidTotals {
  shift: NursingShift;
}

export interface RouteTotal {
  route: FluidRoute;
  direction: FluidDirection;
  volumeMl: number;
}

export interface FluidSummary extends FluidTotals {
  byShift: ShiftTotals[];
  byRoute: RouteTotal[];
}

function totals(entries: readonly FluidEntryInput[]): FluidTotals {
  const intakeMl = entries.filter((e) => e.direction === 'Intake').reduce((sum, e) => sum + e.volumeMl, 0);
  const outputMl = entries.filter((e) => e.direction === 'Output').reduce((sum, e) => sum + e.volumeMl, 0);
  return { intakeMl, outputMl, balanceMl: intakeMl - outputMl };
}

/** Totals for a chart day (entries outside the 08:00–08:00 window are ignored when a duty date is given). */
export function summarizeFluids(entries: readonly FluidEntryInput[], dutyDate?: string): FluidSummary {
  let inWindow = entries;
  if (dutyDate) {
    const { from, to } = chartDay(dutyDate);
    const start = Date.parse(from);
    const end = Date.parse(to);
    inWindow = entries.filter((e) => {
      const at = Date.parse(e.recordedAt);
      return at >= start && at < end;
    });
  }

  const byShift = NURSING_SHIFTS.map((shift) => ({
    shift,
    ...totals(inWindow.filter((e) => shiftAt(e.recordedAt) === shift)),
  }));
  const routeMap = new Map<string, RouteTotal>();
  for (const entry of inWindow) {
    const key = `${entry.direction}:${entry.route}`;
    const current = routeMap.get(key) ?? { route: entry.route, direction: entry.direction, volumeMl: 0 };
    current.volumeMl += entry.volumeMl;
    routeMap.set(key, current);
  }
  const byRoute = [...routeMap.values()].sort((a, b) =>
    a.direction === b.direction
      ? Object.keys(ROUTE_LABEL).indexOf(a.route) - Object.keys(ROUTE_LABEL).indexOf(b.route)
      : a.direction === 'Intake'
        ? -1
        : 1,
  );
  return { ...totals(inWindow), byShift, byRoute };
}

/** Running balance after each entry, in time order (for the chart's balance column). */
export function runningBalance<T extends FluidEntryInput>(entries: readonly T[]): { entry: T; balanceMl: number }[] {
  let balance = 0;
  return [...entries]
    .sort((a, b) => Date.parse(a.recordedAt) - Date.parse(b.recordedAt))
    .map((entry) => {
      balance += entry.direction === 'Intake' ? entry.volumeMl : -entry.volumeMl;
      return { entry, balanceMl: balance };
    });
}

export type BalanceTone = 'positive' | 'negative' | 'even';

export function balanceTone(balanceMl: number): BalanceTone {
  return balanceMl > 0 ? 'positive' : balanceMl < 0 ? 'negative' : 'even';
}

/** "+350 ml", "−200 ml" (true minus sign), "0 ml", with Indian digit grouping. */
export function formatBalance(balanceMl: number): string {
  const magnitude = Math.abs(balanceMl).toLocaleString('en-IN');
  return balanceMl > 0 ? `+${magnitude} ml` : balanceMl < 0 ? `−${magnitude} ml` : '0 ml';
}

export function formatMl(volumeMl: number): string {
  return `${volumeMl.toLocaleString('en-IN')} ml`;
}

/** Quick volumes offered as one-tap chips on tablets. */
export const QUICK_VOLUMES: readonly number[] = [50, 100, 150, 200, 250, 500];
