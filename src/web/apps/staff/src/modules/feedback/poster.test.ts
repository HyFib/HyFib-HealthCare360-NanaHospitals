import { describe, expect, it } from 'vitest';
import { posterHtml } from './poster';

describe('posterHtml', () => {
  const data = {
    facilityName: 'Nana Hospitals <Anna Nagar>',
    formTitle: 'OP "Quick" feedback',
    introText: null,
    url: 'https://portal.example.in/feedback/form/abc123',
  };

  it('escapes every text and keeps the QR markup', () => {
    const html = posterHtml(data, '<svg id="qr"></svg>');
    expect(html).toContain('Nana Hospitals &lt;Anna Nagar&gt;');
    expect(html).toContain('OP &quot;Quick&quot; feedback');
    expect(html).toContain('<svg id="qr"></svg>');
    expect(html).toContain('https://portal.example.in/feedback/form/abc123');
    expect(html).not.toContain('<Anna Nagar>');
  });

  it('uses the intro text when the form has one, and a kiosk headline for kiosk cards', () => {
    expect(posterHtml({ ...data, introText: 'Rate our <b>care</b>' }, '')).toContain('Rate our &lt;b&gt;care&lt;/b&gt;');
    expect(posterHtml({ ...data, kiosk: true }, '')).toContain('Tell us about your visit');
    expect(posterHtml(data, '')).toContain('How was your visit?');
  });
});
