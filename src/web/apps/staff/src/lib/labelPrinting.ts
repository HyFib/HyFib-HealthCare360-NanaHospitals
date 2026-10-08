import { printLabel, type LabelKind, type LabelPrintJob, type LabelPrintResult } from '@hc360/core';
import { api } from './api';

/**
 * Prints labels on this computer's label printer (wristbands on its wristband printer), as set in Printers on this
 * computer: printer commands from the server for a ZPL or TSPL printer, otherwise the job's own page. While the facility
 * trains with the labels simulator, the labels show in the print dialog instead. See `printLabel`.
 */
export async function printLabelJob<K extends LabelKind>(job: LabelPrintJob<K>): Promise<LabelPrintResult> {
  return printLabel(api, job, { simulator: await labelsSimulated() });
}

const MODE_TTL_MS = 60_000;
let mode: { simulator: boolean; at: number } | null = null;

/** Whether the facility's labels go to the simulator, asked at most once a minute. Unknown counts as printing. */
async function labelsSimulated(): Promise<boolean> {
  if (mode && Date.now() - mode.at < MODE_TTL_MS) {
    return mode.simulator;
  }
  try {
    const answer = await api.get<{ simulator: boolean }>('/api/v1/integrations/labels/mode');
    mode = { simulator: answer.simulator === true, at: Date.now() };
    return mode.simulator;
  } catch {
    return false;
  }
}

/** Forgets the facility's label mode, so the next print asks again (after the setting changes, and in tests). */
export function forgetLabelMode(): void {
  mode = null;
}
