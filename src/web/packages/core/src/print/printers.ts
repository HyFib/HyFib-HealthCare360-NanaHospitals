import { bridge, type PageSize, type PrinterRole, type PrinterSetupInfo, type RolePrinter } from '../bridge/bridge';

/**
 * Which of the workstation's printers a page goes to. Labels and wristbands have their own printers; 80 mm slips go to
 * the receipt printer; A4 and A5 pages (and pages of no stated size) to the document printer.
 */
export function roleForPageSize(pageSize: PageSize | undefined): PrinterRole {
  switch (pageSize) {
    case 'Label50x25':
      return 'labels';
    case 'Wristband':
      return 'wristbands';
    case 'Receipt80':
      return 'receipts';
    default:
      return 'documents';
  }
}

/** Plain-language names of the roles, for screens and messages. */
export const PRINTER_ROLE_LABEL: Record<PrinterRole, string> = {
  labels: 'Labels',
  wristbands: 'Wristbands',
  receipts: 'Receipts and token slips (80 mm)',
  documents: 'Documents (A4 and A5)',
};

/**
 * This computer's printers, read from HC360 Desktop. Null in a browser, and on an HC360 Desktop too old to keep
 * printers (or that cannot answer): printing then works as it did before, with the print dialog or the desktop's own
 * choice.
 */
export async function loadPrinterSetup(): Promise<PrinterSetupInfo | null> {
  if (!bridge.isDesktop) {
    return null;
  }
  try {
    return await bridge.getPrinterSetup();
  } catch {
    return null;
  }
}

/** The printer this computer uses for a kind of print, or null when it has none (the print dialog is shown). */
export function printerFor(setup: PrinterSetupInfo | null, role: PrinterRole): RolePrinter | null {
  const printer = setup?.setup[role];
  return printer && printer.printer.trim() ? printer : null;
}
