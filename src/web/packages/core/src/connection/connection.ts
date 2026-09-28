import { create } from 'zustand';
import { bridge, onBridgeEvent, type NetworkStatus } from '../bridge/bridge';

export type RealtimeState = 'connected' | 'connecting' | 'reconnecting' | 'disconnected';

interface ConnectionState {
  browserOnline: boolean;
  serverReachable: boolean;
  lastOkAt: number | null;
  lastFailureAt: number | null;
  realtime: RealtimeState;
  wifi: NetworkStatus | null;
  setReachable(reachable: boolean): void;
  setRealtime(state: RealtimeState): void;
  setWifi(status: NetworkStatus | null): void;
  setBrowserOnline(online: boolean): void;
}

/** Everything the connection pill and the offline banner need (SRS DED-07). */
export const useConnection = create<ConnectionState>((set) => ({
  browserOnline: typeof navigator === 'undefined' ? true : navigator.onLine,
  serverReachable: true,
  lastOkAt: null,
  lastFailureAt: null,
  realtime: 'connecting',
  wifi: null,
  setReachable: (reachable) =>
    set(reachable ? { serverReachable: true, lastOkAt: Date.now() } : { serverReachable: false, lastFailureAt: Date.now() }),
  setRealtime: (realtime) => set({ realtime }),
  setWifi: (wifi) => set({ wifi }),
  setBrowserOnline: (browserOnline) => set({ browserOnline }),
}));

export type ConnectionHealth = 'good' | 'degraded' | 'offline';

export function connectionHealth(state: Pick<ConnectionState, 'browserOnline' | 'serverReachable' | 'realtime' | 'wifi'>): ConnectionHealth {
  if (!state.browserOnline || !state.serverReachable) {
    return 'offline';
  }
  const weakWifi = state.wifi?.detailsAvailable && (state.wifi.signalQuality ?? 100) < 40;
  return state.realtime !== 'connected' || weakWifi ? 'degraded' : 'good';
}

let started = false;

/**
 * Tracks browser online/offline, pings the server every 15 s while visible, and — inside HC360 Desktop —
 * subscribes to WiFi status pushed by the native shell.
 */
export function startConnectionMonitor(): () => void {
  if (started) {
    return () => undefined;
  }
  started = true;
  const store = useConnection.getState();

  const onOnline = () => store.setBrowserOnline(true);
  const onOffline = () => store.setBrowserOnline(false);
  window.addEventListener('online', onOnline);
  window.addEventListener('offline', onOffline);

  const ping = async () => {
    if (document.visibilityState !== 'visible') {
      return;
    }
    try {
      const response = await fetch('/health/live', { cache: 'no-store', signal: AbortSignal.timeout(5000) });
      useConnection.getState().setReachable(response.ok);
    } catch {
      useConnection.getState().setReachable(false);
    }
  };
  const interval = setInterval(() => void ping(), 15_000);
  void ping();

  let stopWifi: (() => void) | undefined;
  if (bridge.isDesktop) {
    bridge.networkStatus().then(store.setWifi, () => undefined);
    stopWifi = onBridgeEvent<NetworkStatus>('network.changed', (status) => useConnection.getState().setWifi(status));
  }

  return () => {
    started = false;
    clearInterval(interval);
    window.removeEventListener('online', onOnline);
    window.removeEventListener('offline', onOffline);
    stopWifi?.();
  };
}
