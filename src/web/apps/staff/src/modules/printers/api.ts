import {
  bridge,
  escapeHtml,
  formatDateTime,
  PRINTER_ROLE_LABEL,
  printDocument,
  printHtml,
  type PageSize,
  type PrinterRole,
  type RolePrinter,
  type WorkstationPrinters,
} from '@hc360/core';
import { queryOptions } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { toChange } from './form';

export const printerKeys = {
  all: ['workstation', 'printers'] as const,
  setup: ['workstation', 'printers', 'setup'] as const,
  installed: ['workstation', 'printers', 'installed'] as const,
  device: ['workstation', 'device'] as const,
};

export const printerQueries = {
  /** This computer's printers, from HC360 Desktop. */
  setup: () => queryOptions({ queryKey: printerKeys.setup, queryFn: () => bridge.getPrinterSetup(), retry: false }),
  /** The printers installed in Windows on this computer, the default first. */
  installed: () =>
    queryOptions({ queryKey: printerKeys.installed, queryFn: () => bridge.listPrinters(), retry: false }),
  /** This computer's name and enrolled device id, for the audit trail. */
  device: () =>
    queryOptions({
      queryKey: printerKeys.device,
      queryFn: () => bridge.deviceInfo(),
      retry: false,
      staleTime: Infinity,
    }),
};

export const printersApi = {
  /**
   * Saves this computer's printers: the server first checks that the user may set up printers and names who the change
   * is saved as; HC360 Desktop then saves it for everyone who uses the computer; and once saved, the server records
   * the change in the audit trail. A change HC360 Desktop refuses is not recorded.
   */
  save: async (setup: WorkstationPrinters, computer: string, deviceId: string | null): Promise<WorkstationPrinters> => {
    const change = toChange(setup, computer, deviceId);
    const approval = await api.post<{ changedBy: string }>('/api/v1/integrations/printers/changes/approval', change);
    const saved = await bridge.savePrinterSetup({ ...setup, updatedBy: approval.changedBy });
    try {
      await api.post<void>('/api/v1/integrations/printers/changes', change);
    } catch (error) {
      throw new PrinterChangeNotRecordedError(error);
    }
    return saved;
  },

  /**
   * Prints a test on a printer as it is set on the screen (saved or not): for a ZPL or TSPL label or band printer the
   * server's test label, framed 1 mm inside the edges, with a sample barcode; for any other printer a test page of the
   * role's paper size.
   */
  test: async (role: PrinterRole, printer: RolePrinter, computer: string): Promise<void> => {
    if ((role === 'labels' || role === 'wristbands') && (printer.language === 'zpl' || printer.language === 'tspl')) {
      const commands = await api.post<{ content: string }>('/api/v1/integrations/labels/test', {
        printer: { language: printer.language, dpi: printer.dpi, widthMm: printer.widthMm, heightMm: printer.heightMm },
        stock: role === 'wristbands' ? 'Wristband' : 'Label',
        computer,
      });
      await bridge.printRaw(printer.printer, commands.content);
      return;
    }
    const pageSize = TEST_PAGE_SIZE[role];
    await printHtml(testPage(role, printer.printer, computer, new Date().toISOString()), {
      pageSize,
      printerName: printer.printer,
      silent: true,
    });
  },
};

/** The printers were saved on this computer, but the server could not record the change in the audit trail. */
export class PrinterChangeNotRecordedError extends Error {
  constructor(readonly reason: unknown) {
    super(
      'The printers were saved on this computer, but the change could not be recorded in the audit trail. Save them again when the server can be reached.',
    );
    this.name = 'PrinterChangeNotRecordedError';
  }
}

const TEST_PAGE_SIZE: Record<PrinterRole, PageSize> = {
  labels: 'Label50x25',
  wristbands: 'Wristband',
  receipts: 'Receipt80',
  documents: 'A4',
};

/** A plain test page in the role's paper size: what it is, for which computer and printer, and when. */
export function testPage(role: PrinterRole, printerName: string, computer: string, printedAt: string): string {
  const lines = [
    `<div class="title">HC360 test print</div>`,
    `<div>${escapeHtml(PRINTER_ROLE_LABEL[role])}</div>`,
    `<div>${escapeHtml(printerName)}</div>`,
    `<div>${escapeHtml(computer)} · ${escapeHtml(formatDateTime(printedAt))}</div>`,
  ].join('');
  const wristband = role === 'wristbands';
  const styles = wristband
    ? `.test { position: absolute; top: 0; left: 22mm; width: 150mm; height: 20mm; transform: rotate(90deg); transform-origin: top left;
               border: 0.3mm solid #000; padding: 1mm 3mm; font-size: 8pt; }
       .title { font-weight: 800; font-size: 10pt; }`
    : `.test { border: 0.3mm solid #000; padding: 1mm 2mm; font-size: ${role === 'labels' ? '7pt' : '10pt'}; }
       .title { font-weight: 800; }`;
  return printDocument(`<div class="test">${lines}</div>`, {
    title: 'HC360 test print',
    pageSize: TEST_PAGE_SIZE[role],
    styles,
  });
}
