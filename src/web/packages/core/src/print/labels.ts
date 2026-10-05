import type { ApiClient } from '../api/client';
import { isApiError } from '../api/errors';
import { bridge } from '../bridge/bridge';
import { printHtml } from './print';
import { loadPrinterSetup, printerFor } from './printers';

// What each label shows, as the server's label endpoints take it (POST /api/v1/integrations/labels/{kind}). The staff
// app fills these from the same data as its own HTML label, so a label looks the same whichever way it prints.

export interface LabTubeLabel {
  barcode: string;
  patientName: string;
  uhid: string;
  ageSex?: string | null;
  accessionNumber?: string | null;
  stat: boolean;
  /** Short tube name, "EDTA". */
  tube?: string | null;
  /** Short bench code, "HAEM". */
  bench?: string | null;
  tests?: string[] | null;
  collectedAt?: string | null;
}

export interface WristbandLabel {
  uhid: string;
  patientName: string;
  ageSex?: string | null;
  admissionNumber?: string | null;
  ward?: string | null;
  bed?: string | null;
  /**
   * What the chart says about allergies: active allergies (a black ALLERGY mark), no known allergies (no mark), or not
   * recorded (an outlined ALLERGIES NOT RECORDED mark).
   */
  allergies: WristbandAllergies;
}

/** The chart's allergy recording status, as the wristband marks it. */
export type WristbandAllergies = 'Recorded' | 'NoKnownAllergies' | 'NotRecorded';

export interface DispensingLabel {
  patientName: string;
  medicine: string;
  /** As the pharmacist reads it, "10 tab". */
  quantity: string;
  directions?: string | null;
  batch?: string | null;
  /** yyyy-MM-dd */
  expiry?: string | null;
  /** yyyy-MM-dd */
  dispensedOn?: string | null;
  hospital?: string | null;
}

export interface BloodUnitLabel {
  unitNumber: string;
  /** "O+", "AB−": printed as "O POS", "AB NEG". */
  bloodGroup: string;
  /** Short component name, "PRBC". */
  component: string;
  volumeMl?: number | null;
  /** Screening complete and non-reactive; otherwise the label says NOT FOR ISSUE. */
  cleared: boolean;
  expiresAt?: string | null;
  storage?: string | null;
}

export interface CssdPackLabel {
  packNumber: string;
  setName: string;
  method?: string | null;
  /** yyyy-MM-dd; null until the load is released (the label then says NOT RELEASED). */
  sterileUntil?: string | null;
}

export interface AssetTagLabel {
  assetTag: string;
  assetName: string;
  hospital?: string | null;
}

/** The kinds of label, named as the server's endpoints are. */
export interface LabelData {
  'lab-tubes': LabTubeLabel;
  wristbands: WristbandLabel;
  dispensing: DispensingLabel;
  'blood-units': BloodUnitLabel;
  'cssd-packs': CssdPackLabel;
  'asset-tags': AssetTagLabel;
}

export type LabelKind = keyof LabelData;

export interface LabelPrintJob<K extends LabelKind> {
  kind: K;
  labels: readonly LabelData[K][];
  /**
   * The same labels as a page (HTML): printed through the printer's Windows driver when this computer has no label
   * printer that takes printer commands, when the label's text has letters the printer's own fonts lack, and in a
   * browser. Built only when it is needed.
   */
  page: () => string | Promise<string>;
  copies?: number;
}

/** How the labels were printed, and on which printer (null: the person chose in the print dialog). */
export interface LabelPrintResult {
  via: 'commands' | 'page';
  printer: string | null;
}

/** The server's answer: printer commands for every label. */
interface LabelCommands {
  language: string;
  content: string;
  labels: number;
}

/**
 * Prints labels (INT-BAR). On HC360 Desktop with a ZPL or TSPL printer set up for the kind of label (Printers on this
 * computer), the server writes the printer commands for that printer's resolution and label size, and the desktop
 * sends them straight to it: barcodes print with every bar a whole number of dots, so they scan. Otherwise the label
 * page is printed: silently on the printer set up for the kind, or with the print dialog when there is none, and
 * always with the dialog in a browser. Wristbands use the wristband printer; every other label the label printer.
 */
export async function printLabel<K extends LabelKind>(
  api: Pick<ApiClient, 'post'>,
  job: LabelPrintJob<K>,
): Promise<LabelPrintResult> {
  if (job.labels.length === 0) {
    return { via: 'page', printer: null };
  }
  const wristband = job.kind === 'wristbands';
  const printer = printerFor(await loadPrinterSetup(), wristband ? 'wristbands' : 'labels');
  const copies = job.copies ?? 1;

  if (printer && (printer.language === 'zpl' || printer.language === 'tspl')) {
    try {
      const commands = await api.post<LabelCommands>(`/api/v1/integrations/labels/${job.kind}`, {
        printer: { language: printer.language, dpi: printer.dpi, widthMm: printer.widthMm, heightMm: printer.heightMm },
        labels: job.labels,
        copies,
        ...(wristband ? { code: printer.code === 'qr' ? 'QrCode' : 'Code128' } : {}),
      });
      await bridge.printRaw(printer.printer, commands.content);
      return { via: 'commands', printer: printer.printer };
    } catch (error) {
      if (!printsAsPage(error)) {
        throw error;
      }
    }
  }

  // Silent on the printer set for the kind; with none set, printHtml shows the print dialog instead.
  await printHtml(await job.page(), {
    pageSize: wristband ? 'Wristband' : 'Label50x25',
    copies,
    silent: true,
    ...(printer ? { printerName: printer.printer } : {}),
  });
  return { via: 'page', printer: printer?.printer ?? null };
}

/**
 * Label data the server would not write as printer commands that the page still prints: letters the printer's own
 * fonts lack (a name in Devanagari), or a value the label endpoint refuses (400, for example a longer ward name than
 * it takes). Sign-in, permission, network and server errors are not: those stop the print and are shown.
 */
function printsAsPage(error: unknown): boolean {
  return isApiError(error) && (error.code === 'labels.text_not_printable' || error.status === 400);
}
