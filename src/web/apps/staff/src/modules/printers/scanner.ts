import type { OptionItem } from '@hc360/ui';
import type { ScannerSetupInfo, ScannerStatus, WorkstationScanner } from '@hc360/core';

/**
 * The serial barcode scanner of this computer (INT-BAR): a scanner on a COM port, which HC360 Desktop reads and types
 * into the focused field. A scanner that types like a keyboard (USB HID) needs no setup. The rules match HC360
 * Desktop's (ScannerSetupStore) and the server's (ScannerSetupChangeValidator).
 */

export const NO_SCANNER = '';

export const BAUD_RATES = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200] as const;

export interface ScannerForm {
  port: string;
  baudRate: string;
  dataBits: '7' | '8';
  parity: WorkstationScanner['parity'];
  stopBits: WorkstationScanner['stopBits'];
}

export const BAUD_OPTIONS: OptionItem[] = BAUD_RATES.map((rate) => ({ value: String(rate), label: `${rate} baud` }));

export const DATA_BIT_OPTIONS: OptionItem[] = [
  { value: '8', label: '8' },
  { value: '7', label: '7' },
];

export const PARITY_OPTIONS: OptionItem[] = [
  { value: 'none', label: 'None' },
  { value: 'even', label: 'Even' },
  { value: 'odd', label: 'Odd' },
];

export const STOP_BIT_OPTIONS: OptionItem[] = [
  { value: 'one', label: '1' },
  { value: 'two', label: '2' },
];

/** The ports to choose from: this computer's, the saved one even when it is not plugged in now, and none. */
export function portOptions(info: Pick<ScannerSetupInfo, 'ports' | 'setup'>): OptionItem[] {
  const saved = info.setup.port?.toUpperCase();
  const ports = info.ports.map((port) => ({ value: port, label: port }));
  if (saved && !info.ports.includes(saved)) {
    ports.push({ value: saved, label: `${saved} (not on this computer now)` });
  }
  return [{ value: NO_SCANNER, label: 'None: keyboard scanners only' }, ...ports];
}

/** Most scanners leave the factory at 9600 baud, 8 data bits, no parity and 1 stop bit. */
export function toScannerForm(setup: WorkstationScanner): ScannerForm {
  return {
    port: setup.port?.toUpperCase() ?? NO_SCANNER,
    baudRate: String(setup.port ? setup.baudRate : 9600),
    dataBits: setup.port && setup.dataBits === 7 ? '7' : '8',
    parity: setup.port ? setup.parity : 'none',
    stopBits: setup.port ? setup.stopBits : 'one',
  };
}

export function toScanner(form: ScannerForm): WorkstationScanner {
  return {
    port: form.port === NO_SCANNER ? null : form.port,
    baudRate: Number(form.baudRate),
    dataBits: form.dataBits === '7' ? 7 : 8,
    parity: form.parity,
    stopBits: form.stopBits,
  };
}

/**
 * The change the server approves and records (POST /api/v1/integrations/scanners/changes/approval, then
 * /api/v1/integrations/scanners/changes).
 */
export function toScannerChange(setup: WorkstationScanner, computer: string, deviceId: string | null) {
  return {
    computer,
    deviceId,
    port: setup.port ?? null,
    baudRate: setup.baudRate,
    dataBits: setup.dataBits,
    parity: setup.parity,
    stopBits: setup.stopBits,
  };
}

/** How the status is shown: listening is good news, a missing, busy or failed port needs someone to look. */
export function statusIntent(status: ScannerStatus): 'success' | 'warning' | 'error' | 'info' {
  switch (status.state) {
    case 'listening':
      return status.lastProblem ? 'warning' : 'success';
    case 'not_found':
    case 'busy':
      return 'warning';
    case 'error':
      return 'error';
    default:
      return 'info';
  }
}
