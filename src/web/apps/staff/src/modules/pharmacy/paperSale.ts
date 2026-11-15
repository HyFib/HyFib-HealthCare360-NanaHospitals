import { isApiError } from '@hc360/core';
import { paperTimeToIso } from '@/modules/patients/downtimeApi';

/**
 * A pharmacy sale written on paper while HC360 was down, entered afterwards (docs/modules/downtime.md): the pharmacy sale
 * number printed on the paper sale book's page and the time written on it. Mirrors `PaperNumber` in `CounterSaleRequest`
 * and `DispenseRequest` (`paper`).
 */
export interface PaperSale {
  number: string;
  /** The time on the paper, as a UTC instant. */
  at: string;
}

/** The counter's entry of a paper sale: off for a sale made now. */
export interface PaperSaleEntry {
  onPaper: boolean;
  number: string;
  /** The date and time on the paper, as `datetime-local` gives it. */
  at: string;
}

export const NOT_A_PAPER_SALE: PaperSaleEntry = { onPaper: false, number: '', at: '' };

/** What the server says is wrong with the number or the time, by field. */
export interface PaperSaleProblems {
  number?: string;
  at?: string;
}

/** The paper sale the request carries, null for a sale made now; `problem` says what is missing. */
export function paperSaleOf(entry: PaperSaleEntry): { paper: PaperSale | null; problem: PaperSaleProblems | null } {
  if (!entry.onPaper) {
    return { paper: null, problem: null };
  }

  const number = entry.number.trim().toUpperCase();
  if (!number) {
    return { paper: null, problem: { number: 'Enter the sale number written on the paper.' } };
  }

  if (!/^[A-Z0-9/-]+$/.test(number)) {
    return { paper: null, problem: { number: 'Enter the number as it is printed.' } };
  }

  const at = paperTimeToIso(entry.at);
  if (!at) {
    return { paper: null, problem: { at: 'Enter the date and time written on the paper.' } };
  }

  if (new Date(at).getTime() > Date.now() + 5 * 60_000) {
    return { paper: null, problem: { at: 'The time on the paper cannot be in the future.' } };
  }

  return { paper: { number, at }, problem: null };
}

/** The server's refusals of the number (`paper.number`) and of the time (`paper.at`, `paper.At`); null for any other error. */
export function paperSaleProblemsOf(error: unknown): PaperSaleProblems | null {
  if (!isApiError(error)) {
    return null;
  }

  const first = (...keys: string[]) => keys.map((k) => error.fieldErrors[k]?.[0]).find((m): m is string => Boolean(m));
  const number = first('paper.number', 'paper.Number');
  const at = first('paper.at', 'paper.At');
  return number || at ? { number, at } : null;
}

/** What the server needs to close a prescription as dispensed on paper, or what is missing from the form. */
export function dispensedOnPaperOf(form: { at: string; invoiceNumber: string; note: string }): {
  body: { at: string; invoiceNumber: string | null; note: string | null } | null;
  problem: string | null;
} {
  const at = paperTimeToIso(form.at);
  if (!at) {
    return { body: null, problem: 'Enter the date and time written on the paper.' };
  }

  if (new Date(at).getTime() > Date.now() + 5 * 60_000) {
    return { body: null, problem: 'The time on the paper cannot be in the future.' };
  }

  const invoiceNumber = form.invoiceNumber.trim().toUpperCase();
  const note = form.note.trim();
  if (!invoiceNumber && !note) {
    return { body: null, problem: 'Enter the sale number from the paper, or say where the paper sale is kept.' };
  }

  return { body: { at, invoiceNumber: invoiceNumber || null, note: note || null }, problem: null };
}
