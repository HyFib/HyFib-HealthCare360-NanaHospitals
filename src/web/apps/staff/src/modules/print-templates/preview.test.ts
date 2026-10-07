import { DEFAULT_PRINT_TEMPLATE, type Letterhead, type PrintTemplate } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import type { PrintTemplateItem } from './api';
import { templatePreviewHtml } from './preview';

const letterhead: Letterhead = {
  name: 'Nana <Hospitals>',
  address: '12 MG Road, Pune',
  gstin: '27AAACN1234F1Z5',
  logo: 'data:image/png;base64,iVBORw0KGgo=',
};

const bills: PrintTemplateItem = {
  key: 'bill',
  name: 'Bills',
  area: 'Billing',
  covers: 'Tax invoices and bills of supply.',
  papers: [],
  allowsPreprinted: true,
  hospitalWide: DEFAULT_PRINT_TEMPLATE,
  facility: null,
};

const stationery: PrintTemplate = {
  letterhead: 'Preprinted',
  topSpaceMm: 40,
  bottomSpaceMm: 15,
  showLogo: true,
  closingNote: 'Subject to Pune jurisdiction.',
  paper: null,
};

describe('the print template preview', () => {
  it('draws the page with the letterhead, the logo and the closing note', () => {
    const html = templatePreviewHtml(bills, DEFAULT_PRINT_TEMPLATE, letterhead);

    expect(html).toContain('width: 210mm;');
    expect(html).toContain('padding: 12mm 12mm 12mm;');
    expect(html).toContain('Nana &lt;Hospitals&gt;');
    expect(html).toContain('<img class="lh-logo"');
    expect(html).toContain('GSTIN 27AAACN1234F1Z5');
    expect(html).toContain('<div class="sample-title">Tax invoice</div>');
    expect(html).not.toContain('class="space"');
    expect(html).not.toContain('<script');
  });

  it('outlines the room left for pre-printed stationery and leaves its letterhead to the paper', () => {
    const html = templatePreviewHtml(bills, stationery, letterhead);

    expect(html).toContain('padding: 52mm 12mm 27mm;');
    expect(html).toContain('<div class="space" style="top:12mm;height:40mm">The stationery\'s letterhead</div>');
    expect(html).toContain('<div class="space" style="bottom:12mm;height:15mm">The stationery\'s footer</div>');
    expect(html).toContain('@page { size: A4; margin: 52mm 12mm 27mm; }');
    expect(html).not.toContain('Nana &lt;Hospitals&gt;');
    expect(html).toContain('GSTIN 27AAACN1234F1Z5');
    expect(html).toContain('<div class="closing-note">Subject to Pune jurisdiction.</div>');
  });

  it('shows prescriptions on the paper chosen, and 80 mm bills on a roll', () => {
    const prescriptions: PrintTemplateItem = {
      ...bills,
      key: 'prescription',
      name: 'Prescriptions',
      papers: ['A5', 'A4'],
    };

    expect(templatePreviewHtml(prescriptions, DEFAULT_PRINT_TEMPLATE, letterhead)).toContain('width: 148mm;');
    expect(templatePreviewHtml(prescriptions, { ...DEFAULT_PRINT_TEMPLATE, paper: 'A4' }, letterhead)).toContain(
      'width: 210mm;',
    );

    const roll = templatePreviewHtml(
      { ...bills, key: 'sales-invoice', name: 'Pharmacy and canteen bills', allowsPreprinted: false },
      stationery,
      letterhead,
    );
    expect(roll).toContain('width: 80mm;');
    expect(roll).toContain('<header class="lh compact">');
    expect(roll).not.toContain('class="space"');
  });
});
