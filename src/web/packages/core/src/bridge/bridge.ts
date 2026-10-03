/**
 * Typed JSON-RPC bridge to HC360 Desktop (WPF + WebView2). In a plain browser every call degrades gracefully:
 * `isDesktop` is false and printing falls back to the browser dialog.
 *
 * Wire format (both directions over chrome.webview.postMessage):
 *   request  { type: 'request', id, method, params }
 *   response { type: 'response', id, ok, result?, error?: { code, message } }
 *   event    { type: 'event', name, payload }
 */

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
  printHtml: (request: PrintHtmlRequest) => invoke<void>('printer.printHtml', request, 60_000),
  printRaw: (printerName: string, data: string) => invoke<void>('printer.printRaw', { printerName, data }),
  openExternal: (url: string) => invoke<void>('shell.openExternal', { url }),
  notify: (title: string, body: string) => invoke<void>('notifications.show', { title, body }),
  dictate: (language = 'en-IN') => invoke<{ text: string }>('speech.dictate', { language }, 120_000),
};
