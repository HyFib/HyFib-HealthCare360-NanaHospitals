import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { NetworkStatus } from '../bridge/bridge';
import {
  checkConnection,
  checkServerReady,
  connectionHealth,
  noteServerFailure,
  onCable,
  serverOutage,
  serverStatus,
  useConnection,
  weakWifiSignal,
} from './connection';

const wifi = (signalQuality: number | null, detailsAvailable = true): NetworkStatus => ({
  wifiConnected: true,
  ssid: 'NH-Staff',
  signalQuality,
  receiveRateMbps: null,
  security: null,
  detailsAvailable,
  note: null,
  serverReachable: true,
  serverLatencyMs: null,
});

const healthy = { browserOnline: true, serverReachable: true, realtime: 'connected', wifi: null } as const;

describe('connectionHealth', () => {
  it('is offline when the browser or the server is', () => {
    expect(connectionHealth({ ...healthy, browserOnline: false })).toBe('offline');
    expect(connectionHealth({ ...healthy, serverReachable: false })).toBe('offline');
  });

  it('is degraded while live updates are off or the WiFi signal is weak', () => {
    expect(connectionHealth({ ...healthy, realtime: 'reconnecting' })).toBe('degraded');
    expect(connectionHealth({ ...healthy, wifi: wifi(39) })).toBe('degraded');
  });

  it('is good otherwise', () => {
    expect(connectionHealth(healthy)).toBe('good');
    expect(connectionHealth({ ...healthy, wifi: wifi(40) })).toBe('good');
  });
});

describe('weakWifiSignal', () => {
  it('gives the signal only when HC360 Desktop reports one under 40%', () => {
    expect(weakWifiSignal({ wifi: wifi(28) })).toBe(28);
    expect(weakWifiSignal({ wifi: wifi(40) })).toBeNull();
    expect(weakWifiSignal({ wifi: wifi(28, false) })).toBeNull();
    expect(weakWifiSignal({ wifi: wifi(null) })).toBeNull();
    expect(weakWifiSignal({ wifi: null })).toBeNull();
  });
});

// DED-07 on a cabled LAN, as at Nana Hospitals: a workstation's cable is its link. A cabled laptop whose WiFi was also
// on, weakly, was "Connection is weak" and told its WiFi signal was weak, while HC360 went over its cable.
describe('on a network cable', () => {
  const cabled = (signalQuality: number | null): NetworkStatus => ({
    ...wifi(signalQuality),
    wired: true,
    cableSpeedMbps: 1000,
  });

  it('is told by HC360 Desktop; one that does not say is not taken to be on a cable', () => {
    expect(onCable({ wifi: cabled(80) })).toBe(true);
    expect(onCable({ wifi: { ...wifi(80), wired: false } })).toBe(false);
    expect(onCable({ wifi: wifi(80) })).toBe(false);
    expect(onCable({ wifi: null })).toBe(false);
  });

  it('a weak WiFi signal beside the cable is not a weak connection', () => {
    expect(weakWifiSignal({ wifi: cabled(28) })).toBeNull();
    expect(connectionHealth({ ...healthy, wifi: cabled(28) })).toBe('good');
    expect(connectionHealth({ ...healthy, wifi: cabled(28), realtime: 'disconnected' })).toBe('degraded');
    expect(connectionHealth({ ...healthy, wifi: cabled(28), serverReachable: false })).toBe('offline');
  });
});

/** The server as its health checks answer: `/health/live` always, `/health/ready` with what its database says. */
function server(ready: () => Response | Promise<Response>, live: () => Response | Promise<Response> = () => ok()) {
  const asked: string[] = [];
  const fetchMock = vi.fn(async (path: string) => {
    asked.push(path);
    return path === '/health/ready' ? ready() : live();
  });
  vi.stubGlobal('fetch', fetchMock);
  return asked;
}

const ok = () => new Response('Healthy', { status: 200 });
const unhealthy = () => new Response('Unhealthy', { status: 503 });
const noAnswer = () => Promise.reject(new DOMException('The operation timed out.', 'TimeoutError'));

/**
 * SRS DED-07: when the server answers but its database is down, every request fails as surely as when the server
 * cannot be reached, though each gets an answer. The app tells the two apart from "online", so the ward is told and
 * offered the offline copy in both.
 */
describe('a database that is down', () => {
  beforeEach(() => {
    // Each test starts past the few seconds within which failures do not ask the server again.
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(Date.now() + 60_000);
    useConnection.setState({ browserOnline: true, serverReachable: true, databaseDown: false });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    useConnection.setState({ browserOnline: true, serverReachable: true, databaseDown: false });
  });

  it('is an outage of its own kind: neither online nor unreachable', () => {
    expect(serverStatus({ ...healthy, databaseDown: true })).toBe('database-down');
    expect(serverOutage({ ...healthy, databaseDown: true })).toBe(true);
    expect(connectionHealth({ ...healthy, databaseDown: true })).toBe('offline');
    expect(serverStatus(healthy)).toBe('online');
    expect(serverOutage(healthy)).toBe(false);
    // A server that cannot be reached says nothing of its database.
    expect(serverStatus({ ...healthy, serverReachable: false, databaseDown: true })).toBe('unreachable');
  });

  it('is found when a request fails for no reason and the server says it is not ready', async () => {
    const asked = server(unhealthy);

    noteServerFailure();
    await checkServerReady();

    expect(asked).toEqual(['/health/ready']);
    expect(useConnection.getState().databaseDown).toBe(true);
    expect(serverStatus(useConnection.getState())).toBe('database-down');
  });

  it('is not claimed when the server says it is ready, or a proxy does not pass the check on', async () => {
    server(ok);
    await checkServerReady();
    expect(useConnection.getState().databaseDown).toBe(false);

    server(() => new Response('Not found', { status: 404 }));
    await checkServerReady();
    expect(useConnection.getState().databaseDown).toBe(false);
  });

  it('is found when the check gets no answer while the server itself answers; otherwise the server is unreachable', async () => {
    server(noAnswer);
    await checkServerReady();
    expect(serverStatus(useConnection.getState())).toBe('database-down');

    useConnection.setState({ databaseDown: false });
    server(noAnswer, noAnswer);
    await checkServerReady();
    expect(serverStatus(useConnection.getState())).toBe('unreachable');
  });

  it('asks the server once for many failures at once, not once each', async () => {
    const asked = server(unhealthy);

    for (let i = 0; i < 20; i++) {
      noteServerFailure();
    }
    await checkServerReady();

    expect(asked.filter((path) => path === '/health/ready')).toHaveLength(1);
  });

  it('is checked again with the connection while it lasts, and over once the server is ready again', async () => {
    let databaseUp = false;
    const asked = server(() => (databaseUp ? ok() : unhealthy()));

    // While all is well, only `/health/live` is asked: the readiness check opens a database connection each time.
    await checkConnection();
    expect(asked).toEqual(['/health/live']);

    useConnection.setState({ databaseDown: true });
    await checkConnection();
    expect(useConnection.getState().databaseDown).toBe(true);

    databaseUp = true;
    await checkConnection();
    expect(useConnection.getState().databaseDown).toBe(false);
    expect(serverStatus(useConnection.getState())).toBe('online');
  });
});
