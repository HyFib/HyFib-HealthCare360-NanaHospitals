import { describe, expect, it } from 'vitest';
import {
  closingNoteHtml,
  DEFAULT_PRINT_TEMPLATE,
  letterheadHtml,
  templateFor,
  type Letterhead,
  type PrintTemplate,
} from './letterhead';
import { printDocument } from './print';

const LOGO = 'data:image/png;base64,iVBORw0KGgo=';

const nana: Letterhead = {
  name: 'Nana Hospitals',
  legalName: 'Nana Hospitals Pvt Ltd',
  address: '12 MG Road, Pune, Maharashtra, 411001',
  phone: '+912012345678',
  email: 'care@nana.example',
  website: 'nana.example',
  registrationNumber: 'MH/PUNE/123',
  nabhNumber: 'H-2024-0042',
  gstin: '27AAACN1234F1Z5',
  pan: 'AAACN1234F',
  note: 'A unit of <Nana> Trust',
  logo: LOGO,
};

const stationery: PrintTemplate = {
  letterhead: 'Preprinted',
  topSpaceMm: 35,
  bottomSpaceMm: 19,
  showLogo: true,
  closingNote: 'Subject to Pune jurisdiction.\nThank you.',
  paper: null,
};

describe('the letterhead', () => {
  it('prints the facility as the server documents do, with the tax ids and registration where asked', () => {
    const plain = letterheadHtml(nana, DEFAULT_PRINT_TEMPLATE);

    expect(plain).toContain(`<img class="lh-logo" src="${LOGO}" alt="">`);
    expect(plain).toContain('<div class="lh-name">Nana Hospitals Pvt Ltd</div>');
    expect(plain).toContain('12 MG Road, Pune, Maharashtra, 411001');
    expect(plain).toContain('Phone +912012345678 · care@nana.example · nana.example');
    expect(plain).toContain('NABH accreditation H-2024-0042');
    expect(plain).toContain('A unit of &lt;Nana&gt; Trust');
    expect(plain).not.toContain('GSTIN');
    expect(plain).not.toContain('Registration');

    const bill = letterheadHtml(nana, DEFAULT_PRINT_TEMPLATE, {
      taxIds: true,
      registration: true,
      aside: '<div>Bill no.: <b>NHOB1</b></div>',
    });
    expect(bill).toContain('GSTIN 27AAACN1234F1Z5 · PAN AAACN1234F');
    expect(bill).toContain('Registration MH/PUNE/123');
    expect(bill).toContain('<div class="lh-aside"><div>Bill no.: <b>NHOB1</b></div></div>');
  });

  it('prints the name alone until the rest arrives', () => {
    expect(letterheadHtml({ name: 'Nana Hospitals' }, DEFAULT_PRINT_TEMPLATE, { taxIds: true })).toBe(
      '<header class="lh"><div class="lh-text"><div class="lh-name">Nana Hospitals</div></div></header>',
    );
  });

  it('leaves the logo off when the template hides it, and never embeds anything but an image', () => {
    expect(letterheadHtml(nana, { ...DEFAULT_PRINT_TEMPLATE, showLogo: false })).not.toContain('<img');
    expect(letterheadHtml({ ...nana, logo: 'javascript:alert(1)' }, DEFAULT_PRINT_TEMPLATE)).not.toContain('<img');
    expect(letterheadHtml({ ...nana, logo: 'https://evil.example/logo.png' }, DEFAULT_PRINT_TEMPLATE)).not.toContain(
      '<img',
    );
    expect(letterheadHtml({ ...nana, logo: `${LOGO}" onerror="alert(1)` }, DEFAULT_PRINT_TEMPLATE)).not.toContain(
      '<img',
    );
  });

  it('on pre-printed stationery keeps only the tax ids and the title, which the paper does not have', () => {
    const bill = letterheadHtml(nana, stationery, { taxIds: true, registration: true, aside: '<b>Receipt</b>' });

    expect(bill).toBe(
      '<header class="lh preprinted"><div class="lh-text"><div class="lh-line">GSTIN 27AAACN1234F1Z5 · PAN AAACN1234F</div></div><div class="lh-aside"><b>Receipt</b></div></header>',
    );
    expect(letterheadHtml(nana, stationery, { aside: '<b>Day-end report</b>' })).not.toContain('Nana');
    expect(letterheadHtml(nana, stationery)).toBe('');
  });

  it('always prints the letterhead on an 80 mm roll, which is never pre-printed, without the website', () => {
    const roll = letterheadHtml(nana, stationery, { compact: true, taxIds: true, aside: '<b>ignored</b>' });

    expect(roll).toContain('<header class="lh compact">');
    expect(roll).toContain('Nana Hospitals Pvt Ltd');
    expect(roll).toContain('Phone +912012345678 · care@nana.example</div>');
    expect(roll).toContain('GSTIN 27AAACN1234F1Z5');
    expect(roll).not.toContain('ignored');
  });

  it('reads the template of each kind of document, the defaults until they arrive', () => {
    expect(templateFor({}, 'bill')).toBe(DEFAULT_PRINT_TEMPLATE);
    expect(templateFor({ templates: { bill: stationery } }, 'bill')).toBe(stationery);
    expect(templateFor({ templates: { bill: stationery } }, 'receipt')).toBe(DEFAULT_PRINT_TEMPLATE);
  });
});

describe('documents printed with a template', () => {
  it('moves the A4 and A5 margins for pre-printed stationery', () => {
    expect(printDocument('<p>Bill</p>', { title: 'Bill', pageSize: 'A4', template: stationery })).toContain(
      '@page { size: A4; margin: 47mm 12mm 31mm; }',
    );
    expect(printDocument('<p>Slip</p>', { title: 'Slip', pageSize: 'A5', template: stationery })).toContain(
      '@page { size: A5; margin: 47mm 12mm 31mm; }',
    );
    expect(printDocument('<p>Bill</p>', { title: 'Bill', pageSize: 'Receipt80', template: stationery })).toContain(
      '@page { size: 80mm auto; margin: 2mm; }',
    );
    expect(printDocument('<p>Bill</p>', { title: 'Bill', template: DEFAULT_PRINT_TEMPLATE })).toContain(
      '@page { size: A4; margin: 12mm; }',
    );
  });

  it('keeps the blank space within what a page can give', () => {
    const stretched = { ...stationery, topSpaceMm: 500, bottomSpaceMm: Number.NaN };

    expect(printDocument('', { title: 'Bill', template: stretched })).toContain('margin: 92mm 12mm 12mm;');
  });

  it('ends the document with the closing note, its line breaks kept and its text escaped', () => {
    const html = printDocument('<p>Bill</p>', {
      title: 'Bill',
      template: { ...stationery, closingNote: ' Goods once sold <are> not taken back.\r\nThank you. ' },
    });

    expect(html).toContain(
      '<p>Bill</p><div class="closing-note">Goods once sold &lt;are&gt; not taken back.<br>Thank you.</div></body>',
    );
    expect(printDocument('<p>Bill</p>', { title: 'Bill' })).toContain('<p>Bill</p></body>');
    expect(closingNoteHtml({ ...stationery, closingNote: '   ' })).toBe('');
    expect(closingNoteHtml(undefined)).toBe('');
  });
});
