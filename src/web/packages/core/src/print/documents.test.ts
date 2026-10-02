// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { printPrintableDocument, saveFile, type PrintableDocumentDto } from './documents';
import { printHtml } from './print';

vi.mock('./print', () => ({ printHtml: vi.fn(async () => undefined) }));

describe('printPrintableDocument', () => {
  it('prints the server page as it is, on its own paper size and orientation', async () => {
    const printable: PrintableDocumentDto = {
      title: 'Prescription RX2600123',
      fileName: 'prescription-RX2600123',
      pageSize: 'A5',
      landscape: false,
      html: '<!doctype html><html><body>Rx</body></html>',
    };

    await printPrintableDocument(printable);

    expect(printHtml).toHaveBeenCalledWith('<!doctype html><html><body>Rx</body></html>', {
      pageSize: 'A5',
      landscape: false,
    });
  });
});

describe('saveFile', () => {
  const original = { create: URL.createObjectURL, revoke: URL.revokeObjectURL };

  afterEach(() => {
    URL.createObjectURL = original.create;
    URL.revokeObjectURL = original.revoke;
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('downloads the file under the given name and releases it afterwards', () => {
    vi.useFakeTimers();
    URL.createObjectURL = vi.fn(() => 'blob:hc360/1');
    URL.revokeObjectURL = vi.fn();
    const clicked: HTMLAnchorElement[] = [];
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      clicked.push(this);
    });

    saveFile(new Blob(['%PDF-1.7'], { type: 'application/pdf' }), 'lab-report-LR2600001.pdf');

    expect(clicked).toHaveLength(1);
    expect(clicked[0]!.download).toBe('lab-report-LR2600001.pdf');
    expect(clicked[0]!.href).toBe('blob:hc360/1');
    expect(document.querySelector('a')).toBeNull();
    expect(URL.revokeObjectURL).not.toHaveBeenCalled();

    vi.advanceTimersByTime(30_000);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:hc360/1');
  });
});
