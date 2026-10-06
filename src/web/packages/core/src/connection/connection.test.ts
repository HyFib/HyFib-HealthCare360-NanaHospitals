import { describe, expect, it } from 'vitest';
import type { NetworkStatus } from '../bridge/bridge';
import { connectionHealth, weakWifiSignal } from './connection';

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
