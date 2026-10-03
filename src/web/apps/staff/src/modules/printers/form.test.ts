import type { WorkstationPrinters } from '@hc360/core';
import { describe, expect, it } from 'vitest';
import { guessLanguage, printerSetupSchema, toChange, toForm, toSetup } from './form';

const counter: WorkstationPrinters = {
  labels: { printer: 'ZDesigner GK420t', language: 'zpl', dpi: 203, widthMm: 50, heightMm: 25 },
  wristbands: { printer: 'ZDesigner HC100', language: 'zpl', dpi: 300, widthMm: 25.4, heightMm: 279.4, code: 'qr' },
  receipts: { printer: 'EPSON TM-T82 Receipt' },
  documents: null,
};

describe('printer setup form', () => {
  it('reads the saved printers and gives them back unchanged', () => {
    const form = toForm(counter);

    expect(form.documents.printer).toBe('');
    expect(form.wristbands).toEqual({
      printer: 'ZDesigner HC100',
      language: 'zpl',
      dpi: '300',
      widthMm: 25.4,
      heightMm: 279.4,
      code: 'qr',
    });
    expect(toSetup(form)).toEqual(counter);
  });

  it('starts a printer not set yet with the usual stock: 50 × 25 mm labels and adult bands', () => {
    const form = toForm({});

    expect(form.labels).toMatchObject({ printer: '', language: 'zpl', dpi: '203', widthMm: 50, heightMm: 25 });
    expect(form.wristbands).toMatchObject({ widthMm: 25.4, heightMm: 279.4, code: 'code128' });
    expect(toSetup(form)).toEqual({ labels: null, wristbands: null, receipts: null, documents: null });
  });

  it('keeps only the printer for a label printer on its Windows driver', () => {
    const form = toForm(counter);
    form.labels.language = 'driver';
    form.labels.widthMm = null;

    expect(printerSetupSchema.safeParse(form).success).toBe(true);
    expect(toSetup(form).labels).toEqual({ printer: 'ZDesigner GK420t', language: 'driver' });
  });

  it('asks for a label size the printer can take, and a band size for wristbands', () => {
    const form = toForm(counter);
    form.labels.widthMm = 120;
    form.wristbands.heightMm = 25;

    const result = printerSetupSchema.safeParse(form);

    expect(result.success).toBe(false);
    expect(result.error?.issues.map((issue) => [issue.path.join('.'), issue.message])).toEqual([
      ['labels.widthMm', 'Enter the label width: 25 to 110 mm.'],
      ['wristbands.heightMm', 'Enter the band length: 80 to 350 mm.'],
    ]);
  });

  it('does not ask for sizes of a role with no printer', () => {
    const form = toForm({});
    form.labels.widthMm = null;

    expect(printerSetupSchema.safeParse(form).success).toBe(true);
  });

  it('guesses the language from the Windows printer name', () => {
    expect(guessLanguage('ZDesigner GK420t')).toBe('zpl');
    expect(guessLanguage('Zebra ZD230')).toBe('zpl');
    expect(guessLanguage('TSC TE244')).toBe('tspl');
    expect(guessLanguage('Brother QL-800')).toBe('driver');
  });

  it('describes the change for the audit trail, every kind of print included, with the wristband code', () => {
    expect(toChange(counter, 'OPD Reception 2', 'device-1')).toEqual({
      computer: 'OPD Reception 2',
      deviceId: 'device-1',
      roles: [
        {
          role: 'labels',
          printer: 'ZDesigner GK420t',
          language: 'zpl',
          dpi: 203,
          widthMm: 50,
          heightMm: 25,
          code: null,
        },
        {
          role: 'wristbands',
          printer: 'ZDesigner HC100',
          language: 'zpl',
          dpi: 300,
          widthMm: 25.4,
          heightMm: 279.4,
          code: 'qr',
        },
        {
          role: 'receipts',
          printer: 'EPSON TM-T82 Receipt',
          language: null,
          dpi: null,
          widthMm: null,
          heightMm: null,
          code: null,
        },
        { role: 'documents', printer: null, language: null, dpi: null, widthMm: null, heightMm: null, code: null },
      ],
    });
  });
});
