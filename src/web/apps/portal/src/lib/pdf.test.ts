import { ApiError } from '@hc360/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { catchDownloads, type SavedFile } from '@/test/downloads';
import { downloadServerPdf, isPdfUnavailable, pdfFileName } from './pdf';

const PATH = '/api/v1/portal/bills/4f6d2c1a-0b3e-4c5d-8e9f-a1b2c3d4e5f6/pdf';
const PDF = '%PDF-1.7\n%portal test\n';

function pdfResponse(disposition: string | null): Response {
  const headers = new Headers({ 'content-type': 'application/pdf' });
  if (disposition) {
    headers.set('content-disposition', disposition);
  }
  return new Response(PDF, { status: 200, headers });
}

function problem(status: number, body: Record<string, unknown>): Response {
  return new Response(JSON.stringify({ status, ...body }), {
    status,
    headers: { 'content-type': 'application/problem+json' },
  });
}

let answer: () => Response;
let saved: SavedFile[];
const fakeServer = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => answer());

beforeEach(() => {
  fakeServer.mockClear();
  vi.stubGlobal('fetch', fakeServer);
  saved = catchDownloads();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('downloading the hospital PDF', () => {
  it('saves the PDF under the name the server gives it', async () => {
    answer = () => pdfResponse(`attachment; filename=bill-OP2600001.pdf; filename*=UTF-8''bill-OP2600001.pdf`);
    await expect(downloadServerPdf(PATH, 'bill-fallback')).resolves.toEqual({
      status: 'saved',
      fileName: 'bill-OP2600001.pdf',
    });
    expect(saved.map((file) => file.name)).toEqual(['bill-OP2600001.pdf']);
    expect(saved[0]?.blob.type).toBe('application/pdf');
    expect(await saved[0]?.blob.text()).toContain('%PDF-1.7');
    const [url, init] = fakeServer.mock.calls[0] ?? [];
    expect(url).toBe(PATH);
    expect(init?.method).toBe('GET');
  });

  it('reads the quoted file name too', async () => {
    answer = () => pdfResponse('attachment; filename="prescription-RX2600123.pdf"');
    await downloadServerPdf(PATH, 'prescription');
    expect(saved.map((file) => file.name)).toEqual(['prescription-RX2600123.pdf']);
  });

  it("uses the page's name for the document when the server gives none", async () => {
    answer = () => pdfResponse(null);
    await expect(downloadServerPdf(PATH, 'bill-OP/2600001')).resolves.toEqual({
      status: 'saved',
      fileName: 'bill-OP-2600001.pdf',
    });
    expect(saved.map((file) => file.name)).toEqual(['bill-OP-2600001.pdf']);
  });

  it('says so, without retrying or saving anything, when the server cannot make PDFs right now', async () => {
    answer = () =>
      problem(503, {
        title: 'The PDF could not be made',
        detail: 'Microsoft Edge was not found on the server.',
        code: 'pdf.unavailable',
      });
    await expect(downloadServerPdf(PATH, 'bill')).resolves.toEqual({ status: 'unavailable' });
    expect(fakeServer).toHaveBeenCalledTimes(1);
    expect(saved).toEqual([]);
  });

  it('leaves other failures to the usual error handling', async () => {
    answer = () =>
      problem(403, { title: 'Forbidden', code: 'portal.patient_denied', detail: 'This record is not linked to you.' });
    const failure = await downloadServerPdf(PATH, 'bill').catch((error: unknown) => error);
    expect(failure).toBeInstanceOf(ApiError);
    expect((failure as ApiError).status).toBe(403);
    expect(isPdfUnavailable(failure)).toBe(false);
    expect(saved).toEqual([]);
  });

  it('never saves a web page as a PDF', async () => {
    answer = () =>
      new Response('<!doctype html><title>Sign in to the WiFi</title>', {
        status: 200,
        headers: { 'content-type': 'text/html' },
      });
    await expect(downloadServerPdf(PATH, 'bill')).rejects.toThrow();
    expect(saved).toEqual([]);
  });
});

describe('PDF file names', () => {
  it('keeps safe characters and always ends in .pdf', () => {
    expect(pdfFileName('discharge-summary-NH26000123-2026-09-25.pdf', 'x')).toBe(
      'discharge-summary-NH26000123-2026-09-25.pdf',
    );
    expect(pdfFileName('lab-report-LAB2600123', 'x')).toBe('lab-report-LAB2600123.pdf');
    expect(pdfFileName('Report of Asha ₹.PDF', 'x')).toBe('Report-of-Asha.pdf');
  });

  it('drops folders from the name', () => {
    expect(pdfFileName('../../windows/evil.pdf', 'x')).toBe('evil.pdf');
    expect(pdfFileName('C:\\temp\\bill.pdf', 'x')).toBe('bill.pdf');
  });

  it("falls back to the page's name, then to 'document'", () => {
    expect(pdfFileName(null, 'bill-OP/1')).toBe('bill-OP-1.pdf');
    expect(pdfFileName('  ', 'bill-OP/1')).toBe('bill-OP-1.pdf');
    expect(pdfFileName('', '')).toBe('document.pdf');
  });

  it('recognises only the "cannot make PDFs right now" answer', () => {
    expect(isPdfUnavailable(new ApiError({ status: 503, code: 'pdf.unavailable' }))).toBe(true);
    expect(isPdfUnavailable(new ApiError({ status: 503 }))).toBe(false);
    expect(isPdfUnavailable(new ApiError({ status: 500, code: 'pdf.unavailable' }))).toBe(false);
    expect(isPdfUnavailable(new Error('pdf.unavailable'))).toBe(false);
  });
});
