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

export interface PrintHtmlRequest {
  html: string;
  printerName?: string;
  silent?: boolean;
  copies?: number;
  pageSize?: 'A4' | 'A5' | 'Receipt80' | 'Label50x25';
  landscape?: boolean;
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
        entry.reject(new Error(message.error?.message ?? 'Desktop bridge call failed'));
      }
    } else if (message?.type === 'event') {
      listeners.get(message.name)?.forEach((listener) => listener(message.payload));
    }
  });
}

export const isDesktop = host !== undefined;

export function invoke<T>(method: string, params?: unknown, timeoutMs = 20_000): Promise<T> {
  if (!host) {
    return Promise.reject(new Error(`'${method}' is only available in HC360 Desktop.`));
  }
  const id = ++sequence;
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`Desktop bridge call '${method}' timed out.`));
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
  printHtml: (request: PrintHtmlRequest) => invoke<void>('printer.printHtml', request, 60_000),
  printRaw: (printerName: string, data: string) => invoke<void>('printer.printRaw', { printerName, data }),
  openExternal: (url: string) => invoke<void>('shell.openExternal', { url }),
  notify: (title: string, body: string) => invoke<void>('notifications.show', { title, body }),
  dictate: (language = 'en-IN') => invoke<{ text: string }>('speech.dictate', { language }, 120_000),
};
