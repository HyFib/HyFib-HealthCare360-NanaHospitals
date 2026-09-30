import { bridge, type PrintHtmlRequest } from '../bridge/bridge';

/**
 * Prints a self-contained HTML document. Inside HC360 Desktop it goes to the chosen printer (silently for
 * token slips and labels); in a browser it opens the print dialog through a hidden iframe.
 */
export async function printHtml(html: string, options: Omit<PrintHtmlRequest, 'html'> = {}): Promise<void> {
  if (bridge.isDesktop) {
    await bridge.printHtml({ html, ...options });
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

/** Wraps body HTML with the page setup for the given paper size. */
export function printDocument(
  body: string,
  options: { title: string; pageSize?: 'A4' | 'A5' | 'Receipt80' | 'Label50x25'; styles?: string },
): string {
  const size =
    options.pageSize === 'Receipt80'
      ? '80mm auto'
      : options.pageSize === 'Label50x25'
        ? '50mm 25mm'
        : options.pageSize === 'A5'
          ? 'A5'
          : 'A4';
  const margin = options.pageSize === 'Receipt80' || options.pageSize === 'Label50x25' ? '2mm' : '12mm';
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>${escapeHtml(options.title)}</title>
<style>
@page { size: ${size}; margin: ${margin}; }
* { box-sizing: border-box; }
body { font-family: "Segoe UI", system-ui, sans-serif; color: #111; font-size: ${options.pageSize === 'Receipt80' ? '11px' : '12px'}; margin: 0; }
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
