import { isApiError } from '@hc360/core';
import { api } from './api';
import { safeFileName, saveBlob } from './print';

/**
 * The hospital's official PDF of a document (a report, prescription, discharge summary or bill). The server makes it
 * from the same template it prints and sends, so it is the English copy with the letterhead, signatures and QR check.
 */

/** The server's code when it cannot make PDFs right now (HTTP 503). The page can still print its own copy. */
export const PDF_UNAVAILABLE = 'pdf.unavailable';

/** Making a PDF can take the server up to half a minute on a busy day, and a slow mobile network adds to that. */
const PDF_TIMEOUT_MS = 60_000;

export type PdfDownload = { status: 'saved'; fileName: string } | { status: 'unavailable' };

/** True for the server's "PDFs cannot be made right now" answer. */
export function isPdfUnavailable(error: unknown): boolean {
  return isApiError(error) && error.status === 503 && error.code === PDF_UNAVAILABLE;
}

/**
 * The name to save the PDF under: the server's (from Content-Disposition) in safe characters, or the page's own name
 * for the document when the server gave none. It always ends in `.pdf`.
 */
export function pdfFileName(serverName: string | null | undefined, fallback: string): string {
  const base = (serverName ?? '').split(/[\\/]/).pop() ?? '';
  const stem = base.replace(/\.pdf$/i, '').trim();
  return `${safeFileName(stem || fallback)}.pdf`;
}

/**
 * Fetches the PDF at `path` (a GET under /api/v1/portal) and hands it to the browser's downloads. "Cannot make PDFs
 * right now" comes back as `unavailable`; any other failure (no connection, 403, 404 …) is thrown for the page's
 * usual error handling.
 */
export async function downloadServerPdf(path: string, fallbackName: string): Promise<PdfDownload> {
  let file: { blob: Blob; fileName: string | null };
  try {
    file = await api.download(path, { timeoutMs: PDF_TIMEOUT_MS });
  } catch (error) {
    if (isPdfUnavailable(error)) {
      return { status: 'unavailable' };
    }
    throw error;
  }
  // Something between the phone and the hospital (a proxy, a WiFi sign-in page) may answer with a web page instead.
  // That must not be saved as a PDF.
  if (!file.blob.type.toLowerCase().includes('pdf')) {
    throw new Error('The answer was not a PDF.');
  }
  const fileName = pdfFileName(file.fileName, fallbackName);
  saveBlob(file.blob, fileName);
  return { status: 'saved', fileName };
}
