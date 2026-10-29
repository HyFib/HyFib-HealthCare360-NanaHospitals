import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createTranslator } from '@/i18n';
import { formattersFor } from '@/i18n/useFormat';
import { billDocument, receiptDocument } from '@/modules/bills/print';
import { dischargeSummaryDocument } from '@/modules/records/print';
import { catchDownloads } from '@/test/downloads';
import {
  downloadPortalDocument,
  escapeMultiline,
  printPortalDocument,
  safeFileName,
  safeSvgMarkup,
  scopeStyles,
  standaloneHtml,
} from './print';
import type { DischargeSummaryDto, PortalBillDetail } from './types';

const t = createTranslator('en');
const f = formattersFor('en');
const EVIL = '<img src=x onerror=alert(1)>';

describe('print helpers', () => {
  it('makes safe file names', () => {
    expect(safeFileName('bill-OP/2026/0001')).toBe('bill-OP-2026-0001');
    expect(safeFileName('../../etc/passwd')).toBe('etc-passwd');
    expect(safeFileName('')).toBe('document');
    expect(safeFileName('x'.repeat(200))).toHaveLength(80);
  });

  it('scopes document styles to the print copy', () => {
    expect(scopeStyles('.a, .b { color: red; }')).toContain('#hc360-print-root .a, #hc360-print-root .b {');
  });

  it('keeps line breaks but escapes clinical text', () => {
    expect(escapeMultiline('a < b\nc & d')).toBe('a &lt; b<br/>c &amp; d');
    expect(escapeMultiline(null)).toBe('');
  });

  it('accepts only a plain QR code SVG', () => {
    expect(safeSvgMarkup('<svg viewBox="0 0 1 1"><path d="M0 0h1v1H0z"/></svg>')).not.toBeNull();
    expect(safeSvgMarkup('<svg><script>alert(1)</script></svg>')).toBeNull();
    expect(safeSvgMarkup('<svg onload="alert(1)"></svg>')).toBeNull();
    expect(safeSvgMarkup('<svg><a href="javascript:alert(1)"/></svg>')).toBeNull();
    expect(safeSvgMarkup('<div></div>')).toBeNull();
    expect(safeSvgMarkup(null)).toBeNull();
  });
});

describe('downloaded copies ("Download a copy")', () => {
  const doc = {
    title: 'Bill <OP/1>',
    body: '<h1>Bill</h1>',
    styles: '\n.head { color: #045E80; }\n',
    fileName: 'bill-OP/1',
  };
  const styleSheet = (html: string) => /<style>([\s\S]*)<\/style>/.exec(html)?.[1] ?? '';

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('keep the font rule and the document styles, and every rule keeps its selector', () => {
    const css = styleSheet(standaloneHtml(doc, 'en'));
    expect(css).toMatch(/\nbody \{ font-family: "Segoe UI", [^}]*; font-size: 11pt; line-height: 1\.4; \}/);
    expect(css).toContain('body th, body td {');
    expect(css).toContain('.head { color: #045E80; }');
    // A "{" right after the start, another rule or a declaration would be a rule without a selector.
    expect(css).not.toMatch(/(^|[{};])\s*\{/);
    expect(css).not.toContain('hc360-print-root');
  });

  it('say which language they are in', () => {
    expect(standaloneHtml(doc, 'en')).toContain('<html lang="en-IN">');
    expect(standaloneHtml(doc, 'hi')).toContain('<html lang="hi-IN">');
  });

  it('escape the title', () => {
    expect(standaloneHtml(doc, 'en')).toContain('<title>Bill &lt;OP/1&gt;</title>');
  });

  it('are saved as an HTML file under a safe name, in the language being read', async () => {
    const saved = catchDownloads();
    downloadPortalDocument(doc, 'hi');
    expect(saved.map((file) => file.name)).toEqual(['bill-OP-1.html']);
    expect(saved[0]?.blob.type).toBe('text/html;charset=utf-8');
    const html = (await saved[0]?.blob.text()) ?? '';
    expect(html).toContain('<html lang="hi-IN">');
    expect(html).toContain('<body><h1>Bill</h1></body>');
  });
});

describe('printing in the page', () => {
  const doc = { title: 'Receipt R/1', body: '<h1>Receipt</h1>', fileName: 'receipt-R-1' };
  const printCopy = () => document.getElementById('hc360-print-root')?.innerHTML;

  beforeEach(() => {
    vi.useFakeTimers();
    document.title = 'Bills';
    vi.spyOn(window, 'print').mockImplementation(() => undefined);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    document.getElementById('hc360-print-root')?.remove();
  });

  it('puts the copy in the page and takes it away once the print dialog closes', () => {
    printPortalDocument(doc);
    expect(window.print).toHaveBeenCalledTimes(1);
    expect(printCopy()).toContain('<h1>Receipt</h1>');
    expect(document.title).toBe('Receipt R/1');

    window.dispatchEvent(new Event('afterprint'));
    expect(printCopy()).toBe('');
    expect(document.title).toBe('Bills');
  });

  it('leaves no timer and no listener behind once the dialog has closed', () => {
    const listening = vi.spyOn(window, 'addEventListener');
    printPortalDocument(doc);
    window.dispatchEvent(new Event('afterprint'));

    expect(vi.getTimerCount()).toBe(0);
    vi.advanceTimersByTime(1000);
    expect(listening.mock.calls.map(([type]) => type)).not.toContain('focus');
  });

  it('takes the copy away when the page has focus again, in a browser that never says the dialog closed', () => {
    printPortalDocument(doc);
    window.dispatchEvent(new Event('focus'));
    expect(printCopy()).toContain('<h1>Receipt</h1>'); // too early: the dialog may still be opening

    vi.advanceTimersByTime(1000);
    window.dispatchEvent(new Event('focus'));
    expect(printCopy()).toBe('');
    expect(document.title).toBe('Bills');
  });

  it('does not reach for the window once it is gone, as when a test file ends before the fallback is due', () => {
    printPortalDocument(doc);
    const page = Object.getOwnPropertyDescriptor(globalThis, 'window');
    // What the test environment does at the end of a file: the window is no longer a global (CI, 28 Oct 2026:
    // "ReferenceError: window is not defined" from the fallback below, a second after the last print in a file).
    Reflect.deleteProperty(globalThis, 'window');
    try {
      expect(() => vi.advanceTimersByTime(1000)).not.toThrow();
    } finally {
      if (page) {
        Object.defineProperty(globalThis, 'window', page);
      }
    }
  });
});

describe('print templates escape everything they print', () => {
  const context = { t, f, hospital: `Nana ${EVIL}`, uhid: 'UH-1', printedAt: '2026-09-28T06:00:00Z' };
  const receipt = {
    id: 'r1',
    receiptNumber: 'R/1',
    purpose: 'BillPayment',
    amount: 500,
    modes: EVIL,
    receivedAt: '2026-09-27T07:00:00Z',
  } as const;
  const bill: PortalBillDetail = {
    id: 'b1',
    billNumber: 'OP/1',
    type: 'OutPatient',
    status: 'Finalized',
    patientId: 'p1',
    patientName: EVIL,
    billedAt: '2026-09-27T06:00:00Z',
    grossAmount: 1000,
    discountAmount: 0,
    taxAmount: 0,
    roundOffAmount: 0,
    netAmount: 1000,
    payerAmount: 0,
    patientAmount: 1000,
    paidAmount: 0,
    dueAmount: 1000,
    lines: [
      {
        description: EVIL,
        quantity: 1,
        unitPrice: 1000,
        discountAmount: 0,
        taxAmount: 0,
        netAmount: 1000,
        serviceDate: '2026-09-27T06:00:00Z',
        isReturn: false,
      },
    ],
    receipts: [receipt],
  };

  it('bill and receipt', () => {
    for (const doc of [billDocument(bill, context), receiptDocument(receipt, bill, context)]) {
      expect(doc.body).not.toContain('<img');
      expect(doc.body).toContain('&lt;img');
      expect(doc.fileName).toMatch(/^[A-Za-z0-9_-]+$/);
    }
  });

  it('discharge summary', () => {
    const summary = {
      id: 's1',
      admissionId: 'a1',
      patientId: 'p1',
      encounterId: null,
      practitionerId: null,
      practitionerName: EVIL,
      admittedAt: '2026-09-20T06:00:00Z',
      dischargedAt: '2026-09-25T06:00:00Z',
      diagnoses: `Dengue\n${EVIL}`,
      diagnosisCodes: ['A90'],
      presentingComplaints: EVIL,
      courseInHospital: null,
      procedures: null,
      investigationsSummary: null,
      conditionAtDischarge: null,
      medications: EVIL,
      advice: null,
      followUpDate: '2026-10-02',
      followUpInstructions: EVIL,
      status: 'Signed',
      signedAt: '2026-09-25T07:00:00Z',
      signedByPractitionerId: null,
      amendmentReason: null,
      amendedAt: null,
      amendmentCount: 0,
      version: 1,
    } satisfies DischargeSummaryDto;
    const doc = dischargeSummaryDocument(summary, {
      t,
      f,
      hospital: 'Nana',
      printedAt: '2026-09-28T06:00:00Z',
      patient: { fullName: EVIL, uhid: 'UH-1' },
    });
    expect(doc.body).not.toContain('<img');
    expect(doc.body).toContain('Dengue<br/>&lt;img');
    expect(doc.fileName).toBe('discharge-summary-2026-09-25');
  });
});
