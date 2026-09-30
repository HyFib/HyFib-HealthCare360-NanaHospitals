import { describe, expect, it } from 'vitest';
import { paragraphs, reportShellHtml } from './reportHtml';

const letterhead = {
  facilityName: 'Nana Hospitals',
  address: '12 MG Road, Pune',
  phone: '+91 20 1234 5678',
  email: 'lab@nanahospitals.in',
  nabhNumber: 'NABH-H-2026-001',
  letterheadNote: null,
  logoFileId: null,
};

describe('report frame', () => {
  it('prints the letterhead, patient block, signatories and QR verification', () => {
    const html = reportShellHtml({
      title: 'Laboratory report',
      letterhead,
      patientRows: [
        ['Patient', 'Asha <Rao>'],
        ['UHID', 'NH26000001'],
      ],
      bodyHtml: '<table id="results"></table>',
      signatories: [{ name: 'Dr. Meena Iyer', qualification: 'MD (Pathology)', registrationNumber: 'MMC 12345' }],
      qr: {
        svg: '<svg id="qr"></svg>',
        code: 'ABCD2345EFGH6789JKLM',
        url: 'https://nh.local/api/v1/lab/reports/verify/ABCD2345EFGH6789JKLM',
      },
    });
    expect(html).toContain('Nana Hospitals');
    expect(html).toContain('NABH accreditation NABH-H-2026-001');
    expect(html).toContain('Asha &lt;Rao&gt;');
    expect(html).toContain('<table id="results"></table>');
    expect(html).toContain('Reg. no. MMC 12345');
    expect(html).toContain('<svg id="qr"></svg>');
    expect(html).toContain('Verification code ABCD2345EFGH6789JKLM');
    expect(html).toContain('End of report');
    expect(html).not.toContain('class="banner"');
  });

  it('marks copies that are not final', () => {
    const html = reportShellHtml({
      title: 'Radiology report',
      letterhead,
      patientRows: [],
      bodyHtml: '',
      signatories: [],
      statusBanner: 'Preliminary: not released',
    });
    expect(html).toContain('class="banner">Preliminary: not released');
    expect(html).not.toContain('Scan to verify');
  });

  it('keeps paragraphs and line breaks of free text, escaped', () => {
    expect(paragraphs('Lungs clear.\nNo effusion.\n\nImpression <normal>')).toBe(
      '<p>Lungs clear.<br/>No effusion.</p><p>Impression &lt;normal&gt;</p>',
    );
    expect(paragraphs('  ')).toBe('');
    expect(paragraphs(null)).toBe('');
  });
});
