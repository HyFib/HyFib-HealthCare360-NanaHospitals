import type * as Core from '@hc360/core';
import type { WorkstationScanner } from '@hc360/core';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { portOptions, statusIntent, toScanner, toScannerForm } from './scanner';

const post = vi.hoisted(() => vi.fn());
const saveScannerSetup = vi.hoisted(() => vi.fn());
const calls = vi.hoisted(() => [] as string[]);

vi.mock('@/lib/api', () => ({ api: { post } }));
vi.mock('@hc360/core', async (importOriginal) => ({
  ...(await importOriginal<typeof Core>()),
  bridge: { saveScannerSetup },
}));

const { PrinterChangeNotRecordedError, scannersApi } = await import('./api');

const scanner: WorkstationScanner = { port: 'COM3', baudRate: 19200, dataBits: 7, parity: 'even', stopBits: 'one' };

describe('the scanner form', () => {
  it('starts a computer with no serial scanner at the settings most scanners leave the factory with', () => {
    expect(toScannerForm({ port: null, baudRate: 0, dataBits: 8, parity: 'odd', stopBits: 'two' })).toEqual({
      port: '',
      baudRate: '9600',
      dataBits: '8',
      parity: 'none',
      stopBits: 'one',
    });
  });

  it('gives HC360 Desktop back what was saved', () => {
    expect(toScanner(toScannerForm(scanner))).toEqual(scanner);
    expect(toScanner({ ...toScannerForm(scanner), port: '' }).port).toBeNull();
  });

  it('offers no scanner, the ports this computer has, and the saved port even when it is unplugged', () => {
    expect(portOptions({ ports: ['COM1', 'COM3'], setup: { ...scanner, port: 'com7' } })).toEqual([
      { value: '', label: 'None: keyboard scanners only' },
      { value: 'COM1', label: 'COM1' },
      { value: 'COM3', label: 'COM3' },
      { value: 'COM7', label: 'COM7 (not on this computer now)' },
    ]);
  });

  it('shows a missing, busy or failed port, or a refused scan, as something to look at', () => {
    expect(statusIntent({ state: 'listening', message: '' })).toBe('success');
    expect(statusIntent({ state: 'listening', message: '', lastProblem: 'Not a barcode' })).toBe('warning');
    expect(statusIntent({ state: 'not_found', message: '' })).toBe('warning');
    expect(statusIntent({ state: 'busy', message: '' })).toBe('warning');
    expect(statusIntent({ state: 'error', message: '' })).toBe('error');
    expect(statusIntent({ state: 'off', message: '' })).toBe('info');
  });
});

describe('saving the scanner of this computer', () => {
  beforeEach(() => {
    calls.length = 0;
    post.mockReset();
    saveScannerSetup.mockReset();
    post.mockImplementation(async (path: string) => {
      calls.push(path);
      return path.endsWith('/approval') ? { changedBy: 'Asha Rao' } : undefined;
    });
    saveScannerSetup.mockImplementation(async (saved: WorkstationScanner) => {
      calls.push('desktop');
      return { ...saved, updatedAt: '2026-10-08T05:00:00Z' };
    });
  });

  it('asks the server first, saves as the user the server names, and records the change after the save', async () => {
    const saved = await scannersApi.save({ ...scanner, updatedBy: 'Someone else' }, 'PHARMACY-1', 'device-1');

    expect(calls).toEqual([
      '/api/v1/integrations/scanners/changes/approval',
      'desktop',
      '/api/v1/integrations/scanners/changes',
    ]);
    expect(saved.updatedBy).toBe('Asha Rao');
    expect(post.mock.calls[1]?.[1]).toEqual({
      computer: 'PHARMACY-1',
      deviceId: 'device-1',
      port: 'COM3',
      baudRate: 19200,
      dataBits: 7,
      parity: 'even',
      stopBits: 'one',
    });
  });

  it('records nothing when HC360 Desktop does not save it, and says so when the record fails', async () => {
    saveScannerSetup.mockRejectedValueOnce(new Error('not_allowed'));
    await expect(scannersApi.save(scanner, 'PHARMACY-1', null)).rejects.toThrow('not_allowed');
    expect(post).not.toHaveBeenCalledWith('/api/v1/integrations/scanners/changes', expect.anything());

    post.mockImplementation(async (path: string) => {
      if (path.endsWith('/approval')) {
        return { changedBy: 'Asha Rao' };
      }
      throw new Error('Network down');
    });
    const failure = await scannersApi.save(scanner, 'PHARMACY-1', null).catch((error: unknown) => error);
    expect(failure).toBeInstanceOf(PrinterChangeNotRecordedError);
    expect((failure as Error).message).toContain('The scanner was saved on this computer');
  });
});
