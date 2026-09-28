/** Worklist helpers for the lab dashboard, widgets and bulk verification (pure; unit-tested). */
import type { ResultSheet, WorklistItem } from './api';
import { parseAgeSex } from './common/banner';
import { interpretLine, isReadyForBulkVerify } from './flags';

/** Tubes in the lab with critical values that are not verified yet (the call to the doctor may still be due). */
export function criticalTubes(items: readonly WorklistItem[]): WorklistItem[] {
  return items.filter((item) => item.critical > 0 && item.entered > 0);
}

export interface WorklistCounts {
  /** Tubes in the lab (received or in process). */
  inLab: number;
  /** Tubes with at least one result waiting for entry. */
  awaitingEntry: number;
  /** Tubes with entered results waiting for a pathologist. */
  awaitingVerification: number;
  /** Tubes with critical values not verified yet. */
  critical: number;
  stat: number;
}

/** Bench workload from one worklist page (the server orders STAT and critical tubes first). */
export function worklistCounts(items: readonly WorklistItem[], totalCount: number): WorklistCounts {
  return {
    inLab: totalCount,
    awaitingEntry: items.filter((item) => item.pending > 0).length,
    awaitingVerification: items.filter((item) => item.entered > 0).length,
    critical: criticalTubes(items).length,
    stat: items.filter((item) => item.priority === 'Stat').length,
  };
}

/** Result progress of one tube in words: "3 to enter · 2 to verify". */
export function tubeProgress(item: Pick<WorklistItem, 'pending' | 'entered' | 'verified'>): string {
  const parts: string[] = [];
  if (item.pending) {
    parts.push(`${item.pending} to enter`);
  }
  if (item.entered) {
    parts.push(`${item.entered} to verify`);
  }
  if (!item.pending && !item.entered && item.verified) {
    parts.push('all verified');
  }
  return parts.join(' · ') || 'no results yet';
}

export interface TubePlan {
  sheet: ResultSheet;
  /** Entered results that may be verified together. */
  ready: string[];
  /** Entered results left for an individual look. */
  held: number;
}

/** Entered results that are in range with nothing unusual (no flag, delta failure, critical value or remark). */
export function planBulkVerification(sheet: ResultSheet): TubePlan {
  const { gender, ageDays } = parseAgeSex(sheet.ageSex);
  const ready: string[] = [];
  let held = 0;
  for (const line of sheet.lines) {
    if (line.status !== 'Entered') {
      continue;
    }
    const interpretation = interpretLine(line, undefined, { gender, ageDays });
    if (isReadyForBulkVerify(line, interpretation)) {
      ready.push(line.resultId);
    } else {
      held++;
    }
  }
  return { sheet, ready, held };
}
