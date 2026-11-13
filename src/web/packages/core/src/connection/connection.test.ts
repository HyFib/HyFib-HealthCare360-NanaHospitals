import { describe, expect, it } from 'vitest';
import type { NetworkStatus } from '../bridge/bridge';
import { connectionHealth, onCable, weakWifiSignal } from './connection';

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
