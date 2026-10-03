import type { LabelLanguage, PrinterRole, RolePrinter, WorkstationPrinters } from '@hc360/core';
import { z } from 'zod';

/** Who may change the printers of a workstation (Administration › Roles and permissions). */
export const PRINTERS_SETUP = 'integrations.printers.setup';

/** "Not set": that kind of print shows the print dialog. */
export const NO_PRINTER = '';

/** What each kind of print covers, in the words staff use. */
export const ROLE_HELP: Record<PrinterRole, string> = {
  labels: 'Sample tube labels, medicine labels, blood unit labels, sterile pack labels and asset tags.',
  wristbands: "Patients' wristbands, printed along the band.",
  receipts: 'Bills, pharmacy invoices, token slips and other 80 mm slips.',
  documents: 'Reports, prescriptions, admission slips and other A4 and A5 pages.',
};

export type LabelRole = Extract<PrinterRole, 'labels' | 'wristbands'>;

/** The sizes a label printer's stock may have, as the server and HC360 Desktop check them. */
export const STOCK_SIZES: Record<
  LabelRole,
  { minWidth: number; maxWidth: number; minHeight: number; maxHeight: number }
> = {
  labels: { minWidth: 25, maxWidth: 110, minHeight: 15, maxHeight: 100 },
  wristbands: { minWidth: 15, maxWidth: 40, minHeight: 80, maxHeight: 350 },
};

/** Common label rolls. */
export const LABEL_SIZES = [
  { widthMm: 50, heightMm: 25 },
  { widthMm: 38, heightMm: 25 },
  { widthMm: 50, heightMm: 38 },
  { widthMm: 75, heightMm: 50 },
  { widthMm: 100, heightMm: 50 },
] as const;

/** A new label or band printer starts with the usual stock: 50 × 25 mm labels, adult bands of 1 × 11 inches. */
export const DEFAULT_STOCK: Record<LabelRole, { widthMm: number; heightMm: number }> = {
  labels: { widthMm: 50, heightMm: 25 },
  wristbands: { widthMm: 25.4, heightMm: 279.4 },
};

export const LANGUAGE_OPTIONS: { value: LabelLanguage; label: string }[] = [
  { value: 'zpl', label: 'ZPL (Zebra and compatible printers)' },
  { value: 'tspl', label: 'TSPL (TSC and compatible printers)' },
  { value: 'driver', label: 'Windows driver (any printer, prints the label as a page)' },
];

export const DPI_OPTIONS = [
  { value: '203', label: '203 dpi (8 dots per mm)' },
  { value: '300', label: '300 dpi (12 dots per mm)' },
];

export const CODE_OPTIONS = [
  { value: 'code128', label: 'Code 128 (line scanners)' },
  { value: 'qr', label: 'QR code (2D scanners)' },
];

/**
 * The language a printer most likely speaks, from its Windows name: Zebra's drivers are called "ZDesigner …", TSC's
 * "TSC …". Anything else is printed through its Windows driver until someone chooses otherwise.
 */
export function guessLanguage(printerName: string): LabelLanguage {
  const name = printerName.toLowerCase();
  if (name.includes('zdesigner') || name.includes('zebra')) {
    return 'zpl';
  }
  if (/\btsc\b/.test(name) || name.startsWith('tsc') || name.includes('tspl')) {
    return 'tspl';
  }
  return 'driver';
}

const oneDecimal = (value: number) => Math.round(value * 10) / 10 === value;

const plainRole = z.object({ printer: z.string() });

function labelRole(role: LabelRole) {
  const sizes = STOCK_SIZES[role];
  const band = role === 'wristbands';
  return z
    .object({
      printer: z.string(),
      language: z.enum(['zpl', 'tspl', 'driver']),
      dpi: z.enum(['203', '300']),
      widthMm: z.number().nullable(),
      heightMm: z.number().nullable(),
      code: z.enum(['code128', 'qr']),
    })
    .superRefine((value, ctx) => {
      if (value.printer === NO_PRINTER || value.language === 'driver') {
        return;
      }
      const width = value.widthMm;
      if (width === null || width < sizes.minWidth || width > sizes.maxWidth || !oneDecimal(width)) {
        ctx.addIssue({
          code: 'custom',
          path: ['widthMm'],
          message: `Enter the ${band ? 'band' : 'label'} width: ${sizes.minWidth} to ${sizes.maxWidth} mm.`,
        });
      }
      const height = value.heightMm;
      if (height === null || height < sizes.minHeight || height > sizes.maxHeight || !oneDecimal(height)) {
        ctx.addIssue({
          code: 'custom',
          path: ['heightMm'],
          message: `Enter the ${band ? 'band length' : 'label height'}: ${sizes.minHeight} to ${sizes.maxHeight} mm.`,
        });
      }
    });
}

export const printerSetupSchema = z.object({
  labels: labelRole('labels'),
  wristbands: labelRole('wristbands'),
  receipts: plainRole,
  documents: plainRole,
});

export type PrinterSetupForm = z.infer<typeof printerSetupSchema>;
export type LabelRoleForm = PrinterSetupForm['labels'];

function labelForm(role: LabelRole, printer: RolePrinter | null | undefined): LabelRoleForm {
  return {
    printer: printer?.printer ?? NO_PRINTER,
    language: printer?.language ?? 'zpl',
    dpi: printer?.dpi === 300 ? '300' : '203',
    widthMm: printer?.widthMm ?? DEFAULT_STOCK[role].widthMm,
    heightMm: printer?.heightMm ?? DEFAULT_STOCK[role].heightMm,
    code: printer?.code === 'qr' ? 'qr' : 'code128',
  };
}

export function toForm(setup: WorkstationPrinters): PrinterSetupForm {
  return {
    labels: labelForm('labels', setup.labels),
    wristbands: labelForm('wristbands', setup.wristbands),
    receipts: { printer: setup.receipts?.printer ?? NO_PRINTER },
    documents: { printer: setup.documents?.printer ?? NO_PRINTER },
  };
}

/** A label or band printer as HC360 Desktop keeps it; null when the role has no printer. */
export function toLabelPrinter(role: LabelRole, value: LabelRoleForm): RolePrinter | null {
  if (value.printer === NO_PRINTER) {
    return null;
  }
  if (value.language === 'driver') {
    return { printer: value.printer, language: 'driver' };
  }
  return {
    printer: value.printer,
    language: value.language,
    dpi: value.dpi === '300' ? 300 : 203,
    widthMm: value.widthMm,
    heightMm: value.heightMm,
    ...(role === 'wristbands' ? { code: value.code } : {}),
  };
}

export function toSetup(form: PrinterSetupForm, updatedBy: string | null): WorkstationPrinters {
  return {
    labels: toLabelPrinter('labels', form.labels),
    wristbands: toLabelPrinter('wristbands', form.wristbands),
    receipts: form.receipts.printer === NO_PRINTER ? null : { printer: form.receipts.printer },
    documents: form.documents.printer === NO_PRINTER ? null : { printer: form.documents.printer },
    updatedBy,
  };
}

/** The change as the server records it in the audit trail (POST /api/v1/integrations/printers/changes). */
export interface PrinterSetupChange {
  computer: string;
  deviceId: string | null;
  roles: {
    role: PrinterRole;
    printer: string | null;
    language: LabelLanguage | null;
    dpi: number | null;
    widthMm: number | null;
    heightMm: number | null;
  }[];
}

export function toChange(setup: WorkstationPrinters, computer: string, deviceId: string | null): PrinterSetupChange {
  const roles: PrinterRole[] = ['labels', 'wristbands', 'receipts', 'documents'];
  return {
    computer,
    deviceId,
    roles: roles.map((role) => {
      const printer = setup[role];
      return {
        role,
        printer: printer?.printer ?? null,
        language: printer?.language ?? null,
        dpi: printer?.dpi ?? null,
        widthMm: printer?.widthMm ?? null,
        heightMm: printer?.heightMm ?? null,
      };
    }),
  };
}

/** "50 × 25 mm" */
export function sizeText(widthMm: number | null | undefined, heightMm: number | null | undefined): string {
  return widthMm && heightMm ? `${widthMm} × ${heightMm} mm` : '';
}
