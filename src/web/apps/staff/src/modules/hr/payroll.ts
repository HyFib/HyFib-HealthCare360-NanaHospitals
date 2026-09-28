/** Pure payroll helpers: which actions a run offers, the maker-checker steps and the CSV import preview. */
import { formatInr } from '@hc360/core';
import type { PayrollInputKind, PayrollRunStatus } from './labels';

export type RunAction = 'process' | 'editInputs' | 'delete' | 'approve' | 'reopen' | 'markPaid' | 'lock' | 'bankFile';

export const RUN_STEPS: readonly PayrollRunStatus[] = ['Draft', 'Processed', 'Approved', 'Paid', 'Locked'];

export const runStepLabel: Record<PayrollRunStatus, string> = {
  Draft: 'Collect inputs',
  Processed: 'Processed',
  Approved: 'Approved',
  Paid: 'Paid',
  Locked: 'Locked',
};

/** Actions offered for a run in its state, for a user with the given payroll permissions (the server re-checks). */
export function runActions(status: PayrollRunStatus, can: { process: boolean; approve: boolean }): RunAction[] {
  const actions: RunAction[] = [];
  if (can.process && (status === 'Draft' || status === 'Processed')) {
    actions.push('process', 'editInputs', 'delete');
  }
  if (can.approve && status === 'Processed') {
    actions.push('approve');
  }
  if (can.approve && status === 'Approved') {
    actions.push('reopen', 'markPaid');
  }
  if (can.approve && status === 'Paid') {
    actions.push('lock');
  }
  if (can.approve && (status === 'Approved' || status === 'Paid' || status === 'Locked')) {
    actions.push('bankFile');
  }
  return actions;
}

export function runStepIndex(status: PayrollRunStatus): number {
  return RUN_STEPS.indexOf(status);
}

/** Amount column of a payroll input: days for a loss-of-pay override, rupees otherwise. */
export function inputAmountLabel(kind: PayrollInputKind, amount: number): string {
  if (kind === 'LopDaysOverride') {
    return `${amount} day${amount === 1 ? '' : 's'}`;
  }
  return formatInr(amount);
}

export interface CsvInputLine {
  line: number;
  employeeCode: string;
  componentCode: string;
  amount: number;
  description: string | null;
}

export interface CsvPreview {
  lines: CsvInputLine[];
  errors: { line: number; message: string }[];
}

/** Splits one CSV line, honouring quoted fields ("a, b" and doubled quotes). */
export function splitCsvLine(line: string): string[] {
  const fields: string[] = [];
  let current = '';
  let quoted = false;
  for (let index = 0; index < line.length; index++) {
    const char = line[index];
    if (quoted) {
      if (char === '"' && line[index + 1] === '"') {
        current += '"';
        index++;
      } else if (char === '"') {
        quoted = false;
      } else {
        current += char;
      }
    } else if (char === '"') {
      quoted = true;
    } else if (char === ',') {
      fields.push(current.trim());
      current = '';
    } else {
      current += char;
    }
  }
  fields.push(current.trim());
  return fields;
}

const CODE = /^[A-Za-z0-9_-]{1,24}$/;
const MAX_AMOUNT = 10_000_000;

/**
 * Checks `employeeCode,componentCode,amount[,description]` lines before upload (header optional) so the payroll
 * officer sees mistakes with their line numbers. The server validates again and imports all-or-nothing.
 */
export function previewInputsCsv(csv: string): CsvPreview {
  const lines: CsvInputLine[] = [];
  const errors: { line: number; message: string }[] = [];
  const rows = csv.replace(/^﻿/, '').split(/\r?\n/);
  let seenData = false;

  rows.forEach((raw, index) => {
    const lineNumber = index + 1;
    if (!raw.trim()) {
      return;
    }
    const [employeeCode = '', componentCode = '', amountText = '', ...rest] = splitCsvLine(raw);
    if (!seenData && /employee/i.test(employeeCode) && Number.isNaN(Number(amountText))) {
      seenData = true;
      return; // header row
    }
    seenData = true;
    const description = rest.join(',').trim() || null;
    const amount = Number(amountText);
    if (!CODE.test(employeeCode)) {
      errors.push({ line: lineNumber, message: 'Employee code is missing or has invalid characters.' });
    } else if (!CODE.test(componentCode)) {
      errors.push({ line: lineNumber, message: 'Pay head code is missing or has invalid characters.' });
    } else if (!amountText || Number.isNaN(amount) || amount <= 0) {
      errors.push({ line: lineNumber, message: 'The amount must be a number above zero.' });
    } else if (amount > MAX_AMOUNT) {
      errors.push({ line: lineNumber, message: 'The amount is too large.' });
    } else if (!/^\d+(\.\d{1,2})?$/.test(amountText)) {
      errors.push({ line: lineNumber, message: 'Write the amount as digits with at most two decimals, without commas.' });
    } else if (description && description.length > 200) {
      errors.push({ line: lineNumber, message: 'The description is longer than 200 characters.' });
    } else {
      lines.push({ line: lineNumber, employeeCode: employeeCode.toUpperCase(), componentCode: componentCode.toUpperCase(), amount, description });
    }
  });

  return { lines, errors };
}

/** Sum of amounts in the preview, for the "₹ total to import" line. */
export function previewTotal(preview: CsvPreview): number {
  return Math.round(preview.lines.reduce((sum, line) => sum + line.amount, 0) * 100) / 100;
}

export interface StructureLineValue {
  code: string;
  kind: 'Earning' | 'Deduction';
  calculationType: 'Fixed' | 'PercentOfBasic';
  value: number;
}

/**
 * Monthly amounts of a salary structure as the payroll engine computes them before attendance: fixed heads as entered,
 * %-of-basic heads from BASIC. Used for the live preview while HR types a revision.
 */
export function structurePreview(lines: StructureLineValue[]): { basic: number; gross: number; deductions: number; amounts: Record<string, number> } {
  const basic = lines.find((line) => line.code === 'BASIC' && line.calculationType === 'Fixed')?.value ?? 0;
  const amounts: Record<string, number> = {};
  let gross = 0;
  let deductions = 0;
  for (const line of lines) {
    const amount = Math.round((line.calculationType === 'PercentOfBasic' ? (basic * line.value) / 100 : line.value) * 100) / 100;
    amounts[line.code] = amount;
    if (line.kind === 'Earning') {
      gross += amount;
    } else {
      deductions += amount;
    }
  }
  return { basic, gross: Math.round(gross * 100) / 100, deductions: Math.round(deductions * 100) / 100, amounts };
}
