/**
 * Typed JSON-RPC bridge to HC360 Desktop (WPF + WebView2). In a plain browser every call degrades gracefully:
 * `isDesktop` is false and printing falls back to the browser dialog.
 *
 * Wire format (both directions over chrome.webview.postMessage):
 *   request  { type: 'request', id, method, params }
 *   response { type: 'response', id, ok, result?, error?: { code, message } }
 *   event    { type: 'event', name, payload }
 */

import type { FingerprintCapture, FingerprintCaptureRequest, FingerprintReaderStatus } from './fingerprint';

interface WebViewHost {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: MessageEvent) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewHost };
  }
}

export interface DeviceInfo {
  deviceId: string | null;
  deviceSecret: string | null;
  deviceName: string;
  machineName: string;
  appVersion: string;
  osVersion: string;
  kiosk: boolean;
}

export interface NetworkStatus {
  wifiConnected: boolean;
  ssid: string | null;
  signalQuality: number | null;
  receiveRateMbps: number | null;
  security: string | null;
  detailsAvailable: boolean;
  note: string | null;
  serverReachable: boolean;
  serverLatencyMs: number | null;
}

export interface PrinterInfo {
  name: string;
  isDefault: boolean;
}

/** The page sizes HC360 prints on (HC360 Desktop has the same table). The wristband is an adult band, 1 × 11 inches. */
export type PageSize = 'A4' | 'A5' | 'Receipt80' | 'Label50x25' | 'Wristband';

export interface PrintHtmlRequest {
  html: string;
  printerName?: string;
  silent?: boolean;
  copies?: number;
  pageSize?: PageSize;
  landscape?: boolean;
}

/** The kinds of print a workstation gives a printer to. */
export type PrinterRole = 'labels' | 'wristbands' | 'receipts' | 'documents';

/**
 * How HC360 talks to a label or wristband printer: printer commands made by the server (`zpl`, `tspl`), or the label
 * page printed through the printer's Windows driver (`driver`).
 */
export type LabelLanguage = 'zpl' | 'tspl' | 'driver';

/** The printer for one kind of print, as HC360 Desktop keeps it. */
export interface RolePrinter {
  printer: string;
  /** Labels and wristbands only. */
  language?: LabelLanguage | null;
  dpi?: 203 | 300 | null;
  /** One label's width, or a band's width across the wrist. */
  widthMm?: number | null;
  /** One label's height, or a band's length. */
  heightMm?: number | null;
  /** Wristbands only: how the UHID is coded. */
  code?: 'code128' | 'qr' | null;
}

/** Which printer this computer uses for each kind of print. A kind with no printer shows the print dialog. */
export interface WorkstationPrinters {
  labels?: RolePrinter | null;
  wristbands?: RolePrinter | null;
  receipts?: RolePrinter | null;
  documents?: RolePrinter | null;
  updatedAt?: string | null;
  updatedBy?: string | null;
}

export interface PrinterSetupInfo {
  setup: WorkstationPrinters;
  /** IT fixed the printers in the HC360 Desktop policy: they are shown, not changed. */
  lockedByPolicy: boolean;
  computerName: string;
  /**
   * Why a printer that was set is not used (not installed any more, or not a valid setup): that kind of print uses the
   * page and the print dialog until someone sets it again. Older HC360 Desktops leave it out.
   */
  problems?: string[] | null;
}

/**
 * The barcode scanner on one of this computer's serial (COM) ports, as HC360 Desktop keeps it: an RS-232 scanner, or a
 * USB scanner in its serial mode. No port means none (a scanner that types like a keyboard needs no setup).
 */
export interface WorkstationScanner {
  port?: string | null;
  baudRate: number;
  dataBits: 7 | 8;
  parity: 'none' | 'even' | 'odd';
  stopBits: 'one' | 'two';
  updatedAt?: string | null;
  updatedBy?: string | null;
}

/** Whether HC360 Desktop is listening to the serial scanner now. */
export interface ScannerStatus {
  state: 'off' | 'listening' | 'not_found' | 'busy' | 'error';
  message: string;
  lastScanAt?: string | null;
  /** Why the last scan was refused (the port's speed or parity does not match the scanner's, say). */
  lastProblem?: string | null;
}

export interface ScannerSetupInfo {
  setup: WorkstationScanner;
  /** IT fixed the scanner in the HC360 Desktop policy: it is shown, not changed. */
  lockedByPolicy: boolean;
  computerName: string;
  /** Why a scanner that was set is not used. */
  problems: string[];
  status: ScannerStatus;
  /** The COM ports this computer has now, in number order. */
  ports: string[];
}

/** The certificate that signed, as HC360 Desktop read it from the Windows store. */
export interface SignerCertificate {
  subject: string;
  /** The subject's common name: the doctor's name on an Indian DSC. */
  signerName: string;
  serialNumber: string;
  issuer: string;
  thumbprint: string;
  notBefore: string;
  notAfter: string;
}

/** A detached CMS/PKCS#7 signature (DER, base64) made with the doctor's Digital Signature Certificate. */
export interface DscSignature {
  signature: string;
  certificate: SignerCertificate;
}

/** A desktop bridge call that failed, with the desktop's reason code (`printer_not_found`, `method_not_found`…). */
export class BridgeError extends Error {
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = 'BridgeError';
    this.code = code;
  }
}

type BridgeResponse = {
  type: 'response';
  id: number;
  ok: boolean;
  result?: unknown;
  error?: { code: string; message: string };
};
type BridgeEvent = { type: 'event'; name: string; payload: unknown };

const host: WebViewHost | undefined = typeof window !== 'undefined' ? window.chrome?.webview : undefined;
const pending = new Map<
  number,
  { resolve: (value: unknown) => void; reject: (reason: Error) => void; timer: ReturnType<typeof setTimeout> }
>();
const listeners = new Map<string, Set<(payload: unknown) => void>>();
let sequence = 0;

if (host) {
  host.addEventListener('message', (event) => {
    const message = event.data as BridgeResponse | BridgeEvent;
    if (message?.type === 'response') {
      const entry = pending.get(message.id);
      if (!entry) {
        return;
      }
      pending.delete(message.id);
      clearTimeout(entry.timer);
      if (message.ok) {
        entry.resolve(message.result);
      } else {
        entry.reject(
          new BridgeError(message.error?.code ?? 'failed', message.error?.message ?? 'Desktop bridge call failed'),
        );
      }
    } else if (message?.type === 'event') {
      listeners.get(message.name)?.forEach((listener) => listener(message.payload));
    }
  });
}

export const isDesktop = host !== undefined;

export function invoke<T>(method: string, params?: unknown, timeoutMs = 20_000): Promise<T> {
  if (!host) {
    return Promise.reject(new BridgeError('not_desktop', `'${method}' is only available in HC360 Desktop.`));
  }
  const id = ++sequence;
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new BridgeError('timeout', `Desktop bridge call '${method}' timed out.`));
    }, timeoutMs);
    pending.set(id, { resolve: resolve as (value: unknown) => void, reject, timer });
    host.postMessage({ type: 'request', id, method, params });
  });
}

export function onBridgeEvent<T>(name: string, listener: (payload: T) => void): () => void {
  const set = listeners.get(name) ?? new Set();
  set.add(listener as (payload: unknown) => void);
  listeners.set(name, set);
  return () => set.delete(listener as (payload: unknown) => void);
}

export const bridge = {
  isDesktop,
  deviceInfo: () => invoke<DeviceInfo>('device.info'),
  networkStatus: () => invoke<NetworkStatus>('network.status'),
  listPrinters: () => invoke<PrinterInfo[]>('printer.list'),
  /** This computer's printers for each kind of print. An HC360 Desktop older than this answers `method_not_found`. */
  getPrinterSetup: () => invoke<PrinterSetupInfo>('printer.getSetup'),
  /** Saves this computer's printers for everyone who uses it; returns what was saved. */
  savePrinterSetup: (setup: WorkstationPrinters) => invoke<WorkstationPrinters>('printer.saveSetup', { setup }),
  /** This computer's serial barcode scanner. An HC360 Desktop older than this answers `method_not_found`. */
  getScannerSetup: () => invoke<ScannerSetupInfo>('scanner.getSetup'),
  /** Saves this computer's serial scanner for everyone who uses it and listens to it; returns what was saved. */
  saveScannerSetup: (setup: WorkstationScanner) => invoke<WorkstationScanner>('scanner.saveSetup', { setup }),
  printHtml: (request: PrintHtmlRequest) => invoke<void>('printer.printHtml', request, 60_000),
  printRaw: (printerName: string, data: string) => invoke<void>('printer.printRaw', { printerName, data }),
  openExternal: (url: string) => invoke<void>('shell.openExternal', { url }),
  notify: (title: string, body: string) => invoke<void>('notifications.show', { title, body }),
  dictate: (language = 'en-IN') => invoke<{ text: string }>('speech.dictate', { language }, 120_000),
  /**
   * Signs a prescription's canonical bytes (base64, from the server) with a DSC the doctor picks in the Windows
   * certificate dialog; the token asks for its PIN, so the call waits up to five minutes. Fails with `cancelled`,
   * `no_certificate`, `signing_failed` or `busy`; an HC360 Desktop older than this answers `method_not_found`.
   */
  signPrescription: (content: string) => invoke<DscSignature>('dsc.signPrescription', { content }, 300_000),
  /** Whether this computer's fingerprint reader can capture now. An HC360 Desktop older than this answers `method_not_found`. */
  fingerprintStatus: () => invoke<FingerprintReaderStatus>('fingerprint.status'),
  /**
   * Asks the fingerprint reader for a capture with the options the server set up (INT-BIO); the device encrypts it for
   * UIDAI alone. The call waits for the device's own timeout, while staff place the finger, and the time to find the
   * reader and encrypt the capture. Fails with a `FingerprintErrorCode`.
   */
  captureFingerprint: (request: FingerprintCaptureRequest) =>
    invoke<FingerprintCapture>(
      'fingerprint.capture',
      {
        fingerCount: request.fingerCount,
        dataType: request.fingerDataType,
        timeoutSeconds: request.timeoutSeconds,
        environment: request.environment,
      },
      (request.timeoutSeconds + 30) * 1000,
    ),
};
