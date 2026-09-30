import { describe, expect, it } from 'vitest';
import { createTranslator } from '@/i18n';
import { formattersFor } from '@/i18n/useFormat';
import { billDocument, receiptDocument } from '@/modules/bills/print';
import { dischargeSummaryDocument } from '@/modules/records/print';
import { escapeMultiline, safeFileName, safeSvgMarkup, scopeStyles } from './print';
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
