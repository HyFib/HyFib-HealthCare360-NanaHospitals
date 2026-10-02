import { printHtml } from './print';

/**
 * A document the server makes from its one template (lab, radiology and cardiology reports, prescriptions, discharge
 * summaries, A4 bills…), as `GET …/document` returns it for printing. The server turns the same HTML into the PDF
 * (`GET …/pdf`), so the printed copy, the download and the copy sent to the patient are identical.
 */
export interface PrintableDocumentDto {
  title: string;
  /** Download name without extension. */
  fileName: string;
  pageSize: 'A4' | 'A5';
  landscape: boolean;
  /** The whole page: styles inline, images as data: URIs, no scripts. */
  html: string;
}

/** Prints a document made by the server as it is, on its own paper size and orientation. */
export function printPrintableDocument(printable: PrintableDocumentDto): Promise<void> {
  return printHtml(printable.html, { pageSize: printable.pageSize, landscape: printable.landscape });
}

/** Saves a file through the browser's download (works in HC360 Desktop and on tablets). */
export function saveFile(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.rel = 'noopener';
  anchor.style.display = 'none';
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  // Give the download a moment to start before releasing the memory.
  setTimeout(() => URL.revokeObjectURL(url), 30_000);
}
