// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '../api/errors';
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

const { printLabel } = await import('./labels');

const tube = {
  barcode: 'LS2600000123',
  patientName: 'Sharma Ravi Kumar',
  uhid: 'NH00012345',
  ageSex: '46Y/M',
  accessionNumber: 'LA2600123',
  stat: true,
  tube: 'EDTA',
  bench: 'HAEM',
  tests: ['CBC'],
  collectedAt: '2026-09-26T05:02:00Z',
};

function setup(setup: PrinterSetupInfo['setup']): PrinterSetupInfo {
  return { computerName: 'LAB-COLLECTION-1', lockedByPolicy: false, setup };
}

describe('printLabel', () => {
  const api = { post: vi.fn() };
  const page = vi.fn(() => '<html>label page</html>');

  beforeEach(() => {
    api.post.mockReset();
    page.mockClear();
    desktop.getPrinterSetup.mockReset();
    desktop.printHtml.mockClear();
    desktop.printRaw.mockClear();
  });

  it('sends the server’s printer commands to a ZPL or TSPL label printer', async () => {
    desktop.getPrinterSetup.mockResolvedValue(
      setup({ labels: { printer: 'TSC TE244', language: 'tspl', dpi: 300, widthMm: 50, heightMm: 25 } }),
    );
    api.post.mockResolvedValue({ language: 'tspl', content: 'SIZE 50 mm,25 mm\r\n', labels: 1 });

    const result = await printLabel(api, { kind: 'lab-tubes', labels: [tube], page, copies: 2 });

    expect(api.post).toHaveBeenCalledWith('/api/v1/integrations/labels/lab-tubes', {
      printer: { language: 'tspl', dpi: 300, widthMm: 50, heightMm: 25 },
      labels: [tube],
      copies: 2,
    });
    expect(desktop.printRaw).toHaveBeenCalledWith('TSC TE244', 'SIZE 50 mm,25 mm\r\n');
    expect(page).not.toHaveBeenCalled();
    expect(result).toEqual({ via: 'commands', printer: 'TSC TE244' });
  });

  it('prints wristbands on the wristband printer, with the code the computer uses', async () => {
    desktop.getPrinterSetup.mockResolvedValue(
      setup({
        labels: { printer: 'ZDesigner GK420t', language: 'zpl', dpi: 203, widthMm: 50, heightMm: 25 },
        wristbands: { printer: 'ZDesigner HC100', language: 'zpl', dpi: 300, widthMm: 25, heightMm: 254, code: 'qr' },
      }),
    );
    api.post.mockResolvedValue({ language: 'zpl', content: '^XA^XZ', labels: 1 });
    const band = { uhid: 'NH00012345', patientName: 'Sharma Ravi Kumar', allergies: 'Recorded' as const };

    await printLabel(api, { kind: 'wristbands', labels: [band], page });

    expect(api.post).toHaveBeenCalledWith('/api/v1/integrations/labels/wristbands', {
      printer: { language: 'zpl', dpi: 300, widthMm: 25, heightMm: 254 },
      labels: [band],
      copies: 1,
      code: 'QrCode',
    });
    expect(desktop.printRaw).toHaveBeenCalledWith('ZDesigner HC100', '^XA^XZ');
  });

  it('prints the label page through the Windows driver when the printer is set up that way', async () => {
    desktop.getPrinterSetup.mockResolvedValue(setup({ labels: { printer: 'Brother QL-800', language: 'driver' } }));

    const result = await printLabel(api, {
      kind: 'asset-tags',
      labels: [{ assetTag: 'AST-1', assetName: 'Monitor' }],
      page,
    });

    expect(api.post).not.toHaveBeenCalled();
    expect(desktop.printHtml).toHaveBeenCalledWith({
      html: '<html>label page</html>',
      pageSize: 'Label50x25',
      copies: 1,
      silent: true,
      printerName: 'Brother QL-800',
    });
    expect(result).toEqual({ via: 'page', printer: 'Brother QL-800' });
  });

  it('prints the page on the same printer when the label has letters the printer fonts lack', async () => {
    desktop.getPrinterSetup.mockResolvedValue(
      setup({ labels: { printer: 'ZDesigner GK420t', language: 'zpl', dpi: 203, widthMm: 50, heightMm: 25 } }),
    );
    api.post.mockRejectedValue(
      new ApiError({ status: 422, title: 'Cannot complete this action', code: 'labels.text_not_printable' }),
    );

    const result = await printLabel(api, { kind: 'lab-tubes', labels: [{ ...tube, patientName: 'रवि शर्मा' }], page });

    expect(desktop.printRaw).not.toHaveBeenCalled();
    expect(desktop.printHtml).toHaveBeenCalledWith(
      expect.objectContaining({ printerName: 'ZDesigner GK420t', silent: true }),
    );
    expect(result.via).toBe('page');
  });

  it('prints the page on the same printer when the label endpoint refuses a value the page still prints', async () => {
    desktop.getPrinterSetup.mockResolvedValue(
      setup({ labels: { printer: 'ZDesigner GK420t', language: 'zpl', dpi: 203, widthMm: 50, heightMm: 25 } }),
    );
    api.post.mockRejectedValue(
      new ApiError({
        status: 400,
        title: 'One or more fields are invalid',
        errors: { 'Labels[0].Bench': ['The bench must be at most 20 characters.'] },
      }),
    );

    const result = await printLabel(api, {
      kind: 'lab-tubes',
      labels: [{ ...tube, bench: 'Haematology and coagulation' }],
      page,
    });

    expect(desktop.printRaw).not.toHaveBeenCalled();
    expect(desktop.printHtml).toHaveBeenCalledWith(
      expect.objectContaining({ printerName: 'ZDesigner GK420t', silent: true }),
    );
    expect(result.via).toBe('page');
  });

  it.each([
    [401, 'Please sign in again'],
    [403, 'You do not have permission for this action'],
  ])('stops on a %i from the label endpoint and prints nothing', async (status, title) => {
    desktop.getPrinterSetup.mockResolvedValue(
      setup({ labels: { printer: 'ZDesigner GK420t', language: 'zpl', dpi: 203, widthMm: 50, heightMm: 25 } }),
    );
    const refusal = new ApiError({ status, title });
    api.post.mockRejectedValue(refusal);

    await expect(printLabel(api, { kind: 'lab-tubes', labels: [tube], page })).rejects.toBe(refusal);
    expect(desktop.printHtml).not.toHaveBeenCalled();
  });

  it('reports any other refusal instead of printing something else', async () => {
    desktop.getPrinterSetup.mockResolvedValue(
      setup({ labels: { printer: 'ZDesigner GK420t', language: 'zpl', dpi: 203, widthMm: 38, heightMm: 25 } }),
    );
    const refusal = new ApiError({
      status: 422,
      title: 'Cannot complete this action',
      detail: 'The sample number is too long to print as a barcode on 38 mm wide labels.',
      code: 'labels.does_not_fit',
    });
    api.post.mockRejectedValue(refusal);

    await expect(printLabel(api, { kind: 'lab-tubes', labels: [tube], page })).rejects.toBe(refusal);
    expect(desktop.printHtml).not.toHaveBeenCalled();
  });

  it('asks with the print dialog when this computer has no label printer', async () => {
    desktop.getPrinterSetup.mockResolvedValue(setup({ receipts: { printer: 'EPSON TM-T82 Receipt' } }));

    const result = await printLabel(api, { kind: 'cssd-packs', labels: [{ packNumber: 'CS1', setName: 'Set' }], page });

    expect(desktop.printHtml).toHaveBeenCalledWith({
      html: '<html>label page</html>',
      pageSize: 'Label50x25',
      copies: 1,
      silent: false,
    });
    expect(result).toEqual({ via: 'page', printer: null });
  });

  describe('with the labels simulator', () => {
    it('has the server write the job for this computer’s printer and shows the label instead of printing it', async () => {
      desktop.getPrinterSetup.mockResolvedValue(
        setup({ labels: { printer: 'TSC TE244', language: 'tspl', dpi: 300, widthMm: 50, heightMm: 25 } }),
      );
      api.post.mockResolvedValue({ language: 'tspl', content: 'SIZE 50 mm,25 mm\r\n', labels: 1 });

      const result = await printLabel(api, { kind: 'lab-tubes', labels: [tube], page, copies: 2 }, { simulator: true });

      expect(api.post).toHaveBeenCalledWith('/api/v1/integrations/labels/lab-tubes', {
        printer: { language: 'tspl', dpi: 300, widthMm: 50, heightMm: 25 },
        labels: [tube],
        copies: 2,
      });
      expect(desktop.printRaw).not.toHaveBeenCalled();
      expect(desktop.printHtml).toHaveBeenCalledWith({
        html: '<html>label page</html>',
        pageSize: 'Label50x25',
        copies: 2,
        silent: false,
      });
      expect(result).toEqual({ via: 'page', printer: null });
    });

    it('writes for standard stock when this computer has no ZPL or TSPL printer', async () => {
      desktop.getPrinterSetup.mockResolvedValue(setup({ wristbands: { printer: 'HP LaserJet', language: 'driver' } }));
      api.post.mockResolvedValue({ language: 'zpl', content: '^XA^XZ', labels: 1 });
      const band = { uhid: 'NH00012345', patientName: 'Sharma Ravi Kumar', allergies: 'Recorded' as const };

      await printLabel(api, { kind: 'wristbands', labels: [band], page }, { simulator: true });

      expect(api.post).toHaveBeenCalledWith('/api/v1/integrations/labels/wristbands', {
        printer: { language: 'zpl', dpi: 203, widthMm: 25.4, heightMm: 279.4 },
        labels: [band],
        copies: 1,
        code: 'Code128',
      });
      expect(desktop.printHtml).toHaveBeenCalledWith(expect.objectContaining({ pageSize: 'Wristband', silent: false }));
    });

    it('still shows a label the printer fonts cannot print, and stops on any other refusal', async () => {
      desktop.getPrinterSetup.mockResolvedValue(setup({}));
      api.post.mockRejectedValueOnce(
        new ApiError({ status: 422, title: 'Cannot complete this action', code: 'labels.text_not_printable' }),
      );

      await printLabel(api, { kind: 'lab-tubes', labels: [tube], page }, { simulator: true });
      expect(desktop.printHtml).toHaveBeenCalledOnce();

      const refusal = new ApiError({ status: 422, title: 'Cannot complete this action', code: 'labels.does_not_fit' });
      api.post.mockRejectedValueOnce(refusal);
      await expect(printLabel(api, { kind: 'lab-tubes', labels: [tube], page }, { simulator: true })).rejects.toBe(
        refusal,
      );
      expect(desktop.printHtml).toHaveBeenCalledOnce();
    });
  });

  it('prints nothing for no labels', async () => {
    await printLabel(api, { kind: 'dispensing', labels: [], page });

    expect(desktop.getPrinterSetup).not.toHaveBeenCalled();
    expect(page).not.toHaveBeenCalled();
  });
});
