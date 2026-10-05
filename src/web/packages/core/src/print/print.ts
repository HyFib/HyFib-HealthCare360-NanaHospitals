import { bridge, type PageSize, type PrintHtmlRequest } from '../bridge/bridge';
import { loadPrinterSetup, printerFor, roleForPageSize } from './printers';

export type PrintOptions = Omit<PrintHtmlRequest, 'html'>;

/**
 * Prints a self-contained HTML document. Inside HC360 Desktop it goes straight to the printer this computer uses for
 * that kind of page (labels, wristbands, 80 mm slips, A4/A5 documents); a kind with no printer set shows the print
 * dialog, so nothing lands on whichever printer happens to be the Windows default. `silent: false` always asks.
 * In a browser it opens the print dialog through a hidden iframe.
 */
export async function printHtml(html: string, options: PrintOptions = {}): Promise<void> {
  if (bridge.isDesktop) {
    await bridge.printHtml({ html, ...(await desktopTarget(options)) });
    return;
  }

  await new Promise<void>((resolve) => {
    const frame = document.createElement('iframe');
    frame.setAttribute('aria-hidden', 'true');
    Object.assign(frame.style, { position: 'fixed', right: '0', bottom: '0', width: '0', height: '0', border: '0' });
    frame.onload = () => {
      setTimeout(() => {
        frame.contentWindow?.focus();
        frame.contentWindow?.print();
        setTimeout(() => {
          frame.remove();
          resolve();
        }, 500);
      }, 150);
    };
    frame.srcdoc = html;
    document.body.appendChild(frame);
  });
}

/**
 * Where HC360 Desktop sends a page: the printer named by the caller, else the printer this computer uses for the
 * page's kind, silently; with neither, the print dialog. An HC360 Desktop too old to keep printers gets the options as
 * they were given.
 */
export async function desktopTarget(options: PrintOptions): Promise<PrintOptions> {
  if (options.silent === false) {
    return options;
  }
  if (options.printerName) {
    return { ...options, silent: true };
  }
  const setup = await loadPrinterSetup();
  if (setup === null) {
    return options;
  }
  const printer = printerFor(setup, roleForPageSize(options.pageSize));
  return printer ? { ...options, printerName: printer.printer, silent: true } : { ...options, silent: false };
}

const PAGE_CSS: Record<PageSize, { size: string; margin: string; fontSize: string }> = {
  A4: { size: 'A4', margin: '12mm', fontSize: '12px' },
  A5: { size: 'A5', margin: '12mm', fontSize: '12px' },
  Receipt80: { size: '80mm auto', margin: '2mm', fontSize: '11px' },
  Label50x25: { size: '50mm 25mm', margin: '2mm', fontSize: '12px' },
  Wristband: { size: '25.4mm 279.4mm', margin: '0', fontSize: '12px' },
};

/** Wraps body HTML with the page setup for the given paper size. */
export function printDocument(body: string, options: { title: string; pageSize?: PageSize; styles?: string }): string {
  const page = PAGE_CSS[options.pageSize ?? 'A4'];
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>${escapeHtml(options.title)}</title>
<style>
@page { size: ${page.size}; margin: ${page.margin}; }
* { box-sizing: border-box; }
body { font-family: "Segoe UI", system-ui, sans-serif; color: #111; font-size: ${page.fontSize}; margin: 0; }
h1, h2, h3 { margin: 0 0 4px; }
table { width: 100%; border-collapse: collapse; }
th, td { text-align: left; padding: 3px 4px; vertical-align: top; }
.muted { color: #555; }
.right { text-align: right; }
.center { text-align: center; }
.rule { border-top: 1px solid #999; margin: 6px 0; }
${options.styles ?? ''}
</style></head><body>${body}</body></html>`;
}

export function escapeHtml(value: string | number | null | undefined): string {
  return String(value ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}
