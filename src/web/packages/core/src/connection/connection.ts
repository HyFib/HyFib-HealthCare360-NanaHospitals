import { create } from 'zustand';
import { bridge, onBridgeEvent, type NetworkStatus } from '../bridge/bridge';

export type RealtimeState = 'connected' | 'connecting' | 'reconnecting' | 'disconnected';

interface ConnectionState {
  browserOnline: boolean;
  serverReachable: boolean;
  /**
   * The server answers but cannot serve: it says it is not ready (`/health/ready` answers 503, or nothing in time while
   * `/health/live` answers), which on a single server is its database down. Every request then gets an answer, an
   * error, so the server counts as reachable; but nothing can be opened or saved, as when it cannot be reached.
   */
  databaseDown: boolean;
  lastOkAt: number | null;
  lastFailureAt: number | null;
  realtime: RealtimeState;
  wifi: NetworkStatus | null;
  setReachable(reachable: boolean): void;
  setDatabaseDown(down: boolean): void;
  setRealtime(state: RealtimeState): void;
  setWifi(status: NetworkStatus | null): void;
  setBrowserOnline(online: boolean): void;
}

/** Everything the connection pill and the offline banner need (SRS DED-07). */
export const useConnection = create<ConnectionState>((set) => ({
  browserOnline: typeof navigator === 'undefined' ? true : navigator.onLine,
  serverReachable: true,
  databaseDown: false,
  lastOkAt: null,
  lastFailureAt: null,
  realtime: 'connecting',
  wifi: null,
  setReachable: (reachable) =>
    set(
      reachable
        ? { serverReachable: true, lastOkAt: Date.now() }
        : { serverReachable: false, lastFailureAt: Date.now() },
    ),
  setDatabaseDown: (databaseDown) => set({ databaseDown }),
  setRealtime: (realtime) => set({ realtime }),
  setWifi: (wifi) => set({ wifi }),
  setBrowserOnline: (browserOnline) => set({ browserOnline }),
}));

/** The facts about the server that say whether the app can work with it. */
type ServerFacts = Pick<ConnectionState, 'browserOnline' | 'serverReachable'> &
  Partial<Pick<ConnectionState, 'databaseDown'>>;

/**
 * What the app can do with the server: work with it, or nothing, because this computer or the server cannot be
 * reached, or because the server answers but its database is down.
 */
export type ServerStatus = 'online' | 'unreachable' | 'database-down';

export function serverStatus(state: ServerFacts): ServerStatus {
  if (!state.browserOnline || !state.serverReachable) {
    return 'unreachable';
  }
  return state.databaseDown ? 'database-down' : 'online';
}

/**
 * True during an outage, whichever kind: the server cannot be reached, or it answers but its database is down. The
 * offline banner shows then, and the offline copy is offered.
 */
export function serverOutage(state: ServerFacts): boolean {
  return serverStatus(state) !== 'online';
}

export type ConnectionHealth = 'good' | 'degraded' | 'offline';

/** Below this WiFi signal quality (per cent) the connection counts as weak. */
export const WEAK_WIFI_SIGNAL = 40;

/** HC360 Desktop reports a network cable up on this workstation: the cable is its link, not its WiFi. */
export function onCable(state: Pick<ConnectionState, 'wifi'>): boolean {
  return state.wifi?.wired === true;
}

/**
 * The WiFi signal quality (per cent) when HC360 Desktop reports it and it is weak, otherwise null. On a network cable the
 * WiFi is not the link, and Windows sends HC360's traffic over the cable, so how weak the WiFi is does not matter.
 */
export function weakWifiSignal(state: Pick<ConnectionState, 'wifi'>): number | null {
  if (onCable(state)) {
    return null;
  }
  const signal = state.wifi?.detailsAvailable ? state.wifi.signalQuality : null;
  return signal !== null && signal !== undefined && signal < WEAK_WIFI_SIGNAL ? signal : null;
}

export function connectionHealth(state: ServerFacts & Pick<ConnectionState, 'realtime' | 'wifi'>): ConnectionHealth {
  if (serverOutage(state)) {
    return 'offline';
  }
  return state.realtime !== 'connected' || weakWifiSignal(state) !== null ? 'degraded' : 'good';
}

const HEALTH_TIMEOUT_MS = 5_000;

/** How often answers that suggest the database is down may ask the server whether it is ready. */
const READINESS_EVERY_MS = 5_000;

let readiness: Promise<void> | null = null;
let readinessAskedAt = Number.NEGATIVE_INFINITY;

/** The server's answer to a health check, or null when none came in time. */
async function healthAnswer(path: string): Promise<Response | null> {
  try {
    return await fetch(path, { cache: 'no-store', signal: AbortSignal.timeout(HEALTH_TIMEOUT_MS) });
  } catch {
    return null;
  }
}

/**
 * Asks the server whether it can serve (`/health/ready`). The server opens a new connection to its database to answer,
 * so it is not asked every 15 s as `/health/live` is, by every workstation, but when an answer suggests the database is
 * down (`noteServerFailure`), and every 15 s while it is (`checkConnection`).
 */
export function checkServerReady(): Promise<void> {
  readiness ??= (async () => {
    readinessAskedAt = Date.now();
    try {
      const ready = await healthAnswer('/health/ready');
      if (ready) {
        // 503 is the server's own "not ready". Any other answer does not say the database is down: 200 (Redis away is
        // still 200), or a proxy's refusal of a path it does not pass on.
        useConnection.getState().setDatabaseDown(ready.status === 503);
        return;
      }
      // No answer in time: the database may hang, which it is only if the server itself still answers.
      const live = await healthAnswer('/health/live');
      if (live?.ok) {
        useConnection.getState().setDatabaseDown(true);
      } else {
        useConnection.getState().setReachable(false);
      }
    } finally {
      readiness = null;
    }
  })();
  return readiness;
}

/**
 * The server answered a request with a failure of its own that names no reason (a 500, or a 502 to 504 no problem code
 * of its explains), as it answers every request while its database is down: asks whether it is ready, at most every
 * few seconds however many such answers come. Once the database is known to be down, the monitor asks every 15 s
 * whether it is back, and such answers say nothing new.
 */
export function noteServerFailure(): void {
  if (!useConnection.getState().databaseDown && Date.now() - readinessAskedAt >= READINESS_EVERY_MS) {
    void checkServerReady();
  }
}

/**
 * Asks whether the server can be reached and, while its database is down, whether it is back: what the monitor does
 * every 15 s, and "Check again" at once.
 */
export async function checkConnection(): Promise<void> {
  const live = await healthAnswer('/health/live');
  useConnection.getState().setReachable(live?.ok === true);
  if (live?.ok && useConnection.getState().databaseDown) {
    await checkServerReady();
  }
}

let started = false;

/**
 * Tracks browser online/offline, pings the server every 15 s while visible (and asks whether it is ready while its
 * database is down), and — inside HC360 Desktop — subscribes to WiFi status pushed by the native shell.
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
    await checkConnection();
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
