import { isApiError, printPrintableDocument, saveFile, type ApiError, type PrintableDocumentDto } from '@hc360/core';
import { useNotify } from '@hc360/ui';
import { useMutation } from '@tanstack/react-query';
import { api } from './api';

/**
 * Documents the server makes from one template each (lab, radiology and cardiology reports, prescriptions, discharge
 * summaries, the full health check-up report, A4 bills). A document is named by its resource path: `<path>/document`
 * is the page to print and `<path>/pdf` the same page as a PDF, so the printed copy, the download and the copy sent to
 * the patient are always identical. Each module's api.ts gives the paths (for example `labReportDocument(id)`).
 */

/** The server makes PDFs on demand and queues them when it is busy. */
const PDF_TIMEOUT_MS = 120_000;

/** What to suggest when a document's PDF cannot be made. */
export const PRINT_INSTEAD = 'You can print it instead: the printed copy is the same document.';

/** Fetches the document and prints it as it is (its own paper size and orientation). */
export async function printServerDocument(path: string): Promise<void> {
  await printPrintableDocument(await api.get<PrintableDocumentDto>(`${path}/document`));
}

/** Downloads the document's PDF under the name the server gave it. */
export async function downloadServerPdf(path: string): Promise<void> {
  const { blob, fileName } = await api.download(`${path}/pdf`, { timeoutMs: PDF_TIMEOUT_MS });
  // A WiFi sign-in page or a proxy may answer with a web page instead; that must not be saved as a PDF.
  if (!blob.type.toLowerCase().includes('pdf')) {
    throw new Error('The answer was not a PDF.');
  }
  saveFile(blob, fileName ?? 'document.pdf');
}

/** The server cannot make PDFs right now (503 `pdf.unavailable`); its detail says why. */
export function isPdfUnavailable(error: unknown): error is ApiError {
  return isApiError(error) && error.code === 'pdf.unavailable';
}

type Notify = ReturnType<typeof useNotify>;

/**
 * Says why a PDF download failed: the server's reason, followed by what to do instead when the reason does not say it
 * already, or the error itself.
 */
export function notifyPdfFailure(notify: Notify, error: unknown, instead?: string): void {
  if (isPdfUnavailable(error)) {
    notify.warning(error.title, instead ? `${error.userMessage} ${instead}` : error.userMessage);
  } else {
    notify.error(error, 'Could not download the PDF');
  }
}

/**
 * Print and Download PDF of server documents, with progress and toasts. Each action takes the document's path, so
 * one hook serves a whole list; `isPending` and `variables` tell which document is busy.
 */
export function useServerDocuments() {
  const notify = useNotify();
  const print = useMutation({
    mutationFn: printServerDocument,
    onError: (error) => notify.error(error, 'Printing failed'),
  });
  const downloadPdf = useMutation({
    mutationFn: downloadServerPdf,
    onSuccess: () => notify.success('PDF downloaded', 'It is in your Downloads folder.'),
    onError: (error) => notifyPdfFailure(notify, error, PRINT_INSTEAD),
  });
  return { print, downloadPdf };
}
