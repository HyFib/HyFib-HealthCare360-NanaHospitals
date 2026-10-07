// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type * as Bridge from '../bridge/bridge';
import type { PrinterSetupInfo } from '../bridge/bridge';

const desktop = vi.hoisted(() => ({
  isDesktop: true,
  getPrinterSetup: vi.fn<() => Promise<PrinterSetupInfo>>(),
  printHtml: vi.fn(async () => undefined),
  printRaw: vi.fn(async () => undefined),
}));

vi.mock('../bridge/bridge', async (importOriginal) => ({
  ...(await importOriginal<typeof Bridge>()),
  bridge: desktop,
}));

const { desktopTarget, printDocument, printHtml } = await import('./print');
const { roleForPageSize } = await import('./printers');

const counter: PrinterSetupInfo = {
  computerName: 'OPD-RECEPTION-2',
  lockedByPolicy: false,
  setup: {
    labels: { printer: 'ZDesigner GK420t', language: 'zpl', dpi: 203, widthMm: 50, heightMm: 25 },
    receipts: { printer: 'EPSON TM-T82 Receipt' },
  },
};

describe('printing on HC360 Desktop', () => {
  beforeEach(() => {
    desktop.getPrinterSetup.mockReset();
    desktop.printHtml.mockClear();
  });

  it('sends each kind of page straight to the printer this computer uses for it', async () => {
    desktop.getPrinterSetup.mockResolvedValue(counter);

    await printHtml('<p>slip</p>', { pageSize: 'Receipt80', silent: true });

    expect(desktop.printHtml).toHaveBeenCalledWith({
      html: '<p>slip</p>',
      pageSize: 'Receipt80',
      printerName: 'EPSON TM-T82 Receipt',
      silent: true,
    });
  });

  it('asks with the print dialog when this computer has no printer for the kind of page', async () => {
    desktop.getPrinterSetup.mockResolvedValue(counter);

    expect(await desktopTarget({ pageSize: 'A4' })).toEqual({ pageSize: 'A4', silent: false });
    expect(await desktopTarget({ pageSize: 'Wristband', silent: true })).toEqual({
      pageSize: 'Wristband',
      silent: false,
    });
  });

  it('prints documents without asking once a document printer is set', async () => {
    desktop.getPrinterSetup.mockResolvedValue({
      ...counter,
      setup: { ...counter.setup, documents: { printer: 'HP LaserJet M404' } },
    });

    expect(await desktopTarget({ pageSize: 'A5', landscape: false })).toEqual({
      pageSize: 'A5',
      landscape: false,
      printerName: 'HP LaserJet M404',
      silent: true,
    });
  });

  it('always asks when the caller asks for the dialog, and uses a printer the caller names', async () => {
    desktop.getPrinterSetup.mockResolvedValue(counter);

    expect(await desktopTarget({ pageSize: 'Label50x25', silent: false })).toEqual({
      pageSize: 'Label50x25',
      silent: false,
    });
    expect(await desktopTarget({ pageSize: 'A4', printerName: 'Ward 3 printer' })).toEqual({
      pageSize: 'A4',
      printerName: 'Ward 3 printer',
      silent: true,
    });
    expect(desktop.getPrinterSetup).not.toHaveBeenCalled();
  });

  it('leaves an older HC360 Desktop, which keeps no printers, to print as it always did', async () => {
    desktop.getPrinterSetup.mockRejectedValue(
      new Error("'printer.getSetup' is not supported by this version of HC360 Desktop."),
    );

    expect(await desktopTarget({ pageSize: 'Label50x25', silent: true })).toEqual({
      pageSize: 'Label50x25',
      silent: true,
    });
  });
});

describe('printing in a browser', () => {
  /** What had focus each time a page was printed. */
  let focusedWhilePrinting: (Element | null)[];

  beforeEach(() => {
    desktop.isDesktop = false;
    focusedWhilePrinting = [];
    vi.useFakeTimers();
    // As in a browser: focusing the frame's window focuses the frame in the page.
    vi.spyOn(HTMLIFrameElement.prototype, 'contentWindow', 'get').mockImplementation(function (
      this: HTMLIFrameElement,
    ) {
      return {
        focus: () => this.focus(),
        print: () => focusedWhilePrinting.push(document.activeElement),
      } as unknown as Window;
    });
  });

  afterEach(() => {
    desktop.isDesktop = true;
    vi.useRealTimers();
    vi.restoreAllMocks();
    document.body.replaceChildren();
  });

  function button(label: string): HTMLButtonElement {
    const element = document.createElement('button');
    element.textContent = label;
    return document.body.appendChild(element);
  }

  it('gives focus back to the Print button once the print frame is gone', async () => {
    const print = button('Print token slip');
    print.focus();

    const printing = printHtml('<p>slip</p>', { pageSize: 'Receipt80' });
    await vi.runAllTimersAsync();
    await printing;

    expect(focusedWhilePrinting).toEqual([expect.any(HTMLIFrameElement)]);
    expect(document.querySelector('iframe')).toBeNull();
    expect(document.activeElement).toBe(print);
  });

  it('gives focus back to the Print button after two prints that overlap (a double click)', async () => {
    const print = button('Print token slip');
    print.focus();

    const printing = [printHtml('<p>slip</p>'), printHtml('<p>slip</p>')];
    await vi.runAllTimersAsync();
    await Promise.all(printing);

    expect(focusedWhilePrinting).toHaveLength(2);
    expect(document.activeElement).toBe(print);
  });

  it('leaves focus where it was moved after the print dialog closed', async () => {
    const print = button('Print token slip');
    const done = button('Done');
    print.focus();

    const printing = printHtml('<p>slip</p>');
    await vi.advanceTimersByTimeAsync(200);
    expect(focusedWhilePrinting).toHaveLength(1);
    done.focus();
    await vi.runAllTimersAsync();
    await printing;

    expect(document.activeElement).toBe(done);
  });
});

describe('page sizes', () => {
  it('maps each page size to the printer that prints it', () => {
    expect(roleForPageSize('Label50x25')).toBe('labels');
    expect(roleForPageSize('Wristband')).toBe('wristbands');
    expect(roleForPageSize('Receipt80')).toBe('receipts');
    expect(roleForPageSize('A4')).toBe('documents');
    expect(roleForPageSize('A5')).toBe('documents');
    expect(roleForPageSize(undefined)).toBe('documents');
  });

  it('lays a wristband page out as an adult band with no margin', () => {
    const html = printDocument('<div>band</div>', { title: 'Wristband <NH1>', pageSize: 'Wristband' });

    expect(html).toContain('@page { size: 25.4mm 279.4mm; margin: 0; }');
    expect(html).toContain('<title>Wristband &lt;NH1&gt;</title>');
  });
});
