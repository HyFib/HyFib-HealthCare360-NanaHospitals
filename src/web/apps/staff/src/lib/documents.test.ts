import type * as Core from '@hc360/core';
import { ApiError, printPrintableDocument, saveFile, type PrintableDocumentDto } from '@hc360/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import { createElement, type ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from './api';
import {
  downloadServerPdf,
  isPdfUnavailable,
  notifyPdfFailure,
  PRINT_INSTEAD,
  printServerDocument,
  useServerDocuments,
} from './documents';

// The server, the printer and the browser's download are outside the test; what is tested is what the app asks of them.
vi.mock('./api', () => ({ api: { get: vi.fn(), download: vi.fn() } }));
vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  printPrintableDocument: vi.fn(async () => undefined),
  saveFile: vi.fn(),
}));
const notify = vi.hoisted(() => ({ success: vi.fn(), info: vi.fn(), warning: vi.fn(), error: vi.fn() }));
vi.mock('@hc360/ui', () => ({ useNotify: () => notify }));

const REPORT = '/api/v1/lab/reports/r1';

const printable: PrintableDocumentDto = {
  title: 'Lab report NHLR260000045',
  fileName: 'lab-report-NHLR260000045',
  pageSize: 'A4',
  landscape: false,
  html: '<!doctype html><html><body>Report</body></html>',
};

const pdfUnavailable = new ApiError({
  status: 503,
  title: 'The PDF could not be made',
  detail: 'PDF generation is not set up on this server.',
  code: 'pdf.unavailable',
});

function wrapper({ children }: { children: ReactNode }) {
  return createElement(QueryClientProvider, { client: new QueryClient() }, children);
}

beforeEach(() => {
  vi.mocked(api.get).mockReset();
  vi.mocked(api.download).mockReset();
  vi.mocked(printPrintableDocument).mockClear();
  vi.mocked(saveFile).mockClear();
  Object.values(notify).forEach((spy) => spy.mockClear());
});

describe('printServerDocument', () => {
  it('fetches the document and prints it as the server made it', async () => {
    vi.mocked(api.get).mockResolvedValue(printable);

    await printServerDocument(REPORT);

    expect(api.get).toHaveBeenCalledWith('/api/v1/lab/reports/r1/document');
    expect(printPrintableDocument).toHaveBeenCalledWith(printable);
  });
});

describe('downloadServerPdf', () => {
  it('saves the PDF under the name the server gave it', async () => {
    const blob = new Blob(['%PDF-1.7'], { type: 'application/pdf' });
    vi.mocked(api.download).mockResolvedValue({
      blob,
      fileName: 'lab-report-NHLR260000045.pdf',
      headers: new Headers(),
    });

    await downloadServerPdf(REPORT);

    expect(api.download).toHaveBeenCalledWith('/api/v1/lab/reports/r1/pdf', { timeoutMs: 120_000 });
    expect(saveFile).toHaveBeenCalledWith(blob, 'lab-report-NHLR260000045.pdf');
  });

  it('still saves the file when the server sent no name', async () => {
    const blob = new Blob(['%PDF-1.7'], { type: 'application/pdf' });
    vi.mocked(api.download).mockResolvedValue({ blob, fileName: null, headers: new Headers() });

    await downloadServerPdf(REPORT);

    expect(saveFile).toHaveBeenCalledWith(blob, 'document.pdf');
  });

  it('does not save a web page as a PDF (a WiFi sign-in page, for example)', async () => {
    const page = new Blob(['<html>Sign in to the WiFi</html>'], { type: 'text/html' });
    vi.mocked(api.download).mockResolvedValue({ blob: page, fileName: null, headers: new Headers() });

    await expect(downloadServerPdf(REPORT)).rejects.toThrow('The answer was not a PDF.');
    expect(saveFile).not.toHaveBeenCalled();
  });
});

describe('PDF failures', () => {
  it('recognises the server saying it cannot make PDFs', () => {
    expect(isPdfUnavailable(pdfUnavailable)).toBe(true);
    expect(isPdfUnavailable(new ApiError({ status: 503, title: 'The server had a problem' }))).toBe(false);
    expect(isPdfUnavailable(new Error('boom'))).toBe(false);
  });

  it("shows the server's reason with what to do instead", () => {
    notifyPdfFailure(notify, pdfUnavailable, PRINT_INSTEAD);
    expect(notify.warning).toHaveBeenCalledWith(
      'The PDF could not be made',
      `PDF generation is not set up on this server. ${PRINT_INSTEAD}`,
    );
  });

  it('shows the reason alone when it already says what to do', () => {
    notifyPdfFailure(notify, pdfUnavailable);
    expect(notify.warning).toHaveBeenCalledWith(
      'The PDF could not be made',
      'PDF generation is not set up on this server.',
    );
  });

  it('reports any other failure as an error', () => {
    const forbidden = new ApiError({ status: 403, title: 'You do not have permission for this action' });
    notifyPdfFailure(notify, forbidden, PRINT_INSTEAD);
    expect(notify.error).toHaveBeenCalledWith(forbidden, 'Could not download the PDF');
    expect(notify.warning).not.toHaveBeenCalled();
  });
});

describe('useServerDocuments', () => {
  it('prints a document and says so when printing fails', async () => {
    vi.mocked(api.get).mockRejectedValueOnce(new ApiError({ status: 404, title: 'Not found' }));
    const { result } = renderHook(() => useServerDocuments(), { wrapper });

    act(() => result.current.print.mutate(REPORT));

    await waitFor(() => expect(notify.error).toHaveBeenCalledWith(expect.any(ApiError), 'Printing failed'));
    expect(printPrintableDocument).not.toHaveBeenCalled();
  });

  it('confirms a downloaded PDF', async () => {
    vi.mocked(api.download).mockResolvedValue({
      blob: new Blob(['%PDF-1.7'], { type: 'application/pdf' }),
      fileName: 'bill-NHOB260000001.pdf',
      headers: new Headers(),
    });
    const { result } = renderHook(() => useServerDocuments(), { wrapper });

    act(() => result.current.downloadPdf.mutate('/api/v1/billing/bills/b1'));

    await waitFor(() =>
      expect(notify.success).toHaveBeenCalledWith('PDF downloaded', 'It is in your Downloads folder.'),
    );
    expect(saveFile).toHaveBeenCalledWith(expect.any(Blob), 'bill-NHOB260000001.pdf');
  });

  it('suggests printing when the server cannot make the PDF', async () => {
    vi.mocked(api.download).mockRejectedValue(pdfUnavailable);
    const { result } = renderHook(() => useServerDocuments(), { wrapper });

    act(() => result.current.downloadPdf.mutate('/api/v1/clinical/prescriptions/p1'));

    await waitFor(() =>
      expect(notify.warning).toHaveBeenCalledWith(
        'The PDF could not be made',
        `PDF generation is not set up on this server. ${PRINT_INSTEAD}`,
      ),
    );
    expect(saveFile).not.toHaveBeenCalled();
  });
});
