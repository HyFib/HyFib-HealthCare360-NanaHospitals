import type * as Core from '@hc360/core';
import type { WorkstationPrinters } from '@hc360/core';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const post = vi.hoisted(() => vi.fn());
const savePrinterSetup = vi.hoisted(() => vi.fn());
const calls = vi.hoisted(() => [] as string[]);

vi.mock('@/lib/api', () => ({ api: { post } }));
vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  bridge: { savePrinterSetup },
}));

const { PrinterChangeNotRecordedError, printersApi } = await import('./api');

const setup: WorkstationPrinters = {
  labels: { printer: 'ZDesigner GK420t', language: 'zpl', dpi: 203, widthMm: 50, heightMm: 25 },
  wristbands: { printer: 'ZDesigner HC100', language: 'zpl', dpi: 300, widthMm: 25, heightMm: 254, code: 'qr' },
  receipts: null,
  documents: null,
};

describe('saving the printers of this computer', () => {
  beforeEach(() => {
    calls.length = 0;
    post.mockReset();
    savePrinterSetup.mockReset();
    post.mockImplementation(async (path: string) => {
      calls.push(path);
      return path.endsWith('/approval') ? { changedBy: 'Asha Rao' } : undefined;
    });
    savePrinterSetup.mockImplementation(async (saved: WorkstationPrinters) => {
      calls.push('desktop');
      return { ...saved, updatedAt: '2026-10-03T04:30:00Z' };
    });
  });

  it('asks the server first, saves as the user the server names, and records the change after the save', async () => {
    const saved = await printersApi.save({ ...setup, updatedBy: 'Someone else' }, 'OPD-2', 'device-1');

    expect(calls).toEqual([
      '/api/v1/integrations/printers/changes/approval',
      'desktop',
      '/api/v1/integrations/printers/changes',
    ]);
    expect(savePrinterSetup).toHaveBeenCalledWith(expect.objectContaining({ updatedBy: 'Asha Rao' }));
    expect(saved.updatedBy).toBe('Asha Rao');
    const recorded = post.mock.calls[1]?.[1] as { roles: { role: string; code: string | null }[] };
    expect(recorded.roles.find((r) => r.role === 'wristbands')?.code).toBe('qr');
  });

  it('records nothing when the server refuses the change or HC360 Desktop does not save it', async () => {
    post.mockRejectedValueOnce(new Error('Forbidden'));
    await expect(printersApi.save(setup, 'OPD-2', null)).rejects.toThrow('Forbidden');
    expect(savePrinterSetup).not.toHaveBeenCalled();

    savePrinterSetup.mockRejectedValueOnce(new Error('printer_not_found'));
    await expect(printersApi.save(setup, 'OPD-2', null)).rejects.toThrow('printer_not_found');
    expect(post).not.toHaveBeenCalledWith('/api/v1/integrations/printers/changes', expect.anything());
  });

  it('says so when the printers were saved but the change could not be recorded', async () => {
    post.mockImplementation(async (path: string) => {
      if (path.endsWith('/approval')) {
        return { changedBy: 'Asha Rao' };
      }
      throw new Error('Network down');
    });

    await expect(printersApi.save(setup, 'OPD-2', null)).rejects.toBeInstanceOf(PrinterChangeNotRecordedError);
    expect(savePrinterSetup).toHaveBeenCalledOnce();
  });
});
