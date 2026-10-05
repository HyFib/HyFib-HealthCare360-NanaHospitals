import { printLabel, type LabelKind, type LabelPrintJob, type LabelPrintResult } from '@hc360/core';
import { api } from './api';

/**
 * Prints labels on this computer's label printer (wristbands on its wristband printer), as set in Printers on this
 * computer: printer commands from the server for a ZPL or TSPL printer, otherwise the job's own page. See `printLabel`.
 */
export function printLabelJob<K extends LabelKind>(job: LabelPrintJob<K>): Promise<LabelPrintResult> {
  return printLabel(api, job);
}
